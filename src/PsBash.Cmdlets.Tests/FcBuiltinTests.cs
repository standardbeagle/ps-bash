using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary><c>fc -l</c> over the session history, checked against bash 5.2 (<c>history -s "cmd N"</c> then <c>fc</c>).</summary>
public class FcBuiltinTests
{
    private static readonly string[] Five = ["cmd 1", "cmd 2", "cmd 3", "cmd 4", "cmd 5"];

    private static List<string> List(string[] history, bool lastIsCurrent, params string[] args)
    {
        var r = InvokeBashFcCommand.Run(args, history, lastIsCurrent);
        Assert.Equal(0, r.Status);
        Assert.Empty(r.Err);
        return r.Out;
    }

    private static int[] Numbers(List<string> lines) => lines.Select(l => int.Parse(l[..l.IndexOf('\t')])).ToArray();

    [Theory]
    [InlineData(new string[0], new[] { 1, 2, 3, 4, 5 })]
    [InlineData(new[] { "4" }, new[] { 4, 5 })]
    [InlineData(new[] { "1", "1" }, new[] { 1 })]
    [InlineData(new[] { "0" }, new[] { 5 })]
    [InlineData(new[] { "-0" }, new[] { 5 })]
    [InlineData(new[] { "-2" }, new[] { 4, 5 })]
    [InlineData(new[] { "-99" }, new[] { 1, 2, 3, 4, 5 })]
    [InlineData(new[] { "99" }, new[] { 1, 2, 3, 4, 5 })]       // a first number past the end lists from the start
    [InlineData(new[] { "2", "99" }, new[] { 2, 3, 4, 5 })]
    [InlineData(new[] { "4", "2" }, new[] { 4, 3, 2 })]
    [InlineData(new[] { "3", "-1" }, new[] { 3, 4, 5 })]
    [InlineData(new[] { "cmd" }, new[] { 5 })]                    // most recent prefix match
    [InlineData(new[] { "cmd 2", "cmd 4" }, new[] { 2, 3, 4 })]
    [InlineData(new[] { "0", "1", "2" }, new[] { 5, 4, 3, 2, 1 })] // extra operands are ignored
    public void List_Ranges_MatchBash(string[] args, int[] expected)
    {
        var a = new[] { "-l" }.Concat(args).ToArray();
        Assert.Equal(expected, Numbers(List(Five, false, a)));
    }

    [Fact]
    public void List_Format_IsNumberTabSpaceCommand()
    {
        Assert.Equal(["1\t cmd 1", "2\t cmd 2"], List(Five, false, "-l", "1", "2"));
        Assert.Equal(["\t cmd 2", "\t cmd 1"], List(Five, false, "-lnr", "1", "2"));
        Assert.Equal(["\t cmd 3", "\t cmd 2"], List(Five, false, "-lr", "-n", "3", "2")); // -r never un-reverses
    }

    [Fact]
    public void List_Default_IsTheLastSixteen()
    {
        var history = Enumerable.Range(1, 20).Select(i => "cmd " + i).ToArray();
        Assert.Equal(Enumerable.Range(5, 16).ToArray(), Numbers(List(history, false, "-l")));
        Assert.Equal([20], Numbers(List(history, false, "-l", "cmd 2")));
        Assert.Equal([19, 20], Numbers(List(history, false, "-l", "cmd 1")));
    }

    // bash -i: the fc line is already in the history; it is skipped, and offsets count from it.
    [Fact]
    public void List_InteractiveShell_SkipsItsOwnLine()
    {
        string[] history = ["history -r", "echo 1", "echo 2", "echo 3", "fc -l"];
        Assert.Equal([1, 2, 3, 4], Numbers(List(history, true, "-l")));
        Assert.Equal([2, 3, 4], Numbers(List(history, true, "-l", "-3")));
        Assert.Equal([2, 3], Numbers(List(history, true, "-l", "-3", "-2")));
        Assert.Equal([4, 3, 2, 1], Numbers(List(history, true, "-lr")));
        Assert.Empty(List(["fc -l"], true, "-l"));
    }

    [Fact]
    public void List_EmptyHistory_PrintsNothing()
    {
        Assert.Empty(List([], false, "-l"));
        Assert.Empty(List([], false, "-l", "0", "1", "2"));
    }

    [Theory]
    [InlineData(1, "fc: no command found", "-l", "zzz")]
    [InlineData(1, "fc: no command found", "-l", "3", "zzz")]
    [InlineData(1, "fc: no command found", "-s", "zzz")]
    [InlineData(2, "fc: -z: invalid option", "-z")]
    [InlineData(1, "fc: editing and re-executing history is not supported", "-s")]
    [InlineData(1, "fc: editing and re-executing history is not supported", "-e", "vi")]
    public void Errors_HaveBashStatus(int status, string message, params string[] args)
    {
        var r = InvokeBashFcCommand.Run(args, Five, false);
        Assert.Equal(status, r.Status);
        Assert.Equal(message, r.Err[0]);
        Assert.Empty(r.Out);
        if (status == 2) Assert.Equal(InvokeBashFcCommand.Usage, r.Err[1]);
    }

    // Pester calls cmdlets directly; a bare -e would bind a common parameter (the decoy catches it). The transpiled
    // `fc -l` must also reach this cmdlet, not PowerShell's `fc` alias (Format-Custom).
    public class Direct(SharedPwshFixture fixture) : IClassFixture<SharedPwshFixture>
    {
        [Fact]
        public void ListsWhatHistoryStored()
        {
            var r = CmdResult.Run(fixture.AcquireFresh(),
                "Invoke-BashHistory -c; Invoke-BashHistory -s one; Invoke-BashHistory -s two; (Invoke-BashFc -l) -join '|'").AssertSuccess();
            Assert.Equal("1\t one|2\t two", r.Lines.Last());
        }

        [Fact]
        public void BareE_BindsTheDecoy()
            => CmdResult.Run(fixture.AcquireFresh(), "Invoke-BashHistory -s one; Invoke-BashFc -e vi")
                .AssertFailed(1, "fc: editing and re-executing history is not supported");
    }
}
