using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashUniq</c> function
/// (REFACTOR-2 follow-on). Collapses adjacent duplicate lines and supports
/// <c>-c</c> count-prefix, <c>-d</c> duplicates-only, <c>-u</c> uniques-only,
/// <c>-i</c> case-insensitive, <c>-f N</c> skip N whitespace-separated fields,
/// <c>-s N</c> skip N chars, <c>-w N</c> compare at most N chars after skips.
///
/// File + pipeline dual mode. Pipeline mode splits each item's <c>BashText</c>
/// on <c>\n</c> (after trailing-newline trim). File mode reads via
/// <see cref="File.ReadAllText(string)"/> with CRLF normalization and splits
/// on <c>\n</c> (StreamReader.ReadLine semantics — trailing newline does not
/// produce a spurious empty final line). Glob expansion routes through
/// <see cref="FileSystemHelpers.ResolveOperandPaths(PSCmdlet, string)"/>; a
/// file-read failure emits a bash-style error via
/// <see cref="FileSystemHelpers.WriteBashError(PSCmdlet, string)"/> and the
/// cmdlet continues with remaining operands.
///
/// Flag binding: three bare-token short flags prefix-collide with PowerShell
/// common parameters and are declared as explicit <see cref="SwitchParameter"/>s
/// (an exact param-name match beats a common-parameter prefix match):
/// <list type="bullet">
/// <item><c>-c</c> (count) vs <c>-Confirm</c> — declared as <c>C</c>.</item>
/// <item><c>-d</c> (dupes-only) vs <c>-Debug</c> — declared as <c>D</c>.</item>
/// <item><c>-i</c> (case-insensitive) vs <c>-InformationAction</c> /
/// <c>-InformationVariable</c> — declared as <c>I</c>.</item>
/// </list>
/// <c>-u</c>, <c>-f</c>, <c>-s</c>, <c>-w</c> have no PS common-parameter
/// prefix collision and stay in <see cref="Arguments"/>; they are parsed
/// (separated and joined forms) by the manual value-flag scan. Bundled forms
/// like <c>-cd</c>, <c>-ci</c>, <c>-cdi</c> survive the binder by landing in
/// <see cref="Arguments"/> intact (they don't bind to any switch's exact name)
/// and are recovered post-parse against the oracle's per-char dispatch.
///
/// Output uses <see cref="BashRuntime.NewBashObject(string)"/> with the
/// default <c>PsBash.TextOutput</c> shape.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashUniq")]
[OutputType(typeof(string))]
public sealed class InvokeBashUniqCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter]
    public SwitchParameter C { get; set; }

    [Parameter]
    public SwitchParameter D { get; set; }

    [Parameter]
    public SwitchParameter I { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _pipeline = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null)
        {
            _pipeline.Add(InputObject);
        }
    }

    protected override void EndProcessing()
    {
        var rawArgs = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "uniq", rawArgs)) return;
        if (Array.IndexOf(rawArgs, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "uniq"))
            {
                WriteObject(line);
            }
            return;
        }

        bool countMode = C.IsPresent;
        bool duplicatesOnly = D.IsPresent;
        bool ignoreCase = I.IsPresent;
        bool uniqueOnly = false;
        int skipFields = 0;
        int skipChars = 0;
        int checkChars = 0;
        var operands = new List<string>();

        int i = 0;
        while (i < rawArgs.Length)
        {
            var arg = rawArgs[i];

            if (arg == "--")
            {
                i++;
                while (i < rawArgs.Length)
                {
                    operands.Add(rawArgs[i]);
                    i++;
                }
                break;
            }

            if (arg == "--ignore-case")
            {
                ignoreCase = true;
                i++;
                continue;
            }

            if (arg.StartsWith("--skip-fields=", StringComparison.Ordinal))
            {
                if (int.TryParse(arg.Substring("--skip-fields=".Length), out var sf))
                {
                    skipFields = sf;
                }
                i++;
                continue;
            }

            if (arg.StartsWith("--skip-chars=", StringComparison.Ordinal))
            {
                if (int.TryParse(arg.Substring("--skip-chars=".Length), out var sc))
                {
                    skipChars = sc;
                }
                i++;
                continue;
            }

            if (arg.StartsWith("--check-chars=", StringComparison.Ordinal))
            {
                if (int.TryParse(arg.Substring("--check-chars=".Length), out var cc))
                {
                    checkChars = cc;
                }
                i++;
                continue;
            }

            // Short-flag bundle. Numeric-leading (e.g. "-5") is treated as an
            // operand (oracle parity).
            if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1 && !IsNumericFlag(arg))
            {
                var body = arg.Substring(1);
                int j = 0;
                while (j < body.Length)
                {
                    char ch = body[j];
                    switch (ch)
                    {
                        case 'c': countMode = true; j++; break;
                        case 'd': duplicatesOnly = true; j++; break;
                        case 'u': uniqueOnly = true; j++; break;
                        case 'i': ignoreCase = true; j++; break;
                        case 'f':
                        {
                            string rest = body.Substring(j + 1);
                            if (rest.Length > 0 && IsDigitRun(rest, out var n))
                            {
                                skipFields = n;
                                j = body.Length;
                            }
                            else
                            {
                                i++;
                                if (i < rawArgs.Length && int.TryParse(rawArgs[i], out var nv))
                                {
                                    skipFields = nv;
                                }
                                j = body.Length;
                            }
                            break;
                        }
                        case 's':
                        {
                            string rest = body.Substring(j + 1);
                            if (rest.Length > 0 && IsDigitRun(rest, out var n))
                            {
                                skipChars = n;
                                j = body.Length;
                            }
                            else
                            {
                                i++;
                                if (i < rawArgs.Length && int.TryParse(rawArgs[i], out var nv))
                                {
                                    skipChars = nv;
                                }
                                j = body.Length;
                            }
                            break;
                        }
                        case 'w':
                        {
                            string rest = body.Substring(j + 1);
                            if (rest.Length > 0 && IsDigitRun(rest, out var n))
                            {
                                checkChars = n;
                                j = body.Length;
                            }
                            else
                            {
                                i++;
                                if (i < rawArgs.Length && int.TryParse(rawArgs[i], out var nv))
                                {
                                    checkChars = nv;
                                }
                                j = body.Length;
                            }
                            break;
                        }
                        default:
                            j++;
                            break;
                    }
                }
                i++;
                continue;
            }

            operands.Add(arg);
            i++;
        }

        bool hadError = false;
        string? prevLine = null;
        string? prevKey = null;
        int runCount = 0;

        void FlushRun()
        {
            if (prevLine == null) return;
            if (duplicatesOnly && runCount < 2) return;
            if (uniqueOnly && runCount > 1) return;

            if (countMode)
            {
                string text = string.Format("{0,7} {1}", runCount, prevLine);
                WriteObject(BashRuntime.NewBashObject(text));
            }
            else
            {
                WriteObject(BashRuntime.NewBashObject(prevLine));
            }
        }

        void ProcessLine(string line)
        {
            string key = GetUniqKey(line, skipFields, skipChars, checkChars);
            bool same;
            if (prevKey == null)
            {
                same = false;
            }
            else if (ignoreCase)
            {
                same = string.Equals(key, prevKey, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                same = string.Equals(key, prevKey, StringComparison.Ordinal);
            }

            if (same)
            {
                runCount++;
                return;
            }

            FlushRun();
            prevLine = line;
            prevKey = key;
            runCount = 1;
        }

        if (operands.Count == 0 && _pipeline.Count > 0)
        {
            foreach (var item in _pipeline)
            {
                string text = BashRuntime.GetBashText(item);
                string trimmed = text.TrimEnd('\n');
                if (trimmed.Contains('\n'))
                {
                    foreach (var subLine in trimmed.Split('\n'))
                    {
                        ProcessLine(subLine);
                    }
                }
                else
                {
                    ProcessLine(trimmed);
                }
            }
        }
        else
        {
            foreach (var raw in operands)
            {
                foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
                {
                    try
                    {
                        foreach (var line in BashFileSystem.ReadLines(filePath))
                        {
                            ProcessLine(line);
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteReadError(filePath, ex);
                        hadError = true;
                    }
                }
            }
        }

        FlushRun();

        if (hadError)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
        }
    }

    private static bool IsNumericFlag(string arg)
    {
        if (arg.Length < 2) return false;
        return char.IsDigit(arg[1]);
    }

    private static bool IsDigitRun(string s, out int value)
    {
        int end = 0;
        while (end < s.Length && char.IsDigit(s[end])) end++;
        if (end == 0 || end != s.Length)
        {
            value = 0;
            return false;
        }
        return int.TryParse(s, out value);
    }

    private static string GetUniqKey(string line, int skipFields, int skipChars, int checkChars)
    {
        string key = line;

        if (skipFields > 0)
        {
            int unboundedFieldCount = CountWhitespaceFields(key);
            if (unboundedFieldCount > skipFields)
            {
                key = ConsumeSkipFields(line, skipFields);
            }
            else
            {
                key = "";
            }
        }

        if (skipChars > 0 && key.Length > skipChars)
        {
            key = key.Substring(skipChars);
        }
        else if (skipChars > 0)
        {
            key = "";
        }

        if (checkChars > 0 && key.Length > checkChars)
        {
            key = key.Substring(0, checkChars);
        }

        return key;
    }

    private static int CountWhitespaceFields(string s)
    {
        // Same shape as PowerShell's `$s -split '\s+'` (unbounded).
        return System.Text.RegularExpressions.Regex.Split(s, @"\s+").Length;
    }

    private static string ConsumeSkipFields(string line, int skipFields)
    {
        // Reproduce `$line -split '\s+', (N+1)` then `parts[N]` selection
        // by walking N fields. A leading whitespace run counts as an empty
        // first field.
        int idx = 0;
        int len = line.Length;
        for (int n = 0; n < skipFields; n++)
        {
            while (idx < len && !char.IsWhiteSpace(line[idx])) idx++;
            while (idx < len && char.IsWhiteSpace(line[idx])) idx++;
        }
        if (idx >= len) return "";
        return line.Substring(idx);
    }

    private void WriteReadError(string path, Exception ex)
    {
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"uniq: {normalized}: {msg}");
    }
}
