using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>compgen</c> builtin: the completion candidates for a prefix. Supports <c>-W WORDLIST</c> (split on
/// whitespace), <c>-f</c>, <c>-d</c>, <c>-v</c> and <c>-A file|directory|variable|function|alias|command</c>; a
/// trailing <c>[--] PREFIX</c> filters the candidates. Status 1 when nothing matches.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashCompgen")]
public sealed class InvokeBashCompgenCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "compgen", args)) return;

        var kinds = new List<string>();
        string? wordList = null;
        string prefix = "";
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--") { if (i + 1 < args.Length) prefix = args[i + 1]; break; }
            if (a == "-W" && i + 1 < args.Length) { wordList = args[++i]; continue; }
            if (a == "-A" && i + 1 < args.Length)
            {
                var kind = args[++i];
                if (!KnownKinds.Contains(kind))
                {
                    FileSystemHelpers.WriteBashError(this, $"compgen: {kind}: invalid action name");
                    FileSystemHelpers.SetLastExitCode(this, 2);
                    return;
                }
                kinds.Add(kind);
                continue;
            }
            if (a == "-e") { kinds.Add("export"); continue; }
            if (a == "-f") { kinds.Add("file"); continue; }
            if (a == "-d") { kinds.Add("directory"); continue; }
            if (a == "-v") { kinds.Add("variable"); continue; }
            if (a == "-c") { kinds.Add("command"); continue; }
            if (a.StartsWith('-') && a.Length > 1) continue; // -o/-P/-S/-X options: no effect on the candidate set
            prefix = a;
        }

        var found = new List<string>();
        if (wordList is not null)
            found.AddRange(wordList.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        foreach (var k in kinds) found.AddRange(Candidates(k, prefix));

        var matches = found.Where(w => w.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        foreach (var m in matches) WriteObject(m);
    }

    private IEnumerable<string> Candidates(string kind, string prefix)
    {
        switch (kind)
        {
            case "file":
            case "directory":
                return PathCandidates(prefix, kind == "directory");
            case "variable":
                return Script("Get-Variable | ForEach-Object Name").Concat(Script("Get-ChildItem Env: | ForEach-Object { if ($_.Name -ieq 'Path') { 'PATH' } else { $_.Name } }"));
            case "function":
                return DeclarePrinter.AllBashFunctions(this).Select(f => f.Name);
            case "export":
                return Script("Get-ChildItem Env: | ForEach-Object Name");
            case "setopt":
                return SetOptions;
            case "alias":
                return Script("Get-Alias | ForEach-Object Name");
            case "command":
                return Script("Get-Command -ErrorAction SilentlyContinue | ForEach-Object Name");
            default:
                return Array.Empty<string>();
        }
    }

    private static readonly string[] SetOptions =
    {
        "allexport", "braceexpand", "emacs", "errexit", "errtrace", "functrace", "hashall", "histexpand", "history", "ignoreeof",
        "interactive-comments", "keyword", "monitor", "noclobber", "noexec", "noglob", "nolog", "notify", "nounset", "onecmd",
        "physical", "pipefail", "posix", "privileged", "verbose", "vi", "xtrace",
    };

    private static readonly HashSet<string> KnownKinds = new(StringComparer.Ordinal)
    {
        "alias", "arrayvar", "binding", "builtin", "command", "directory", "disabled", "enabled", "export", "file", "function",
        "group", "helptopic", "hostname", "job", "keyword", "running", "service", "setopt", "shopt", "signal", "stopped", "user", "variable",
    };

    private IEnumerable<string> Script(string script)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var r in InvokeCommand.InvokeScript(script))
            if (r?.BaseObject is string s) names.Add(s);
        return names;
    }

    private IEnumerable<string> PathCandidates(string prefix, bool dirsOnly)
    {
        string cwd = SessionState.Path.CurrentFileSystemLocation.ProviderPath;
        int slash = prefix.LastIndexOf('/');
        string dirPart = slash >= 0 ? prefix[..(slash + 1)] : "";
        string lookIn = dirPart.Length == 0 ? cwd : Path.Combine(cwd, dirPart);
        var result = new List<string>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(lookIn))
            {
                if (dirsOnly && !Directory.Exists(entry)) continue;
                result.Add(dirPart + Path.GetFileName(entry));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
