using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// awk leftovers, all against gawk 5.2.1 (WSL Ubuntu-24.04): a dynamically named <c>getline &lt; name</c> that
/// evaluates to "-" in a streaming stdin run, <c>close()</c> of a command that was not fully read (141 once
/// it would have died of SIGPIPE), the 1-vs-2 exit codes of static vs runtime errors, division by zero,
/// the "called with more arguments than declared" warning and indirect calls <c>@name()</c>.
/// </summary>
public class AwkLeftoversTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public AwkLeftoversTests(SharedPwshFixture fixture) { _fixture = fixture; }

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    private CmdResult AwkR(string program) => Run("Invoke-BashAwk " + Q(program));

    private string[] Awk(string program) =>
        AwkR(program).Lines.Select(l => l.TrimEnd('\n', '\r')).ToArray();

    private string[] AwkStdin(string input, string program) =>
        Run(string.Join(",", input.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Q)) +
            " | Invoke-BashAwk " + Q(program)).Lines.Select(l => l.TrimEnd('\n', '\r')).ToArray();

    // ── dynamic getline < "-" ────────────────────────────────────────────────

    [Fact]
    public void Getline_DynamicNameDash_ReadsTheStreamingStdin() =>
        // gawk: `printf 'a\nb\n' | gawk 'BEGIN{ f="-"; while ((getline l < f) > 0) print "got " l }'`
        Assert.Equal(new[] { "got a", "got b" },
            AwkStdin("a\nb\n", "BEGIN{ f=\"-\"; while ((getline l < f) > 0) print \"got \" l }"));

    [Fact]
    public void Getline_DynamicNameDevStdin_ReadsTheStreamingStdin() =>
        Assert.Equal(new[] { "got x" },
            AwkStdin("x\n", "BEGIN{ f=\"/dev/\" \"stdin\"; getline l < f; print \"got \" l }"));

    // ── close() of a command that was not fully read ─────────────────────────

    [Fact]
    public void Close_CommandWithUnreadOutputBeyondThePipeBuffer_Returns141()
    {
        // gawk: `"seq 100000" | getline x; close(...)` -> 141 (the command would have died of SIGPIPE).
        Assert.Equal(new[] { "1", "r=141" },
            Awk("BEGIN{ c = \"seq 100000\"; c | getline x; print x; print \"r=\" close(c) }"));
    }

    [Fact]
    public void Close_NeverEndingCommand_Returns141()
    {
        Assert.Equal(new[] { "y", "r=141" },
            Awk("BEGIN{ \"yes\" | getline x; print x; print \"r=\" close(\"yes\") }"));
    }

    [Fact]
    public void Close_SmallCommandWithUnreadOutput_ReturnsItsOwnExitStatus()
    {
        // the unread line fits the pipe buffer: the command finishes normally, so its own status comes back.
        Assert.Equal(new[] { "a", "3" },
            Awk("BEGIN{ c = \"echo a; echo b; exit 3\"; c | getline x; print x; print close(c) }"));
    }

    // ── error exit codes ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("BEGIN{ print 1", 1)]                       // syntax error
    [InlineData("BEGIN{ x = }", 1)]
    [InlineData("BEGIN{ print 1/0 }", 1)]                   // constant divisor: static `error:`
    [InlineData("BEGIN{ x=5; print x%0 }", 1)]
    [InlineData("BEGIN{ x=0; print 1/x }", 2)]              // runtime `fatal:`
    [InlineData("BEGIN{ x=5; x/=0 }", 2)]
    [InlineData("BEGIN{ nofunc(1) }", 2)]
    public void Errors_ExitLikeGawk(string program, int exit) =>
        AwkR(program).AssertFailed(exit);

    [Fact]
    public void DivisionByZero_Messages_AreGawks()
    {
        AwkR("BEGIN{ x=0; print 1/x }").AssertFailed(2, "division by zero attempted");
        AwkR("BEGIN{ x=0; print 1%x }").AssertFailed(2, "division by zero attempted in `%'");
        AwkR("BEGIN{ x=5; x/=0 }").AssertFailed(2, "division by zero attempted in `/='");
        AwkR("BEGIN{ x=5; x%=0 }").AssertFailed(2, "division by zero attempted in `%='");
    }

    // ── "called with more arguments than declared" ───────────────────────────

    [Fact]
    public void Function_CalledWithTooManyArguments_WarnsOnStderrEveryCallAndStillRuns()
    {
        var r = AwkR("function f(a) { print \"in f\" } BEGIN { f(1,2,3); f(1,2) }");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(new[] { "in f", "in f" }, r.Lines.Select(l => l.TrimEnd('\n', '\r')).ToArray());
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            r.Stderr, "warning: function `f' called with more arguments than declared").Count);
    }

    // ── indirect calls ───────────────────────────────────────────────────────

    [Fact]
    public void IndirectCall_ThroughAVariable_CallsTheNamedFunction() =>
        Assert.Equal(new[] { "8" }, Awk("function g(x){ return x*2 } BEGIN { n=\"g\"; print @n(4) }"));

    [Fact]
    public void IndirectCall_AsAStatement_RunsTheFunction() =>
        Assert.Equal(new[] { "f7" }, Awk("function f(a){ print \"f\" a } BEGIN { fn = \"f\"; @fn(7) }"));

    [Fact]
    public void IndirectCall_WithTooManyArguments_WarnsAndReturns()
    {
        var r = AwkR("function f(a){ return a+1 } BEGIN{ x=\"f\"; print @x(1,2) }");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(new[] { "2" }, r.Lines.Select(l => l.TrimEnd('\n', '\r')).ToArray());
        Assert.Contains("called with more arguments than declared", r.Stderr);
    }

    [Fact]
    public void IndirectCall_ThroughAFunctionParameter_Works() =>
        Assert.Equal(new[] { "11" },
            Awk("function inc(v){ return v+1 } function apply(fn, v){ return @fn(v) } BEGIN{ print apply(\"inc\", 10) }"));

    [Fact]
    public void IndirectCall_ToAnUnknownFunction_IsGawksFatal()
    {
        AwkR("BEGIN{ n=\"nosuch\"; @n() }").AssertFailed(2, "`nosuch' is not a function, so it cannot be called indirectly");
        AwkR("BEGIN{ n=\"\"; @n() }").AssertFailed(2, "is not a function, so it cannot be called indirectly");
    }

    [Fact]
    public void IndirectCall_NamingTheFunctionDirectly_IsGawksStaticError() =>
        // gawk: `@f(1)` where f is a function is "function `f' called with space between name and `(', or used as a variable"
        AwkR("function f(a){ return a+1 } BEGIN{ print @f(1) }").AssertFailed(1, "used as a variable or an array");
}
