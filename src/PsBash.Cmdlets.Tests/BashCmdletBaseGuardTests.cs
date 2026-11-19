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
            // ShellSignalTraps puts back the status from before a trap ran, eval the one from before or inside its
            // script: neither is a status of its own.
            if (name is "FileSystemHelpers.cs" or "BashCmdlet.cs" or "ShellSignalTraps.cs" or "InvokeBashEvalCommand.cs") continue;
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
