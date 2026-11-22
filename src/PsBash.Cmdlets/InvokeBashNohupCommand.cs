using System.Management.Automation;
using PsBash.Core;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>nohup</c> builtin: runs a command immune to hangups, as GNU nohup does.
/// <list type="bullet">
/// <item><c>nohup CMD &amp;</c> (the emitter's <c>--psbash-bg</c>): CMD runs so it outlives this shell (see
/// <see cref="DetachedProcess"/>) and nohup returns at once. The new process's real OS pid becomes <c>$!</c>, and none of
/// this shell's pipes are inherited, so an agent waiting for EOF on the tool call's output does not hang.</item>
/// <item>A foreground <c>nohup CMD</c> WAITS for CMD and its status is CMD's (<c>nohup ls /x &gt; o</c> is 2): it still
/// runs detached when its output goes to a file (the caller's redirect, or <c>nohup.out</c> when stdout is a terminal), so
/// the shell going away does not take it down.</item>
/// <item>A foreground <c>nohup CMD</c> whose stdout is not a terminal and not redirected (<c>nohup CMD | cat</c>,
/// <c>$(nohup CMD)</c>) writes where nohup's stdout goes, as GNU's does, and creates no <c>nohup.out</c>: CMD runs in
/// place through <see cref="InnerCommand"/>, fed nohup's pipe.</item>
/// </list>
/// <para>The emitter hands over the caller's redirects as leading private flags (<c>--psbash-out FILE</c>,
/// <c>--psbash-append</c>, <c>--psbash-err FILE|&amp;1</c>, <c>--psbash-err-append</c> for <c>2&gt;&gt;</c>) because a PowerShell redirect only reaches this cmdlet's own
/// output, never the detached child's. Exit status follows GNU nohup: 125 own failure, 127 command not found, 126 not
/// runnable, else CMD's.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashNohup")]
public sealed class InvokeBashNohupCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>The pipe into nohup: CMD's stdin when CMD runs in place (<c>printf 'a\n' | nohup cat</c>).</summary>
    [Parameter(ValueFromPipeline = true)]
    [AllowNull]
    public PSObject? InputObject { get; set; }

    private readonly List<object> _stdin = new();
    private readonly CancellationTokenSource _stop = new();

    protected override void ProcessRecord()
    {
        if (InputObject is not null) _stdin.Add(InputObject);
    }

    protected override void StopProcessing() => _stop.Cancel();

    private static readonly OptSpecSet NohupSpec = new(Array.Empty<OptSpec>(), allowAbbrev: true, gnuInfoOptions: true,
        usageExitCode: 125, requireOrder: true);

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);

        string? outFile = null, errFile = null;
        bool append = false, errToOut = false, errAppend = false, background = false;
        int i = 0;
        for (; i < args.Length; i++)
        {
            if (args[i] == "--psbash-bg") background = true;
            else if (args[i] == "--psbash-out" && i + 1 < args.Length) outFile = args[++i];
            else if (args[i] == "--psbash-append") append = true;
            else if (args[i] == "--psbash-err-append") errAppend = true;
            else if (args[i] == "--psbash-err" && i + 1 < args.Length) { var v = args[++i]; if (v == "&1") errToOut = true; else errFile = v; }
            else break;
        }
        var rest = args.Skip(i).ToArray();

        // GNU nohup's getopt_long("+"): its own options (--help, --version) come before the command; an option it does
        // not have is a usage error (125), not a command name (`nohup --bogus` ran a program "--bogus": 127).
        var parsed = ArgParser.Parse(rest, NohupSpec);
        if (FileSystemHelpers.TryWriteParseError(this, "nohup", parsed)) return;
        if (parsed.Has(OptSpecSet.HelpId)) { Help(); return; }
        if (FileSystemHelpers.TryHandleInfoOptions(this, "nohup", parsed)) return;
        rest = parsed.Operands().ToArray();
        if (rest.Length == 0)
        {
            FileSystemHelpers.WriteMissingOperand(this, "nohup", exitCode: 125);
            return;
        }

        // Foreground, stdout neither a terminal nor redirected: GNU nohup leaves it alone, so CMD's output is nohup's
        // (`x=$(nohup echo hi)` is hi, no nohup.out) and nohup's status is CMD's.
        if (!background && outFile is null && !FileSystemHelpers.StdoutIsTerminal(this))
        {
            InnerCommand.Run(this, InPlace, rest, _stdin);
            return;
        }

        var cwd = SessionState.Path.CurrentFileSystemLocation.ProviderPath;
        bool defaultLog = outFile is null;
        var outPath = defaultLog ? Path.Combine(cwd, "nohup.out") : ResolveTarget(outFile!, cwd);
        string? errPath = errFile is null || errToOut ? null : ResolveTarget(errFile, cwd);
        string exe; var exeArgs = new List<string>();
        var resolved = Resolve(rest[0], cwd);
        if (resolved is not null && ProgramFile.Plan(resolved) is { } launch)
        {
            // A file the OS cannot start as it is (a #! script on Windows, a file with no execute bit): its interpreter
            // runs it, a shell's through the ps-bash launcher, as the kernel would; anything else is GNU's 126.
            var (program, interpreterArgs) = launch.CommandLine(InvokeBashBashCommand.ResolvePsBashExecutable(this),
                ExecutableLookup.PathDirectories(cwd));
            if (program is null)
            {
                bool missing = launch.Runnable;
                Fail($"nohup: failed to run command '{rest[0]}': " + (missing ? "No such file or directory" : "Permission denied"),
                    missing ? 127 : 126, defaultLog ? null : outPath, append, errToOut, errPath, errAppend);
                return;
            }
            exe = program;
            exeArgs.AddRange(interpreterArgs);
            exeArgs.AddRange(rest.Skip(1));
        }
        else if (resolved is not null) { exe = resolved; exeArgs.AddRange(rest.Skip(1)); }
        else if (IsShellCommand(rest[0]) && InvokeBashBashCommand.ResolvePsBashExecutable(this) is { } shell)
        {
            // A ps-bash builtin or mapped command (sleep, seq, sh, ...) has no executable of its own: run it in a fresh
            // shell of THIS build. A PATH-installed ps-bash of another build judged this host stale and retired it in
            // the middle of the command (`nohup sh -c 'exit 3' > f` under the test harness: "host process exited").
            exe = shell;
            exeArgs.Add("-c");
            exeArgs.Add(string.Join(' ', rest.Select(SingleQuote)));
        }
        else
        {
            Fail($"nohup: failed to run command '{rest[0]}': No such file or directory", 127,
                defaultLog ? null : outPath, append, errToOut, errPath, errAppend);
            return;
        }

        if (defaultLog)
            FileSystemHelpers.WriteStderr(this, "nohup: ignoring input and appending output to 'nohup.out'");
        try
        {
            using var child = DetachedProcess.SpawnChild(exe, exeArgs, cwd, outPath, append || defaultLog, errPath, errAppend);
            if (background)
            {
                InvokeCommand.InvokeScript("param($p) $global:BashBgLastPid = $p", child.Pid);
                return;
            }
            // Foreground: nohup's status is CMD's. A stop (Ctrl+C) ends the wait, never CMD: it was started to survive.
            if (child.Wait(_stop.Token) is int status) FileSystemHelpers.SetLastExitCode(this, status);
            else FileSystemHelpers.SetLastExitCode(this, 130);
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            Fail($"nohup: failed to run command '{rest[0]}': {ex.Message}", 126,
                defaultLog ? null : outPath, append: true, errToOut, errPath, errAppend);
        }
    }

    /// <summary>
    /// nohup's own failure, after the caller's redirects: in bash the shell opened them before nohup ran, so the files
    /// exist and the message lands in the stderr target. The emitter moved those redirects into flags for the child
    /// (<c>--psbash-out</c> / <c>--psbash-err</c>), so they are opened here; without one the message goes to stderr.
    /// </summary>
    private void Fail(string message, int code, string? outPath, bool append, bool errToOut, string? errPath, bool errAppend)
    {
        FileSystemHelpers.SetLastExitCode(this, code);
        try
        {
            if (outPath is not null)
                using (var o = RedirectFileWriter.OpenFile(outPath, append, this))
                    if (errToOut) o.Write(RawBytes.GetBytes(message + "\n"));
            if (errToOut && outPath is not null) return;
            if (errPath is not null)
            {
                using var e = RedirectFileWriter.OpenFile(errPath, errAppend, this);
                e.Write(RawBytes.GetBytes(message + "\n"));
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the target cannot be opened: say it on stderr */ }
        FileSystemHelpers.WriteStderr(this, message);
    }

    /// <summary>CMD run in place: a name nothing answers to is GNU's <c>nohup: failed to run command 'X': No such file or
    /// directory</c>, 127.</summary>
    private static readonly InnerCommand.Wrapper InPlace = new("nohup", static (cmdlet, name) =>
    {
        FileSystemHelpers.WriteStderr(cmdlet, $"nohup: failed to run command '{name}': No such file or directory");
        FileSystemHelpers.SetLastExitCode(cmdlet, 127);
    }, ShellBuiltins: false);

    private static string ResolveTarget(string raw, string cwd)
    {
        if (PsBash.Core.DevicePath.IsNull(raw)) return PsBash.Core.DevicePath.NativeDevice(PsBash.Core.DeviceKind.Null)!;
        var p = FileSystemHelpers.NormalizeOperandPath(raw);
        return Path.IsPathRooted(p) ? p : Path.Combine(cwd, p);
    }

    /// <summary>The program file NAME runs: a path as given (any file: ProgramFile judges whether it can run), else the
    /// first runnable PATH hit.</summary>
    private static string? Resolve(string name, string cwd)
    {
        if (name.Contains('/') || name.Contains('\\'))
        {
            var p = FileSystemHelpers.NormalizeOperandPath(name);
            if (!Path.IsPathRooted(p)) p = Path.Combine(cwd, p);
            return File.Exists(p) ? p : null;
        }
        return ExecutableLookup.Find(name, ExecutableLookup.PathDirectories(cwd));
    }

    /// <summary>A name ps-bash itself runs (mapped command, builtin, function, alias) as opposed to a typo.</summary>
    private bool IsShellCommand(string name)
    {
        if (name.Length == 0 || !char.IsLetter(name[0])) return false;
        var cmdlet = "Invoke-Bash" + char.ToUpperInvariant(name[0]) + name[1..];
        var items = SessionState.InvokeProvider.Item;
        return InvokeCommand.GetCmdlet(cmdlet) is not null
            || items.Exists("function:" + cmdlet) || items.Exists("function:" + name) || items.Exists("alias:" + name);
    }

    private static string SingleQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private void Help()
    {
        WriteObject("Usage: nohup COMMAND [ARG]...");
        WriteObject("Run COMMAND, ignoring hangup signals: it keeps running after this shell exits.");
        WriteObject("Output goes to nohup.out unless redirected; $! is the new process's pid.");
    }
}
