using System.Diagnostics;
using System.Management.Automation;
using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashRg</c> function
/// (REFACTOR-2 Phase 4 follow-on). Wrapper around the ripgrep binary
/// <c>rg.exe</c> when present on PATH, with an internal regex-search
/// fallback that mirrors the psm1 oracle byte-for-byte.
///
/// Native passthrough: <c>Get-Command rg -CommandType Application</c> via
/// parameter-bound <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>;
/// when found, shells out via <see cref="Process"/> with
/// <c>UseShellExecute=false</c>, <c>RedirectStandardOutput=true</c>, and
/// arguments bound via <see cref="ProcessStartInfo.ArgumentList"/>
/// (Directive 12: no shell, no string concatenation into the command line).
/// Captured stdout is emitted line-by-line as bare <c>PsBash.TextOutput</c>
/// strings.
///
/// Fallback (no <c>rg</c> on PATH): the cmdlet implements the same flag
/// surface the psm1 oracle implemented internally — <c>-i</c>, <c>-w</c>,
/// <c>-c</c>, <c>-l</c>, <c>-n</c>, <c>-N</c>, <c>-o</c>, <c>-v</c>,
/// <c>-F</c>, <c>-g</c>, <c>-A</c>, <c>-B</c>, <c>-C</c>, <c>--hidden</c>
/// — over pipeline or recursive file-mode input.
///
/// Common-parameter collisions per the playbook table — each declared as
/// an explicit parameter with a single-letter name so the binder routes
/// the bare token by exact parameter-name match (which beats a
/// common-parameter prefix match): <c>-i</c> vs <c>-InformationAction</c>
/// → <see cref="I"/>; <c>-v</c> vs <c>-Verbose</c> → <see cref="V"/>;
/// <c>-c</c> vs <c>-Confirm</c> → <see cref="C"/>; <c>-w</c> vs
/// <c>-WarningAction</c> → <see cref="W"/>. The bare tokens <c>-A</c> /
/// <c>-B</c> prefix-match the cmdlet's own <see cref="Arguments"/>
/// parameter (same hazard <c>grep</c> / <c>ls</c> hit) — both declared
/// as nullable <c>int? A</c> / <c>int? B</c>. <c>-n</c>, <c>-N</c>,
/// <c>-l</c>, <c>-F</c>, <c>-o</c>, <c>-g</c>, <c>--hidden</c>, joined
/// <c>-AN</c> / <c>-BN</c> / <c>-CN</c>, and the long-form aliases
/// stay in <see cref="Arguments"/>.
///
/// Known binder limitation (same shape as <c>grep</c>'s <c>-C N</c>
/// residual gap): the bare token <c>-C</c> case-folds to the cmdlet's
/// <see cref="C"/> count-switch under PowerShell's case-insensitive
/// binder. Use the joined <c>-CN</c> form or pass <c>-A N -B N</c>
/// equivalently. The single-flag forms <c>-A N</c> / <c>-B N</c> /
/// <c>-A2</c> etc. are unaffected.
///
/// Output: when the internal fallback runs and produces a match, the
/// cmdlet emits a typed <c>PsBash.RgMatch</c> PSObject per match with
/// <c>FileName</c>, <c>LineNumber</c>, <c>Line</c>, and <c>BashText</c>
/// properties (oracle parity). <c>-c</c> / <c>-l</c> emit bare strings
/// via <see cref="BashRuntime.NewBashObject"/>. The native-passthrough
/// branch emits the rg binary's stdout as bare <c>PsBash.TextOutput</c>
/// strings (one per line). Errors route through <see cref="FileSystemHelpers.WriteBashError"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashRg")]
[OutputType("PsBash.RgMatch")]
[OutputType(typeof(string))]
public sealed class InvokeBashRgCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Bash <c>-i</c> (ignore case). Prefix-collides with <c>-InformationAction</c>.</summary>
    [Parameter] public SwitchParameter I { get; set; }

    /// <summary>Bash <c>-v</c> (invert match). Prefix-collides with <c>-Verbose</c>.</summary>
    [Parameter] public SwitchParameter V { get; set; }

    /// <summary>Bash <c>-c</c> (count-only). Prefix-collides with <c>-Confirm</c>.</summary>
    [Parameter] public SwitchParameter C { get; set; }

    /// <summary>Bash <c>-w</c> (word-regexp). Prefix-collides with <c>-WarningAction</c>.</summary>
    [Parameter] public SwitchParameter W { get; set; }

    /// <summary>Bash <c>-o</c> (only matching). Prefix-collides with <c>-OutBuffer</c> / <c>-OutVariable</c>.</summary>
    [Parameter] public SwitchParameter O { get; set; }

    /// <summary>
    /// Bash <c>-A N</c> (after-context). The bare token <c>-A</c> prefix-matches
    /// the cmdlet's own <see cref="Arguments"/> parameter — same hazard
    /// <c>ls</c> / <c>grep</c> hit. Declared as <c>int?</c>.
    /// </summary>
    [Parameter] public int? A { get; set; }

    /// <summary>
    /// Bash <c>-B N</c> (before-context). Same hazard as <see cref="A"/>.
    /// </summary>
    [Parameter] public int? B { get; set; }

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
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "rg"))
            {
                WriteObject(line);
            }
            return;
        }

        // Parse flags / operands. Build the operand list we'll forward to a
        // native rg binary verbatim, and the internal-fallback state.
        bool ignoreCase = I.IsPresent;
        bool wordRegexp = W.IsPresent;
        bool countOnly = C.IsPresent;
        bool invertMatch = V.IsPresent;
        bool filesOnly = false;
        bool showLineNumbers = true;
        bool onlyMatching = O.IsPresent;
        bool fixedStrings = false;
        bool includeHidden = false;
        bool noIgnore = false;
        int afterContext = A ?? 0;
        int beforeContext = B ?? 0;
        string? globPattern = null;
        var operands = new List<string>();
        bool pastDoubleDash = false;

        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];

            if (pastDoubleDash)
            {
                operands.Add(a);
                i++;
                continue;
            }

            if (a == "--")
            {
                pastDoubleDash = true;
                i++;
                continue;
            }

            // -A NUM / -B NUM / -C NUM (separated) — but bare -A / -B were
            // already consumed by the binder into A / B; -C separated case-folds
            // to the C switch. Joined forms -A2 / -B2 / -C2 land here.
            var ctxJoined = Regex.Match(a, @"^-([ABC])(\d+)$");
            if (ctxJoined.Success)
            {
                int v = int.Parse(ctxJoined.Groups[2].Value);
                switch (ctxJoined.Groups[1].Value)
                {
                    case "A": afterContext = v; break;
                    case "B": beforeContext = v; break;
                    case "C": afterContext = v; beforeContext = v; break;
                }
                i++;
                continue;
            }
            // Bare -C N (separated). Bare -A / -B already bound; bare -C
            // case-folded to the C switch so this rarely fires, but handle it
            // defensively for literal -C in Arguments via ValueFromRemainingArguments.
            var ctxBare = Regex.Match(a, @"^-([ABC])$");
            if (ctxBare.Success && i + 1 < args.Length && int.TryParse(args[i + 1], out int cv))
            {
                switch (ctxBare.Groups[1].Value)
                {
                    case "A": afterContext = cv; break;
                    case "B": beforeContext = cv; break;
                    case "C": afterContext = cv; beforeContext = cv; break;
                }
                i += 2;
                continue;
            }

            if (a == "-g" || a == "--glob")
            {
                i++;
                if (i < args.Length) globPattern = args[i];
                i++;
                continue;
            }
            var gJoined = Regex.Match(a, @"^-g(.+)$");
            if (gJoined.Success)
            {
                globPattern = gJoined.Groups[1].Value;
                i++;
                continue;
            }

            if (a == "--hidden") { includeHidden = true; i++; continue; }

            // --no-ignore / --no-ignore-vcs and the unrestricted shorthands
            // (-u, -uu, -uuu / --unrestricted) turn OFF the default directory
            // pruning so .git / bin / obj / node_modules are searched too. -uu
            // additionally implies --hidden (ripgrep semantics).
            if (a == "--no-ignore" || a == "--no-ignore-vcs") { noIgnore = true; i++; continue; }
            if (a == "--unrestricted") { noIgnore = true; i++; continue; }
            if (Regex.IsMatch(a, @"^-u+$"))
            {
                noIgnore = true;
                if (a.Length >= 3) includeHidden = true; // -uu and beyond
                i++;
                continue;
            }

            // Long-form flags (oracle parity).
            if (a == "--ignore-case") { ignoreCase = true; i++; continue; }
            if (a == "--word-regexp") { wordRegexp = true; i++; continue; }
            if (a == "--count") { countOnly = true; i++; continue; }
            if (a == "--files-with-matches") { filesOnly = true; i++; continue; }
            if (a == "--line-number") { showLineNumbers = true; i++; continue; }
            if (a == "--no-line-number") { showLineNumbers = false; i++; continue; }
            if (a == "--only-matching") { onlyMatching = true; i++; continue; }
            if (a == "--invert-match") { invertMatch = true; i++; continue; }
            if (a == "--fixed-strings") { fixedStrings = true; i++; continue; }
            // --color[=WHEN] / --colour[=WHEN]: ripgrep accepts these; the
            // internal fallback has no per-match ANSI coloring, so accept and
            // ignore. Without this, the common `alias rg='rg --color=auto'`
            // makes the fallback treat `--color=auto` as the search pattern.
            // (When native rg.exe is on PATH the arg is forwarded verbatim and
            // honored by the binary — this branch only affects the fallback.)
            if (a == "--color" || a == "--colour"
                || a.StartsWith("--color=", StringComparison.Ordinal)
                || a.StartsWith("--colour=", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            // Short-flag bundle (single dash, length > 1, not --). Walk per-char
            // case-sensitively, matching the oracle's switch slice.
            if (a.Length > 1 && a[0] == '-' && a[1] != '-')
            {
                foreach (var ch in a.Substring(1))
                {
                    switch (ch)
                    {
                        case 'i': ignoreCase = true; break;
                        case 'w': wordRegexp = true; break;
                        case 'c': countOnly = true; break;
                        case 'l': filesOnly = true; break;
                        case 'n': showLineNumbers = true; break;
                        case 'N': showLineNumbers = false; break;
                        case 'o': onlyMatching = true; break;
                        case 'v': invertMatch = true; break;
                        case 'F': fixedStrings = true; break;
                        // Other bundle chars silently ignored (oracle parity).
                    }
                }
                i++;
                continue;
            }

            operands.Add(a);
            i++;
        }

        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "rg: usage: rg [options] pattern [path ...]");
            return;
        }

        // Native passthrough: only when we have no pipeline input. The psm1
        // oracle did not actually shell out (it implemented internally); but
        // the task requests probing for a native rg binary first. When the
        // binary is present and we have no pipeline (rg cannot read PowerShell
        // pipeline objects), shell out with the full original arg list.
        // Otherwise (binary missing or pipeline mode) fall through to the
        // internal regex engine.
        if (_pipeline.Count == 0 && TryRunNativeRg(args))
        {
            return;
        }

        // --- Internal fallback (psm1 oracle parity) ---
        string pattern = operands[0];
        var fileOperands = operands.Count > 1
            ? operands.GetRange(1, operands.Count - 1)
            : new List<string>();

        if (fixedStrings) pattern = Regex.Escape(pattern);
        if (wordRegexp) pattern = "\\b" + pattern + "\\b";

        var regexOpts = RegexOptions.None;
        if (ignoreCase) regexOpts |= RegexOptions.IgnoreCase;

        Regex regex;
        try
        {
            regex = new Regex(pattern, regexOpts);
        }
        catch (ArgumentException ex)
        {
            FileSystemHelpers.WriteBashError(this, $"rg: invalid regular expression: {ex.Message}");
            return;
        }

        // --- Pipeline mode ---
        if (_pipeline.Count > 0 && fileOperands.Count == 0)
        {
            RunPipelineMode(regex, invertMatch, countOnly, onlyMatching);
            return;
        }

        // --- File mode (recursive by default; cwd if no operands) ---
        RunFileMode(regex, fileOperands, invertMatch, showLineNumbers, countOnly,
            filesOnly, onlyMatching, includeHidden, noIgnore, globPattern,
            beforeContext, afterContext);
    }

    private bool TryRunNativeRg(string[] originalArgs)
    {
        string? nativeSource = null;
        try
        {
            var probe = InvokeCommand.InvokeScript(
                "Get-Command rg -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1");
            if (probe.Count > 0 && probe[0] != null)
            {
                nativeSource = probe[0].Properties["Source"]?.Value as string;
            }
        }
        catch
        {
            // Probe failed; treat as no native rg.
        }

        if (string.IsNullOrEmpty(nativeSource))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = nativeSource!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = SessionState.Path.CurrentLocation.Path,
            };
            // Rebuild the original arg list verbatim. The binder may have
            // consumed I/V/C/W/A/B switches into our cmdlet parameters, so
            // reconstruct the equivalents from those parameters + Arguments.
            // Order: declared switches first (their bash form), then Arguments.
            if (I.IsPresent) psi.ArgumentList.Add("-i");
            if (V.IsPresent) psi.ArgumentList.Add("-v");
            if (C.IsPresent) psi.ArgumentList.Add("-c");
            if (W.IsPresent) psi.ArgumentList.Add("-w");
            if (O.IsPresent) psi.ArgumentList.Add("-o");
            if (A.HasValue) { psi.ArgumentList.Add("-A"); psi.ArgumentList.Add(A.Value.ToString()); }
            if (B.HasValue) { psi.ArgumentList.Add("-B"); psi.ArgumentList.Add(B.Value.ToString()); }
            foreach (var arg in originalArgs)
            {
                psi.ArgumentList.Add(arg);
            }

            // Bounded spawn + concurrent stdout/stderr drain + kill-tree on timeout
            // (BashRuntime.RunChildProcess). The old code drained stderr only AFTER
            // the stdout ReadLine loop, so a large stderr burst from rg could fill
            // its pipe buffer and deadlock; and the unbounded WaitForExit could
            // wedge the host. Native rg here never reads stdin (only reached when
            // _pipeline.Count == 0), so the helper's closed stdin is safe.
            var spawn = BashRuntime.RunChildProcess(psi);

            // Emit one object per stdout line — ReadLine semantics: split on \n with
            // no spurious trailing empty line.
            var outText = spawn.Stdout.Replace("\r\n", "\n");
            if (outText.EndsWith('\n'))
                outText = outText.Substring(0, outText.Length - 1);
            if (outText.Length > 0)
            {
                foreach (var ln in outText.Split('\n'))
                    WriteObject(BashRuntime.NewBashObject(ln));
            }
            FileSystemHelpers.SetLastExitCode(this, spawn.ExitCode);
            return true;
        }
        catch
        {
            // Native invocation failed; fall through to internal engine.
            return false;
        }
    }

    private void RunPipelineMode(Regex regex, bool invertMatch, bool countOnly, bool onlyMatching)
    {
        int matchCount = 0;

        foreach (var item in _pipeline)
        {
            string text = BashRuntime.GetBashText(item);
            string trimmed = text.TrimEnd('\n');

            if (trimmed.Contains('\n'))
            {
                foreach (var subLine in trimmed.Split('\n'))
                {
                    bool isMatch = regex.IsMatch(subLine);
                    if (invertMatch) isMatch = !isMatch;
                    if (isMatch)
                    {
                        matchCount++;
                        if (!countOnly)
                        {
                            if (onlyMatching)
                            {
                                foreach (Match m in regex.Matches(subLine))
                                {
                                    WriteObject(BashRuntime.NewBashObject(m.Value));
                                }
                            }
                            else
                            {
                                WriteObject(BashRuntime.NewBashObject(subLine));
                            }
                        }
                    }
                }
            }
            else
            {
                bool isMatch = regex.IsMatch(trimmed);
                if (invertMatch) isMatch = !isMatch;
                if (isMatch)
                {
                    matchCount++;
                    if (!countOnly)
                    {
                        if (onlyMatching)
                        {
                            foreach (Match m in regex.Matches(trimmed))
                            {
                                WriteObject(BashRuntime.NewBashObject(m.Value));
                            }
                        }
                        else
                        {
                            // Pass original typed object through.
                            WriteObject(item);
                        }
                    }
                }
            }
        }

        if (countOnly)
        {
            WriteObject(BashRuntime.NewBashObject(matchCount.ToString()));
        }
    }

    private void RunFileMode(
        Regex regex, List<string> fileOperands, bool invertMatch,
        bool showLineNumbers, bool countOnly, bool filesOnly, bool onlyMatching,
        bool includeHidden, bool noIgnore, string? globPattern,
        int beforeContext, int afterContext)
    {
        var searchTargets = fileOperands.Count > 0 ? fileOperands : new List<string> { "." };
        // ripgrep filters by default (.gitignore + hidden + .git). We approximate
        // that with the shared directory-prune set; PSBASH_SEARCH_NO_IGNORE or the
        // --no-ignore / -u flags turn it off. Pruning happens BEFORE descent in
        // EnumerateSearchFiles, so .git / bin / obj / node_modules are never
        // walked — this is the fix for the old AllDirectories walk that descended
        // into them and went silent long enough to trip the host idle-timeout.
        bool includeIgnored = noIgnore || FileSystemHelpers.DefaultFilteringDisabled();

        // Resolve each target to either a single file or a lazy directory walk.
        // multipleFiles must be known before emitting, so derive it from the
        // target shapes (any directory target, or more than one file target).
        var sources = new List<IEnumerable<string>>();
        bool anyTargetIsDir = false;
        int fileTargetCount = 0;

        foreach (var target in searchTargets)
        {
            string resolved;
            try
            {
                resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(target);
            }
            catch
            {
                FileSystemHelpers.WriteBashError(this, $"rg: {target}: No such file or directory");
                continue;
            }

            if (!File.Exists(resolved) && !Directory.Exists(resolved))
            {
                FileSystemHelpers.WriteBashError(this, $"rg: {target}: No such file or directory");
                continue;
            }

            if (Directory.Exists(resolved))
            {
                anyTargetIsDir = true;
                sources.Add(WalkSearchDir(resolved, includeIgnored, includeHidden, globPattern));
            }
            else
            {
                fileTargetCount++;
                sources.Add(new[] { resolved });
            }
        }

        bool multipleFiles = anyTargetIsDir || fileTargetCount > 1;
        var matchedFiles = new List<string>();
        var perFileCounts = new Dictionary<string, int>();
        // Files actually read, in walk order — drives the multi-file count pass.
        var scanned = new List<string>();
        int totalMatchCount = 0;

        foreach (var source in sources)
        foreach (var filePath in source)
        {
            var lines = ReadFileLines(filePath);
            if (lines == null) continue;
            scanned.Add(filePath);

            var matchIndices = new List<int>();
            for (int li = 0; li < lines.Length; li++)
            {
                bool isMatch = regex.IsMatch(lines[li]);
                if (invertMatch) isMatch = !isMatch;
                if (isMatch) matchIndices.Add(li);
            }

            int fileMatchCount = matchIndices.Count;
            totalMatchCount += fileMatchCount;
            perFileCounts[filePath] = fileMatchCount;

            if (filesOnly)
            {
                if (fileMatchCount > 0) matchedFiles.Add(filePath);
                continue;
            }

            if (countOnly) continue;

            var emitLines = new SortedSet<int>();
            foreach (var mi in matchIndices)
            {
                int start = Math.Max(0, mi - beforeContext);
                int end = Math.Min(lines.Length - 1, mi + afterContext);
                for (int li = start; li <= end; li++) emitLines.Add(li);
            }

            foreach (var li in emitLines)
            {
                string line = lines[li];
                int lineNum = li + 1;

                if (onlyMatching && matchIndices.Contains(li))
                {
                    foreach (Match m in regex.Matches(line))
                    {
                        string matchText = m.Value;
                        string bashText = BuildBashText(filePath, lineNum, matchText, multipleFiles, showLineNumbers);
                        WriteObject(BuildRgMatch(filePath, lineNum, line, bashText));
                    }
                    continue;
                }

                string bt = BuildBashText(filePath, lineNum, line, multipleFiles, showLineNumbers);
                WriteObject(BuildRgMatch(filePath, lineNum, line, bt));
            }
        }

        if (filesOnly)
        {
            foreach (var fp in matchedFiles)
            {
                WriteObject(BashRuntime.NewBashObject(fp));
            }
            return;
        }

        if (countOnly)
        {
            if (multipleFiles)
            {
                foreach (var fp in scanned)
                {
                    if (perFileCounts.TryGetValue(fp, out int n))
                    {
                        WriteObject(BashRuntime.NewBashObject($"{fp}:{n}"));
                    }
                }
            }
            else
            {
                WriteObject(BashRuntime.NewBashObject(totalMatchCount.ToString()));
            }
        }
    }

    /// <summary>
    /// Lazy directory walk for one search-root directory: the shared
    /// dir-pruning enumerator (<see cref="FileSystemHelpers.EnumerateSearchFiles"/>)
    /// with the optional <c>-g</c>/<c>--glob</c> filename filter applied. Kept as
    /// an iterator so the caller streams matches as files are visited.
    /// </summary>
    private static IEnumerable<string> WalkSearchDir(
        string root, bool includeIgnored, bool includeHidden, string? globPattern)
    {
        WildcardPattern? glob = globPattern != null
            ? WildcardPattern.Get(globPattern, WildcardOptions.IgnoreCase)
            : null;

        foreach (var fp in FileSystemHelpers.EnumerateSearchFiles(root, includeIgnored, includeHidden))
        {
            if (glob != null && !glob.IsMatch(Path.GetFileName(fp))) continue;
            yield return fp;
        }
    }

    private static string BuildBashText(string filePath, int lineNum, string body, bool multipleFiles, bool showLineNumbers)
    {
        if (multipleFiles && showLineNumbers) return $"{filePath}:{lineNum}:{body}";
        if (multipleFiles) return $"{filePath}:{body}";
        if (showLineNumbers) return $"{lineNum}:{body}";
        return body;
    }

    /// <summary>
    /// Build a typed <c>PsBash.RgMatch</c> PSObject (oracle parity).
    /// </summary>
    private static PSObject BuildRgMatch(string fileName, int lineNumber, string line, string bashText)
    {
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.RgMatch");
        obj.Properties.Add(new PSNoteProperty("FileName", fileName));
        obj.Properties.Add(new PSNoteProperty("LineNumber", lineNumber));
        obj.Properties.Add(new PSNoteProperty("Line", line));
        obj.Properties.Add(new PSNoteProperty("BashText", bashText));
        return obj;
    }

    private string[]? ReadFileLines(string path)
    {
        try
        {
            string text = File.ReadAllText(path).Replace("\r\n", "\n");
            if (text.Length == 0) return Array.Empty<string>();
            bool trailingNl = text.EndsWith("\n", StringComparison.Ordinal);
            if (trailingNl) text = text.Substring(0, text.Length - 1);
            if (text.Length == 0 && trailingNl) return new[] { string.Empty };
            return text.Split('\n');
        }
        catch
        {
            return null;
        }
    }
}
