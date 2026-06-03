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
    /// Always normalizes a trailing <c>\n</c> off BashText first.
    /// </summary>
    public static object NewBashObject(
        string bashText,
        string typeName = "PsBash.TextOutput",
        bool noTrailingNewline = false,
        string? command = null)
    {
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
    /// call: the child's captured stdout/stderr, its exit code, and whether the
    /// wait budget elapsed (in which case the whole process tree was killed and
    /// <see cref="ExitCode"/> is the GNU-<c>timeout</c> convention 124).
    /// </summary>
    public readonly record struct ChildProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

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

        // Drain BOTH streams concurrently. Draining only one while the child fills
        // the other's pipe buffer (~64KB) is the classic deadlock these cmdlets hit.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        int waitMs = budget <= System.TimeSpan.Zero
            ? System.Threading.Timeout.Infinite
            : (int)System.Math.Min(budget.TotalMilliseconds, int.MaxValue);

        if (!proc.WaitForExit(waitMs))
        {
            // Budget elapsed — kill the whole tree so no descendant outlives the
            // call, then collect whatever partial output already drained.
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone / race */ }
            try { proc.WaitForExit(2_000); } catch { /* best effort */ }
            return new ChildProcessResult(124, DrainBounded(stdoutTask), DrainBounded(stderrTask), TimedOut: true);
        }

        // WaitForExit(int) does NOT guarantee the async stdout/stderr readers have
        // hit EOF; the parameterless overload does. Call it so the captured text
        // is complete before we read the tasks.
        try { proc.WaitForExit(); } catch { /* already reaped */ }
        return new ChildProcessResult(
            proc.ExitCode, DrainBounded(stdoutTask), DrainBounded(stderrTask), TimedOut: false);

        static string DrainBounded(System.Threading.Tasks.Task<string> t)
        {
            try { return t.Wait(System.TimeSpan.FromSeconds(5)) ? t.Result : string.Empty; }
            catch { return string.Empty; }
        }
    }

    private static System.TimeSpan GetChildProcessTimeout()
    {
        var env = Environment.GetEnvironmentVariable("PSBASH_TIMEOUT");
        if (env is not null && int.TryParse(env, out var seconds) && seconds > 0)
            return System.TimeSpan.FromSeconds(seconds);
        return System.TimeSpan.FromSeconds(120);
    }
}
