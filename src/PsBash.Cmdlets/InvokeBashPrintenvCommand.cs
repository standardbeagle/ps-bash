using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Coreutils <c>printenv [-0] [NAME]...</c>: each named variable's VALUE (one per line), or every exported variable
/// as <c>NAME=VALUE</c> with no NAME. A NAME not in the environment prints nothing and makes the status 1; a usage
/// error is status 2. Its own cmdlet, not an alias of <c>Invoke-BashEnv</c> told apart by InvocationName: under an
/// indirect call (<c>env X=1 printenv X</c>, <c>xargs printenv</c>) that name is <c>&amp;</c>, and printenv behaved as
/// env. printenv is a child of the shell, so it sees exported variables only.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPrintenv")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashPrintenvCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Stdin sink: printenv ignores standard input, but an unbound piped record would be a binder error.</summary>
    [Parameter(ValueFromPipeline = true)]
    [AllowNull]
    public PSObject? InputObject { get; set; }

    protected override void ProcessRecord() { }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (args.Length > 0 && args[0] == "--help")
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "printenv"))
                WriteObject(line);
            return;
        }
        if (args.Length > 0 && args[0] == "--version" && FileSystemHelpers.TryHandleVersion(this, "printenv", args)) return;

        bool nul = false;
        var names = new List<string>();
        bool options = true;
        foreach (var a in args)
        {
            if (options && a == "--") { options = false; continue; }
            if (options && a.StartsWith("--", StringComparison.Ordinal))
            {
                if (a == "--null") { nul = true; continue; }
                UsageError($"unrecognized option '{a}'");
                return;
            }
            if (options && a.Length > 1 && a[0] == '-')
            {
                foreach (char ch in a.AsSpan(1))
                {
                    if (ch == '0') { nul = true; continue; }
                    UsageError($"invalid option -- '{ch}'");
                    return;
                }
                continue;
            }
            names.Add(a);
        }

        string end = nul ? "\0" : "\n";
        if (names.Count == 0)
        {
            var all = new List<KeyValuePair<string, string>>(BashVariableStore.EnumerateExported());
            all.Sort(static (x, y) => StringComparer.Ordinal.Compare(x.Key, y.Key));
            if (nul)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var (k, v) in all) sb.Append(k).Append('=').Append(v).Append(end);
                if (sb.Length > 0) WriteObject(BashRuntime.BytesRecord(sb.ToString()));
                return;
            }
            foreach (var (k, v) in all) WriteObject(InvokeBashEnvCommand.BuildEntry(k, v));
            return;
        }

        var nulText = nul ? new System.Text.StringBuilder() : null;
        foreach (var name in names)
        {
            var v = name.Contains('=') ? null : BashVariableStore.GetExported(name);
            if (v is null) { FileSystemHelpers.SetLastExitCode(this, 1); continue; }
            if (nulText is not null) nulText.Append(v).Append(end);
            else WriteObject(InvokeBashEnvCommand.BuildEntry(name, v, valueOnly: true));
        }
        if (nulText is { Length: > 0 }) WriteObject(BashRuntime.BytesRecord(nulText.ToString()));
    }

    private void UsageError(string what)
    {
        string message = "printenv: " + what;
        if (FileSystemHelpers.UsageHint("printenv") is { } h) message += "\n" + h;
        FileSystemHelpers.WriteBashError(this, message);
        FileSystemHelpers.SetLastExitCode(this, 2);
    }
}
