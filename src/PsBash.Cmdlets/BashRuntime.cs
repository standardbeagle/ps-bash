using System.Diagnostics;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Shared AOT-safe runtime helpers extracted from <c>PsBash.psm1</c>
/// (REFACTOR-2 Phase 2). Every leaf <c>Invoke-Bash*</c> function depends on
/// this small surface; hosting it as C# static methods lets the migrated
/// binary cmdlets call it directly, with NO C#-&gt;PowerShell callback.
///
/// Parity oracle: the original psm1 helper functions. The psm1 versions are
/// kept as thin wrappers that delegate here (see PsBash.psm1) so the script
/// surface is unchanged for callers and the differential test suite proves
/// these implementations against the live runtime.
///
/// AOT safety: no <see cref="ScriptBlock"/> construction, no
/// <c>Invoke-Expression</c>, no reflection on hot paths. The only PowerShell
/// type touched is <see cref="PSObject"/> for the <see cref="GetBashText"/>
/// duck-typed property probe, which is reflection-free.
/// </summary>
public static class BashRuntime
{
    /// <summary>
    /// Parse a numeric count/width flag value without the <see cref="int.Parse(string)"/>
    /// overflow-crash trap. Joined flag forms (e.g. <c>head -n99999999999</c>, <c>grep -A9…9</c>)
    /// reach a bare <c>int.Parse</c> on a digit string the regex/substring admitted but that
    /// overflows <see cref="int"/> — an unhandled <see cref="OverflowException"/> that crashes the
    /// host runspace. A well-formed but too-large value clamps to <see cref="int.MaxValue"/>
    /// (negative to <see cref="int.MinValue"/>) — the semantically correct "no practical limit";
    /// a non-numeric value returns <paramref name="fallback"/>. Never throws. Mirrors the clamp
    /// idiom already used in <c>InvokeBashFindCommand.ParseSizeExpr</c>/<c>ParseMtimeExpr</c>.
    /// </summary>
    public static int ParseCountClamped(ReadOnlySpan<char> s, int fallback = int.MaxValue)
    {
        if (int.TryParse(s, out int v)) return v;
        var t = s.Trim();
        if (t.Length > 0)
        {
            char c0 = t[0];
            if (c0 == '-') return int.MinValue;
            if (c0 == '+' || (c0 >= '0' && c0 <= '9')) return int.MaxValue;
        }
        return fallback;
    }

    /// <summary>
    /// Returns ONLY the current command's segment of the pipeline text in
    /// <see cref="InvocationInfo.Line"/>. Several migrated cmdlets recover a flag the
    /// case-insensitive PowerShell binder swallowed (grep/sed <c>-E</c>, cut <c>-d:</c>,
    /// uniq <c>-D</c>, echo <c>-e</c>/<c>-E</c>) by regex-scanning the raw invocation
    /// line. Scanning the WHOLE line lets a flag belonging to another command in the same
    /// pipeline leak in — e.g. <c>grep 'a+' f | sed -E 's/x/y/'</c> would flip grep into
    /// extended-regex mode. Scoping the scan to this command's own segment fixes that.
    ///
    /// Splits <see cref="InvocationInfo.Line"/> on top-level <c>|</c> (pipes inside single-
    /// or double-quoted runs, escaped with a backtick, or part of a <c>||</c> chain operator
    /// are not separators) and returns the element at <see cref="InvocationInfo.PipelinePosition"/>
    /// (1-based). Falls back to the whole line for a single-command pipeline or when the
    /// position metadata is unavailable.
    /// </summary>
    public static string CurrentPipelineSegment(InvocationInfo? invocation)
    {
        var line = invocation?.Line ?? string.Empty;
        if (invocation is null || line.Length == 0)
            return line;

        int position = invocation.PipelinePosition;   // 1-based
        if (invocation.PipelineLength <= 1 || position < 1)
            return line;

        var segments = SplitPipelineSegments(line);
        return position <= segments.Count ? segments[position - 1] : line;
    }

    /// <summary>
    /// Split an emitted PowerShell command line on TOP-LEVEL <c>|</c>. A <c>|</c> nested
    /// inside a <c>(...)</c> / <c>{...}</c> / <c>[...]</c> group — the emitted PowerShell
    /// for a bash subshell <c>&amp; { a | b }</c>, a command sub <c>$(a | b)</c>, or
    /// <c>@(...)</c> — is part of a CHILD pipeline that PowerShell's
    /// <c>PipelinePosition</c>/<c>PipelineLength</c> does NOT count, so it is not a
    /// separator. Pipes inside single/double quotes, a backtick escape, or a <c>||</c>
    /// chain operator are likewise not separators. Pure and side-effect-free so
    /// <see cref="CurrentPipelineSegment"/>'s segmentation is unit-testable without an
    /// <see cref="InvocationInfo"/> (which has no public constructor).
    /// </summary>
    public static List<string> SplitPipelineSegments(string line)
    {
        var segments = new List<string>();
        int start = 0;
        char quote = '\0';
        int depth = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            switch (c)
            {
                case '\'':
                case '"':
                    quote = c;
                    break;
                case '`':
                    i++;   // backtick escape — skip the escaped char
                    break;
                case '(':
                case '{':
                case '[':
                    depth++;
                    break;
                case ')':
                case '}':
                case ']':
                    if (depth > 0) depth--;
                    break;
                case '|':
                    if (depth > 0) break;   // nested pipe — not a top-level separator
                    if (i + 1 < line.Length && line[i + 1] == '|')
                    {
                        i++;   // `||` chain operator, not a pipeline separator
                        break;
                    }
                    segments.Add(line.Substring(start, i - start));
                    start = i + 1;
                    break;
            }
        }
        segments.Add(line.Substring(start));
        return segments;
    }

    /// <summary>
    /// Prepend the tokens of any PRESENT decoy switches to <paramref name="arguments"/>.
    /// A binary cmdlet declares a single-letter <c>[Parameter]</c> decoy for each bash
    /// short flag that would otherwise collide with a PowerShell common parameter (crash
    /// on <c>-e/-i/-o/-p</c>, silent-bind of <c>-Debug/-Verbose/-Confirm</c> on
    /// <c>-c/-d/-v</c>, or <c>-Arguments</c> swallow on <c>-a</c>); the bound flag then
    /// never reaches <see cref="System.Management.Automation.PSCmdlet"/>'s remaining-args
    /// list, so it must be re-injected here for the cmdlet's own scan / classifier to see
    /// it. Single source for the ~13 cmdlets that were each open-coding this
    /// <c>new List; if (X.IsPresent) Add("-x"); AddRange(Arguments)</c> block in three
    /// different spellings. Returns the original array unchanged when no decoy is present
    /// (the common case), so it allocates only when a decoy actually fired.
    /// </summary>
    public static string[] PrependDecoys(string[]? arguments, params (bool Present, string Flag)[] decoys)
    {
        var raw = arguments ?? Array.Empty<string>();
        int extra = 0;
        foreach (var (present, _) in decoys)
            if (present) extra++;
        if (extra == 0) return raw;

        var list = new List<string>(raw.Length + extra);
        foreach (var (present, flag) in decoys)
            if (present) list.Add(flag);
        list.AddRange(raw);
        return list.ToArray();
    }

    /// <summary>
    /// Strips a single trailing <c>\n</c> from BashText, matching the psm1
    /// <c>New-BashObject</c> / <c>Set-BashDisplayProperty</c> normalization
    /// (the worker serializer owns line endings).
    /// </summary>
    public static string NormalizeBashText(string text)
    {
        if (text.Length > 0 && text[^1] == '\n')
        {
            return text.Substring(0, text.Length - 1);
        }
        return text;
    }

    /// <summary>
    /// Forwards to the shared <see cref="PsBash.Core.WindowsPath"/> mapper, gated
    /// on running under Windows. Exposed here — in the reliably-loaded Cmdlets
    /// assembly the psm1 already calls — so a psm1 function can normalize a
    /// unix-style drive path (<c>/c/..</c>, <c>/mnt/c/..</c>, <c>c:/..</c>)
    /// WITHOUT a script-level <c>[PsBash.Core.WindowsPath]</c> reference. That
    /// type lives in PsBash.Transpiler, a transitively-referenced assembly that
    /// is not necessarily loaded when a psm1 function runs (e.g. an isolated
    /// Pester runspace that never transpiled anything) — script-level type
    /// resolution only searches already-loaded assemblies, so it would throw
    /// "Unable to find type". A JIT reference from inside this already-loaded
    /// method instead triggers the assembly resolver to load Transpiler.dll from
    /// beside Cmdlets.dll on first call. No-op off Windows, where <c>/c/..</c>
    /// and <c>/mnt/c/..</c> can be real paths.
    /// </summary>
    public static string NormalizeWindowsPath(string path)
        => OperatingSystem.IsWindows() ? PsBash.Core.WindowsPath.Normalize(path) : path;

    /// <summary>
    /// Builds a BashObject, reproducing the psm1 <c>New-BashObject</c> contract:
    /// <list type="bullet">
    /// <item>Fast path — default <c>PsBash.TextOutput</c> type with no
    /// <paramref name="noTrailingNewline"/> returns a bare <see cref="string"/>
    /// (avoids the PSObject allocation for the common line-by-line case).</item>
    /// <item>Slow path — a typed object or a no-trailing-newline marker returns
    /// a <see cref="PSObject"/> carrying <c>PSTypeName</c>, <c>BashText</c>, and
    /// optional <c>NoTrailingNewline</c> / <c>Command</c> note properties.</item>
    /// </list>
    /// Normalizes a single trailing <c>\n</c> off BashText for the implicit-
    /// newline path only. A <paramref name="noTrailingNewline"/> object means
    /// "the serializer must emit these bytes EXACTLY — it adds no record
    /// boundary"; stripping there would silently delete output the producer
    /// emitted on purpose (<c>printf '%s\n' b</c> builds <c>"b\n"</c> and passes
    /// the flag so no extra newline is appended — the <c>"\n"</c> it already
    /// contains must survive; the old unconditional strip turned it into
    /// <c>"b"</c>, dropping the newline whenever another frame followed).
    /// </summary>
    public static object NewBashObject(
        string bashText,
        string typeName = "PsBash.TextOutput",
        bool noTrailingNewline = false,
        string? command = null)
    {
        if (!noTrailingNewline)
            bashText = NormalizeBashText(bashText);

        if (typeName == "PsBash.TextOutput" && !noTrailingNewline)
        {
            return bashText;
        }

        var obj = new PSObject();
        obj.TypeNames.Insert(0, typeName);
        obj.Properties.Add(new PSNoteProperty("BashText", bashText));
        if (noTrailingNewline)
        {
            obj.Properties.Add(new PSNoteProperty("NoTrailingNewline", true));
        }
        if (command != null)
        {
            obj.Properties.Add(new PSNoteProperty("Command", command));
        }
        return obj;
    }

    /// <summary>
    /// Splits <paramref name="text"/> on <c>\n</c> and returns one BashObject
    /// per line, matching the psm1 <c>Emit-BashLine</c> contract: bash stdout is
    /// a byte stream and <c>\n</c> is a record boundary. The final line is
    /// marked <c>NoTrailingNewline</c> when the source text had no trailing
    /// newline. Empty input yields an empty sequence.
    /// </summary>
    public static IEnumerable<object> EmitBashLines(string? text, string? command = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        bool hasTrailingNewline = text![^1] == '\n';
        string stripped = hasTrailingNewline
            ? text.Substring(0, text.Length - 1)
            : text;
        string[] lines = stripped.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            bool isLast = i == lines.Length - 1;
            bool noNl = isLast && !hasTrailingNewline;
            yield return NewBashObject(lines[i], "PsBash.TextOutput", noNl, command);
        }
    }

    /// <summary>
    /// Extracts the string payload of any pipeline object, matching the psm1
    /// <c>Get-BashText</c>: <c>null</c> -&gt; empty, <see cref="string"/> -&gt;
    /// itself, an object exposing a <c>BashText</c> property -&gt; that value,
    /// otherwise the object's <c>ToString()</c>.
    /// </summary>
    public static string GetBashText(object? inputObject)
    {
        if (inputObject is null)
        {
            return string.Empty;
        }
        if (inputObject is string s)
        {
            return s;
        }

        var pso = inputObject as PSObject ?? PSObject.AsPSObject(inputObject);
        var prop = pso.Properties["BashText"];
        if (prop != null)
        {
            return prop.Value?.ToString() ?? string.Empty;
        }

        return inputObject.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Builds a case-sensitive (ordinal) flag-definition dictionary from a flat
    /// <c>flag, description, flag, description, ...</c> entry list, matching the
    /// psm1 <c>New-FlagDefs</c>. Throws on an odd-length entry list, surfacing a
    /// malformed caller flag table at the source.
    /// </summary>
    public static Dictionary<string, string> NewFlagDefs(string[] entries)
    {
        if (entries.Length % 2 != 0)
        {
            throw new ArgumentException(
                "New-FlagDefs entry list must have an even element count " +
                "(flag, description pairs).",
                nameof(entries));
        }

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Length; i += 2)
        {
            dict[entries[i]] = entries[i + 1];
        }
        return dict;
    }

    /// <summary>
    /// Result of <see cref="ConvertFromBashArgs"/>: parsed boolean
    /// <see cref="Flags"/> (ordinal-keyed) and the collected non-flag
    /// <see cref="Operands"/>.
    /// </summary>
    public sealed class BashArgs
    {
        public Dictionary<string, bool> Flags { get; }
        public List<string> Operands { get; }

        public BashArgs(Dictionary<string, bool> flags, List<string> operands)
        {
            Flags = flags;
            Operands = operands;
        }
    }

    /// <summary>
    /// Boolean flag parser, matching the psm1 <c>ConvertFrom-BashArgs</c>:
    /// handles <c>--</c> (end of flags), recognized long flags
    /// (<c>--word</c>), and bundled short flags (<c>-ab</c>). An unrecognized
    /// bundle char makes the whole token an operand and stops scanning that
    /// token, exactly as the psm1 version does. Every key in
    /// <paramref name="flagDefs"/> is initialized to <c>false</c>.
    /// </summary>
    public static BashArgs ConvertFromBashArgs(
        IEnumerable<string> arguments,
        IDictionary<string, string> flagDefs)
    {
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        var operands = new List<string>();

        foreach (var key in flagDefs.Keys)
        {
            flags[key] = false;
        }

        var args = arguments as IList<string> ?? new List<string>(arguments);
        int i = 0;
        while (i < args.Count)
        {
            var arg = args[i];

            if (arg == "--")
            {
                i++;
                while (i < args.Count)
                {
                    operands.Add(args[i]);
                    i++;
                }
                break;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                // Accept the `--flag=value` form for a registered long flag by
                // matching on the base name (text before '='). The dominant
                // real-world case is the near-universal `alias ls='ls
                // --color=auto'` / `--color=always` — without this, `--color=auto`
                // fails the exact-key lookup, falls into operands, and the cmdlet
                // treats it as a file → "No such file or directory". These are
                // boolean FlagDefs, so the value is not consumed: any
                // `--flag=WHEN` sets the flag (an explicit `--color=never` is the
                // known minor wart — it enables rather than disables).
                string baseName = arg;
                int eq = arg.IndexOf('=');
                if (eq > 2) baseName = arg.Substring(0, eq);

                if (flags.ContainsKey(baseName))
                {
                    flags[baseName] = true;
                }
                else
                {
                    operands.Add(arg);
                }
            }
            else if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1)
            {
                foreach (char ch in arg.Substring(1))
                {
                    var flag = "-" + ch;
                    if (flags.ContainsKey(flag))
                    {
                        flags[flag] = true;
                    }
                    else
                    {
                        operands.Add(arg);
                        break;
                    }
                }
            }
            else
            {
                operands.Add(arg);
            }
            i++;
        }

        return new BashArgs(flags, operands);
    }

    private const string EscapedBackslashSentinel = "\0ESCAPED_BACKSLASH\0";

    /// <summary>
    /// Converts C-style escape sequences in a string literal, matching the psm1
    /// <c>Expand-EscapeSequences</c>: a sentinel-based two-pass scheme protects
    /// <c>\\</c> so it becomes a literal backslash rather than seeding a later
    /// expansion (<c>\\n</c> -&gt; literal <c>\n</c>, not a newline). Used by
    /// <c>echo -e</c>, <c>printf</c>, and <c>tr</c>.
    /// </summary>
    public static string ExpandEscapeSequences(string text)
    {
        text = text.Replace("\\\\", EscapedBackslashSentinel);
        text = text.Replace("\\n", "\n");
        text = text.Replace("\\t", "\t");
        text = text.Replace("\\r", "\r");
        text = text.Replace("\\a", "\a");
        text = text.Replace("\\b", "\b");
        text = text.Replace("\\f", "\f");
        text = text.Replace("\\v", "\v");
        text = text.Replace(EscapedBackslashSentinel, "\\");
        return text;
    }

    /// <summary>
    /// Emits a bash-style error: sets <c>$global:LASTEXITCODE</c> and writes the
    /// message to the appropriate sink. Because the error sink is a script-scoped
    /// concern (the psm1 <c>$script:BashErrorMode</c> switch between the host
    /// IPC stderr frame and <c>Write-Error</c>), the psm1 <c>Write-BashError</c>
    /// wrapper stays the public entry point; this method only owns the pieces a
    /// binary cmdlet can do without script scope. A migrated cmdlet calls
    /// <see cref="FormatBashError"/> for the message text and sets the exit code
    /// via the runspace variable itself.
    /// </summary>
    public static string FormatBashError(string command, string message)
    {
        return $"{command}: {message}";
    }

    /// <summary>
    /// Result of a <see cref="RunChildProcess(string, IReadOnlyList{string}?, System.TimeSpan?)"/>
    /// call: the child's captured stdout/stderr, its exit code, whether the wait
    /// budget elapsed, and whether either captured stream was truncated at the
    /// configured memory ceiling.
    /// </summary>
    public readonly record struct ChildProcessResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        bool TimedOut,
        bool StdoutTruncated = false,
        bool StderrTruncated = false);

    /// <summary>
    /// Safe child-process spawn for cmdlets that shell out to native tools. The
    /// host runspace is single-threaded: a child that hangs (or fills a pipe
    /// buffer while only one stream is drained) blocks the runspace forever,
    /// wedging the host and poisoning every later invocation. This helper enforces
    /// the spawn contract that prevents that:
    /// <list type="bullet">
    /// <item>both stdout and stderr are drained <b>concurrently</b> (no
    /// pipe-buffer deadlock);</item>
    /// <item>the wait is <b>bounded</b> by <paramref name="timeout"/> (default
    /// <c>PSBASH_TIMEOUT</c>s, else 120s);</item>
    /// <item>on timeout the <b>entire process tree</b> is killed so no descendant
    /// lingers, and the call returns with <see cref="ChildProcessResult.TimedOut"/>
    /// = <see langword="true"/> and exit code 124.</item>
    /// </list>
    /// AOT-safe: only <see cref="System.Diagnostics.Process"/> and stream reads,
    /// no <see cref="ScriptBlock"/> / reflection.
    /// </summary>
    public static ChildProcessResult RunChildProcess(
        string fileName, IReadOnlyList<string>? arguments = null, System.TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (arguments is not null)
            foreach (var a in arguments)
                psi.ArgumentList.Add(a);
        return RunChildProcess(psi, timeout);
    }

    /// <summary>
    /// <see cref="RunChildProcess(string, IReadOnlyList{string}?, System.TimeSpan?)"/>
    /// overload taking a caller-prepared <see cref="ProcessStartInfo"/> (for env
    /// vars, working directory, etc.). The redirection + no-shell invariants the
    /// spawn contract requires are forced on regardless of how the caller built it.
    /// </summary>
    public static ChildProcessResult RunChildProcess(ProcessStartInfo startInfo, System.TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        // Redirect stdin so we can close it immediately (below): a non-interactive
        // capture must never inherit a live stdin that a child could block reading
        // (e.g. ps-bash's no-args REPL, or `sort`/`cat` with no file operand).
        startInfo.RedirectStandardInput = true;

        var budget = timeout ?? GetChildProcessTimeout();

        using var proc = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{startInfo.FileName}'.");

        // Hand the child an EOF-closed stdin so it never hangs waiting for input.
        try { proc.StandardInput.Close(); } catch { /* child may not have opened it */ }

        // Drain BOTH streams concurrently. Each drain keeps reading after the
        // capture cap and discards excess text, so a noisy child cannot exhaust
        // memory or block on a full pipe.
        var captureLimit = GetChildCaptureLimitChars();
        var stdoutCapture = new BoundedTextCapture(captureLimit);
        var stderrCapture = new BoundedTextCapture(captureLimit);
        using var stdoutDrain = StreamDrain.Start(proc.StandardOutput, stdoutCapture);
        using var stderrDrain = StreamDrain.Start(proc.StandardError, stderrCapture);

        int waitMs = budget <= System.TimeSpan.Zero
            ? System.Threading.Timeout.Infinite
            : (int)System.Math.Min(budget.TotalMilliseconds, int.MaxValue);

        if (!proc.WaitForExit(waitMs))
        {
            // Budget elapsed — kill the process and the descendants still attached to
            // it via OS parent-pid links. NOTE: this is best-effort, not a guarantee —
            // a double-forked grandchild reparented to PID 1 (or a Windows-detached
            // process) has no live parent link back to `proc` and so survives. A real
            // guarantee would need a Windows Job Object / POSIX process group (setsid).
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone / race */ }
            try { proc.WaitForExit(2_000); } catch { /* best effort */ }
            stdoutDrain.Join(System.TimeSpan.FromSeconds(5));
            stderrDrain.Join(System.TimeSpan.FromSeconds(5));
            return new ChildProcessResult(
                124,
                stdoutCapture.Text,
                stderrCapture.Text,
                TimedOut: true,
                StdoutTruncated: stdoutCapture.Truncated,
                StderrTruncated: stderrCapture.Truncated);
        }

        // WaitForExit(int) does NOT guarantee the stdout/stderr drainers have hit
        // EOF; the parameterless overload does. Call it before joining them.
        try { proc.WaitForExit(); } catch { /* already reaped */ }
        stdoutDrain.Join(System.TimeSpan.FromSeconds(5));
        stderrDrain.Join(System.TimeSpan.FromSeconds(5));
        return new ChildProcessResult(
            proc.ExitCode,
            stdoutCapture.Text,
            stderrCapture.Text,
            TimedOut: false,
            StdoutTruncated: stdoutCapture.Truncated,
            StderrTruncated: stderrCapture.Truncated);
    }

    private static System.TimeSpan GetChildProcessTimeout()
    {
        var env = Environment.GetEnvironmentVariable("PSBASH_TIMEOUT");
        if (env is not null && int.TryParse(env, out var seconds) && seconds > 0)
            return System.TimeSpan.FromSeconds(seconds);
        return System.TimeSpan.FromSeconds(120);
    }

    private static int GetChildCaptureLimitChars()
    {
        var env = Environment.GetEnvironmentVariable("PSBASH_CHILD_CAPTURE_MAX_CHARS");
        if (env is not null && int.TryParse(env, out var chars) && chars > 0)
            return Math.Max(chars, 1024);
        return 16 * 1024 * 1024;
    }

    private sealed class BoundedTextCapture
    {
        private readonly int _maxChars;
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();

        public BoundedTextCapture(int maxChars)
        {
            _maxChars = maxChars;
        }

        private bool _truncated;

        public bool Truncated
        {
            get
            {
                lock (_gate) return _truncated;
            }
        }

        public string Text
        {
            get
            {
                lock (_gate) return _text.ToString();
            }
        }

        public void Append(char[] buffer, int length)
        {
            if (length <= 0) return;

            lock (_gate)
            {
                var remaining = _maxChars - _text.Length;
                if (remaining <= 0)
                {
                    _truncated = true;
                    return;
                }

                var toAppend = Math.Min(length, remaining);
                _text.Append(buffer, 0, toAppend);
                if (toAppend < length)
                {
                    _truncated = true;
                }
            }
        }
    }

    private sealed class StreamDrain : IDisposable
    {
        private readonly Thread _thread;

        private StreamDrain(Thread thread)
        {
            _thread = thread;
        }

        public static StreamDrain Start(TextReader reader, BoundedTextCapture capture)
        {
            var thread = new Thread(() =>
            {
                var buffer = new char[4096];
                try
                {
                    int read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        capture.Append(buffer, read);
                    }
                }
                catch
                {
                    // Process teardown races can close redirected streams while
                    // drains are exiting. Partial output is still useful.
                }
            })
            {
                IsBackground = true,
                Name = "ps-bash child stream drain",
            };
            thread.Start();
            return new StreamDrain(thread);
        }

        public bool Join(System.TimeSpan timeout) => _thread.Join(timeout);

        public void Dispose()
        {
            if (_thread.IsAlive)
            {
                _thread.Join(System.TimeSpan.FromMilliseconds(100));
            }
        }
    }
}
