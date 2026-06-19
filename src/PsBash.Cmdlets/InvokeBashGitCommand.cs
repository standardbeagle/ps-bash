using System.Diagnostics;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>psgit</c> — git as structured PowerShell objects. A read-oriented companion to native
/// <c>git</c> (which is left untouched and fully interactive): the query subcommands
/// (<c>status</c>, <c>log</c>, <c>branch</c>, <c>remote</c>, <c>tag</c>, <c>stash list</c>,
/// <c>diff</c>) are parsed into typed <c>PsBash.Git*</c> objects you can filter, sort, and pipe to
/// <c>Show-Styled</c> / <c>Format-Styled git</c> for a colour-by-state view. Any other subcommand is
/// passed through to native git (buffered) so <c>psgit</c> is a safe drop-in for viewing; use real
/// <c>git</c> for mutating / interactive work (commit editor, push auth, rebase).
/// </summary>
/// <remarks>Shells out to the <c>git</c> binary via <see cref="BashRuntime.RunChildProcess(ProcessStartInfo, System.TimeSpan?)"/> (bounded + kill-tree).</remarks>
[Cmdlet(VerbsLifecycle.Invoke, "BashGit")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashGitCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    // Unit separator: a git --format / for-each-ref field delimiter that can't occur in normal output.
    private const char FieldSep = '\u001f';

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);

        if (args.Length == 0)
        {
            Passthrough(new[] { "status", "--short", "--branch" });
            return;
        }

        var sub = args[0];
        var rest = args.Skip(1).ToArray();
        switch (sub)
        {
            case "status" or "st": RunStatus(rest); break;
            case "log" or "lg": RunLog(rest); break;
            case "branch" or "br": RunBranch(rest); break;
            case "remote": RunRemote(rest); break;
            case "tag": RunTag(rest); break;
            case "stash" when rest.Length > 0 && rest[0] == "list": RunStash(rest.Skip(1).ToArray()); break;
            case "diff": RunDiff(rest); break;
            default: Passthrough(args); break;
        }
    }

    // ── status ────────────────────────────────────────────────────────────────────────────────

    private void RunStatus(string[] userArgs)
    {
        var args = new List<string> { "status", "--porcelain=v1", "--branch" };
        args.AddRange(userArgs);
        if (!RunGit(args, out var r))
        {
            return;
        }

        foreach (var line in SplitLines(r.Stdout))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                WriteObject(MakeStatus(branch: line.Substring(3), x: ' ', y: ' ', path: line.Substring(3),
                    state: "branch", cls: "branch", staged: false, text: line));
                continue;
            }
            if (line.Length < 3)
            {
                continue;
            }

            char x = line[0], y = line[1];
            var path = line.Substring(3);
            var (state, cls, staged) = ClassifyStatus(x, y);
            WriteObject(MakeStatus(branch: null, x, y, path, state, cls, staged, text: line));
        }
    }

    internal static (string State, string Class, bool Staged) ClassifyStatus(char x, char y)
    {
        if (x == '?' && y == '?')
        {
            return ("untracked", "untracked", false);
        }
        if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D'))
        {
            return ("conflict", "conflict", false);
        }

        bool staged = x != ' ' && x != '?';
        char primary = staged ? x : y;
        var state = primary switch
        {
            'A' => "added",
            'M' => "modified",
            'D' => "deleted",
            'R' => "renamed",
            'C' => "copied",
            'T' => "typechange",
            _ => "changed",
        };
        var cls = primary switch
        {
            'A' => "staged",
            'D' => "deleted",
            'R' => "renamed",
            _ => staged ? "staged" : "modified",
        };
        return (state, cls, staged);
    }

    internal static PSObject MakeStatus(string? branch, char x, char y, string path, string state, string cls, bool staged, string text)
    {
        var o = NewGit("PsBash.GitStatusEntry", text);
        o.Properties.Add(new PSNoteProperty("State", state));
        o.Properties.Add(new PSNoteProperty("Path", path));
        o.Properties.Add(new PSNoteProperty("Index", x == ' ' ? "" : x.ToString()));
        o.Properties.Add(new PSNoteProperty("Work", y == ' ' ? "" : y.ToString()));
        o.Properties.Add(new PSNoteProperty("Staged", staged));
        o.Properties.Add(new PSNoteProperty("class", cls));
        SetColumns(o, "State", "Path");
        return o;
    }

    // ── log ───────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] LogPassthroughFlags =
        { "--oneline", "--graph", "--pretty", "--format", "-p", "--patch", "--stat", "--shortstat" };

    private void RunLog(string[] userArgs)
    {
        // Output-reshaping flags can't be parsed back into commit objects — defer to native git.
        if (userArgs.Any(a => LogPassthroughFlags.Any(f => a == f || a.StartsWith(f + "=", StringComparison.Ordinal))))
        {
            Passthrough(Prepend("log", userArgs));
            return;
        }

        var fmt = string.Join(FieldSep, "%H", "%h", "%an", "%ad", "%s");
        var args = new List<string> { "log", "--no-color", "--date=relative", $"--pretty=format:{fmt}" };
        // Bound the default to keep a styled view sane; an explicit -N / --max-count overrides.
        if (!userArgs.Any(a => a == "-n" || a == "--max-count"
                || a.StartsWith("--max-count=", StringComparison.Ordinal)
                || (a.Length > 1 && a[0] == '-' && char.IsDigit(a[1]))))
        {
            args.Add("--max-count=50");
        }
        args.AddRange(userArgs);

        if (!RunGit(args, out var r))
        {
            return;
        }

        foreach (var line in SplitLines(r.Stdout))
        {
            var f = line.Split(FieldSep);
            if (f.Length < 5)
            {
                continue;
            }

            var o = NewGit("PsBash.GitCommit", $"{f[1]}  {f[4]}  ({f[3]}, {f[2]})");
            o.Properties.Add(new PSNoteProperty("Hash", f[0]));
            o.Properties.Add(new PSNoteProperty("ShortHash", f[1]));
            o.Properties.Add(new PSNoteProperty("Author", f[2]));
            o.Properties.Add(new PSNoteProperty("Date", f[3]));
            o.Properties.Add(new PSNoteProperty("Subject", f[4]));
            o.Properties.Add(new PSNoteProperty("class", "commit"));
            SetColumns(o, "ShortHash", "Date", "Author", "Subject");
            WriteObject(o);
        }
    }

    // ── branch ────────────────────────────────────────────────────────────────────────────────

    private void RunBranch(string[] userArgs)
    {
        // Anything beyond a plain listing (create/delete/move/etc.) → native git.
        if (userArgs.Any(a => a is "-d" or "-D" or "-m" or "-M" or "-c" or "-C" or "--delete" or "--move" or "--edit-description")
            || userArgs.Any(a => !a.StartsWith('-')))
        {
            Passthrough(Prepend("branch", userArgs));
            return;
        }

        var fmt = string.Join(FieldSep, "%(HEAD)", "%(refname)", "%(refname:short)", "%(upstream:short)", "%(objectname:short)", "%(contents:subject)");
        var args = new List<string> { "branch", "--no-color", "--all", $"--format={fmt}" };
        args.AddRange(userArgs);
        if (!RunGit(args, out var r))
        {
            return;
        }

        foreach (var line in SplitLines(r.Stdout))
        {
            var f = line.Split(FieldSep);
            if (f.Length < 6)
            {
                continue;
            }

            bool current = f[0] == "*";
            bool remote = f[1].StartsWith("refs/remotes/", StringComparison.Ordinal);
            var cls = current ? "current" : remote ? "remote" : "local";
            var name = f[2];

            var o = NewGit("PsBash.GitBranch", $"{(current ? "*" : " ")} {name}");
            o.Properties.Add(new PSNoteProperty("Current", current));
            o.Properties.Add(new PSNoteProperty("Name", name));
            o.Properties.Add(new PSNoteProperty("Remote", remote));
            o.Properties.Add(new PSNoteProperty("Upstream", f[3]));
            o.Properties.Add(new PSNoteProperty("Tip", f[4]));
            o.Properties.Add(new PSNoteProperty("Subject", f[5]));
            o.Properties.Add(new PSNoteProperty("class", cls));
            SetColumns(o, "Current", "Name", "Upstream", "Subject");
            WriteObject(o);
        }
    }

    // ── remote ────────────────────────────────────────────────────────────────────────────────

    private void RunRemote(string[] userArgs)
    {
        // Only the listing form is structured; add/remove/set-url/etc. → native git.
        if (userArgs.Any(a => !a.StartsWith('-')))
        {
            Passthrough(Prepend("remote", userArgs));
            return;
        }

        if (!RunGit(new List<string> { "remote", "-v" }, out var r))
        {
            return;
        }

        // Lines: "origin\thttps://… (fetch)" / "origin\thttps://… (push)" — fold per remote.
        var fetch = new Dictionary<string, string>(StringComparer.Ordinal);
        var push = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var line in SplitLines(r.Stdout))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0)
            {
                continue;
            }
            var name = line.Substring(0, tab);
            var rest = line.Substring(tab + 1);
            var sp = rest.LastIndexOf(' ');
            var url = sp >= 0 ? rest.Substring(0, sp) : rest;
            var kind = sp >= 0 ? rest.Substring(sp + 1).Trim('(', ')') : "";
            if (!fetch.ContainsKey(name) && !push.ContainsKey(name))
            {
                order.Add(name);
            }
            if (kind == "push")
            {
                push[name] = url;
            }
            else
            {
                fetch[name] = url;
            }
        }

        foreach (var name in order)
        {
            var furl = fetch.TryGetValue(name, out var fu) ? fu : "";
            var purl = push.TryGetValue(name, out var pu) ? pu : furl;
            var o = NewGit("PsBash.GitRemote", $"{name}\t{furl}");
            o.Properties.Add(new PSNoteProperty("Name", name));
            o.Properties.Add(new PSNoteProperty("FetchUrl", furl));
            o.Properties.Add(new PSNoteProperty("PushUrl", purl));
            o.Properties.Add(new PSNoteProperty("class", "remote"));
            SetColumns(o, "Name", "FetchUrl", "PushUrl");
            WriteObject(o);
        }
    }

    // ── tag ───────────────────────────────────────────────────────────────────────────────────

    private void RunTag(string[] userArgs)
    {
        // Only the listing form (no operands, no create/delete flags) is structured.
        if (userArgs.Any(a => a is "-a" or "-d" or "-s" or "-m" or "-f" or "--delete" or "--annotate")
            || userArgs.Any(a => !a.StartsWith('-')))
        {
            Passthrough(Prepend("tag", userArgs));
            return;
        }

        var fmt = string.Join(FieldSep, "%(refname:short)", "%(objectname:short)", "%(contents:subject)");
        if (!RunGit(new List<string> { "tag", "--list", $"--format={fmt}" }, out var r))
        {
            return;
        }

        foreach (var line in SplitLines(r.Stdout))
        {
            var f = line.Split(FieldSep);
            if (f.Length < 1 || f[0].Length == 0)
            {
                continue;
            }

            var o = NewGit("PsBash.GitTag", f[0]);
            o.Properties.Add(new PSNoteProperty("Name", f[0]));
            o.Properties.Add(new PSNoteProperty("Tip", f.Length > 1 ? f[1] : ""));
            o.Properties.Add(new PSNoteProperty("Subject", f.Length > 2 ? f[2] : ""));
            o.Properties.Add(new PSNoteProperty("class", "tag"));
            SetColumns(o, "Name", "Tip", "Subject");
            WriteObject(o);
        }
    }

    // ── stash list ────────────────────────────────────────────────────────────────────────────

    private void RunStash(string[] userArgs)
    {
        var fmt = string.Join(FieldSep, "%gd", "%gs");
        if (!RunGit(new List<string> { "stash", "list", $"--format={fmt}" }, out var r))
        {
            return;
        }

        foreach (var line in SplitLines(r.Stdout))
        {
            var f = line.Split(FieldSep);
            if (f.Length < 1 || f[0].Length == 0)
            {
                continue;
            }

            var msg = f.Length > 1 ? f[1] : "";
            var o = NewGit("PsBash.GitStash", $"{f[0]}: {msg}");
            o.Properties.Add(new PSNoteProperty("Ref", f[0]));
            o.Properties.Add(new PSNoteProperty("Description", msg));
            o.Properties.Add(new PSNoteProperty("class", "stash"));
            SetColumns(o, "Ref", "Description");
            WriteObject(o);
        }
    }

    // ── diff (numstat summary) ────────────────────────────────────────────────────────────────

    private void RunDiff(string[] userArgs)
    {
        // A real patch was asked for — defer to native git rather than summarising.
        if (userArgs.Any(a => a is "-p" or "--patch"))
        {
            Passthrough(Prepend("diff", userArgs));
            return;
        }

        var args = new List<string> { "diff", "--numstat", "--no-color" };
        args.AddRange(userArgs);
        if (!RunGit(args, out var r))
        {
            return;
        }

        foreach (var line in SplitLines(r.Stdout))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            bool binary = parts[0] == "-" || parts[1] == "-";
            int added = binary ? 0 : ParseInt(parts[0]);
            int deleted = binary ? 0 : ParseInt(parts[1]);
            var path = parts[2];
            var cls = binary ? "binary" : deleted == 0 ? "added" : added == 0 ? "deleted" : "modified";
            var churn = binary ? "bin" : $"+{added} -{deleted}";

            var o = NewGit("PsBash.GitDiffStat", $"{churn}  {path}");
            o.Properties.Add(new PSNoteProperty("Path", path));
            o.Properties.Add(new PSNoteProperty("Added", added));
            o.Properties.Add(new PSNoteProperty("Deleted", deleted));
            o.Properties.Add(new PSNoteProperty("Binary", binary));
            o.Properties.Add(new PSNoteProperty("class", cls));
            SetColumns(o, "Added", "Deleted", "Path");
            WriteObject(o);
        }
    }

    // ── passthrough + shared plumbing ─────────────────────────────────────────────────────────

    /// <summary>Run an unrecognised / output-reshaped git command and surface its text + exit code.</summary>
    private void Passthrough(string[] args)
    {
        if (!TryRunGit(args, out var r))
        {
            return;
        }

        foreach (var o in BashRuntime.EmitBashLines(r.Stdout))
        {
            WriteObject(o);
        }

        if (r.ExitCode != 0 && !string.IsNullOrWhiteSpace(r.Stderr))
        {
            FileSystemHelpers.WriteBashError(this, r.Stderr.TrimEnd());
        }
        else if (!string.IsNullOrEmpty(r.Stderr))
        {
            // git routinely writes informational text (hints, progress) to stderr on success.
            foreach (var o in BashRuntime.EmitBashLines(r.Stderr))
            {
                WriteObject(o);
            }
        }

        FileSystemHelpers.SetLastExitCode(this, r.ExitCode);
    }

    /// <summary>Run git for a structured handler; on non-zero exit, surface the error and return false (no objects).</summary>
    private bool RunGit(IReadOnlyList<string> args, out BashRuntime.ChildProcessResult result)
    {
        if (!TryRunGit(args, out result))
        {
            return false;
        }

        if (result.ExitCode != 0)
        {
            var msg = string.IsNullOrWhiteSpace(result.Stderr) ? $"git exited with code {result.ExitCode}" : result.Stderr.TrimEnd();
            FileSystemHelpers.WriteBashError(this, msg);
            FileSystemHelpers.SetLastExitCode(this, result.ExitCode);
            return false;
        }

        return true;
    }

    /// <summary>Spawn the git binary (bounded + kill-tree) in the session's current directory. False = git not found.</summary>
    private bool TryRunGit(IReadOnlyList<string> args, out BashRuntime.ChildProcessResult result)
    {
        result = default;
        string? cwd = null;
        try { cwd = SessionState.Path.CurrentFileSystemLocation.Path; }
        catch { /* provider path unavailable — inherit the process cwd */ }

        try
        {
            result = RunGitCapture(cwd, args);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            FileSystemHelpers.WriteBashError(this, "psgit: git is not installed or not on PATH");
            FileSystemHelpers.SetLastExitCode(this, 127);
            return false;
        }
    }

    /// <summary>
    /// Spawn git (bounded + kill-tree) in <paramref name="workingDir"/> and capture its output.
    /// Shared by the cmdlet and the interactive TUI. GIT_TERMINAL_PROMPT=0 keeps a credential helper
    /// that would need a terminal prompt from hanging: psgit's child git has no usable interactive
    /// TTY and buffers output, so an un-answerable prompt fails fast with "terminal prompts disabled"
    /// instead of blocking to the timeout. Non-terminal auth (SSH agent, cached / GUI credential
    /// helpers) is unaffected and reuses your configured git auth. Throws Win32Exception if git is
    /// not on PATH.
    /// </summary>
    internal static BashRuntime.ChildProcessResult RunGitCapture(string? workingDir, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        if (!string.IsNullOrEmpty(workingDir))
        {
            try { psi.WorkingDirectory = workingDir; } catch { /* inherit process cwd */ }
        }
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        return BashRuntime.RunChildProcess(psi);
    }

    // ── interactive TUI building blocks (used by StyledInteractiveSession.RunGitStatus) ──────────

    /// <summary>Fetch the working-tree status as typed GitStatusEntry rows (branch header first). Empty on error / not-a-repo.</summary>
    internal static List<PSObject> FetchStatus(string? workingDir)
    {
        var rows = new List<PSObject>();
        BashRuntime.ChildProcessResult r;
        try { r = RunGitCapture(workingDir, new[] { "status", "--porcelain=v1", "--branch" }); }
        catch (System.ComponentModel.Win32Exception) { return rows; }
        if (r.ExitCode != 0)
        {
            return rows;
        }

        foreach (var line in SplitLines(r.Stdout))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                rows.Add(MakeStatus(line.Substring(3), ' ', ' ', line.Substring(3), "branch", "branch", false, line));
                continue;
            }
            if (line.Length < 3)
            {
                continue;
            }
            char x = line[0], y = line[1];
            var (state, cls, staged) = ClassifyStatus(x, y);
            rows.Add(MakeStatus(null, x, y, line.Substring(3), state, cls, staged, line));
        }
        return rows;
    }

    /// <summary>Stage (git add) or unstage (git reset HEAD) a path; no-op for the branch header row. Returns true on success.</summary>
    internal static bool ToggleStage(string? workingDir, PSObject row)
    {
        var path = row.Properties["Path"]?.Value?.ToString();
        var cls = row.Properties["class"]?.Value?.ToString();
        if (string.IsNullOrEmpty(path) || cls == "branch")
        {
            return false;
        }

        var staged = row.Properties["Staged"]?.Value is bool b && b;
        // Renamed entries arrive as "old -> new"; act on the new path git reports.
        var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            path = path.Substring(arrow + 4);
        }

        var args = staged
            ? new[] { "reset", "-q", "HEAD", "--", path }
            : new[] { "add", "--", path };
        try { return RunGitCapture(workingDir, args).ExitCode == 0; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>An action the interactive git-status pane takes for a keypress.</summary>
    public enum GitTuiAction { None, Up, Down, ToggleStage, ToggleExpand, Refresh, Quit }

    /// <summary>Map a keystroke to a pane action (pure — unit-tested; the loop wiring is not).</summary>
    internal static GitTuiAction Decide(ConsoleKey key, char ch) => (key, ch) switch
    {
        (ConsoleKey.Q, _) => GitTuiAction.Quit,
        (ConsoleKey.Escape, _) => GitTuiAction.Quit,
        (ConsoleKey.DownArrow, _) => GitTuiAction.Down,
        (ConsoleKey.UpArrow, _) => GitTuiAction.Up,
        (_, 'j') => GitTuiAction.Down,
        (_, 'k') => GitTuiAction.Up,
        (ConsoleKey.Spacebar, _) => GitTuiAction.ToggleStage,
        (_, 's') => GitTuiAction.ToggleStage,
        (_, 'u') => GitTuiAction.ToggleStage,
        (_, 'r') => GitTuiAction.Refresh,
        (ConsoleKey.Enter, _) => GitTuiAction.ToggleExpand,
        _ => GitTuiAction.None,
    };

    /// <summary>A new typed git PSObject carrying a native-style <c>BashText</c> line.</summary>
    private static PSObject NewGit(string typeName, string bashText)
    {
        var o = new PSObject();
        o.TypeNames.Insert(0, typeName);
        o.Properties.Add(new PSNoteProperty("BashText", bashText));
        return o;
    }

    /// <summary>Declare the default display columns (honoured by raw output and Format-Styled).</summary>
    private static void SetColumns(PSObject o, params string[] columns)
    {
        var set = new PSPropertySet("DefaultDisplayPropertySet", columns);
        o.Members.Add(new PSMemberSet("PSStandardMembers", new PSMemberInfo[] { set }));
    }

    private static string[] Prepend(string head, string[] rest)
    {
        var a = new string[rest.Length + 1];
        a[0] = head;
        Array.Copy(rest, 0, a, 1, rest.Length);
        return a;
    }

    private static int ParseInt(string s) => int.TryParse(s, out var n) ? n : 0;

    /// <summary>Split captured stdout into non-empty lines (CRLF-tolerant).</summary>
    private static IEnumerable<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }
}
