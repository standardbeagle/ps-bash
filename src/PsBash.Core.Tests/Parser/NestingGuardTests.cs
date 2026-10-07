using PsBash.Core.Parser;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// No input may overflow the stack: a .NET StackOverflow kills the process, and the transpiler runs
/// inside the shared host (eval/source/bash -c) and the runtime arithmetic evaluator. Each deep shape
/// below overflowed (process exit 0xC00000FD) before NestingGuard; now each is a clean parse error.
/// The end-to-end proof that the HOST survives is Shell.Tests HostRobustnessTests (spawn-based, so a
/// regression there fails an assertion instead of killing the test run, as these would).
/// </summary>
public class NestingGuardTests
{
    private const int Deep = 5000;

    private static string Rep(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    public static IEnumerable<object[]> DeepInputs() => new[]
    {
        new object[] { "cmdsub", "echo " + Rep("$(echo ", Deep) + "x" + Rep(")", Deep) },
        new object[] { "arith-paren", "echo $((" + Rep("(", Deep) + "1" + Rep(")", Deep) + "))" },
        new object[] { "param", "echo " + Rep("${x:-", Deep) + "d" + Rep("}", Deep) },
        new object[] { "if", Rep("if true; then ", Deep) + "echo deep; " + Rep("fi; ", Deep) },
        new object[] { "while", Rep("while false; do ", Deep) + "echo w; " + Rep("done; ", Deep) },
        new object[] { "subshell", Rep("( ", Deep) + "echo s" + Rep(" )", Deep) },
        new object[] { "brace-group", Rep("{ ", Deep) + "echo b; " + Rep("}; ", Deep) },
        new object[] { "quoted-cmdsub", "echo " + Rep("\"$(echo ", Deep) + "x" + Rep(")\"", Deep) },
    };

    [Theory]
    [MemberData(nameof(DeepInputs))]
    public void Transpile_DeepNesting_ThrowsParseException_NotStackOverflow(string shape, string bash)
    {
        var ex = Assert.Throws<ParseException>(() => BashTranspiler.Transpile(bash));
        Assert.Contains(NestingGuard.TooDeepMessage, ex.Message);
        _ = shape;
    }

    [Theory]
    [MemberData(nameof(DeepInputs))]
    public void Parse_DeepNesting_OnSmallStackThread_ThrowsParseException(string shape, string bash)
    {
        // BashParser.Parse is called directly (interactive shell, compact routing), not through the
        // large-stack transpile entry: the stack probe must stop it on a small (256 KB) stack too.
        Exception? caught = null;
        var t = new Thread(() =>
        {
            try { BashParser.Parse(bash); }
            catch (Exception e) { caught = e; }
        }, 256 * 1024);
        t.Start();
        t.Join();
        Assert.IsType<ParseException>(caught);
        _ = shape;
    }

    [Theory]
    [InlineData("paren")]
    [InlineData("unary")]
    [InlineData("power")]
    [InlineData("assign")]
    public void ArithmeticParse_DeepExpression_ThrowsArithmeticParseException(string shape)
    {
        // Runtime arithmetic ($(( $expr )) with an expanded value) parses inside the host's pipeline
        // thread: every self-recursion of the arithmetic parser is bounded.
        string expr = shape switch
        {
            "paren" => Rep("(", Deep) + "1" + Rep(")", Deep),
            "unary" => Rep("- ", Deep) + "1",
            "power" => Rep("2**", Deep) + "1",
            _ => Rep("a=", Deep) + "1",
        };
        var ex = Assert.Throws<BashArithmeticParseException>(() => BashArithmeticParser.Parse(expr));
        Assert.Contains(NestingGuard.TooDeepMessage, ex.Message);
    }

    [Fact]
    public void Transpile_RealisticNesting_IsNotRejected()
    {
        // The limit must never bite real scripts: 100 levels of each kind transpile.
        foreach (var bash in new[]
                 {
                     "echo " + Rep("$(echo ", 100) + "x" + Rep(")", 100),
                     Rep("if true; then ", 100) + "echo deep; " + Rep("fi; ", 100),
                     "echo " + Rep("${x:-", 100) + "d" + Rep("}", 100),
                     "echo $((" + Rep("(", 100) + "1" + Rep(")", 100) + "))",
                 })
        {
            Assert.False(string.IsNullOrEmpty(BashTranspiler.Transpile(bash)));
        }
    }

    [Fact]
    public void Transpile_AcceptedDepth_OnSmallStackThread_SucceedsViaLargeStackRetry()
    {
        // 900 levels is under the cap but more than a 256 KB stack holds: the inline attempt runs out
        // of stack (a clean probe failure, never an overflow) and the retry on a large-stack worker
        // completes it. The inline fast path is what keeps an STA caller from paying a thread hop.
        string bash = Rep("if true; then ", 900) + "echo deep; " + Rep("fi; ", 900);
        string? ps = null;
        Exception? caught = null;
        var t = new Thread(() =>
        {
            try { ps = BashTranspiler.Transpile(bash); }
            catch (Exception e) { caught = e; }
        }, 256 * 1024);
        t.Start();
        t.Join();
        Assert.Null(caught);
        Assert.Contains("deep", ps);
    }

    [Fact]
    public void Guard_DepthIsRestoredAfterFailure()
    {
        // A rejected deep input must not leak depth on the thread: a normal transpile right after works.
        string deep = Rep("if true; then ", Deep) + "echo d; " + Rep("fi; ", Deep);
        string fine = Rep("if true; then ", 50) + "echo d; " + Rep("fi; ", 50);
        Assert.Throws<ParseException>(() => BashParser.Parse(deep));
        Assert.NotNull(BashParser.Parse(fine));
        Assert.Throws<ParseException>(() => BashParser.Parse(deep));
        Assert.NotNull(BashParser.Parse(fine));
    }

    // Brace expansion runs at transpile time (launcher, or inside the host for eval/source). An
    // unbounded product (2^30 words) exhausted memory; an int range counter at int.MaxValue wrapped
    // and looped forever. Both are bounded now.
    [Fact]
    public void Transpile_HugeBraceProduct_ThrowsParseException()
    {
        var ex = Assert.Throws<ParseException>(() => BashTranspiler.Transpile("echo " + Rep("{a,b}", 30)));
        Assert.Contains("brace expansion too large", ex.Message);
    }

    [Fact]
    public void Transpile_HugeBraceRange_ThrowsParseException()
    {
        var ex = Assert.Throws<ParseException>(() => BashTranspiler.Transpile("echo {1..2000000}"));
        Assert.Contains("brace expansion too large", ex.Message);
    }

    [Fact]
    public void Transpile_BraceRangeEndingAtIntMax_Terminates()
    {
        // 48 words; the old int counter overflowed past End and never stopped.
        var ps = BashTranspiler.Transpile("echo {2147483600..2147483647}");
        Assert.Contains("2147483647", ps);
    }

    [Fact]
    public void Transpile_OrdinaryBraceExpansion_Unaffected()
    {
        Assert.Contains("1..1000", BashTranspiler.Transpile("echo {1..1000}"));
        Assert.Contains("'a1'", BashTranspiler.Transpile("echo {a,b}{1,2}"));
    }

    // Breadth: a long FLAT chain is emitted as a PowerShell operator chain that PowerShell parses
    // left-deep and compiles recursively — [ a -o a … ] (~48k), $((1+1+…)), [[ … && … ]] and a 2000-stage
    // pipeline each overflowed PowerShell's pipeline thread and killed the host.
    public static IEnumerable<object[]> TooLongChains() => new[]
    {
        new object[] { "test -o", "[ " + Rep("a -o ", 5000) + "a ]" },
        new object[] { "[[ &&", "[[ " + Rep("a && ", 5000) + "a ]]" },
        new object[] { "and-or list", Rep("true && ", 5000) + "true" },
        new object[] { "pipeline", "echo x | " + Rep("cat | ", 300) + "cat" },
        new object[] { "arith chain", "echo $((" + Rep("1+", 5000) + "1))" },
    };

    [Theory]
    [MemberData(nameof(TooLongChains))]
    public void Transpile_TooLongFlatChain_ThrowsParseException(string shape, string bash)
    {
        var ex = Assert.Throws<ParseException>(() => BashTranspiler.Transpile(bash));
        Assert.Contains(NestingGuard.TooLongMessage, ex.Message);
        _ = shape;
    }

    [Fact]
    public void Transpile_ChainsAtTheirCaps_StillTranspile()
    {
        int n = NestingGuard.MaxChainLength;
        Assert.NotNull(BashTranspiler.Transpile("[ " + Rep("a -o ", n - 1) + "a ]"));
        Assert.NotNull(BashTranspiler.Transpile(Rep("true && ", n) + "true"));
        Assert.NotNull(BashTranspiler.Transpile("echo $((" + Rep("1+", n) + "1))"));
        Assert.NotNull(BashTranspiler.Transpile("echo x" + Rep(" | cat", NestingGuard.MaxPipelineStages - 1)));
    }

    [Fact]
    public void OnLargeStack_PropagatesExceptionAndCulture()
    {
        var prior = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var name = NestingGuard.OnLargeStack(() => Thread.CurrentThread.CurrentCulture.Name);
            Assert.Equal("de-DE", name);
            var ex = Assert.Throws<InvalidOperationException>(
                () => NestingGuard.OnLargeStack<int>(() => throw new InvalidOperationException("boom")));
            Assert.Equal("boom", ex.Message);
        }
        finally { Thread.CurrentThread.CurrentCulture = prior; }
    }
}
