using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A command inside a steppable pipeline that stops the commands before it (head, <c>Select-Object -First</c>): the
/// steppable pipeline hands the engine's internal stop request out RAW, naming a command the outer pipeline does not
/// have, and the outer pipeline then ends none of its commands. Invoke-BashPipeStage (every stage of a PIPESTATUS /
/// pipefail pipeline) asks again as itself. REGRESSION: <c>printf 'ab\0' | head -c 3 | od -c</c> printed nothing and
/// <c>nosuchcmd 2&gt;&amp;1 | head -1</c> gave PIPESTATUS <c>0 0</c> once the script read PIPESTATUS. Oracle note
/// (qa-rubric Directive 1): the cmdlet is not bash surface; the bash behaviour is pinned by
/// PipeStageUpstreamStopDifferentialTests.
/// </summary>
public class PipeStageUpstreamStopTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public PipeStageUpstreamStopTests(SharedPwshFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("Invoke-BashHead '-n' '1'")]
    [InlineData("Select-Object -First 1")]
    public void PipeStage_OwnCommandStopsUpstream_LaterStagesStillEnd(string stage)
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            "$s = [int[]]::new(3); "
            + "Invoke-BashPipeStage -Slot $s -Index 0 -Script { Invoke-BashSeq 1 5 } | "
            + "Invoke-BashPipeStage -Slot $s -Index 1 -Script { " + stage + " } | "
            + "Invoke-BashPipeStage -Slot $s -Index 2 -Script { ForEach-Object -Process { 'got' } -End { 'end'; $global:LASTEXITCODE = 4 } }; "
            + "$global:LASTEXITCODE = 0; 'status ' + ($s -join ' ')").AssertSuccess();
        var lines = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
        Assert.Equal(new[] { "got", "end", "status 0 0 4" }, lines);
    }

    /// <summary>
    /// Every stage starts with the status from before the pipeline, so the words its commands expand as it starts see
    /// it (bash: <c>false; echo $? | cat</c> prints 1, <c>false; true | echo $?</c> prints 1): the first stage's commands
    /// run from it, and a later stage's start (here a ForEach-Object -Begin) sees it too. REGRESSION: each stage's start
    /// reset the status first, and the words read 0. Behaviour against bash: PipeStageEntryStatusTests.Differential_StatusWordOfASimpleStage_*.
    /// </summary>
    [Fact]
    public void PipeStage_StageWords_SeeTheStatusFromBeforeThePipeline()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            "$s = [int[]]::new(3); $global:LASTEXITCODE = 3; "
            + "Invoke-BashPipeStage -Slot $s -Index 0 -Script { & { 'first ' + $global:LASTEXITCODE; $global:LASTEXITCODE = 0 } } | "
            + "Invoke-BashPipeStage -Slot $s -Index 1 -Script { ForEach-Object -Begin { 'second ' + $global:LASTEXITCODE } -Process { $_ } } | "
            + "Invoke-BashPipeStage -Slot $s -Index 2 -Script { ForEach-Object -Process { $_ } }; "
            + "'status ' + ($s -join ' ')").AssertSuccess();
        var lines = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
        Assert.Equal(new[] { "second 3", "first 3", "status 0 0 0" }, lines);
    }

    /// <summary>
    /// A producer a later stage stops reports what its own commands set (seq: 0), never the status from before the
    /// pipeline it started from (ps-bash-specific: bash reports 141, SIGPIPE; docs/specs/intentional-differences.md).
    /// REGRESSION: with the first stage running from the entry status, <c>false; seq 1 100000 | head -1</c> gave
    /// PIPESTATUS <c>1 0</c>.
    /// </summary>
    [Fact]
    public void PipeStage_StoppedProducer_DoesNotReportTheStatusFromBeforeThePipeline()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            "$s = [int[]]::new(2); $global:LASTEXITCODE = 5; "
            + "Invoke-BashPipeStage -Slot $s -Index 0 -Script { & { Invoke-BashSeq 1 100000 } } | "
            + "Invoke-BashPipeStage -Slot $s -Index 1 -Script { Invoke-BashHead '-1' } | Out-Null; "
            + "'status ' + ($s -join ' ')").AssertSuccess();
        Assert.Equal("status 0 0", r.Stdout.Trim());
    }

    /// <summary>
    /// Every host of a steppable pipeline must turn an inner command's raw stop request into its own (as
    /// InvokeBashPipeStageCommand does), or the outer pipeline ends nothing. The native-command wrapper and a job's
    /// program stand-in (JobProgramLookup, <c>Invoke-BashNative</c>) step a native program only, which never asks for
    /// that stop.
    /// </summary>
    [Fact]
    public void Cmdlets_HostASteppablePipelineOnlyWhereTheUpstreamStopIsHandled()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "InvokeBashPipeStageCommand.cs", "InvokeBashNativeCommand.cs", "JobProgramLookup.cs",
        };
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var name = Path.GetFileName(file);
            if (allowed.Contains(name)) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal)) continue;
                if (Regex.IsMatch(code, @"\.GetSteppablePipeline\(")) offenders.Add($"{name}:{i + 1}: {code}");
            }
        }
        Assert.True(offenders.Count == 0,
            "A steppable pipeline host must handle an inner upstream stop (UpstreamStop.RequestedBy; see InvokeBashPipeStageCommand):\n"
            + string.Join("\n", offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }
}
