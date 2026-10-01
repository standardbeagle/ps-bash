using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// awk output redirection: <c>print/printf &gt; file</c>, <c>&gt;&gt; file</c>, <c>| "cmd"</c>, plus
/// <c>close()</c> / <c>fflush()</c> of those names. Expected values are gawk 5.2.1's (WSL
/// Ubuntu-24.04) with stdout a pipe — including its block-buffered stdout (output printed while an
/// output pipe is open appears AFTER the command's output when the program ends). Byte-level bash
/// parity of the idioms lives in AwkRedirectDifferentialTests. A program refers to the per-test
/// scratch directory as <c>@D@</c> (forward slashes: an awk string is data, so no path mapping).
/// </summary>
public class AwkRedirectTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;
    private readonly string _d; // forward-slash form of _tmpDir

    public AwkRedirectTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(Path.GetTempPath(), "psbash-awkrd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _d = _tmpDir.Replace('\\', '/');
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    private string Prog(string program) => program.Replace("@D@", _d);

    private string Mk(string name, string content)
    {
        var p = Path.Combine(_tmpDir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private string Read(string name) => File.ReadAllText(Path.Combine(_tmpDir, name));

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    private CmdResult AwkR(string program, params string[] files) =>
        Run("Invoke-BashAwk " + Q(Prog(program)) + " " + string.Join(" ", files.Select(Q)));

    private string[] Awk(string program, params string[] files) =>
        AwkR(program, files).Lines.Select(l => l.TrimEnd('\n', '\r')).ToArray();

    private string[] AwkStdin(string input, string program) =>
        Run(string.Join(",", input.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Q)) +
            " | Invoke-BashAwk " + Q(Prog(program))).Lines.Select(l => l.TrimEnd('\n', '\r')).ToArray();

    // ── > and >> ─────────────────────────────────────────────────────────────

    [Fact]
    public void Print_Redirect_TruncatesOnFirstOpenThenAppendsWhileOpen()
    {
        Awk("BEGIN{ print \"a\" > \"@D@/o\"; print \"b\" > \"@D@/o\" }");
        Assert.Equal("a\nb\n", Read("o"));
    }

    [Fact]
    public void Print_Redirect_TruncatesPreExistingFile()
    {
        Mk("o", "old\n");
        Awk("BEGIN{ print \"new\" > \"@D@/o\" }");
        Assert.Equal("new\n", Read("o"));
    }

    [Fact]
    public void Print_Append_AddsToExistingFile()
    {
        Mk("o", "old\n");
        Awk("BEGIN{ print \"x\" >> \"@D@/o\"; print \"y\" >> \"@D@/o\" }");
        Assert.Equal("old\nx\ny\n", Read("o"));
    }

    [Fact]
    public void Print_ReopenedAfterClose_TruncatesAgain()
    {
        Awk("BEGIN{ f=\"@D@/o\"; print \"a\" > f; print \"b\" > f; close(f); print \"c\" > f }");
        Assert.Equal("c\n", Read("o"));
    }

    [Fact]
    public void Print_AppendAfterClose_KeepsEarlierContent()
    {
        Awk("BEGIN{ f=\"@D@/o\"; print \"a\" > f; close(f); print \"b\" >> f }");
        Assert.Equal("a\nb\n", Read("o"));
    }

    [Fact]
    public void Print_AppendThenTruncateOperator_SameOpenStreamKeepsAppending()
    {
        // The first operator decides how the name is opened; later prints to the open name just write.
        Mk("g", "old\n");
        Awk("BEGIN{ print \"1\" >> \"@D@/g\"; print \"2\" > \"@D@/g\" }");
        Assert.Equal("old\n1\n2\n", Read("g"));
    }

    [Fact]
    public void Printf_Redirect_WritesFormattedText()
    {
        Awk("BEGIN{ printf \"%s-%d\\n\", \"a\", 3 > \"@D@/pf\"; printf \"z\\n\" >> \"@D@/pf\" }");
        Assert.Equal("a-3\nz\n", Read("pf"));
    }

    [Fact]
    public void Printf_ParenthesizedArgs_WithRedirect()
    {
        Awk("BEGIN{ printf(\"%s|%s\\n\", \"a\", \"b\") > \"@D@/pf\" }");
        Assert.Equal("a|b\n", Read("pf"));
    }

    [Fact]
    public void Print_ParenthesizedArgList_WithRedirect()
    {
        Awk("BEGIN{ print(\"a\", \"b\") > \"@D@/pp\" }");
        Assert.Equal("a b\n", Read("pp"));
    }

    [Fact]
    public void Print_NoArgs_RedirectWritesRecord()
    {
        var f = Mk("in", "l1 l2\nm1\n");
        Awk("{ print > \"@D@/na\" }", f);
        Assert.Equal("l1 l2\nm1\n", Read("na"));
    }

    [Fact]
    public void Print_SplitIntoFilesByField_KeepsEachFileOpen()
    {
        var f = Mk("in", "a 1\nb 2\na 3\n");
        Awk("{ print $2 > (\"@D@/\" $1 \".txt\") }", f);
        Assert.Equal("1\n3\n", Read("a.txt"));
        Assert.Equal("2\n", Read("b.txt"));
    }

    [Fact]
    public void Print_ManyFilesInLoop()
    {
        Awk("BEGIN{ for (i = 0; i < 3; i++) print i > (\"@D@/f\" i) }");
        Assert.Equal("0\n", Read("f0"));
        Assert.Equal("1\n", Read("f1"));
        Assert.Equal("2\n", Read("f2"));
    }

    [Fact]
    public void Print_RedirectToDevNull_DiscardsOutput()
    {
        Assert.Equal(new[] { "ok" }, Awk("BEGIN{ print \"x\" > \"/dev/null\"; print \"ok\" }"));
    }

    [Fact]
    public void Print_RedirectedOutput_IsNotOnStdout()
    {
        Assert.Equal(new[] { "kept" }, Awk("BEGIN{ print \"hidden\" > \"@D@/o\"; print \"kept\" }"));
    }

    // ── precedence of the target ─────────────────────────────────────────────

    [Fact]
    public void Print_ArgsThenRedirect_CommaListIsNotComparison()
    {
        Awk("BEGIN{ print 1, 2 > \"@D@/p1\" }");
        Assert.Equal("1 2\n", Read("p1"));
    }

    [Fact]
    public void Print_RedirectTarget_IsAConcatenation()
    {
        // gawk: `print > "p2" 5` writes $0 to the file named "p25"; a variable target concatenates too.
        Awk("BEGIN{ x = \"@D@/p\"; print \"q\" > x \"3\"; print > \"@D@/p2\" 5 }");
        Assert.Equal("q\n", Read("p3"));
        Assert.Equal("\n", Read("p25"));
    }

    [Fact]
    public void Print_ParenthesizedComparison_IsAnExpression_NotARedirect()
    {
        Assert.Equal(new[] { "1", "0" }, Awk("BEGIN{ print (3 > 2); print (2 > 3) }"));
    }

    [Fact]
    public void Print_ComparisonInsideParenthesesWithTernary()
    {
        Assert.Equal(new[] { "B" }, Awk("BEGIN{ print (1 > 2) ? \"B2\" : \"B\" }"));
    }

    [Fact]
    public void Print_UnparenthesizedTernaryAfterRedirectTarget_IsASyntaxError()
    {
        // gawk: the target is a concatenation-level expression, so `? :` after it is a syntax error.
        Run("Invoke-BashAwk " + Q("BEGIN{ print 1 > 2 ? \"A\" : \"B\" }")).AssertFailed(2);
    }

    [Fact]
    public void Print_GreaterThanInsideCallArgs_StaysAComparison()
    {
        Assert.Equal(new[] { "1" }, Awk("BEGIN{ print length(\"ab\" > \"a\" ? \"xyz\" : \"q\") - 2 }"));
    }

    [Fact]
    public void Print_GreaterThanInIfCondition_StillCompares()
    {
        Assert.Equal(new[] { "gt" }, Awk("BEGIN{ if (2 > 1) print \"gt\" }"));
    }

    // ── special names ────────────────────────────────────────────────────────

    [Fact]
    public void DevStdout_JoinsStdoutInOrder()
    {
        Assert.Equal(new[] { "1", "2", "3" },
            Awk("BEGIN{ print \"1\"; print \"2\" > \"/dev/stdout\"; print \"3\" }"));
    }

    [Fact]
    public void Dash_Target_IsStdout_NotAFile()
    {
        Assert.Equal(new[] { "dash" }, Awk("BEGIN{ print \"dash\" > \"-\" }"));
        Assert.False(File.Exists("-"));
    }

    [Fact]
    public void DevStderr_IsNotStdout()
    {
        Assert.Equal(new[] { "o" }, Awk("BEGIN{ print \"e\" > \"/dev/stderr\"; print \"o\" }"));
    }

    [Fact]
    public void DevStdout_CloseReturnsZero()
    {
        Assert.Equal(new[] { "a", "0" }, Awk("BEGIN{ print \"a\" > \"/dev/stdout\"; print close(\"/dev/stdout\") }"));
    }

    // ── close / fflush ───────────────────────────────────────────────────────

    [Fact]
    public void Close_OfFile_ReturnsZeroThenMinusOne()
    {
        Assert.Equal(new[] { "0", "-1" },
            Awk("BEGIN{ print \"x\" > \"@D@/f\"; print close(\"@D@/f\"); print close(\"@D@/f\") }"));
    }

    [Fact]
    public void Close_PublishesBufferedWrites_ToGetline()
    {
        Assert.Equal(new[] { "r:1", "r:2" },
            AwkStdin("a\nb\n", "{ print NR > \"@D@/nr\" } END{ close(\"@D@/nr\"); while ((getline l < \"@D@/nr\") > 0) print \"r:\" l }"));
    }

    [Fact]
    public void Fflush_Named_ReturnsZeroWhenOpenMinusOneOtherwise_AndPublishesData()
    {
        Assert.Equal(new[] { "0", "-1", "0", "got[x]" },
            Awk("BEGIN{ f = \"@D@/ff\"; print \"x\" > f; print fflush(f); print fflush(\"@D@/zz\"); print fflush(); getline l < f; print \"got[\" l \"]\" }"));
    }

    [Fact]
    public void System_SeesRedirectedFileContent_WithoutClose()
    {
        // gawk flushes every output before running a command.
        var r = Awk("BEGIN{ print \"fromawk\" > \"@D@/sys.txt\"; r = system(\"cat '@D@/sys.txt'\"); print \"rc\", r }");
        Assert.Equal(new[] { "fromawk", "rc 0" }, r);
    }

    [Fact]
    public void GetlineCommand_SeesRedirectedFileContent_WithoutClose()
    {
        Assert.Equal(new[] { "fromawk" },
            Awk("BEGIN{ print \"fromawk\" > \"@D@/gc.txt\"; \"cat '@D@/gc.txt'\" | getline v; print v }"));
    }

    // ── output pipes ─────────────────────────────────────────────────────────

    [Fact]
    public void Pipe_Sort_OutputAppearsWhenProgramEnds_BeforeStdoutHeldSincePipeOpened()
    {
        // gawk (stdout a pipe): h is flushed when the pipe opens, f stays buffered and is flushed AFTER
        // the pipe's command has produced its output at exit.
        var lines = AwkStdin("c\nb\na\n", "BEGIN{ print \"h\" } { print | \"sort\" } END{ print \"f\" }");
        Assert.Equal(new[] { "h", "a", "b", "c", "f" }, lines);
    }

    [Fact]
    public void Pipe_Sort_CloseBeforeFooter_SameOrder()
    {
        var lines = AwkStdin("c\nb\na\n", "BEGIN{ print \"h\" } { print | \"sort\" } END{ close(\"sort\"); print \"f\" }");
        Assert.Equal(new[] { "h", "a", "b", "c", "f" }, lines);
    }

    [Fact]
    public void Pipe_Close_FlushesPendingStdoutFirst_ThenEmitsCommandOutput()
    {
        // gawk: close() writes out the stdout buffer before the command's output lands.
        Assert.Equal(new[] { "mid", "x", "end" },
            Awk("BEGIN{ print \"x\" | \"cat\"; print \"mid\"; close(\"cat\"); print \"end\" }"));
    }

    [Fact]
    public void Pipe_NumericSort_FieldsOfInput()
    {
        Assert.Equal(new[] { "1", "2", "3" },
            AwkStdin("3\n1\n2\n", "{ print | \"sort -n\" }"));
    }

    [Fact]
    public void Pipe_Printf_ToSort()
    {
        Assert.Equal(new[] { "a", "b" }, Awk("BEGIN{ printf \"b\\na\\n\" | \"sort\" }"));
    }

    [Fact]
    public void Pipe_CommaArgs_JoinWithOfs()
    {
        Assert.Equal(new[] { "b a" }, Awk("BEGIN{ print \"b\", \"a\" | \"cat\" }"));
    }

    [Fact]
    public void Pipe_CloseReturnsCommandExitStatus()
    {
        Assert.Equal(new[] { "3", "-1" },
            Awk("BEGIN{ c = \"cat >/dev/null; exit 3\"; print \"x\" | c; print close(c); print close(\"nope\") }"));
    }

    [Fact]
    public void Pipe_CloseReturnsPipelineExitStatus_OfTheLastCommand()
    {
        // grep -q: 0 when the input matches, 1 when it does not (no output either way).
        Assert.Equal(new[] { "0", "1" },
            Awk("BEGIN{ print \"x\" | \"grep -q x\"; print close(\"grep -q x\"); print \"y\" | \"grep -q x\"; print close(\"grep -q x\") }"));
    }

    [Fact]
    public void Pipe_Pipeline_RunsEveryStageOnTheData()
    {
        Assert.Equal(new[] { "      2 a", "      1 b" },
            Awk("BEGIN{ print \"a\" | \"sort | uniq -c | sort -rn\"; print \"b\" | \"sort | uniq -c | sort -rn\"; print \"a\" | \"sort | uniq -c | sort -rn\" }"));
    }

    [Theory]
    [InlineData("sort", true)]
    [InlineData("sort -n | uniq -c", true)]
    [InlineData("cat > '/tmp/a b;c'", true)]
    [InlineData("grep \"a;b\" | wc -l", true)]
    [InlineData("sort; echo done", false)]
    [InlineData("a && b", false)]
    [InlineData("a || b", false)]
    [InlineData("a &", false)]
    [InlineData("(sort)", false)]
    [InlineData("{ sort; }", false)]
    [InlineData("sort < f", false)]
    [InlineData("echo $(date)", false)]
    [InlineData("echo `date`", false)]
    [InlineData("sort\nuniq", false)]
    [InlineData("grep 'unterminated", false)]
    [InlineData("", false)]
    public void IsPlainPipeline_ClassifiesCommandShapes(string command, bool expected)
    {
        Assert.Equal(expected, AwkShell.IsPlainPipeline(command));
    }

    [Fact]
    public void Pipe_ReopenedAfterClose_RunsCommandAgainOnNewData()
    {
        Assert.Equal(new[] { "a", "b" },
            Awk("BEGIN{ print \"a\" | \"sort\"; close(\"sort\"); print \"b\" | \"sort\" }"));
    }

    [Fact]
    public void Pipe_CommandWritingToFile_RunsByClose()
    {
        Awk("BEGIN{ print \"zeta\" | \"sort > '@D@/sorted'\"; print \"alpha\" | \"sort > '@D@/sorted'\"; close(\"sort > '@D@/sorted'\") }");
        Assert.Equal("alpha\nzeta\n", Read("sorted"));
    }

    [Fact]
    public void Pipe_CommandOutputKeepsBytesOfInput()
    {
        Assert.Equal(new[] { "héllo" }, Awk("BEGIN{ print \"héllo\" | \"cat\" }"));
    }

    // ── errors ───────────────────────────────────────────────────────────────

    [Fact]
    public void Redirect_ToUnwritablePath_IsFatalExit2()
    {
        var r = AwkR("BEGIN{ print \"x\" > \"@D@/no/such/dir/f\"; print \"after\" }");
        r.AssertFailed(2, "can't redirect to");
        Assert.DoesNotContain("after", r.Stdout);
    }

    [Fact]
    public void Redirect_ToEmptyName_IsFatalExit2()
    {
        var r = AwkR("BEGIN{ print \"x\" > \"\"; print \"after\" }");
        r.AssertFailed(2, "null string");
        Assert.DoesNotContain("after", r.Stdout);
    }

    [Fact]
    public void Redirect_ToUninitializedVariable_IsFatalExit2()
    {
        AwkR("BEGIN{ print \"x\" > u }").AssertFailed(2, "null string");
    }

    [Fact]
    public void ExitStatus_IsKept_AndRedirectedFileStillFlushed()
    {
        var r = AwkR("BEGIN{ print \"x\" > \"@D@/ex\"; exit 3 }");
        r.AssertFailed(3);
        Assert.Equal("x\n", Read("ex"));
    }
}
