using System.Management.Automation;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Guard for the class "a pipeline stage's status is overwritten by a stage ahead of it that finishes later": PowerShell
/// runs every stage's BeginProcessing before the first record, so <c>echo x | grep --bogus</c> came out 0 (GNU 2: grep
/// found its usage error while parsing, then echo set <c>$LASTEXITCODE = 0</c>). The primitive is
/// <see cref="BashCmdlet"/>: a stage after the first re-asserts the status it set once the engine disposes it, after
/// every EndProcessing. It only knows a status that went through <see cref="FileSystemHelpers.SetLastExitCode"/>, so
/// (1) every cmdlet derives from it and (2) no cmdlet writes <c>$global:LASTEXITCODE</c> itself.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant; the statuses are the oracle's
/// (PipelineStageStatusDifferentialTests).
/// </summary>
public class BashCmdletBaseGuardTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public BashCmdletBaseGuardTests(SharedPwshFixture fixture) => _fixture = fixture;

    [Fact]
    public void EveryCmdlet_DerivesFromBashCmdlet()
    {
        var offenders = typeof(BashCmdlet).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsDefined(typeof(CmdletAttribute), inherit: false))
            .Where(t => !typeof(BashCmdlet).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToList();
        Assert.True(offenders.Count == 0, "Derive from BashCmdlet (its status survives the stages ahead of it):\n"
            + string.Join("\n", offenders));
    }

    // A C# write of the status (a PowerShell script string that assigns it is a nested pipeline's, not this cmdlet's).
    private static readonly Regex DirectWrite = new(@"PSVariable\.Set\([^;]*LASTEXITCODE", RegexOptions.Compiled);

    [Fact]
    public void Cmdlets_SetTheStatusOnlyThroughSetLastExitCode()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var name = Path.GetFileName(file);
            // eval puts back the status from before or inside its script: not a status of its own. A status that is not
            // the cmdlet's (before a trap, a first pipeline stage's start) goes through FileSystemHelpers.RestoreShellStatus.
            if (name is "FileSystemHelpers.cs" or "BashCmdlet.cs" or "InvokeBashEvalCommand.cs") continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                if (DirectWrite.IsMatch(code)) offenders.Add($"{name}:{i + 1}: {code}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Set the status through FileSystemHelpers.SetLastExitCode (BashCmdlet re-asserts only what it records):\n"
            + string.Join("\n", offenders));
    }

    // The primitive end to end, without the transpiler: a status set at BeginProcessing (a usage error) and one set at
    // ProcessRecord survive an upstream stage that sets 0 after them; a status-less stage after them does not reset it.
    [Theory]
    [InlineData("Invoke-BashEcho x | Invoke-BashTail --bogus", 1)]
    [InlineData("Invoke-BashEcho x | Invoke-BashTee --bogus", 1)]
    [InlineData("Invoke-BashEcho x | Invoke-BashTr --bogus", 1)]
    public void StatusSetByALaterStage_SurvivesTheStageAheadOfIt(string script, int exit)
        => CmdResult.Run(_fixture.AcquireFresh(), script).AssertFailed(exit, "");

    [Fact]
    public void LaterStageStatus_WinsOverAFailedStageAheadOfIt()
        => Assert.Equal(0, CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashCat /no/such/psbash-file | Invoke-BashTail -1").ExitCode);

    // The re-assert undoes only a stage AHEAD. A status set later by something that is not a cmdlet (a program, the
    // command-not-found handler; here a script block) stands, and a wrapper's status is the command it ran
    // (FileSystemHelpers.AdoptLastExitCode): both were re-asserted over (echo x | grep zzz | nosuchcmd gave 1, not 127;
    // echo x | command ls /nosuch gave 0).
    [Fact]
    public void LaterStatus_SetByNoCmdlet_IsNotUndone()
        => Assert.Equal(9, CmdResult.Run(_fixture.AcquireFresh(),
            "Invoke-BashEcho x | Invoke-BashTail --bogus 2>$null | & { end { $global:LASTEXITCODE = 9 } }").ExitCode);

    [Fact]
    public void Wrapper_InAPipeline_HasTheStatusOfTheCommandItRan()
        => CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashEcho x | Invoke-BashCommand ls /no/such/psbash-file")
            .AssertFailed(2, "No such file or directory");

    // A cmdlet that runs another command by name (`& $c @args`): its own recorded status must end as that command's.
    private static readonly Regex RunsACommand = new(@"""[^""]*[&.] ?\$[A-Za-z_]+ @", RegexOptions.Compiled);

    // Takes the nested command's status: adopts it, or reads it back and sets it.
    private static readonly Regex TakesItsStatus = new(@"AdoptLastExitCode\(|\$global:LASTEXITCODE", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> RunsWithoutAdopting = new()
    {
        ["InvokeBashNativeCommand.cs"] = "PowerShell publishes the program's status after the stage ends; it is set by no cmdlet and stands (BashCmdlet)",
    };

    [Fact]
    public void Wrappers_TakeTheStatusOfTheCommandTheyRun()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        var seen = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            if (!RunsACommand.IsMatch(text)) continue;
            seen.Add(name);
            if (!RunsWithoutAdopting.ContainsKey(name) && !TakesItsStatus.IsMatch(text)) offenders.Add(name);
        }
        Assert.True(offenders.Count == 0,
            "Call FileSystemHelpers.AdoptLastExitCode(this) after the command this cmdlet runs (BashCmdlet re-asserts a stale own status):\n"
            + string.Join("\n", offenders));
        var stale = RunsWithoutAdopting.Keys.Where(k => !seen.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Stale allowlist entries: " + string.Join(", ", stale));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }
}
