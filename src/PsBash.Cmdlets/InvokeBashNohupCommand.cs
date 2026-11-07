using System.Management.Automation;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>nohup</c> builtin: runs a command so it outlives this shell (see <see cref="DetachedProcess"/>) and returns at
/// once. The new process's real OS pid becomes <c>$!</c>, its stdout/stderr go to the redirect targets the caller wrote
/// (<c>nohup cmd &gt; log 2&gt;&amp;1 &amp;</c>) or else to <c>nohup.out</c>, and none of this shell's pipes are inherited, so
/// an agent waiting for EOF on the tool call's output does not hang.
/// <para>The emitter hands over the caller's redirects as leading private flags (<c>--psbash-out FILE</c>,
/// <c>--psbash-append</c>, <c>--psbash-err FILE|&amp;1</c>) because a PowerShell redirect only reaches this cmdlet's own
/// output, never the detached child's. Exit status follows GNU nohup: 125 own failure, 127 command not found.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashNohup")]
public sealed class InvokeBashNohupCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);

        string? outFile = null, errFile = null;
        bool append = false, errToOut = false;
        int i = 0;
        for (; i < args.Length; i++)
        {
            if (args[i] == "--psbash-out" && i + 1 < args.Length) outFile = args[++i];
            else if (args[i] == "--psbash-append") append = true;
            else if (args[i] == "--psbash-err" && i + 1 < args.Length) { var v = args[++i]; if (v == "&1") errToOut = true; else errFile = v; }
            else break;
        }
        var rest = args.Skip(i).ToArray();

        if (rest.Length > 0 && rest[0] == "--help") { Help(); return; }
        if (FileSystemHelpers.TryHandleVersion(this, "nohup", rest)) return;
        if (rest.Length > 0 && rest[0] == "--") rest = rest.Skip(1).ToArray();
        if (rest.Length == 0)
        {
            FileSystemHelpers.WriteStderr(this, "nohup: missing operand\nTry 'nohup --help' for more information.");
            FileSystemHelpers.SetLastExitCode(this, 125);
            return;
        }

        var cwd = SessionState.Path.CurrentFileSystemLocation.ProviderPath;
        bool defaultLog = outFile is null;
        var outPath = defaultLog ? Path.Combine(cwd, "nohup.out") : ResolveTarget(outFile!, cwd);
        string? errPath = errFile is null || errToOut ? null : ResolveTarget(errFile, cwd);
        string exe; var exeArgs = new List<string>();
        var resolved = Resolve(rest[0], cwd);
        if (resolved is not null) { exe = resolved; exeArgs.AddRange(rest.Skip(1)); }
        else if (IsShellCommand(rest[0]) && ExecutableLookup.Find(OperatingSystem.IsWindows() ? "ps-bash.exe" : "ps-bash", ExecutableLookup.PathDirectories()) is { } shell)
        {
            // A ps-bash builtin or mapped command (sleep, seq, ...) has no executable of its own: run it in a fresh shell.
            exe = shell;
            exeArgs.Add("-c");
            exeArgs.Add(string.Join(' ', rest.Select(SingleQuote)));
        }
        else
        {
            FileSystemHelpers.WriteStderr(this, $"nohup: failed to run command '{rest[0]}': No such file or directory");
            FileSystemHelpers.SetLastExitCode(this, 127);
            return;
        }

        if (defaultLog)
            FileSystemHelpers.WriteStderr(this, "nohup: ignoring input and appending output to 'nohup.out'");
        try
        {
            var pid = DetachedProcess.Spawn(exe, exeArgs, cwd, outPath, append || defaultLog, errPath);
            InvokeCommand.InvokeScript("param($p) $global:BashBgLastPid = $p", pid);
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteStderr(this, $"nohup: failed to run command '{rest[0]}': {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 126);
        }
    }

    private static string ResolveTarget(string raw, string cwd)
    {
        if (raw == "/dev/null") return OperatingSystem.IsWindows() ? "NUL" : raw;
        var p = FileSystemHelpers.NormalizeOperandPath(raw);
        return Path.IsPathRooted(p) ? p : Path.Combine(cwd, p);
    }

    private static string? Resolve(string name, string cwd)
    {
        if (name.Contains('/') || name.Contains('\\'))
        {
            var p = FileSystemHelpers.NormalizeOperandPath(name);
            if (!Path.IsPathRooted(p)) p = Path.Combine(cwd, p);
            return ExecutableLookup.IsExecutableFile(p) ? p : null;
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
