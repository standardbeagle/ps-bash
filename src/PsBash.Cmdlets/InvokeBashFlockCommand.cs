using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The util-linux <c>flock [options] file command [arg ...]</c> / <c>flock [options] file -c 'string'</c> form: take an
/// advisory lock on <c>file</c> (created if missing), run the command while holding it, release it, and return the
/// command's status. The lock is an OS-level share-mode open of the file (a held exclusive lock denies every other open),
/// so it excludes other ps-bash processes and anything else that opens the file for write.
///
/// <list type="bullet">
/// <item><c>-s</c> shared, <c>-x</c>/<c>-e</c> exclusive (default).</item>
/// <item><c>-n</c>/<c>--nb</c>/<c>--nonblock</c> fail at once when the lock is held; <c>-w SECS</c>/<c>--timeout</c> give up after SECS.</item>
/// <item><c>-E CODE</c> the status for a failed acquisition (default 1); <c>-c STRING</c> run STRING as a bash command line.</item>
/// <item><c>-o</c>/<c>--close</c> and <c>-F</c>/<c>--no-fork</c> are accepted and have no effect here.</item>
/// </list>
/// Not provided: the <c>flock FD</c> form (it locks a descriptor the shell holds open) and <c>-u</c>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashFlock")]
public sealed class InvokeBashFlockCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var a = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "flock", a)) return;

        if (!Parse(a, out var o, out var error))
        {
            FileSystemHelpers.WriteStderr(this, "flock: " + error);
            FileSystemHelpers.SetLastExitCode(this, 64);
            return;
        }

        string path = FileSystemHelpers.NormalizeOperandPath(o.File);
        FileStream? lockFile;
        try { lockFile = Acquire(path, o.Shared, o.Wait); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileSystemHelpers.WriteStderr(this, $"flock: cannot open lock file {o.File}: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        if (lockFile is null)
        {
            FileSystemHelpers.SetLastExitCode(this, o.ConflictCode);
            return;
        }

        using (lockFile)
        {
            if (o.CommandString is not null)
                Run("param($s) & ([scriptblock]::Create($s))", Transpile(o.CommandString));
            // `flock f prog 2>&1`: a program gets ONE pipe for stdout and stderr (docs/specs/stdio-messaging.md §4).
            else if (o.Command.Length > 0 && InvokeBashNativeCommand.MergedProgramPath(this, o.Command[0]) is { } program)
                FileSystemHelpers.SetLastExitCode(this,
                    InvokeBashNativeCommand.RunMerged(this, program, o.Command[1..], inheritStdin: true));
            else if (o.Command.Length > 0)
                Run("param($c,$a) & $c @a", o.Command[0], o.Command[1..]);
            else
            {
                FileSystemHelpers.WriteStderr(this, "flock: requires a command to run");
                FileSystemHelpers.SetLastExitCode(this, 64);
            }
        }
    }

    /// <summary>The <c>-c</c> string is bash text, so it runs transpiled; text that does not parse runs as written.</summary>
    private static string Transpile(string bash)
    {
        try { return PsBash.Core.Transpiler.BashTranspiler.Transpile(bash, PsBash.Core.Transpiler.TranspileContext.Eval) ?? bash; }
        catch (Exception) { return bash; }
    }

    private void Run(string script, params object[] args)
    {
        try
        {
            foreach (var r in InvokeCommand.InvokeScript(script, args)) WriteObject(r);
        }
        catch (CommandNotFoundException ex)
        {
            FileSystemHelpers.WriteStderr(this, $"flock: failed to execute {ex.CommandName}: No such file or directory");
            FileSystemHelpers.SetLastExitCode(this, 127);
        }
    }

    /// <summary>Opens the lock file with a share mode that makes the open itself the lock; null when not acquired in time.</summary>
    private FileStream? Acquire(string path, bool shared, TimeSpan? wait)
    {
        var deadline = wait is null ? (DateTime?)null : DateTime.UtcNow + wait.Value;
        while (true)
        {
            try
            {
                return shared
                    ? new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read)
                    : new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (IsContended(path))
            {
                if (deadline is null || DateTime.UtcNow >= deadline || Stopping) return null;
                System.Threading.Thread.Sleep(25);
            }
        }
    }

    /// <summary>A sharing violation (someone holds the lock) as opposed to a real I/O failure such as a missing directory.</summary>
    private static bool IsContended(string path)
        => File.Exists(path);

    internal sealed record Options(bool Shared, TimeSpan? Wait, int ConflictCode, string File, string[] Command, string? CommandString);

    internal static bool Parse(string[] a, out Options o, out string error)
    {
        bool shared = false, nonblock = false;
        double? timeout = null;
        int conflict = 1;
        string? cstring = null;
        int i = 0;
        o = new Options(false, null, 1, "", Array.Empty<string>(), null);
        error = "";
        for (; i < a.Length; i++)
        {
            string x = a[i];
            if (x == "--") { i++; break; }
            if (!x.StartsWith('-') || x.Length < 2) break;
            switch (x)
            {
                case "-s" or "--shared": shared = true; continue;
                case "-x" or "-e" or "--exclusive": shared = false; continue;
                case "-n" or "--nb" or "--nonblock": nonblock = true; continue;
                case "-o" or "--close" or "-F" or "--no-fork": continue;
                case "-w" or "--timeout" or "-E" or "--conflict-exit-code" or "-c" or "--command":
                    if (i + 1 >= a.Length) { error = $"option requires an argument -- '{x.TrimStart('-')}'"; return false; }
                    string v = a[++i];
                    if (x is "-w" or "--timeout")
                    {
                        if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) || t < 0)
                        { error = $"invalid timeout value: {v}"; return false; }
                        timeout = t;
                    }
                    else if (x is "-E" or "--conflict-exit-code")
                    {
                        if (!int.TryParse(v, out conflict)) { error = $"invalid exit code: {v}"; return false; }
                    }
                    else cstring = v;
                    continue;
                default:
                    error = $"unrecognized option '{x}'";
                    return false;
            }
        }
        if (i >= a.Length) { error = "requires a file to lock"; return false; }
        string file = a[i++];
        var cmd = a[i..];
        // `flock FILE -c STRING` puts -c after the file.
        if (cstring is null && cmd.Length >= 2 && cmd[0] is "-c" or "--command") { cstring = cmd[1]; cmd = Array.Empty<string>(); }
        o = new Options(shared, nonblock ? TimeSpan.Zero : timeout is null ? null : TimeSpan.FromSeconds(timeout.Value), conflict, file, cmd, cstring);
        return true;
    }
}
