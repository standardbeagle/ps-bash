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
        string? wordList = null, funcName = null, command = null, pre = "", suf = "";
        string prefix = "";
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--") { if (i + 1 < args.Length) prefix = args[i + 1]; break; }
            if (a == "-W" && i + 1 < args.Length) { wordList = args[++i]; continue; }
            if (a == "-F" && i + 1 < args.Length) { funcName = args[++i]; continue; }
            if (a == "-C" && i + 1 < args.Length) { command = args[++i]; continue; }
            if (a == "-P" && i + 1 < args.Length) { pre = args[++i]; continue; }
            if (a == "-S" && i + 1 < args.Length) { suf = args[++i]; continue; }
            if (a is "-o" or "-X" or "-G" && i + 1 < args.Length) { i++; continue; } // post-processing / filters: no effect on the set
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
            if (a == "-k") { kinds.Add("keyword"); continue; }
            if (a == "-b") { kinds.Add("builtin"); continue; }
            if (a == "-a") { kinds.Add("alias"); continue; }
            if (a == "-u") { kinds.Add("user"); continue; }
            if (a.StartsWith('-') && a.Length > 1) continue;
            prefix = a;
        }

        var found = new List<string>();
        if (wordList is not null)
        {
            var words = ExpandWordList(wordList);
            if (words is null) { FileSystemHelpers.SetLastExitCode(this, 1); return; }
            found.AddRange(words);
        }
        // -F / -C results are the generator's own: the prefix does not filter them.
        var generated = new List<string>();
        if (funcName is not null) generated.AddRange(RunFunction(funcName, prefix));
        if (command is not null) generated.AddRange(RunBashLines(command) ?? new List<string>());
        foreach (var k in kinds) found.AddRange(Candidates(k, prefix));

        var matches = found.Where(w => w.StartsWith(prefix, StringComparison.Ordinal)).Concat(generated).ToList();
        if (matches.Count == 0)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        foreach (var m in matches) WriteObject(pre + m + suf);
    }

    private IEnumerable<string> Candidates(string kind, string prefix)
    {
        switch (kind)
        {
            case "file":
            case "directory":
                return PathCandidates(prefix, kind == "directory");
            case "variable":
                return Script("Get-Variable | ForEach-Object Name").Concat(Script("Get-ChildItem Env: | ForEach-Object { if ($_.Name -ieq 'Path') { 'PATH' } else { $_.Name } }")).Distinct(StringComparer.Ordinal);
            case "function":
                return DeclarePrinter.AllBashFunctions(this).Select(f => f.Name);
            case "export":
                return Script("Get-ChildItem Env: | ForEach-Object Name");
            case "setopt":
                return SetOptions;
            case "alias":
                return UserAliases();
            case "command":
                return Script("Get-Command -ErrorAction SilentlyContinue | ForEach-Object Name")
                    .Concat(InvokeBashTypeCommand.Builtins).Concat(InvokeBashTypeCommand.Keywords).Distinct(StringComparer.Ordinal);
            case "builtin":
                return InvokeBashTypeCommand.Builtins.OrderBy(x => x, StringComparer.Ordinal);
            case "keyword":
                return InvokeBashTypeCommand.Keywords.OrderBy(x => x, StringComparer.Ordinal);
            case "helptopic":
                return InvokeBashTypeCommand.Builtins.Concat(InvokeBashTypeCommand.Keywords).Where(x => x.Length > 0 && char.IsLetter(x[0])).OrderBy(x => x, StringComparer.Ordinal);
            case "shopt":
                return InvokeBashShoptCommand.SnapshotOptions().Keys.Where(k => !k.StartsWith("set:", StringComparison.Ordinal)).OrderBy(x => x, StringComparer.Ordinal);
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

    private IEnumerable<string> UserAliases()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (SessionState.PSVariable.GetValue("BashUserAliases") is System.Collections.IDictionary d)
            foreach (var k in d.Keys) if (k?.ToString() is { } s) names.Add(s);
        return names;
    }

    /// <summary>Run bash text through the transpiler and return its output lines; null when it fails.</summary>
    private List<string>? RunBashLines(string src)
    {
        try
        {
            var results = InvokeCommand.InvokeScript(
                "param($s) $ErrorActionPreference = 'Stop'; $global:LASTEXITCODE = 0; $out = @(Invoke-Expression ([PsBash.Core.Transpiler.BashTranspiler, PsBash.Transpiler]::Transpile($s, [PsBash.Core.Transpiler.TranspileContext, PsBash.Transpiler]::Eval)) -ErrorVariable ev -ErrorAction SilentlyContinue | ForEach-Object { [string]$_ }); if ($global:LASTEXITCODE -ne 0 -or $ev.Count -gt 0) { throw 'failed' }; $out",
                src);
            var lines = new List<string>();
            foreach (var r in results)
                foreach (var l in (r?.BaseObject?.ToString() ?? "").Split('\n')) if (l.Length > 0) lines.Add(l.TrimEnd('\r'));
            return lines;
        }
        catch (RuntimeException) { return null; }
    }

    /// <summary><c>-W</c>: the list is expanded like a double-quoted word, then split on IFS (a backslash keeps a delimiter in its word).</summary>
    private List<string>? ExpandWordList(string list)
    {
        string text = list;
        if (list.Contains('$') || list.Contains('`'))
        {
            // Quotes inside $( ) nest in the double quotes; any other quote must not end them.
            var lines = RunBashLines("printf '%s' \"" + (list.Contains("$(") ? list : list.Replace("\"", "\\\"")) + "\"");
            if (lines is null) return null;
            text = string.Join("\n", lines);
        }
        string ifs = BashVariableStore.Get("IFS") ?? " \t\n";
        var words = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length) { cur.Append(text[++i]); any = true; continue; }
            if (ifs.IndexOf(c) >= 0)
            {
                if (any) { words.Add(cur.ToString()); cur.Clear(); any = false; }
                continue;
            }
            cur.Append(c); any = true;
        }
        if (any) words.Add(cur.ToString());
        return words;
    }

    /// <summary><c>-F func</c>: call the completion function and read back <c>COMPREPLY</c>.</summary>
    private IEnumerable<string> RunFunction(string func, string cur)
    {
        System.Collections.ObjectModel.Collection<PSObject> results;
        try
        {
            results = InvokeCommand.InvokeScript(
            "param($f, $c) $global:COMPREPLY = @(); $env:COMPREPLY = $null; $Error.Clear(); $global:LASTEXITCODE = 0; & $f 'compgen' $c '' 2>$null >$null; if ($Error.Count -gt 0) { throw 'failed' }; " +
            "if ($null -ne $env:COMPREPLY -and $env:COMPREPLY -ne '') { $env:COMPREPLY } else { @($global:COMPREPLY) }",
            func, cur);
        }
        catch (RuntimeException) { FileSystemHelpers.SetLastExitCode(this, 1); return Array.Empty<string>(); }
        var list = new List<string>();
        foreach (var r in results)
            if (r?.BaseObject is { } o && o.ToString() is { } s && s.Length > 0) list.Add(s);
        return list;
    }

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
