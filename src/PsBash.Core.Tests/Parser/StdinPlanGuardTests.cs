using PsBash.Core.Parser;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Guard for the ONE decision how a command in a stdin scope gets that stdin (<see cref="StdinReaders.Plan"/>): a literal
/// name (PsEmitter.EmitSimpleFed), <c>command NAME</c>, and a run-time name (the psm1 <c>BashDynamicCommandBody</c>) all
/// go through it, and a program or an unknown name is never piped the scope's feed - that feed waits for a first record,
/// so under an open, silent launcher stdin (an agent's shell tool) <c>PY=/usr/bin/python3; $PY -c 'print(2)'</c> and
/// <c>command nosuch_zz 2&gt;/dev/null</c> hung until the stdin closed. They get it as their process stdin
/// (<c>Enter-BashNativeStdin</c>, which installs a pipe only for a real program). A ps-bash reader is still piped.
/// </summary>
public class StdinPlanGuardTests
{
    private const string Feed = "TryDequeue([ref]$__psbash_r)) { $__psbash_r } } | ";

    [Theory]
    [InlineData("cat", StdinPlan.Pipe)]
    [InlineData("sort", StdinPlan.Pipe)]
    [InlineData("echo", StdinPlan.None)]
    [InlineData("cat f", StdinPlan.None)]
    [InlineData("/usr/bin/python3 -c x", StdinPlan.ProcessStdin)]
    [InlineData("nosuch_zz", StdinPlan.ProcessStdin)]
    [InlineData("command cat", StdinPlan.Pipe)]
    [InlineData("command -p sort", StdinPlan.Pipe)]
    [InlineData("command echo hi", StdinPlan.None)]
    [InlineData("command /usr/bin/echo hi", StdinPlan.ProcessStdin)]
    [InlineData("command nosuch_zz", StdinPlan.ProcessStdin)]
    [InlineData("command -v cat", StdinPlan.None)]
    public void Plan_OneDecision_CommandIsPlannedAsWhatItRuns(string words, StdinPlan expected)
    {
        var w = words.Split(' ');
        Assert.Equal(expected, StdinReaders.Plan(w[0], w[1..]));
    }

    [Theory]
    [InlineData("command nosuch_zz 2>/dev/null", "nosuch_zz")]
    [InlineData("command /usr/bin/echo hi", "/usr/bin/echo")]
    [InlineData("command -- /usr/bin/python3 -c 'print(1)'", "/usr/bin/python3")]
    public void Transpile_CommandProgramUnderLauncherStdin_GetsProcessStdinNotAFeed(string bash, string program)
    {
        var ps = BashTranspiler.TranspileWithLauncherStdin(bash)!;

        Assert.Contains("Enter-BashNativeStdin '" + program + "'", ps);
        Assert.DoesNotContain(Feed + "Invoke-BashCommand", ps);
    }

    [Fact]
    public void Transpile_CommandOfAReaderUnderLauncherStdin_IsStillPiped()
    {
        var ps = BashTranspiler.TranspileWithLauncherStdin("command cat")!;

        Assert.Contains(Feed + "Invoke-BashCommand", ps);
    }

    [Fact]
    public void DynamicCommandBody_FedBranch_AsksThePlan_AndStartsAProgramWithProcessStdin()
    {
        var psm1 = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Module", "PsBash.psm1"));
        int start = psm1.IndexOf("$script:BashDynamicCommandBody = @'", StringComparison.Ordinal);
        Assert.True(start >= 0, "BashDynamicCommandBody not found");
        int fed = psm1.IndexOf("} elseif ($__bfed) {", start, StringComparison.Ordinal);
        int piped = psm1.IndexOf("} elseif ($MyInvocation.ExpectingInput) {", start, StringComparison.Ordinal);
        Assert.True(fed > start && piped > fed, "the fed branch of BashDynamicCommandBody moved");
        string branch = psm1[fed..piped];

        Assert.Contains("::Plan(", branch);
        Assert.Contains("Enter-BashNativeStdin", branch);
        Assert.DoesNotContain("::ShouldFeed(", branch);
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
