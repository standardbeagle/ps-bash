using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashDiff</c> function
/// (REFACTOR-2 follow-on). Compares two text files line-by-line via an
/// LCS-based edit script (the exact slice the psm1 oracle used: an N+1 x M+1
/// integer DP table backtracked into '=' / '-' / '+' edits), then emits the
/// result in one of three formats: normal (default), unified (<c>-u</c>), or
/// context (<c>-c</c>). When the two files compare equal under the active
/// whitespace/case flags, no output is emitted (matching the oracle).
///
/// Behavioral parity oracle: the original psm1 function. Flags and their
/// effects reproduce the oracle's switch byte-for-byte: <c>-u</c> unified,
/// <c>-c</c> context, <c>-q</c>/<c>--brief</c> brief, <c>-w</c>/<c>--ignore-all-space</c>,
/// <c>-b</c>/<c>--ignore-space-change</c>, <c>-B</c>/<c>--ignore-blank-lines</c>,
/// <c>-i</c>/<c>--ignore-case</c>.
///
/// Flag-binding hazards declared explicitly: <c>-i</c> (prefix-collides with
/// <c>-InformationAction</c>/<c>-InformationVariable</c>) and <c>-w</c>
/// (prefix-collides with <c>-WarningAction</c>/<c>-WarningVariable</c>) are
/// declared as named <see cref="SwitchParameter"/>s. <c>-u</c>, <c>-c</c>,
/// <c>-b</c>, <c>-B</c>, <c>-q</c> share no prefix with PowerShell common
/// parameters and stay in <see cref="Arguments"/>, parsed by the manual loop.
/// Long forms (<c>--brief</c>, <c>--ignore-all-space</c>, etc.) also flow
/// through <see cref="Arguments"/>.
///
/// File-only mode. Reads via <see cref="File.ReadAllText(string)"/> with CRLF
/// normalization; the trailing-newline-eats-empty-line slice matches
/// <c>StreamReader.ReadLine()</c> semantics (a file ending in <c>\n</c> does
/// not produce a spurious empty final line). Each emitted line goes through
/// <see cref="BashRuntime.NewBashObject(string)"/>.
///
/// Exit code: when the files differ under the active flags, sets
/// <c>$global:LASTEXITCODE = 1</c> via
/// <see cref="FileSystemHelpers.SetLastExitCode"/>; identical files leave it at 0.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashDiff")]
[OutputType(typeof(string))]
public sealed class InvokeBashDiffCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// <c>-i</c> ignore-case — declared explicitly because the bare token
    /// <c>-i</c> would otherwise prefix-match <c>-InformationAction</c> /
    /// <c>-InformationVariable</c> under the PSCmdlet binder.
    /// </summary>
    [Parameter]
    public SwitchParameter I { get; set; }

    /// <summary>
    /// <c>-w</c> ignore-all-whitespace — declared explicitly because the bare
    /// token <c>-w</c> would otherwise prefix-match <c>-WarningAction</c> /
    /// <c>-WarningVariable</c>.
    /// </summary>
    [Parameter]
    public SwitchParameter W { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "diff"))
            {
                WriteObject(line);
            }
            return;
        }

        bool unified = false;
        bool context = false;
        bool brief = false;
        bool ignoreAllSpace = W.IsPresent;
        bool ignoreSpaceChange = false;
        bool ignoreBlankLines = false;
        bool ignoreCase = I.IsPresent;
        var operands = new List<string>();
        bool pastDoubleDash = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (pastDoubleDash)
            {
                operands.Add(arg);
                continue;
            }

            if (arg == "--") { pastDoubleDash = true; continue; }

            // Case-sensitive matches mirror the oracle's `-ceq` slice.
            if (arg.Equals("-u", StringComparison.Ordinal)) { unified = true; continue; }
            if (arg.Equals("-c", StringComparison.Ordinal)) { context = true; continue; }
            if (arg.Equals("-q", StringComparison.Ordinal) || arg == "--brief") { brief = true; continue; }
            if (arg.Equals("-w", StringComparison.Ordinal) || arg == "--ignore-all-space") { ignoreAllSpace = true; continue; }
            if (arg.Equals("-b", StringComparison.Ordinal) || arg == "--ignore-space-change") { ignoreSpaceChange = true; continue; }
            if (arg.Equals("-B", StringComparison.Ordinal) || arg == "--ignore-blank-lines") { ignoreBlankLines = true; continue; }
            if (arg.Equals("-i", StringComparison.Ordinal) || arg == "--ignore-case") { ignoreCase = true; continue; }

            operands.Add(arg);
        }

        if (operands.Count < 2)
        {
            FileSystemHelpers.WriteBashError(this, "diff: missing operand");
            return;
        }

        string path1 = SessionState.Path.GetUnresolvedProviderPathFromPSPath(operands[0]);
        string path2 = SessionState.Path.GetUnresolvedProviderPathFromPSPath(operands[1]);

        string[]? lines1 = ReadFileLines(path1);
        if (lines1 == null) return;
        string[]? lines2 = ReadFileLines(path2);
        if (lines2 == null) return;

        // Build comparison keys applying whitespace/case flags.
        var cmp1 = new string[lines1.Length];
        for (int xi = 0; xi < lines1.Length; xi++)
        {
            cmp1[xi] = NormalizeKey(lines1[xi], ignoreAllSpace, ignoreSpaceChange, ignoreCase);
        }
        var cmp2 = new string[lines2.Length];
        for (int yi = 0; yi < lines2.Length; yi++)
        {
            cmp2[yi] = NormalizeKey(lines2[yi], ignoreAllSpace, ignoreSpaceChange, ignoreCase);
        }

        // Build filtered indices (skip blank lines if -B is set).
        var idx1 = new List<int>();
        for (int xi = 0; xi < cmp1.Length; xi++)
        {
            if (ignoreBlankLines && cmp1[xi].Length == 0) continue;
            idx1.Add(xi);
        }
        var idx2 = new List<int>();
        for (int yi = 0; yi < cmp2.Length; yi++)
        {
            if (ignoreBlankLines && cmp2[yi].Length == 0) continue;
            idx2.Add(yi);
        }

        int n = idx1.Count;
        int m = idx2.Count;

        // LCS DP table over filtered comparison keys.
        var dp = new int[n + 1, m + 1];
        for (int xi = n - 1; xi >= 0; xi--)
        {
            for (int yi = m - 1; yi >= 0; yi--)
            {
                if (string.Equals(cmp1[idx1[xi]], cmp2[idx2[yi]], StringComparison.Ordinal))
                {
                    dp[xi, yi] = dp[xi + 1, yi + 1] + 1;
                }
                else
                {
                    int a = dp[xi + 1, yi];
                    int b = dp[xi, yi + 1];
                    dp[xi, yi] = a >= b ? a : b;
                }
            }
        }

        // Backtrack into edit script using original indices.
        var edits = new List<Edit>();
        {
            int xi = 0, yi = 0;
            while (xi < n && yi < m)
            {
                if (string.Equals(cmp1[idx1[xi]], cmp2[idx2[yi]], StringComparison.Ordinal))
                {
                    edits.Add(new Edit('=', idx1[xi], idx2[yi]));
                    xi++; yi++;
                }
                else if (dp[xi + 1, yi] >= dp[xi, yi + 1])
                {
                    edits.Add(new Edit('-', idx1[xi], -1));
                    xi++;
                }
                else
                {
                    edits.Add(new Edit('+', -1, idx2[yi]));
                    yi++;
                }
            }
            while (xi < n) { edits.Add(new Edit('-', idx1[xi], -1)); xi++; }
            while (yi < m) { edits.Add(new Edit('+', -1, idx2[yi])); yi++; }
        }

        bool hasDiff = edits.Any(e => e.Op != '=');
        if (!hasDiff) return;

        FileSystemHelpers.SetLastExitCode(this, 1);

        if (brief)
        {
            WriteObject(BashRuntime.NewBashObject($"Files {operands[0]} and {operands[1]} differ"));
            return;
        }

        if (unified || context)
        {
            const int contextLines = 3;
            var hunkGroups = new List<List<Edit>>();
            int ei = 0;
            while (ei < edits.Count)
            {
                if (edits[ei].Op != '=')
                {
                    int start = Math.Max(0, ei - contextLines);
                    int end = ei;
                    while (end < edits.Count)
                    {
                        if (edits[end].Op != '=') { end++; continue; }
                        int lookAhead = 0;
                        int j = end;
                        while (j < edits.Count && edits[j].Op == '=') { lookAhead++; j++; }
                        if (lookAhead <= contextLines * 2 && j < edits.Count)
                        {
                            end = j;
                        }
                        else
                        {
                            end = Math.Min(end + contextLines, edits.Count);
                            break;
                        }
                    }
                    var group = new List<Edit>();
                    for (int k = start; k < end; k++) group.Add(edits[k]);
                    hunkGroups.Add(group);
                    ei = end;
                }
                else
                {
                    ei++;
                }
            }

            if (unified)
            {
                WriteObject(BashRuntime.NewBashObject($"--- {operands[0]}"));
                WriteObject(BashRuntime.NewBashObject($"+++ {operands[1]}"));
                foreach (var group in hunkGroups)
                {
                    int l1Start = -1, l1Count = 0, l2Start = -1, l2Count = 0;
                    var hunkLines = new List<string>();
                    foreach (var e in group)
                    {
                        switch (e.Op)
                        {
                            case '=':
                                if (l1Start == -1) l1Start = e.Line1 + 1;
                                if (l2Start == -1) l2Start = e.Line2 + 1;
                                l1Count++; l2Count++;
                                hunkLines.Add(" " + lines1[e.Line1]);
                                break;
                            case '-':
                                if (l1Start == -1) l1Start = e.Line1 + 1;
                                if (l2Start == -1) l2Start = e.Line1 + 1;
                                l1Count++;
                                hunkLines.Add("-" + lines1[e.Line1]);
                                break;
                            case '+':
                                if (l1Start == -1) l1Start = e.Line2 + 1;
                                if (l2Start == -1) l2Start = e.Line2 + 1;
                                l2Count++;
                                hunkLines.Add("+" + lines2[e.Line2]);
                                break;
                        }
                    }
                    WriteObject(BashRuntime.NewBashObject($"@@ -{l1Start},{l1Count} +{l2Start},{l2Count} @@"));
                    foreach (var hl in hunkLines) WriteObject(BashRuntime.NewBashObject(hl));
                }
            }
            else // context
            {
                WriteObject(BashRuntime.NewBashObject($"*** {operands[0]}"));
                WriteObject(BashRuntime.NewBashObject($"--- {operands[1]}"));
                foreach (var group in hunkGroups)
                {
                    int l1Start = -1, l1End = -1, l2Start = -1, l2End = -1;
                    foreach (var e in group)
                    {
                        switch (e.Op)
                        {
                            case '=':
                                if (l1Start == -1) l1Start = e.Line1 + 1;
                                l1End = e.Line1 + 1;
                                if (l2Start == -1) l2Start = e.Line2 + 1;
                                l2End = e.Line2 + 1;
                                break;
                            case '-':
                                if (l1Start == -1) l1Start = e.Line1 + 1;
                                l1End = e.Line1 + 1;
                                break;
                            case '+':
                                if (l2Start == -1) l2Start = e.Line2 + 1;
                                l2End = e.Line2 + 1;
                                break;
                        }
                    }
                    WriteObject(BashRuntime.NewBashObject("***************"));
                    WriteObject(BashRuntime.NewBashObject($"*** {l1Start},{l1End}"));
                    var changeLine1 = new HashSet<int>();
                    for (int gi = 0; gi < group.Count; gi++)
                    {
                        if (group[gi].Op == '-' && gi + 1 < group.Count && group[gi + 1].Op == '+')
                        {
                            changeLine1.Add(group[gi].Line1);
                        }
                    }
                    foreach (var e in group)
                    {
                        switch (e.Op)
                        {
                            case '=': WriteObject(BashRuntime.NewBashObject("  " + lines1[e.Line1])); break;
                            case '-':
                                if (changeLine1.Contains(e.Line1))
                                    WriteObject(BashRuntime.NewBashObject("! " + lines1[e.Line1]));
                                else
                                    WriteObject(BashRuntime.NewBashObject("- " + lines1[e.Line1]));
                                break;
                        }
                    }
                    WriteObject(BashRuntime.NewBashObject($"--- {l2Start},{l2End}"));
                    var changeLine2 = new HashSet<int>();
                    for (int gi = 0; gi < group.Count; gi++)
                    {
                        if (group[gi].Op == '+' && gi > 0 && group[gi - 1].Op == '-')
                        {
                            changeLine2.Add(group[gi].Line2);
                        }
                    }
                    foreach (var e in group)
                    {
                        switch (e.Op)
                        {
                            case '=': WriteObject(BashRuntime.NewBashObject("  " + lines2[e.Line2])); break;
                            case '+':
                                if (changeLine2.Contains(e.Line2))
                                    WriteObject(BashRuntime.NewBashObject("! " + lines2[e.Line2]));
                                else
                                    WriteObject(BashRuntime.NewBashObject("+ " + lines2[e.Line2]));
                                break;
                        }
                    }
                }
            }
        }
        else
        {
            // Normal diff format.
            int ei = 0;
            while (ei < edits.Count)
            {
                if (edits[ei].Op == '=') { ei++; continue; }

                int delStart = -1, delEnd = -1, addStart = -1, addEnd = -1;
                var delLines = new List<string>();
                var addLines = new List<string>();

                while (ei < edits.Count && edits[ei].Op != '=')
                {
                    var e = edits[ei];
                    if (e.Op == '-')
                    {
                        if (delStart == -1) delStart = e.Line1 + 1;
                        delEnd = e.Line1 + 1;
                        delLines.Add(lines1[e.Line1]);
                    }
                    else if (e.Op == '+')
                    {
                        if (addStart == -1) addStart = e.Line2 + 1;
                        addEnd = e.Line2 + 1;
                        addLines.Add(lines2[e.Line2]);
                    }
                    ei++;
                }

                string delRange = (delStart == delEnd || delStart == -1) ? $"{delStart}" : $"{delStart},{delEnd}";
                string addRange = (addStart == addEnd || addStart == -1) ? $"{addStart}" : $"{addStart},{addEnd}";

                if (delLines.Count > 0 && addLines.Count > 0)
                {
                    WriteObject(BashRuntime.NewBashObject($"{delRange}c{addRange}"));
                    foreach (var dl in delLines) WriteObject(BashRuntime.NewBashObject("< " + dl));
                    WriteObject(BashRuntime.NewBashObject("---"));
                    foreach (var al in addLines) WriteObject(BashRuntime.NewBashObject("> " + al));
                }
                else if (delLines.Count > 0)
                {
                    int addPos = addStart == -1 ? (delStart > 1 ? delStart - 1 : 0) : addStart;
                    WriteObject(BashRuntime.NewBashObject($"{delRange}d{addPos}"));
                    foreach (var dl in delLines) WriteObject(BashRuntime.NewBashObject("< " + dl));
                }
                else if (addLines.Count > 0)
                {
                    int delPos = delStart == -1 ? (addStart > 1 ? addStart - 1 : 0) : delStart;
                    WriteObject(BashRuntime.NewBashObject($"{delPos}a{addRange}"));
                    foreach (var al in addLines) WriteObject(BashRuntime.NewBashObject("> " + al));
                }
            }
        }
    }

    private static string NormalizeKey(string line, bool ignoreAllSpace, bool ignoreSpaceChange, bool ignoreCase)
    {
        string key = line;
        if (ignoreAllSpace)
        {
            key = System.Text.RegularExpressions.Regex.Replace(key, @"\s", "");
        }
        else if (ignoreSpaceChange)
        {
            key = System.Text.RegularExpressions.Regex.Replace(key, @"^\s+", "");
            key = System.Text.RegularExpressions.Regex.Replace(key, @"\s+$", "");
            key = System.Text.RegularExpressions.Regex.Replace(key, @"\s+", " ");
        }
        if (ignoreCase) key = key.ToLowerInvariant();
        return key;
    }

    private string[]? ReadFileLines(string path)
    {
        string content;
        try
        {
            content = File.ReadAllText(path).Replace("\r\n", "\n");
        }
        catch (Exception ex)
        {
            bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
            string msg = notFound ? "No such file or directory" : ex.Message;
            string normalized = path.Replace('\\', '/');
            FileSystemHelpers.WriteBashError(this, $"diff: {normalized}: {msg}");
            return null;
        }

        if (content.Length == 0) return Array.Empty<string>();

        bool trailingNl = content.EndsWith("\n");
        string body = trailingNl ? content.Substring(0, content.Length - 1) : content;
        if (body.Length == 0) return new[] { string.Empty };
        return body.Split('\n');
    }

    private readonly struct Edit
    {
        public readonly char Op;
        public readonly int Line1;
        public readonly int Line2;
        public Edit(char op, int line1, int line2) { Op = op; Line1 = line1; Line2 = line2; }
    }
}
