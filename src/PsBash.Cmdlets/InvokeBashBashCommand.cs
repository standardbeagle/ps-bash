using System.Diagnostics;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashBash</c> function
/// (REFACTOR-2 follow-on). Implements the bash builtin <c>bash</c> — runs a
/// nested ps-bash transpiler against a script string (<c>-c "..."</c>), a
/// script file operand, stdin (<c>-</c>), or emits <c>--version</c> info.
///
/// Behavioral parity oracle: the original psm1 function. The oracle's exact
/// dispatch — locate the ps-bash executable (parent process path → on-PATH
/// Get-Command → sibling of the current PowerShell binary), then shell out
/// to it via <c>&amp; $psBashExe @Arguments 2>&amp;1</c> and re-emit each
/// non-error output object's <c>BashText</c> via <c>Emit-BashLine</c>, with
/// <c>ErrorRecord</c> items routed to <c>[Console]::Error</c> — is preserved
/// byte-for-byte. Forwarding to a fresh ps-bash child rather than the
/// in-process <c>Invoke-BashEval</c> matches the oracle and keeps a nested
/// <c>bash</c> invocation isolated from the caller's runspace state (no
/// LASTEXITCODE crosstalk, no errexit / variable leakage).
///
/// <para><b>Colliding flag:</b> bash's <c>-c "script"</c> prefix-collides
/// with the PowerShell common parameter <c>-Confirm</c> under the cmdlet
/// binder. The remedy from the playbook collision table — declare a
/// value-bearing parameter with the literal single-letter name <c>C</c> so
/// exact-name match beats the common-parameter prefix match — is applied
/// here.</para>
///
/// <para><b>Directive 12:</b> the script body passed via <c>-c</c> is
/// forwarded as a single positional argument to a child ps-bash process via
/// <see cref="ProcessStartInfo.ArgumentList"/> — never concatenated into a
/// shell or script body. The nested ps-bash performs its own transpile +
/// execute, where bash-level quoting rules apply to the inner script
/// (e.g. <c>$(throw)</c> is evaluated as a bash command substitution of the
/// command <c>throw</c>, which fails inside ps-bash and is reported by the
/// child — the host cmdlet itself does not re-parse the string). The
/// <c>--help</c> path delegates to the psm1 <c>Show-BashHelp</c> via a
/// parameter-bound <c>InvokeCommand.InvokeScript</c> (AOT-safe).</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashBash")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashBashCommand : PSCmdlet, IDisposable
{
    /// <summary>
    /// Bash's <c>-c SCRIPT</c> one-shot flag. Declared as a literal
    /// single-letter parameter name <c>C</c> so the cmdlet binder routes
    /// the bare token by exact-name match — beating the common-parameter
    /// prefix-match against <c>-Confirm</c>.
    /// </summary>
    [Parameter]
    public string? C { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    // Decoys for bash's -i, -e, -v and -o NAME when they come from a splat (`f() { bash "$@"; }; f -i`): PowerShell
    // binds a splatted "-i" as a parameter name, a prefix of InputObject / InformationAction (and -e / -o / -v
    // of ErrorAction, OutVariable, Verbose), which failed the call. The emitter single-quotes literal dash
    // words, so those always reach Arguments. BuildArgv puts them back in bash's option zone.
    [Parameter] public SwitchParameter i { get; set; }
    [Parameter] public SwitchParameter e { get; set; }
    [Parameter] public SwitchParameter v { get; set; }
    [Parameter] public string? o { get; set; }

    /// <summary>
    /// Pipeline input. Usually the child's stdin, streamed into it record by record as it arrives
    /// (<c>producer | bash -c 'while read l; ...'</c>). When the argv names no program (<see cref="ProgramOnStdin"/>:
    /// <c>echo cmd | bash</c>, <c>bash &lt;&lt;&lt; cmd</c>, <c>curl … | sh</c>) it IS the program, collected and run as <c>-c</c>.
    /// </summary>
    [Parameter(ValueFromPipeline = true)]
    [AllowNull]
    public PSObject? InputObject { get; set; }

    private List<string> _argv = new();
    private bool _programOnStdin;
    private System.Text.StringBuilder? _stdinScript;
    private StreamingChildProcess? _child;
    private bool _finished;

    protected override void BeginProcessing()
    {
        // Reconstruct the argument vector exactly as the oracle saw it. The
        // binder may have consumed the bare token "-c <value>" into the C
        // property; if so, splice the equivalent ["-c", value] tokens back
        // into the forwarded args at the front (oracle parity).
        _argv = BuildArgv(C, Arguments, i, e, v, o);
        // The program arrives on stdin: wait for all of it, then run it (EndProcessing).
        _programOnStdin = MyInvocation.ExpectingInput && ProgramOnStdin(_argv);
        if (_programOnStdin) return;
        // Otherwise the child starts NOW, as bash starts a pipeline's commands together: `slow | bash -c 'echo hi'`
        // prints hi at once, and piped input reaches the child as it is produced.
        Start(keepStdinOpen: MyInvocation.ExpectingInput);
    }

    protected override void ProcessRecord()
    {
        if (InputObject is null) return;
        if (_programOnStdin)
        {
            _stdinScript ??= new System.Text.StringBuilder();
            var text = BashRuntime.GetBashText(InputObject);
            _stdinScript.Append(text);
            if (!text.EndsWith('\n')) _stdinScript.Append('\n');
            return;
        }
        if (_child is not { StdinOpen: true }) return;
        if (!_child.WriteStdin(this, NativeStdinBridge.RecordBytes(InputObject)))
        {
            // The child stopped reading (`yes | bash -c 'head -1'`): bash's producer dies of SIGPIPE. Finish first,
            // so the child's output and status are complete whether or not EndProcessing runs after the stop.
            Finish();
            UpstreamStop.Throw(this);
            return;
        }
        _child.PumpAvailable(this);
    }

    protected override void EndProcessing()
    {
        if (_programOnStdin)
        {
            // A nested ps-bash given the program as -c runs the same text.
            if (_stdinScript is { Length: > 0 })
            {
                _argv.RemoveAll(a => a is "-s" or "-");
                _argv.Add("-c");
                _argv.Add(_stdinScript.ToString());
            }
            Start(keepStdinOpen: false);
        }
        Finish();
    }

    /// <summary>Ctrl-C, or the pipeline stopping: the child's whole tree goes with it.</summary>
    protected override void StopProcessing() => _child?.Kill();

    public void Dispose() => _child?.Dispose();

    /// <summary>
    /// True when the argv names no program — no <c>-c</c> and no script operand, only options (<c>-s</c> included) —
    /// so piped input is the program. Not for <c>-i</c>: <c>echo cmds | bash -i</c> is an interactive session
    /// reading the pipe (prompts, history), which the child runs from its stdin.
    /// </summary>
    internal static bool ProgramOnStdin(IReadOnlyList<string> argv)
        => !argv.Contains("-c") && argv.All(a => a.StartsWith('-') && a.Length > 1 || a == "-s") && !AsksInteractive(argv);

    /// <summary>--help / --version, or spawn the child. Sets <see cref="_child"/> only when one started.</summary>
    private void Start(bool keepStdinOpen)
    {
        var forwarded = _argv;
        // Only flags in bash's OPTION zone (before the script path / -c string) are bash's own;
        // `bash s.sh --version` hands --version to the script.
        if (OptionZoneHas(forwarded, "--help"))
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "bash"))
            {
                WriteObject(line);
            }
            return;
        }

        // --version short-circuit: emit the ps-bash version banner without
        // spawning a child. Matches the oracle's `Emit-BashLine -Text`
        // shape (one bare TextOutput per line of the banner).
        if (OptionZoneHas(forwarded, "--version"))
        {
            var version = ResolveModuleVersion() ?? "0.7.6";
            // Trailing \n: without it the last line is NoTrailingNewline and the
            // next command's output glues onto "transpiler".
            var banner = $"ps-bash, version {version}\nBash-to-PowerShell transpiler\n";
            foreach (var obj in BashRuntime.EmitBashLines(banner))
            {
                WriteObject(obj);
            }
            return;
        }

        // Resolve the ps-bash executable, matching the oracle's three-tier
        // preference: 1) the parent process binary (via $__parentPid global
        // set by the host); 2) Get-Command ps-bash; 3) sibling of the
        // current PowerShell process's MainModule path. An embedding that ships
        // the module without the launcher (a session server deployed to a Linux
        // host) has none of them: there the host's own bash runs the script, as
        // awk's pipes and sed's `e` already fall back to /bin/sh. Without it every
        // `bash -c` / `bash script.sh` in such a session failed outright.
        string? psBashExe = ResolvePsBashExecutable(this) ?? ResolveHostShell();
        if (string.IsNullOrEmpty(psBashExe))
        {
            FileSystemHelpers.WriteBashError(this, "bash: ps-bash executable not found");
            return;
        }

        // Shell out. UseShellExecute=false + redirected streams keeps every
        // user-controlled token bound through ArgumentList (Directive 12).
        var psi = new ProcessStartInfo
        {
            FileName = psBashExe!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in forwarded)
        {
            psi.ArgumentList.Add(a);
        }
        // An exported SHELLOPTS (`export SHELLOPTS; set -x; bash -c ...`) is the options on NOW: bash keeps the exported
        // value current as `set` changes them, and the child shell turns each one on at startup.
        if (psi.Environment.ContainsKey("SHELLOPTS"))
            psi.Environment["SHELLOPTS"] = CurrentShellOpts();
        // The session's location, not the process cwd: an embedding runs several sessions in
        // one process, so `bash -c ls` listed wherever the process happened to stand (awk's
        // pipes already do this).
        try
        {
            var loc = SessionState.Path.CurrentFileSystemLocation.ProviderPath;
            if (!string.IsNullOrEmpty(loc) && System.IO.Directory.Exists(loc)) psi.WorkingDirectory = loc;
        }
        catch { /* not a filesystem location: inherit */ }
        // bash puts no time limit on a child shell. PSBASH_TIMEOUT is the launcher's idle bound for the OUTER call,
        // which already covers this child's silence (the child's output is the outer call's output); inherited, it
        // killed a quiet nested `bash script.sh` (a `sleep`, a long compile step) with 124 and no message.
        psi.Environment.Remove("PSBASH_TIMEOUT");

        try
        {
            // Streaming spawn: output is emitted while the child runs (progress shows, `2>&1` keeps its order), no
            // time budget, kill-tree on StopProcessing. Without piped input the child's stdin is closed at once, so a
            // no-args / REPL-mode child sees EOF and exits instead of hanging on a read. Under `2>&1` (or any merge) the
            // child's stdout and stderr are ONE pipe, so its lines keep the order it wrote them in.
            _child = BashRuntime.StartChildProcessStreaming(psi, keepStdinOpen,
                new ChildStdioMode(MergeStderr: StderrMerge.JoinsStdout(this)));
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, "bash: " + ex.Message);
            SessionState.PSVariable.Set("global:LASTEXITCODE", 1);
        }
    }

    /// <summary>Run the child to its exit, emitting its output; forward its status. Once.</summary>
    private void Finish()
    {
        if (_child is null || _finished) return;
        _finished = true;
        int exitCode = _child.Complete(this);
        if (Stopping) return;
        // Forward child exit code to the caller's $LASTEXITCODE — oracle parity.
        SessionState.PSVariable.Set("global:LASTEXITCODE", exitCode);
    }

    /// <summary>
    /// The value of <c>$SHELLOPTS</c> in this shell: the <c>set -o</c> options on, alphabetical, colon-joined. The same
    /// list as <c>PsBuild.ShellOptsExpr</c>, read here without running script (which <c>set -x</c> would trace).
    /// </summary>
    private string CurrentShellOpts()
    {
        bool On(string global) => LanguagePrimitives.IsTrue(SessionState.PSVariable.GetValue("global:" + global));
        var o = new List<string> { "braceexpand" };
        if (InvokeBashShoptCommand.SetOptionOn("emacs")) o.Add("emacs");
        if (On("__BashErrexit")) o.Add("errexit");
        if (On("__BashErrtrace")) o.Add("errtrace");
        o.Add("hashall");
        if (InvokeBashShoptCommand.SetOptionOn("histexpand")) o.Add("histexpand");
        if (InvokeBashShoptCommand.SetOptionOn("history")) o.Add("history");
        o.Add("interactive-comments");
        if (InvokeBashShoptCommand.SetOptionOn("monitor")) o.Add("monitor");
        if (On("__BashNoclobber")) o.Add("noclobber");
        if (On("__BashNoglob")) o.Add("noglob");
        if (On("__BashNounset")) o.Add("nounset");
        if (On("__BashPipefail")) o.Add("pipefail");
        if (On("__BashVerbose")) o.Add("verbose");
        if (InvokeBashShoptCommand.SetOptionOn("vi")) o.Add("vi");
        if (On("__BashXtrace")) o.Add("xtrace");
        return string.Join(':', o);
    }

    /// <summary>
    /// True when <paramref name="flag"/> appears in bash's OPTION zone: the leading run of
    /// dash-words, ending at the first operand (the script path, or the <c>-c</c> command
    /// string) or at <c>--</c>. Everything after that is a positional parameter of the script
    /// (<c>bash s.sh --version</c> gives the script <c>$1=--version</c>; oracle bash 5.2).
    /// <c>-o</c>/<c>+o</c> take a value, which is skipped.
    /// </summary>
    internal static bool OptionZoneHas(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--" || a.Length == 0 || (a[0] != '-' && a[0] != '+') || a == "-")
                return false;
            if (a == flag)
                return true;
            if (a is "-o" or "+o")
                i++;
        }
        return false;
    }

    /// <summary>
    /// The argument vector the child bash gets. Options the binder took (the decoys, and <c>-c</c> into
    /// <paramref name="c"/>) only come from a splat or a direct call; they go back first, and <c>-c STRING</c>
    /// after the options at the head of <paramref name="args"/>, so a following word is still <c>$0</c>
    /// (<c>bash --norc "$@"</c> with <c>-c 'echo $0' a</c>). Their places among later operands are lost.
    /// </summary>
    internal static List<string> BuildArgv(string? c, IReadOnlyList<string>? args, bool i, bool e, bool v, string? o)
    {
        var argv = new List<string>();
        if (i) argv.Add("-i");
        if (e) argv.Add("-e");
        if (v) argv.Add("-v");
        if (o is not null) { argv.Add("-o"); argv.Add(o); }
        int k = 0;
        if (args is not null && !string.IsNullOrEmpty(c))
        {
            while (k < args.Count && args[k].Length > 1 && args[k][0] is '-' or '+' && args[k] != "--")
            {
                if (args[k] is "-o" or "+o" or "-O" or "+O" or "--rcfile" or "--init-file" && k + 1 < args.Count) argv.Add(args[k++]);
                argv.Add(args[k++]);
            }
        }
        if (!string.IsNullOrEmpty(c)) { argv.Add("-c"); argv.Add(c); }
        if (args is not null) for (; k < args.Count; k++) argv.Add(args[k]);
        return argv;
    }

    /// <summary>True when bash's option zone holds <c>-i</c>, alone or in a bundle (<c>-il</c>).</summary>
    internal static bool AsksInteractive(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--" || a.Length < 2 || (a[0] != '-' && a[0] != '+')) return false;
            if (a is "-o" or "+o") { i++; continue; }
            if (a[0] == '-' && a[1] != '-' && a.AsSpan(1).Contains('i')) return true;
        }
        return false;
    }

    private string? ResolveModuleVersion()
    {
        try
        {
            // Two probe sources: (1) the imported PsBash module's Version
            // property when the manifest path was used; (2) the
            // $global:PsBashVersion string the psm1 sets at the bottom of its
            // body when loaded as a script (the ps-bash host). Not
            // $global:BashVersion — that is the emulated "X.Y.0(1)-release"
            // $BASH_VERSION, not the ps-bash version. Either source can be a 0.0
            // placeholder (a bare-psm1 module, or the psm1 having run mid-import),
            // so both come back in order and the first real one wins.
            var result = InvokeCommand.InvokeScript(
                "$m = Get-Module PsBash -ErrorAction SilentlyContinue | Select-Object -First 1 ; " +
                "[string]$m.Version ; [string]$global:PsBashVersion");
            foreach (var item in result)
            {
                if (IsRealVersion(item?.BaseObject as string))
                    return (string)item!.BaseObject;
            }
        }
        catch
        {
            // Best-effort; fall through to the default below.
        }
        return null;
    }

    /// <summary>A parseable version other than the 0.0 placeholder.</summary>
    internal static bool IsRealVersion(string? s) =>
        Version.TryParse(s, out var v) && (v.Major != 0 || v.Minor != 0);

    /// <summary>Explicit launcher for nested ps-bash children (<c>bash -c</c>, awk pipes/getline): a test seam and an
    /// escape hatch for embeddings whose host does not sit beside its launcher.</summary>
    internal const string NestedExeEnvVar = "PSBASH_NESTED_EXE";

    /// <summary>
    /// The host's own shell, for when no ps-bash launcher can be found: the first of
    /// <see cref="HostShellCandidates"/> that exists. Never on Windows, where <c>bash.exe</c> on
    /// PATH is as likely to be WSL's launcher (a different machine's filesystem) as Git Bash.
    /// </summary>
    internal static string? ResolveHostShell()
    {
        if (OperatingSystem.IsWindows()) return null;
        foreach (var candidate in HostShellCandidates)
        {
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Absolute paths only: a PATH search could find a ps-bash installed AS <c>bash</c>,
    /// which is the launcher the tiers above already looked for. <c>/bin/sh</c> last, for a host
    /// with no bash at all (Alpine).</summary>
    internal static readonly string[] HostShellCandidates =
        ["/bin/bash", "/usr/bin/bash", "/usr/local/bin/bash", "/bin/sh"];

    internal static string? ResolvePsBashExecutable(PSCmdlet cmdlet)
    {
        var explicitExe = Environment.GetEnvironmentVariable("PSBASH_NESTED_EXE") /* = NestedExeEnvVar; host config, literal for the guard */;
        if (!string.IsNullOrEmpty(explicitExe) && System.IO.File.Exists(explicitExe))
            return explicitExe;

        // Tier 0: a `ps-bash` beside the process we run in. Inside ps-bash-host that is the
        // launcher of the SAME build, so a nested child never lands on a different
        // (e.g. PATH-installed older) build — which would otherwise misjudge this host's
        // build identity and could retire the host that is executing us.
        try
        {
            var self = Environment.ProcessPath;
            var selfDir = string.IsNullOrEmpty(self) ? null : System.IO.Path.GetDirectoryName(self);
            if (!string.IsNullOrEmpty(selfDir)
                && System.IO.Path.GetFileNameWithoutExtension(self)!.StartsWith("ps-bash", StringComparison.OrdinalIgnoreCase))
            {
                var sib = System.IO.Path.Combine(selfDir, OperatingSystem.IsWindows() ? "ps-bash.exe" : "ps-bash");
                if (System.IO.File.Exists(sib))
                    return sib;
            }
        }
        catch
        {
            // fall through
        }

        // Tier 1: $global:__parentPid → parent process MainModule.FileName.
        // This is the exact binary the user spawned; preferred so a dev
        // build's nested `bash -c` re-enters the same dev build, not a
        // PATH-installed one.
        try
        {
            var pidObj = cmdlet.SessionState.PSVariable.GetValue("global:__parentPid");
            if (pidObj != null && LanguagePrimitives.TryConvertTo<int>(pidObj, out int parentPid) && parentPid > 0)
            {
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    var path = parent.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                        return path;
                }
                catch
                {
                    // Parent gone / not accessible — fall through.
                }
            }
        }
        catch
        {
            // Variable lookup failed — fall through.
        }

        // Tier 2: Get-Command ps-bash on PATH.
        try
        {
            var probe = cmdlet.InvokeCommand.InvokeScript(
                // Ignore, not SilentlyContinue: SilentlyContinue still records the miss in the
                // user's $Error, so every host-shell `bash -c` left a "'ps-bash' is not
                // recognized" behind for the next `$Error[0]` to report.
                "Get-Command ps-bash -ErrorAction Ignore | Select-Object -First 1");
            if (probe.Count > 0 && probe[0] != null)
            {
                var src = probe[0].Properties["Source"]?.Value as string;
                if (!string.IsNullOrEmpty(src))
                    return src;
            }
        }
        catch
        {
            // Get-Command failure — fall through.
        }

        // Tier 3: sibling of the current PowerShell process.
        try
        {
            using var cur = Process.GetCurrentProcess();
            var curMain = cur.MainModule?.FileName;
            if (!string.IsNullOrEmpty(curMain))
            {
                var dir = System.IO.Path.GetDirectoryName(curMain);
                if (!string.IsNullOrEmpty(dir))
                {
                    var candidate = System.IO.Path.Combine(dir, "ps-bash");
                    if (OperatingSystem.IsWindows())
                        candidate += ".exe";
                    if (System.IO.File.Exists(candidate))
                        return candidate;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
