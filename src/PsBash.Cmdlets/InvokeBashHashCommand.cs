using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using PsBash.Core;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>hash</c> builtin over the shell's hash table (<see cref="CommandHashTable"/>): the host remembers each
/// command word that ran a program file found through PATH, with a hit count. Oracle: bash 5.2 (<c>wsl bash</c>).
/// <list type="bullet">
/// <item>no NAME: list <c>hits\tcommand</c> then <c>%4d\tPATH</c> per entry, or <c>hash: hash table empty</c>;
/// <c>-l</c> lists as <c>builtin hash -p PATH NAME</c> (nothing when empty).</item>
/// <item><c>hash NAME</c>: search PATH and remember it (0 hits). A builtin, a function and a runtime command
/// (<c>ls</c>, <c>whoami</c>: the shell's own implementation, so like a builtin) are accepted and not hashed; a NAME
/// with a slash is skipped; a miss is <c>hash: NAME: not found</c>, status 1.</item>
/// <item><c>-r</c> forgets everything first; <c>-d NAME</c> forgets NAME; <c>-p PATH NAME</c> remembers NAME as PATH;
/// <c>-t NAME</c> prints the remembered path (<c>NAME\tPATH</c> for several names).</item>
/// </list>
/// The hit order of a listing is the order commands were first remembered (bash's is its hash-bucket order).
/// <para>Flag collisions: <c>hash</c> is on <c>PsEmitter.OrderedArgCommands</c>, so transpiled calls pass every flag
/// as a string. <c>D</c>/<c>P</c> decoys catch a direct <c>-d</c> (prefix of <c>-Debug</c>) and <c>-p PATH</c>
/// (ambiguous with <c>-PipelineVariable</c>/<c>-ProgressAction</c>).</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashHash")]
public sealed class InvokeBashHashCommand : PSCmdlet
{
    private const string Usage = "hash: usage: hash [-lr] [-p pathname] [-dt] [name ...]";

    [Parameter] public SwitchParameter D { get; set; }

    [Parameter] public string? P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = BashRuntime.PrependDecoys(Arguments, (D.IsPresent, "-d"));
        if (P is not null) args = ["-p", P, .. args];
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "hash", args)) return;

        bool reset = false, delete = false, targets = false, portable = false;
        string? pathname = null;
        int i = 0;
        for (; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--") { i++; break; }
            if (a.Length < 2 || a[0] != '-') break;
            for (int k = 1; k < a.Length; k++)
            {
                switch (a[k])
                {
                    case 'r': reset = true; break;
                    case 'd': delete = true; break;
                    case 't': targets = true; break;
                    case 'l': portable = true; break;
                    case 'p':
                        // The rest of the word, else the next word, is the pathname (getopt).
                        if (k + 1 < a.Length) pathname = a[(k + 1)..];
                        else if (i + 1 < args.Length) pathname = args[++i];
                        else { Fail(2, "hash: -p: option requires an argument", Usage); return; }
                        k = a.Length;
                        break;
                    default:
                        { Fail(2, $"hash: -{a[k]}: invalid option", Usage); return; }
                }
            }
        }
        var names = args[i..];
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        var table = CommandHashTable.For(Runspace.DefaultRunspace ?? (object)SessionState);

        if (names.Length == 0 && targets) { Fail(1, "hash: -t: option requires an argument"); return; }
        if (names.Length == 0 && delete) { Fail(1, "hash: -d: option requires an argument"); return; }
        if (reset) table.Clear(pathValue);
        if (names.Length == 0)
        {
            if (!reset) List(table.Snapshot(pathValue), portable);
            return;
        }

        if (targets)
        {
            foreach (var name in names)
            {
                if (table.Lookup(name, pathValue) is not { } found)
                {
                    Fail(1, $"hash: {name}: not found");
                    continue;
                }
                if (portable) WriteObject($"builtin hash -p {found} {name}");
                else WriteObject(names.Length > 1 ? $"{name}\t{found}" : found);
            }
            return;
        }

        foreach (var name in names)
        {
            if (name.IndexOfAny(['/', '\\']) >= 0) continue; // bash runs a slash name as given: nothing to hash
            if (pathname is not null)
            {
                if (Directory.Exists(BashRuntime.MapPath(pathname))) Fail(1, $"hash: {pathname}: Is a directory");
                else table.Add(name, pathname, pathValue);
            }
            else if (delete)
            {
                if (!table.Remove(name, pathValue)) Fail(1, $"hash: {name}: not found");
            }
            else if (!IsShellCommand(name))
            {
                if (ExecutableLookup.Find(name, ExecutableLookup.PathDirectories().Select(BashRuntime.MapPath)) is { } file)
                    table.Add(name, file, pathValue);
                else
                    Fail(1, $"hash: {name}: not found");
            }
        }
    }

    private void List(CommandHashTable.Entry[] entries, bool portable)
    {
        if (entries.Length == 0)
        {
            // bash 5.2 prints nothing for an empty `hash -l`.
            if (!portable) WriteObject("hash: hash table empty");
            return;
        }
        if (!portable) WriteObject("hits\tcommand");
        foreach (var e in entries)
            WriteObject(portable
                ? $"builtin hash -p {e.Path} {e.Name}"
                : e.Hits.ToString(CultureInfo.InvariantCulture).PadLeft(4) + "\t" + e.Path);
    }

    /// <summary>A NAME bash would not search PATH for: a builtin or function, or the runtime's own command.</summary>
    private bool IsShellCommand(string name)
    {
        if (InvokeBashTypeCommand.Builtins.Contains(name) || DeclarePrinter.FindBashFunction(this, name) is not null)
            return true;
        using var probe = PsBash.Core.CommandHashTable.Probe();
        try
        {
            return InvokeCommand.GetCommand(name, CommandTypes.Alias) is AliasInfo alias
                && (alias.Definition ?? "").StartsWith("Invoke-Bash", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private void Fail(int status, params string[] lines)
    {
        foreach (var line in lines) FileSystemHelpers.WriteStderr(this, line);
        FileSystemHelpers.SetLastExitCode(this, status);
    }
}
