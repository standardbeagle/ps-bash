using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The non-interactive <c>history</c> and <c>fc</c> builtins. Every expected value, message and status is bash
/// 5.2's in a script (WSL Ubuntu-24.04), where the list starts empty and only <c>history -s</c> / <c>-r</c> fill it.
/// The pure rules are tested through <see cref="BashHistoryBuiltins"/> with an in-memory IO seam; the cmdlet tests
/// cover the decoy flags a direct PowerShell call binds.
/// </summary>
public class BashHistoryTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    public BashHistoryTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private sealed class Sink
    {
        public readonly List<string> Out = new();
        public readonly List<string> Err = new();
        public readonly Dictionary<string, List<string>> Files = new();
        public BashHistoryBuiltins.Io Io(string? histFile = null) => new(
            Out.Add, Err.Add,
            f => Files.TryGetValue(f, out var l) ? l : null,
            (f, lines, append) =>
            {
                if (!append || !Files.ContainsKey(f)) Files[f] = new List<string>();
                Files[f].AddRange(lines);
                return true;
            },
            histFile);
    }

    private static BashHistoryList Hist(params string[] entries)
    {
        var h = new BashHistoryList();
        h.AddRange(entries);
        return h;
    }

    private static (int Status, Sink S) History(BashHistoryList h, params string[] args)
    {
        var s = new Sink();
        return (BashHistoryBuiltins.History(h, args, s.Io()), s);
    }

    private static (int Status, Sink S, string? Exec) Fc(BashHistoryList h, params string[] args)
    {
        var s = new Sink();
        int st = BashHistoryBuiltins.Fc(h, args, s.Io(), out var exec);
        return (st, s, exec);
    }

    // ── history ──────────────────────────────────────────────────────────────

    [Fact]
    public void History_Empty_PrintsNothing()
    {
        var (st, s) = History(Hist());
        Assert.Equal(0, st);
        Assert.Empty(s.Out);
    }

    [Fact]
    public void History_S_AddsArgsAsOneEntry_ListedWithFiveWideNumber()
    {
        var h = Hist();
        Assert.Equal(0, History(h, "-s", "foo", "bar").Status);
        Assert.Equal(new[] { "    1  foo bar" }, History(h).S.Out);
    }

    [Fact]
    public void History_N_ListsLastN() =>
        Assert.Equal(new[] { "    2  b" }, History(Hist("a", "b"), "1").S.Out);

    [Fact]
    public void History_NonNumeric_IsNumericArgumentRequired()
    {
        var (st, s) = History(Hist("a"), "f");
        Assert.Equal(1, st);
        Assert.Equal(new[] { "history: f: numeric argument required" }, s.Err);
    }

    [Fact]
    public void History_TwoOperands_IsTooManyArguments()
    {
        var (st, s) = History(Hist("a"), "3", "x");
        Assert.Equal(1, st);
        Assert.Equal(new[] { "history: too many arguments" }, s.Err);
    }

    [Fact]
    public void History_NegativeNumber_IsInvalidOption_Status2()
    {
        var (st, s) = History(Hist("a"), "-5");
        Assert.Equal(2, st);
        Assert.Equal(new[] { "history: -5: invalid option", BashHistoryBuiltins.HistoryUsage }, s.Err);
    }

    [Fact]
    public void History_DWithoutArgument_Status2() =>
        Assert.Equal(2, History(Hist("a"), "-d").Status);

    [Theory]
    [InlineData("1", new[] { "b", "c" })]
    [InlineData("-1", new[] { "a", "b" })]
    [InlineData("1-2", new[] { "c" })]
    public void History_D_DeletesPositionOrRange(string arg, string[] left)
    {
        var h = Hist("a", "b", "c");
        Assert.Equal(0, History(h, "-d", arg).Status);
        Assert.Equal(left, h.ToArray());
    }

    [Theory]
    [InlineData("99")]
    [InlineData("x")]
    public void History_D_OutOfRange(string arg)
    {
        var (st, s) = History(Hist("a"), "-d", arg);
        Assert.Equal(1, st);
        Assert.Equal(new[] { $"history: {arg}: history position out of range" }, s.Err);
    }

    [Fact]
    public void History_C_Clears()
    {
        var h = Hist("a", "b");
        Assert.Equal(0, History(h, "-c").Status);
        Assert.Empty(h);
    }

    [Fact]
    public void History_P_ExpandsEventDesignators()
    {
        var (st, s) = History(Hist("echo one", "ls"), "-p", "a", "!!", "!1", "!-2", "!ec");
        Assert.Equal(0, st);
        Assert.Equal(new[] { "a", "ls", "echo one", "echo one", "echo one" }, s.Out);
    }

    [Fact]
    public void History_P_UnknownEvent_Fails()
    {
        var (st, s) = History(Hist("a"), "-p", "!zz");
        Assert.Equal(1, st);
        Assert.Equal(new[] { "history: !zz: history expansion failed" }, s.Err);
    }

    [Fact]
    public void History_WriteThenRead_RoundTripsThroughHistFile()
    {
        var h = Hist("echo one", "echo two");
        var s = new Sink();
        Assert.Equal(0, BashHistoryBuiltins.History(h, new[] { "-w" }, s.Io("hf")));
        Assert.Equal(new[] { "echo one", "echo two" }, s.Files["hf"]);
        BashHistoryBuiltins.History(h, new[] { "-r" }, s.Io("hf"));
        Assert.Equal(4, h.Count);
    }

    [Fact]
    public void History_R_MissingFile_Status1() =>
        Assert.Equal(1, BashHistoryBuiltins.History(Hist(), new[] { "-r", "nofile" }, new Sink().Io()));

    // ── fc ───────────────────────────────────────────────────────────────────

    private static BashHistoryList Five() => Hist("echo 1", "echo 2", "echo 3", "echo 4", "echo 5");

    [Fact]
    public void Fc_L_Empty_PrintsNothing() => Assert.Equal(0, Fc(Hist(), "-l").Status);

    [Fact]
    public void Fc_S_Empty_NoCommandFound()
    {
        var (st, s, _) = Fc(Hist(), "-s");
        Assert.Equal(1, st);
        Assert.Equal(new[] { "fc: no command found" }, s.Err);
    }

    [Fact]
    public void Fc_InvalidOption_Status2()
    {
        var (st, s, _) = Fc(Hist(), "-z");
        Assert.Equal(2, st);
        Assert.Equal(new[] { "fc: -z: invalid option", BashHistoryBuiltins.FcUsage }, s.Err);
    }

    [Fact]
    public void Fc_E_MissingArgument_Status2() => Assert.Equal(2, Fc(Hist(), "-e").Status);

    // bash 5.2 fc_gethnum: a positive number at or past the newest entry's is out of range (oldest as `first`,
    // newest as `last`); 0 is the newest; text is the newest entry with that prefix.
    [Theory]
    [InlineData(new string[0], "1 2 3 4 5")]
    [InlineData(new[] { "3", "2" }, "3 2")]
    [InlineData(new[] { "2", "4" }, "2 3 4")]
    [InlineData(new[] { "4", "2" }, "4 3 2")]
    [InlineData(new[] { "5", "1" }, "1")]
    [InlineData(new[] { "-1" }, "5")]
    [InlineData(new[] { "-3", "-1" }, "3 4 5")]
    [InlineData(new[] { "-1", "-3" }, "5 4 3")]
    [InlineData(new[] { "0" }, "5")]
    [InlineData(new[] { "0", "2" }, "5 4 3 2")]
    [InlineData(new[] { "9" }, "1 2 3 4 5")]
    [InlineData(new[] { "1", "9" }, "1 2 3 4 5")]
    [InlineData(new[] { "e" }, "5")]
    [InlineData(new[] { "echo", "2" }, "5 4 3 2")]
    public void Fc_L_RangesMatchBash(string[] spec, string numbers)
    {
        var (st, s, _) = Fc(Five(), new[] { "-l" }.Concat(spec).ToArray());
        Assert.Equal(0, st);
        Assert.Equal(numbers, string.Join(" ", s.Out.Select(l => l[..l.IndexOf('\t')])));
    }

    [Fact]
    public void Fc_L_LineFormat_NumberTabSpace()
    {
        Assert.Equal("1\t echo 1", Fc(Five(), "-l", "1", "1").S.Out.Single());
        Assert.Equal("\t echo 1", Fc(Five(), "-ln", "1", "1").S.Out.Single());
    }

    [Fact]
    public void Fc_Lr_Reverses() =>
        Assert.Equal(new[] { "4\t echo 4", "3\t echo 3", "2\t echo 2" }, Fc(Five(), "-lr", "2", "4").S.Out);

    [Fact]
    public void Fc_L_NoMatch_NoCommandFound()
    {
        var (st, s, _) = Fc(Five(), "-l", "zz");
        Assert.Equal(1, st);
        Assert.Equal(new[] { "fc: no command found" }, s.Err);
    }

    [Fact]
    public void Fc_S_SubstitutesGlobally_EchoesAndReplacesNewest()
    {
        var h = Hist("echo a a", "ls");
        var (st, s, exec) = Fc(h, "-s", "a=b", "1");
        Assert.Equal(0, st);
        Assert.Equal("echo b b", exec);
        Assert.Equal(new[] { "echo b b" }, s.Err);
        Assert.Equal(new[] { "echo a a", "echo b b" }, h.ToArray());
    }

    [Theory]
    [InlineData("2", "echo 2")]
    [InlineData("9", "echo 5")]
    [InlineData("-2", "echo 4")]
    [InlineData("echo 3", "echo 3")]
    public void Fc_S_Spec(string spec, string expected) => Assert.Equal(expected, Fc(Five(), "-s", spec).Exec);

    [Fact]
    public void Fc_EDash_IsS() => Assert.Equal("echo 1", Fc(Five(), "-e", "-", "1").Exec);

    [Fact]
    public void Fc_MinusZero_WithS_NoCommandFound() => Assert.Equal(1, Fc(Five(), "-s", "-0").Status);

    [Fact]
    public void Fc_WithoutLOrS_RefusesTheEditor()
    {
        var (st, s, exec) = Fc(Five());
        Assert.Equal(1, st);
        Assert.Null(exec);
        Assert.Contains("not supported", s.Err.Single());
    }

    // ── cmdlets (direct PowerShell calls bind the decoy switches) ─────────────

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    [Fact]
    public void Cmdlet_DirectCall_DecoyFlagsReachTheBuiltin()
    {
        var r = Run("$global:BashHistory = $null; Invoke-BashHistory -s 'echo one'; Invoke-BashHistory -s 'echo two'; " +
                    "Invoke-BashHistory -d 1; Invoke-BashHistory -p '!!'; Invoke-BashHistory");
        r.AssertSuccess();
        Assert.Equal(new[] { "echo two", "    1  echo two" }, r.Lines);
    }

    [Fact]
    public void Cmdlet_C_Clears() =>
        Assert.Empty(Run("$global:BashHistory = $null; Invoke-BashHistory -s x; Invoke-BashHistory -c; Invoke-BashHistory").AssertSuccess().Lines);

    [Fact]
    public void Cmdlet_Fc_L() =>
        Assert.Equal(new[] { "1\t a", "2\t b" },
            Run("$global:BashHistory = $null; Invoke-BashHistory -s a; Invoke-BashHistory -s b; Invoke-BashFc -l").AssertSuccess().Lines);

    [Fact]
    public void Cmdlet_Fc_EDash_RunsTheCommand()
    {
        var r = Run("$global:BashHistory = $null; Invoke-BashHistory -s 'echo hi'; Invoke-BashFc -e - hi=yo");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(new[] { "yo" }, r.Lines);
    }

    [Fact]
    public void Cmdlet_InvalidOption_Status2() =>
        Run("Invoke-BashHistory -x").AssertFailed(2, "history: -x: invalid option");
}
