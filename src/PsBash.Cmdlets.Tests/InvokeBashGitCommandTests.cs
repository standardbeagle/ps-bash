using System.Diagnostics;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Structured-output tests for <c>Invoke-BashGit</c> (psgit). Each test builds a throwaway git repo
/// and asserts the typed objects the cmdlet emits. Skipped when git is not on PATH (so the suite
/// stays green on a machine without git); CI runners have git. No Strata dependency.
/// </summary>
public class InvokeBashGitCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashGitCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public void Status_ClassifiesStagedModifiedAndUntracked()
    {
        Skip.IfNot(GitAvailable(), "git is not installed");
        var repo = NewRepo();
        try
        {
            // tracked.txt is committed then modified; new.txt is staged; junk.txt is untracked.
            File.WriteAllText(Path.Combine(repo, "tracked.txt"), "v2 changed\n");
            File.WriteAllText(Path.Combine(repo, "new.txt"), "added\n");
            File.WriteAllText(Path.Combine(repo, "junk.txt"), "untracked\n");
            Git(repo, "add", "new.txt");

            var rows = InvokeGit(repo, "status");

            var tracked = rows.Single(r => Prop(r, "Path") == "tracked.txt");
            Assert.Equal("modified", Prop(tracked, "class"));
            Assert.False((bool)Val(tracked, "Staged")!);

            var added = rows.Single(r => Prop(r, "Path") == "new.txt");
            Assert.Equal("staged", Prop(added, "class"));
            Assert.True((bool)Val(added, "Staged")!);

            var junk = rows.Single(r => Prop(r, "Path") == "junk.txt");
            Assert.Equal("untracked", Prop(junk, "class"));

            // A typed object, with the curated default columns declared.
            Assert.Equal("PsBash.GitStatusEntry", added.TypeNames[0]);
        }
        finally { Cleanup(repo); }
    }

    [SkippableFact]
    public void Log_EmitsTypedCommitsWithSubjectAndShortHash()
    {
        Skip.IfNot(GitAvailable(), "git is not installed");
        var repo = NewRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "b.txt"), "b\n");
            Git(repo, "add", "b.txt");
            Commit(repo, "second commit");

            var rows = InvokeGit(repo, "log");

            Assert.Equal(2, rows.Count);
            Assert.Equal("PsBash.GitCommit", rows[0].TypeNames[0]);
            Assert.Equal("second commit", Prop(rows[0], "Subject"));
            Assert.Equal("initial", Prop(rows[1], "Subject"));
            Assert.False(string.IsNullOrEmpty(Prop(rows[0], "ShortHash")));
        }
        finally { Cleanup(repo); }
    }

    [SkippableFact]
    public void Branch_MarksCurrentBranch()
    {
        Skip.IfNot(GitAvailable(), "git is not installed");
        var repo = NewRepo();
        try
        {
            var rows = InvokeGit(repo, "branch");
            var current = rows.Single(r => (bool)Val(r, "Current")!);
            Assert.Equal("current", Prop(current, "class"));
            Assert.Equal("PsBash.GitBranch", current.TypeNames[0]);
        }
        finally { Cleanup(repo); }
    }

    [SkippableFact]
    public void Diff_NumstatProducesAddedDeletedCounts()
    {
        Skip.IfNot(GitAvailable(), "git is not installed");
        var repo = NewRepo();
        try
        {
            // Append two lines to the committed tracked.txt (git diff only shows TRACKED changes).
            File.WriteAllText(Path.Combine(repo, "tracked.txt"), "a\ntwo\nthree\n");

            var rows = InvokeGit(repo, "diff");

            var row = rows.Single(r => Prop(r, "Path") == "tracked.txt");
            Assert.Equal("PsBash.GitDiffStat", row.TypeNames[0]);
            Assert.Equal(2, (int)Val(row, "Added")!);     // two appended lines
            Assert.Equal(0, (int)Val(row, "Deleted")!);
            Assert.Equal("added", Prop(row, "class"));
        }
        finally { Cleanup(repo); }
    }

    [SkippableFact]
    public void NotARepo_ReportsErrorAndEmitsNoObjects()
    {
        Skip.IfNot(GitAvailable(), "git is not installed");
        var dir = Path.Combine(Path.GetTempPath(), "psbash-nogit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pwsh = _fixture.AcquireFresh();
            var result = pwsh.AddScript($"Set-Location -LiteralPath '{dir}'; Invoke-BashGit status").Invoke();
            Assert.Empty(result);
            Assert.True(pwsh.HadErrors);   // "fatal: not a git repository" surfaced as an error
        }
        finally { Cleanup(dir); }
    }

    [SkippableFact]
    public void UnknownSubcommand_PassesThroughToNativeGit()
    {
        Skip.IfNot(GitAvailable(), "git is not installed");
        var repo = NewRepo();
        try
        {
            // rev-parse isn't a structured subcommand → passthrough emits git's text.
            var pwsh = _fixture.AcquireFresh();
            var result = pwsh.AddScript(
                $"Set-Location -LiteralPath '{repo}'; Invoke-BashGit rev-parse --abbrev-ref HEAD").Invoke();
            Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
            var text = string.Join("\n", result.Select(r => r.ToString())).Trim();
            Assert.Equal("main", text);
        }
        finally { Cleanup(repo); }
    }

    // ── interactive TUI building blocks (headless-testable; the ReadKey loop is not) ────────────

    [Theory]
    [InlineData(ConsoleKey.Q, '\0', InvokeBashGitCommand.GitTuiAction.Quit)]
    [InlineData(ConsoleKey.Escape, '\0', InvokeBashGitCommand.GitTuiAction.Quit)]
    [InlineData(ConsoleKey.DownArrow, '\0', InvokeBashGitCommand.GitTuiAction.Down)]
    [InlineData(ConsoleKey.UpArrow, '\0', InvokeBashGitCommand.GitTuiAction.Up)]
    [InlineData(ConsoleKey.J, 'j', InvokeBashGitCommand.GitTuiAction.Down)]
    [InlineData(ConsoleKey.K, 'k', InvokeBashGitCommand.GitTuiAction.Up)]
    [InlineData(ConsoleKey.Spacebar, ' ', InvokeBashGitCommand.GitTuiAction.ToggleStage)]
    [InlineData(ConsoleKey.S, 's', InvokeBashGitCommand.GitTuiAction.ToggleStage)]
    [InlineData(ConsoleKey.U, 'u', InvokeBashGitCommand.GitTuiAction.ToggleStage)]
    [InlineData(ConsoleKey.R, 'r', InvokeBashGitCommand.GitTuiAction.Refresh)]
    [InlineData(ConsoleKey.Enter, '\r', InvokeBashGitCommand.GitTuiAction.ToggleExpand)]
    [InlineData(ConsoleKey.X, 'x', InvokeBashGitCommand.GitTuiAction.None)]
    public void Decide_MapsKeysToPaneActions(ConsoleKey key, char ch, InvokeBashGitCommand.GitTuiAction expected)
    {
        Assert.Equal(expected, InvokeBashGitCommand.Decide(key, ch));
    }

    [SkippableFact]
    public void ToggleStage_StagesThenUnstagesAFile()
    {
        Skip.IfNot(GitAvailable(), "git is not installed");
        var repo = NewRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "junk.txt"), "x\n");   // untracked

            var untracked = InvokeBashGitCommand.FetchStatus(repo).Single(r => RowPath(r) == "junk.txt");
            Assert.Equal("untracked", Cls(untracked));

            // Stage it.
            Assert.True(InvokeBashGitCommand.ToggleStage(repo, untracked));
            var staged = InvokeBashGitCommand.FetchStatus(repo).Single(r => RowPath(r) == "junk.txt");
            Assert.Equal("staged", Cls(staged));
            Assert.True((bool)staged.Properties["Staged"]!.Value!);

            // Unstage it (back to untracked).
            Assert.True(InvokeBashGitCommand.ToggleStage(repo, staged));
            var back = InvokeBashGitCommand.FetchStatus(repo).Single(r => RowPath(r) == "junk.txt");
            Assert.Equal("untracked", Cls(back));
        }
        finally { Cleanup(repo); }
    }

    [SkippableFact]
    public void ToggleStage_BranchHeaderRow_IsNoOp()
    {
        Skip.IfNot(GitAvailable(), "git is not installed");
        var repo = NewRepo();
        try
        {
            var header = InvokeBashGitCommand.FetchStatus(repo).First(r => Cls(r) == "branch");
            Assert.False(InvokeBashGitCommand.ToggleStage(repo, header));
        }
        finally { Cleanup(repo); }
    }

    private static string RowPath(System.Management.Automation.PSObject r) => r.Properties["Path"]?.Value?.ToString() ?? "";
    private static string Cls(System.Management.Automation.PSObject r) => r.Properties["class"]?.Value?.ToString() ?? "";

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private System.Collections.Generic.List<System.Management.Automation.PSObject> InvokeGit(string repo, string sub)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript($"Set-Location -LiteralPath '{repo}'; Invoke-BashGit {sub}").Invoke();
        Assert.False(pwsh.HadErrors, string.Join("; ", pwsh.Streams.Error.Select(e => e.ToString())));
        return result.ToList();
    }

    private static string Prop(System.Management.Automation.PSObject o, string name)
        => o.Properties[name]?.Value?.ToString() ?? string.Empty;

    private static object? Val(System.Management.Automation.PSObject o, string name)
        => o.Properties[name]?.Value;

    /// <summary>Create a temp git repo with one commit (fixed identity, default branch main).</summary>
    private static string NewRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psbash-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-b", "main");
        File.WriteAllText(Path.Combine(dir, "tracked.txt"), "a\n");
        Git(dir, "add", "tracked.txt");
        Commit(dir, "initial");
        return dir;
    }

    private static void Commit(string repo, string message)
        => Git(repo, "commit", "-m", message);

    private static void Git(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Fixed identity + no global config influence, so the repo is byte-stable across machines.
        psi.Environment["GIT_AUTHOR_NAME"] = "psbash-test";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "test@psbash.local";
        psi.Environment["GIT_COMMITTER_NAME"] = "psbash-test";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "test@psbash.local";
        psi.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(repo, ".gitconfig-none");
        psi.Environment["GIT_CONFIG_SYSTEM"] = Path.Combine(repo, ".gitconfig-none");
        using var p = Process.Start(psi)!;
        p.WaitForExit(15000);
    }

    private static bool GitAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort; .git holds read-only packs */ }
    }
}
