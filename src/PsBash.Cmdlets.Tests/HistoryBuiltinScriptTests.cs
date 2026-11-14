using PsBash.Cmdlets;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>Script-mode <c>history</c> (bash 5.2 non-interactive, checked against the oracle).</summary>
public class HistoryBuiltinScriptTests
{
    private static InvokeBashHistoryCommand.Result Run(List<string> list, params string[] args) =>
        InvokeBashHistoryCommand.Run(args, list, histFile: null, _ => null, (_, _, _) => { });

    [Theory]
    [InlineData(0, new string[0])]
    [InlineData(0, new[] { "+5" })]          // bash takes a signed number
    [InlineData(0, new[] { "0" })]
    [InlineData(2, new[] { "-5" })]          // invalid option + usage
    [InlineData(1, new[] { "f" })]           // numeric argument required
    [InlineData(1, new[] { "not-a-number" })]
    [InlineData(1, new[] { "too", "many", "args" })] // the first operand is checked first
    [InlineData(1, new[] { "3", "too-many" })]       // too many arguments
    public void Status_MatchesBash(int status, string[] args)
    {
        var r = Run(new List<string>(), args);
        Assert.Equal(status, r.Status);
        Assert.Empty(r.Out);
    }

    [Fact]
    public void Messages_MatchBash()
    {
        Assert.Equal(["history: -5: invalid option", InvokeBashHistoryCommand.Usage], Run([], "-5").Err);
        Assert.Equal(["history: f: numeric argument required"], Run([], "f").Err);
        Assert.Equal(["history: too many arguments"], Run([], "3", "x").Err);
    }

    [Fact]
    public void StoreListDeleteClear_RoundTrip()
    {
        var list = new List<string>();
        Assert.Equal(0, Run(list, "-s", "foo", "bar").Status);
        Run(list, "-s", "baz");
        Assert.Equal(["    1  foo bar", "    2  baz"], Run(list).Out);
        Assert.Equal(["    2  baz"], Run(list, "1").Out);
        Assert.Equal(0, Run(list, "-d", "1").Status);
        Assert.Equal(["    1  baz"], Run(list).Out);
        Assert.Equal(1, Run(list, "-d", "5").Status);
        Assert.Equal(0, Run(list, "-c").Status);
        Assert.Empty(Run(list).Out);
    }

    [Fact]
    public void Print_EchoesEachArg() => Assert.Equal(["a", "b"], Run([], "-p", "a", "b").Out);

    // Pester calls cmdlets directly, where a bare -c / -d / -p / -w would bind common parameters (decoys catch them).
    public class Direct(SharedPwshFixture fixture) : IClassFixture<SharedPwshFixture>
    {
        [Fact]
        public void CollidingFlags_BindToTheDecoys()
        {
            var pwsh = fixture.AcquireFresh();
            var r = CmdResult.Run(pwsh,
                "Invoke-BashHistory -c; Invoke-BashHistory -s one; Invoke-BashHistory -s two; Invoke-BashHistory -d 1; " +
                "$a = (Invoke-BashHistory) -join ','; $p = (Invoke-BashHistory -p x y) -join ','; " +
                "Invoke-BashHistory -c; $c = @(Invoke-BashHistory).Count; \"$a|$p|$c\"").AssertSuccess();
            Assert.Equal("    1  two|x,y|0", r.Lines.Last());
        }

        [Fact]
        public void InvalidOption_IsStatus2_WithUsage()
            => CmdResult.Run(fixture.AcquireFresh(), "Invoke-BashHistory -5")
                .AssertFailed(2, "history: -5: invalid option", "history: usage:");
    }

    [Fact]
    public void Write_HandsTheListToTheFile()
    {
        string? file = null; List<string>? written = null;
        var r = InvokeBashHistoryCommand.Run(["-w", "h.txt"], ["x"], null, _ => null,
            (f, lines, _) => { file = f; written = lines.ToList(); });
        Assert.Equal(0, r.Status);
        Assert.Equal("h.txt", file);
        Assert.Equal(["x"], written);
    }

    // bash -i: `history -c; HISTFILE=tmp; echo 1; history -a` appends the three lines after the clear; a second
    // -a appends only what came after the first; -r reads the file and leaves nothing new to append.
    [Fact]
    public void Append_WritesOnlyLinesSinceTheLastSave()
    {
        var list = new List<string> { "old 1", "old 2" };
        var mark = new InvokeBashHistoryCommand.HistoryMark();
        var appended = new List<List<string>>();
        InvokeBashHistoryCommand.Result Hist(params string[] args) => InvokeBashHistoryCommand.Run(args, list, "tmp",
            _ => ["from file"], (_, lines, append) => { Assert.True(append || args[0] == "-w"); appended.Add(lines.ToList()); }, mark);

        list.Clear(); Hist("-c");
        list.AddRange(["HISTFILE=tmp", "echo 1", "history -a"]);
        Assert.Equal(0, Hist("-a").Status);
        list.AddRange(["echo 2", "history -a"]);
        Hist("-a");
        list.Add("history -r");
        Hist("-r");
        Hist("-a");
        Assert.Equal([["HISTFILE=tmp", "echo 1", "history -a"], ["echo 2", "history -a"], []], appended);
        Assert.Equal("from file", list[^1]);
    }

    // bash 5.2 with HISTFILE and HOME unset: -w is a silent no-op, -a (with lines to append), -r and -n fail silently.
    [Theory]
    [InlineData("-w", 0)]
    [InlineData("-a", 1)]
    [InlineData("-r", 1)]
    [InlineData("-n", 1)]
    public void NoHistoryFile_StatusLikeBash(string op, int status)
    {
        var r = InvokeBashHistoryCommand.Run([op], ["unset HISTFILE", op], null, _ => throw new InvalidOperationException(),
            (_, _, _) => throw new InvalidOperationException());
        Assert.Equal(status, r.Status);
        Assert.Empty(r.Err);
    }

    [Fact]
    public void NoHistFile_UsesTheDefaultFile_AndAWriteErrorIsStatusOne()
    {
        string? file = null;
        var r = InvokeBashHistoryCommand.Run(["-a"], ["x"], null, _ => null,
            (f, _, _) => { file = f; throw new DirectoryNotFoundException(); }, defaultFile: "/nonexistent/.history");
        Assert.Equal("/nonexistent/.history", file);
        Assert.Equal(1, r.Status);
        Assert.Equal(["history: /nonexistent/.history: cannot create: No such file or directory"], r.Err);
    }
}
