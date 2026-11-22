using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary><c>fc -l</c> over the session history, checked against bash 5.2 (<c>history -s "cmd N"</c> then <c>fc</c>).</summary>
public class FcBuiltinTests
{
    private static readonly string[] Five = ["cmd 1", "cmd 2", "cmd 3", "cmd 4", "cmd 5"];

    private static List<string> List(string[] history, bool lastIsCurrent, params string[] args)
    {
        var r = InvokeBashFcCommand.Run(args, [.. history], lastIsCurrent);
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
    [InlineData(new[] { "5" }, new[] { 1, 2, 3, 4, 5 })]        // ... and so does one AT the end (fc_gethnum: n >= last)
    [InlineData(new[] { "5", "1" }, new[] { 1 })]               // bash 5.2: "1  cmd 1", not 5..1
    [InlineData(new[] { "6", "2" }, new[] { 1, 2 })]
    [InlineData(new[] { "4", "5" }, new[] { 4, 5 })]            // a last number at the end is the last entry
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
    [InlineData(1, "fc: no command found", "-s", "-0")]          // -0 names nothing outside a listing
    [InlineData(2, "fc: -z: invalid option", "-z")]
    [InlineData(1, InvokeBashFcCommand.EditorRefused, "-e", "vi")]
    [InlineData(1, InvokeBashFcCommand.EditorRefused, "2")]
    public void Errors_HaveBashStatus(int status, string message, params string[] args)
    {
        var r = InvokeBashFcCommand.Run(args, [.. Five], false);
        Assert.Equal(status, r.Status);
        Assert.Equal(message, r.Err[0]);
        Assert.Empty(r.Out);
        Assert.Null(r.Rerun);
        if (status == 2) Assert.Equal(InvokeBashFcCommand.Usage, r.Err[1]);
    }

    [Fact]
    public void Rerun_EmptyHistory_IsNoCommandFound()
    {
        var r = InvokeBashFcCommand.Run(["-s"], [], false);
        Assert.Equal((1, "fc: no command found"), (r.Status, r.Err.Single()));
        Assert.Null(r.Rerun);
    }

    /// <summary>
    /// <c>fc -s [pat=rep ...] [cmd]</c> / <c>fc -e - ...</c>, bash 5.2 (history <c>echo x1</c>, <c>echo x2</c>): the
    /// command is echoed to stderr and replaces the LAST entry (also with history recording off), and is handed back to
    /// run. Every pat=rep applies in order, each to every match; an empty pat replaces each character.
    /// </summary>
    [Theory]
    [InlineData(new[] { "-s" }, "echo x2")]
    [InlineData(new[] { "-s", "9" }, "echo x2")]          // past the end: the last entry
    [InlineData(new[] { "-s", "-9" }, "echo x1")]         // before the start: the first
    [InlineData(new[] { "-s", "0" }, "echo x2")]
    [InlineData(new[] { "-s", "1" }, "echo x1")]
    [InlineData(new[] { "-s", "a=b" }, "echo x2")]        // no match: unchanged
    [InlineData(new[] { "-s", "x=Q", "x2=y" }, "echo Q2")]
    [InlineData(new[] { "-s", "x=Q", "echo x1" }, "echo Q1")]
    [InlineData(new[] { "-s", "o=0" }, "ech0 x2")]
    [InlineData(new[] { "-s", "=Z" }, "ZZZZZZZ")]
    [InlineData(new[] { "-e", "-", "c=C", "1" }, "eCho x1")]
    [InlineData(new[] { "-e", "-", "-l" }, "echo x2")]    // -e - wins over -l
    [InlineData(new[] { "-ls" }, "echo x2")]
    public void Rerun_ReplacesTheLastEntry_AndReturnsTheCommand(string[] args, string command)
    {
        var history = new List<string> { "echo x1", "echo x2" };
        var r = InvokeBashFcCommand.Run(args, history, false);
        Assert.Equal(0, r.Status);
        Assert.Equal(command, r.Rerun);
        Assert.Equal([command], r.Err);
        Assert.Empty(r.Out);
        Assert.Equal(["echo x1", command], history);
    }

    // bash -i: the fc line itself is the last entry; it is not a candidate, and the re-run replaces it.
    [Fact]
    public void Rerun_InteractiveShell_ReplacesItsOwnLine()
    {
        var history = new List<string> { "echo a", "echo b", "fc -s echo" };
        var r = InvokeBashFcCommand.Run(["-s", "echo"], history, true);
        Assert.Equal("echo b", r.Rerun);
        Assert.Equal(["echo a", "echo b", "echo b"], history);
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
                .AssertFailed(1, InvokeBashFcCommand.EditorRefused);

        // A direct call cannot run the command (only the emitted PsBuild.FcCall does): it is left in the global.
        [Fact]
        public void BareE_Dash_LeavesTheRerunForTheEmittedCall()
        {
            var r = CmdResult.Run(fixture.AcquireFresh(),
                "Invoke-BashHistory -c; Invoke-BashHistory -s 'echo one'; Invoke-BashFc -e - one=two 2>$null; $global:__BashFcRerun").AssertSuccess();
            Assert.Equal("echo two", r.Lines.Last());
        }
    }
}
