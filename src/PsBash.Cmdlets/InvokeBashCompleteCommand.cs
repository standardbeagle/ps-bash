using System.Collections;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>complete</c> builtin: a registry of completion specs keyed by command name (<c>-D</c>/<c>-E</c>/<c>-I</c> for
/// the default, empty and initial-word specs). Specs are only stored and listed (<c>complete</c>, <c>complete -p</c>,
/// <c>complete -r</c>); the line editor's completion is not driven by them. Usage errors exit 2 like bash.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashComplete")]
public sealed class InvokeBashCompleteCommand : PSCmdlet
{
    private const string StoreVar = "__BashCompSpecs";
    private static readonly string ValueFlags = "AGWCFXPSo";
    private static readonly string ActionFlags = "abcdefgjksuv";

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "complete", args)) return;

        var specOpts = new List<string>();
        var names = new List<string>();
        bool print = false, remove = false, special = false;
        string? function = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--") { names.AddRange(args[(i + 1)..]); break; }
            if (a.Length < 2 || a[0] != '-') { names.Add(a); continue; }
            for (int k = 1; k < a.Length; k++)
            {
                char f = a[k];
                if (f == 'p') print = true;
                else if (f == 'r') remove = true;
                else if (f is 'D' or 'E' or 'I') { special = true; names.Add("-" + f); }
                else if (ActionFlags.Contains(f)) specOpts.Add("-" + f);
                else if (ValueFlags.Contains(f))
                {
                    string value;
                    if (k + 1 < a.Length) { value = a[(k + 1)..]; k = a.Length; }
                    else if (i + 1 < args.Length) value = args[++i];
                    else { Usage($"-{f}: option requires an argument"); return; }
                    if (f == 'F') function = value;
                    specOpts.Add("-" + f + " " + (value.Length > 0 && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or '/') ? value : BashQuoteText.ForReuse(value)));
                }
                else { Usage($"-{f}: invalid option"); return; }
            }
        }

        var store = Store();
        if (remove)
        {
            if (names.Count == 0) store.Clear();
            foreach (var n in names)
            {
                if (!store.Contains(n)) { FileSystemHelpers.WriteBashError(this, $"complete: {n}: no completion specification"); FileSystemHelpers.SetLastExitCode(this, 1); }
                else store.Remove(n);
            }
            return;
        }
        if (print || (specOpts.Count == 0 && names.Count == 0))
        {
            if (names.Count == 0) { foreach (DictionaryEntry e in store) WriteObject(Format(e)); return; }
            foreach (var n in names)
            {
                if (store.Contains(n)) WriteObject(Format(new DictionaryEntry(n, store[n])));
                else { FileSystemHelpers.WriteBashError(this, $"complete: {n}: no completion specification"); FileSystemHelpers.SetLastExitCode(this, 1); }
            }
            return;
        }
        if (names.Count == 0) { Usage(null); return; }
        _ = function; _ = special;
        foreach (var n in names) store[n] = string.Join(' ', specOpts);
    }

    private static string Format(DictionaryEntry e)
    {
        var opts = e.Value?.ToString() ?? "";
        var name = e.Key?.ToString() ?? "";
        if (name is "-D" or "-E" or "-I") return ("complete " + opts + " " + name).Replace("  ", " ");
        return opts.Length == 0 ? "complete " + name : "complete " + opts + " " + name;
    }

    private void Usage(string? message)
    {
        if (message is not null) FileSystemHelpers.WriteBashError(this, "complete: " + message);
        FileSystemHelpers.WriteBashError(this, "complete: usage: complete [-abcdefgjksuv] [-pr] [-DEI] [-o option] [-A action] [-G globpat] [-W wordlist]  [-F function] [-C command] [-X filterpat] [-P prefix] [-S suffix] [name ...]");
        FileSystemHelpers.SetLastExitCode(this, 2);
    }

    private IDictionary Store()
    {
        if (SessionState.PSVariable.Get("global:" + StoreVar)?.Value is IDictionary d) return d;
        var created = new System.Collections.Specialized.OrderedDictionary();
        InvokeCommand.InvokeScript("param($v) $global:" + StoreVar + " = $v", created);
        return created;
    }
}

/// <summary>The <c>compopt</c> builtin. It only changes a running completion function's options, and ps-bash runs none, so it validates and fails like bash does outside one.</summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashCompopt")]
public sealed class InvokeBashCompoptCommand : PSCmdlet
{
    private static readonly HashSet<string> Options = new(StringComparer.Ordinal)
        { "bashdefault", "default", "dirnames", "filenames", "noquote", "nosort", "nospace", "plusdirs" };

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "compopt", args)) return;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "-o" or "+o")
            {
                if (i + 1 >= args.Length || !Options.Contains(args[i + 1]))
                {
                    FileSystemHelpers.WriteBashError(this, $"compopt: {(i + 1 < args.Length ? args[i + 1] : "")}: invalid option name");
                    FileSystemHelpers.SetLastExitCode(this, 2);
                    return;
                }
                i++;
            }
        }
        FileSystemHelpers.WriteBashError(this, "compopt: not currently executing completion function");
        FileSystemHelpers.SetLastExitCode(this, 1);
    }
}
