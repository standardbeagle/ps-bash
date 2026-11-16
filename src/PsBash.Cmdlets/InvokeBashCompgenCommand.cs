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
        string? wordList = null, funcName = null, command = null, filter = null, pre = "", suf = "";
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
            if (a == "-X" && i + 1 < args.Length) { filter = args[++i]; continue; }
            if (a is "-o" or "-G" && i + 1 < args.Length) { i++; continue; } // post-processing / filters: no effect on the set
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
        if (filter is not null) matches = ApplyFilter(matches, filter, prefix);
        if (matches.Count == 0)
        {
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        // The -F function's last command (a command not found: 127) leaves its own status; compgen's is 0.
        FileSystemHelpers.SetLastExitCode(this, 0);
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
                // -e: the exported variables (a variable that is merely set is -v only).
                return BashVariableStore.EnumerateExported().Select(kv => string.Equals(kv.Key, "Path", StringComparison.OrdinalIgnoreCase) ? "PATH" : kv.Key)
                    .OrderBy(n => n, StringComparer.Ordinal);
            case "setopt":
                return SetOptions;
            case "alias":
                return UserAliases();
            case "command":
                // The Alias: and Function: drives, not a bare Get-Command: that lists neither the runtime's
                // global AllScope aliases (xargs, grep, ...) nor quickly (it walks every module on PSModulePath).
                return Script("Get-ChildItem Alias:, Function: -ErrorAction SilentlyContinue | ForEach-Object Name; " +
                              "Get-Command -CommandType Cmdlet -ListImported -ErrorAction SilentlyContinue | ForEach-Object Name")
                    .Concat(InvokeBashTypeCommand.Builtins).Concat(InvokeBashTypeCommand.Keywords).Distinct(StringComparer.Ordinal);
            case "builtin":
                return InvokeBashTypeCommand.Builtins.OrderBy(x => x, StringComparer.Ordinal);
            case "keyword":
                return InvokeBashTypeCommand.Keywords.OrderBy(x => x, StringComparer.Ordinal);
            case "helptopic":
                return InvokeBashTypeCommand.Builtins.Concat(InvokeBashTypeCommand.Keywords).Where(x => x.Length > 0 && char.IsLetter(x[0])).OrderBy(x => x, StringComparer.Ordinal);
            case "shopt":
                return InvokeBashShoptCommand.SnapshotOptions().Keys.Where(k => !k.StartsWith("set:", StringComparison.Ordinal)).OrderBy(x => x, StringComparer.Ordinal);
            case "user":
                return UserNames(OperatingSystem.IsWindows() ? null : "/etc/passwd");
            default:
                return Array.Empty<string>();
        }
    }

    /// <summary><c>-X filterpat</c>: removes the candidates that match the pattern (all of them, the <c>-F</c> /
    /// <c>-C</c> results too). A leading <c>!</c> negates it (removes those that do not match); an unescaped <c>&amp;</c>
    /// stands for the word being completed.</summary>
    internal static List<string> ApplyFilter(List<string> candidates, string filter, string word)
    {
        bool negate = filter.StartsWith('!');
        if (negate) filter = filter[1..];
        var pat = new System.Text.StringBuilder();
        for (int i = 0; i < filter.Length; i++)
        {
            if (filter[i] == '\\' && i + 1 < filter.Length && filter[i + 1] == '&') { pat.Append('&'); i++; continue; }
            if (filter[i] == '&')
            {
                foreach (var ch in word) { if (ch is '*' or '?' or '[' or ']' or '\\' or '(' or ')' or '|' or '@' or '!' or '+') pat.Append('\\'); pat.Append(ch); }
                continue;
            }
            pat.Append(filter[i]);
        }
        var p = pat.ToString();
        return candidates.Where(c => BashPatternMatch.MatchesPattern(c, p) == negate).ToList();
    }

    /// <summary><c>-A user</c> / <c>-u</c>: the account names of <paramref name="passwd"/> in file order (bash reads
    /// them with getpwent). Windows has no passwd database: the current user only. getpwent also returns accounts
    /// that are not in the file (macOS keeps real users in Directory Services, Linux may use NSS/LDAP), so the current
    /// user is added when the file lacks it.</summary>
    internal static IEnumerable<string> UserNames(string? passwd, string? currentUser = null)
    {
        currentUser ??= Environment.UserName;
        if (passwd is null || !File.Exists(passwd)) return [currentUser];
        var names = new List<string>();
        foreach (var line in BashFileSystem.ReadLines(passwd))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[0] != '#') names.Add(line[..colon]);
        }
        if (!names.Contains(currentUser)) names.Add(currentUser);
        return names;
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
        string ps;
        try { ps = BashRuntime.TranspileEval(src) ?? ""; }
        catch (Exception ex) when (ex is not PipelineStoppedException) { return null; } // a syntax error fails the word list
        try
        {
            var results = InvokeCommand.InvokeScript(
                "param($p) $ErrorActionPreference = 'Stop'; $global:LASTEXITCODE = 0; $out = @(Invoke-Expression $p -ErrorVariable ev -ErrorAction SilentlyContinue | ForEach-Object { [string]$_ }); if ($global:LASTEXITCODE -ne 0 -or $ev.Count -gt 0) { throw 'failed' }; $out",
                ps);
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
            "param($f, $c) $global:COMPREPLY = @(); $env:COMPREPLY = $null; $Error.Clear(); $global:LASTEXITCODE = 0; & $f 'compgen' $c '' 2>$null >$null; " +
            // A runtime error in the function fails compgen. A lookup that found nothing does not (bash): a command
            // it runs that is not found, or `unset -v COMPREPLY` of a name with no variable (a silenced
            // ObjectNotFound that $Error still records).
            "if (@($Error | Where-Object { [string]$_.CategoryInfo.Category -ne 'ObjectNotFound' }).Count -gt 0) { throw 'failed' }; " +
            "if ($null -ne $env:COMPREPLY -and $env:COMPREPLY -ne '') { $env:COMPREPLY } else { @($global:COMPREPLY) }",
            func, cur);
        }
        catch (RuntimeException) { FileSystemHelpers.SetLastExitCode(this, 1); return Array.Empty<string>(); }
        var list = new List<string>();
        foreach (var r in results)
        {
            // `unset -v 'COMPREPLY[2]'` leaves a sparse array: its values, in index order (it printed the type name).
            if (r?.BaseObject is BashSparseArray sparse)
            {
                foreach (var v in sparse.Values)
                    if (v?.ToString() is { Length: > 0 } sv) list.Add(sv);
                continue;
            }
            if (r?.BaseObject is { } o && o.ToString() is { } s && s.Length > 0) list.Add(s);
        }
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
