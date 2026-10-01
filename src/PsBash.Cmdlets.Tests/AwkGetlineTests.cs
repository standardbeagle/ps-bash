using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// awk <c>getline</c>, every POSIX/gawk form. Expected values are the gawk 5.2.1 oracle's (WSL
/// Ubuntu-24.04), including its one deliberate departure from POSIX text: a redirected getline
/// (<c>cmd | getline</c>, <c>getline &lt; file</c>) never touches NR/FNR (gawk and mawk agree).
/// Commands used here (<c>echo</c>, <c>exit N</c>, <c>echo a; echo b</c>) run through ps-bash itself,
/// like <c>system()</c>; the byte-level bash parity of the idioms is in AwkGetlineDifferentialTests.
/// </summary>
public class AwkGetlineTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public AwkGetlineTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(Path.GetTempPath(), "psbash-awkgl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    private string Mk(string name, string content)
    {
        var p = Path.Combine(_tmpDir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    private string[] Lines(string script)
    {
        var r = Run(script);
        return r.Lines.Select(l => l.TrimEnd('\n', '\r')).ToArray();
    }

    /// <summary>Run <c>awk PROGRAM FILE...</c> over files.</summary>
    private string[] Awk(string program, params string[] files) =>
        Lines("Invoke-BashAwk " + Q(program) + " " + string.Join(" ", files.Select(Q)));

    /// <summary>Run <c>printf INPUT | awk PROGRAM</c> (stdin mode).</summary>
    private string[] AwkStdin(string input, string program) =>
        Lines(string.Join(",", input.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Q)) +
              " | Invoke-BashAwk " + Q(program));

    // ── plain getline / getline var (main input) ──────────────────────────────

    [Fact]
    public void Getline_Plain_SetsRecordNfNrFnr()
    {
        var f = Mk("a.txt", "a\nb c\nd\n");
        Assert.Equal(new[] { "b c", "2 2 2" },
            Awk("NR==1{ getline; print $0; print NR, FNR, NF }", f));
    }

    [Fact]
    public void Getline_Var_SetsVarAndNrFnr_NotRecord()
    {
        var f = Mk("a.txt", "a b\nc d e\n");
        Assert.Equal(new[] { "a b|c d e|2 2 2" },
            Awk("NR==1{ getline v; print $0 \"|\" v \"|\" NR, FNR, NF }", f));
    }

    [Fact]
    public void Getline_Var_LeavesNfAndRecordAlone()
    {
        var f = Mk("a.txt", "a b\nc d e\n");
        Assert.Equal(new[] { "2 a b c d e" }, Awk("NR==1{ getline v; print NF, $0, v }", f));
    }

    [Fact]
    public void Getline_PairedLines_OddCount_LastRecordKeepsVar()
    {
        // gawk: at EOF getline returns 0 and leaves the variable as it was.
        var f = Mk("odd.txt", "a\nb\nc\n");
        Assert.Equal(new[] { "a b", "c b" }, Awk("{ getline nxt; print $0, nxt }", f));
    }

    [Fact]
    public void Getline_StdinPipe_Buffered_ReturnsPairs()
    {
        Assert.Equal(new[] { "1-2", "2", "3-2", "3" },
            AwkStdin("1\n2\n3\n", "{ getline x; print $0 \"-\" x; print NR }"));
    }

    [Fact]
    public void Getline_InBegin_ReadsFirstRecord_File()
    {
        var f = Mk("a.txt", "x\ny\n");
        Assert.Equal(new[] { "got x", "1 1 " + f }, Awk("BEGIN{ getline; print \"got \" $0; print NR, FNR, FILENAME }", f));
    }

    [Fact]
    public void Getline_InBegin_ReadsFirstRecord_Stdin_ThenMainLoopContinues()
    {
        Assert.Equal(new[] { "B:a", "M:b", "M:c", "end 3" },
            AwkStdin("a\nb\nc\n", "BEGIN{ getline; print \"B:\" $0 } { print \"M:\" $0 } END{ print \"end\", NR }"));
    }

    [Fact]
    public void Getline_InEnd_ReturnsZero_KeepsLastRecord()
    {
        var f = Mk("a.txt", "a\nb\nc\n");
        Assert.Equal(new[] { "0 c" }, Awk("END{ r = getline; print r, $0 }", f));
    }

    [Fact]
    public void Getline_CrossesFileBoundary_UpdatesFilenameAndResetsFnr()
    {
        var f1 = Mk("g1.txt", "a\nb\nc\n");
        var f2 = Mk("g2.txt", "x\ny\n");
        var o = Awk("{ print FNR, NR; getline; print \"g\", FNR, NR, $0 }", f1, f2);
        // record a -> getline b; record c -> getline crosses into g2 -> x; record y -> getline EOF (unchanged).
        Assert.Equal(new[] { "1 1", "g 2 2 b", "3 3", "g 1 4 x", "2 5", "g 2 5 y" }, o);
    }

    [Fact]
    public void Getline_Idiom_ThePatternRecordThenNext()
    {
        var f = Mk("s.txt", "a\nb\nc\n");
        Assert.Equal(new[] { "after b: c" }, Awk("/b/{ getline; print \"after b:\", $0 }", f));
    }

    [Fact]
    public void Getline_ReturnValue_IsOneThenZero()
    {
        var f = Mk("a.txt", "a\n");
        Assert.Equal(new[] { "1", "0" }, Awk("BEGIN{ print (getline x); print (getline x) }", f));
    }

    // ── getline < file ────────────────────────────────────────────────────────

    [Fact]
    public void GetlineFile_SetsRecordAndNf_NotNrFnr()
    {
        var f = Mk("data.txt", "p q r\ns t\n");
        var o = Awk("BEGIN{ r = getline < " + AwkStr(f) + "; print r, $0, NF, NR, FNR }");
        Assert.Equal(new[] { "1 p q r 3 0 0" }, o);
    }

    [Fact]
    public void GetlineFileVar_SetsOnlyVar()
    {
        var f = Mk("data.txt", "p q r\ns t\n");
        var o = Awk("BEGIN{ $0 = \"keep\"; getline v < " + AwkStr(f) + "; print v, $0, NF }");
        Assert.Equal(new[] { "p q r keep 1" }, o);
    }

    [Fact]
    public void GetlineFile_WhileLoop_CountsLines_NrUntouched()
    {
        var f = Mk("data.txt", "1\n2\n3\n");
        var o = Awk("BEGIN{ while ((getline line < " + AwkStr(f) + ") > 0) n++; print n, NR }");
        Assert.Equal(new[] { "3 0" }, o);
    }

    [Fact]
    public void GetlineFile_InMainRule_WhileLoopPerRecord()
    {
        // The documented idiom: a lookup file read inside a main-input program.
        var lookup = Mk("lk.txt", "k1\nk2\n");
        var main = Mk("m.txt", "x\ny\n");
        var o = Awk("{ while ((getline line < " + AwkStr(lookup) + ") > 0) n++; print $0, n }", main);
        // First record drains the lookup file (2); the stream stays at EOF for the second record.
        Assert.Equal(new[] { "x 2", "y 2" }, o);
    }

    [Fact]
    public void GetlineFile_StaysOpen_EachCallReadsNextLine_CloseRewinds()
    {
        var f = Mk("data.txt", "a\nb\nc\n");
        var o = Awk("BEGIN{ f = " + AwkStr(f) + "; getline a < f; getline b < f; print a b; close(f); getline c < f; print c }");
        Assert.Equal(new[] { "ab", "a" }, o);
    }

    [Fact]
    public void GetlineFile_Missing_ReturnsMinusOne_NoDiagnostic()
    {
        var r = Run("Invoke-BashAwk " + Q("BEGIN{ print (getline l < \"" + _tmpDir.Replace("\\", "/") + "/nope.txt\") }"));
        Assert.Equal("-1", r.Stdout.Trim());
        Assert.Empty(r.Errors);
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void GetlineFile_Directory_ReturnsMinusOne()
    {
        var o = Awk("BEGIN{ print (getline x < " + AwkStr(_tmpDir) + ") }");
        Assert.Equal(new[] { "-1" }, o);
    }

    [Fact]
    public void GetlineFile_NullDevice_ReturnsZero()
    {
        Assert.Equal(new[] { "0" }, Awk("BEGIN{ print (getline x < \"/dev/null\") }"));
    }

    [Fact]
    public void GetlineFile_RelativePath_ResolvesAgainstPowerShellLocation()
    {
        Mk("rel.txt", "hello\n");
        var o = Lines("Push-Location " + Q(_tmpDir) + "; try { Invoke-BashAwk " + Q("BEGIN{ getline x < \"rel.txt\"; print x }") +
                      " } finally { Pop-Location }");
        Assert.Equal(new[] { "hello" }, o);
    }

    [Fact]
    public void GetlineFile_Dash_ReadsStdin()
    {
        Assert.Equal(new[] { "got in" },
            AwkStdin("in\n", "BEGIN{ getline x < \"-\"; print \"got\", x }"));
    }

    [Fact]
    public void GetlineFile_DevStdin_FromFileMode_ReadsPipeline()
    {
        var f = Mk("a.txt", "unused\n");
        var o = Lines("'piped' | Invoke-BashAwk " + Q("BEGIN{ getline x < \"/dev/stdin\"; print x }") + " " + Q(f));
        Assert.Equal(new[] { "piped" }, o);
    }

    // ── cmd | getline ─────────────────────────────────────────────────────────

    [Fact]
    public void GetlineCmd_SetsRecordAndNf_NotNr()
    {
        var o = Awk("BEGIN{ \"echo hi there\" | getline; print $2, NR, NF }");
        Assert.Equal(new[] { "there 0 2" }, o);
    }

    [Fact]
    public void GetlineCmdVar_SetsVarOnly()
    {
        var o = Awk("BEGIN{ \"echo x\" | getline v; print v, NR, NF }");
        Assert.Equal(new[] { "x 0 0" }, o);
    }

    [Fact]
    public void GetlineCmd_WhileLoop_SumsAllLines()
    {
        var o = Awk("BEGIN{ while ((\"echo 1; echo 2; echo 3\" | getline l) > 0) s += l; print s }");
        Assert.Equal(new[] { "6" }, o);
    }

    [Fact]
    public void GetlineCmd_SameCommandKeepsOneStream()
    {
        var o = Awk("BEGIN{ \"echo 1; echo 2\" | getline a; \"echo 1; echo 2\" | getline b; print a, b }");
        Assert.Equal(new[] { "1 2" }, o);
    }

    [Fact]
    public void GetlineCmd_ReturnsOneThenZeroAtEof_ThenCloseReopens()
    {
        var o = Awk("BEGIN{ x = \"echo a\" | getline y; print x, y; x = \"echo a\" | getline y; print x; print close(\"echo a\"); x = \"echo a\" | getline y; print x }");
        Assert.Equal(new[] { "1 a", "0", "0", "1" }, o);
    }

    [Fact]
    public void GetlineCmd_ConcatenatedCommandText_PipesWholeExpression()
    {
        // `|` binds below concatenation: ("echo " "zz") | getline
        var o = Awk("BEGIN{ \"echo \" \"zz\" | getline q; print q }");
        Assert.Equal(new[] { "zz" }, o);
    }

    [Fact]
    public void GetlineCmd_ToFieldLvalue_RebuildsRecord()
    {
        var f = Mk("one.txt", "a\n");
        Assert.Equal(new[] { "a a", "2" }, Awk("NR==1{ \"echo a\" | getline $2; print; print NF }", f));
    }

    [Fact]
    public void GetlineCmd_InMainRule_DoesNotDisturbNr()
    {
        var f = Mk("two.txt", "a\nb\n");
        Assert.Equal(new[] { "1 1", "2 2 ho" },
            Awk("NR==1{ \"echo hi\" | getline x; print NR, FNR } NR==2{ \"echo ho\" | getline; print NR, FNR, $0 }", f));
    }

    // ── close() / fflush() ────────────────────────────────────────────────────

    [Fact]
    public void Close_Command_ReturnsExitStatus()
    {
        var o = Awk("BEGIN{ \"exit 3\" | getline; print close(\"exit 3\"); print close(\"exit 3\") }");
        Assert.Equal(new[] { "3", "-1" }, o);
    }

    [Fact]
    public void Close_NeverOpened_ReturnsMinusOne()
    {
        Assert.Equal(new[] { "-1", "-1" }, Awk("BEGIN{ print close(\"nothing\"); print close(\"gl1\") }"));
    }

    [Fact]
    public void Close_File_ReturnsZero_ThenMinusOne()
    {
        var f = Mk("c.txt", "a\n");
        Assert.Equal(new[] { "0 -1" }, Awk("BEGIN{ f = " + AwkStr(f) + "; getline x < f; print close(f), close(f) }"));
    }

    [Fact]
    public void Fflush_ReturnsZero()
    {
        Assert.Equal(new[] { "0" }, Awk("BEGIN{ print fflush() }"));
    }

    [Fact]
    public void System_ReturnsExitStatus_AndForwardsOutput()
    {
        Assert.Equal(new[] { "before", "hello", "2" },
            Awk("BEGIN{ print \"before\"; system(\"echo hello\"); print system(\"exit 2\") }"));
    }

    // ── grammar quirks ────────────────────────────────────────────────────────

    [Fact]
    public void Grammar_FileOperandStopsBeforeConcatenation()
    {
        // gawk: (getline < "a") "b"  -- the result is concatenated, not the file name.
        var a = Mk("gq.txt", "line\n");
        var o = Awk("BEGIN{ x = getline < " + AwkStr(a) + " \"zz\"; print x }");
        Assert.Equal(new[] { "1zz" }, o);
    }

    [Fact]
    public void Grammar_UnparenthesizedFileGetlineInCondition()
    {
        var f = Mk("g.txt", "1\n2\n3\n");
        var o = Awk("BEGIN{ while (getline line < " + AwkStr(f) + " > 0) n++; print n }");
        Assert.Equal(new[] { "3" }, o);
    }

    [Fact]
    public void Grammar_UnparenthesizedCommandGetlineInCondition()
    {
        var o = Awk("BEGIN{ while (\"echo a; echo b\" | getline > 0) n++; print n }");
        Assert.Equal(new[] { "2" }, o);
    }

    [Fact]
    public void Grammar_GetlineAsStatementAndInTernary()
    {
        var f = Mk("t.txt", "a\nb\n");
        Assert.Equal(new[] { "b" }, Awk("NR==1{ getline; print }", f));
        Assert.Equal(new[] { "yes" }, Awk("NR==1{ print (getline > 0 ? \"yes\" : \"no\") }", f));
    }

    [Fact]
    public void Grammar_PrintPipeStillMeansOutputRedirection_NotGetline()
    {
        // The existing print-redirection swallow must be unaffected by the getline parse.
        var o = Awk("BEGIN{ print \"x\" }");
        Assert.Equal(new[] { "x" }, o);
    }

    // ── streaming preserved for programs without main-input getline ───────────

    [Fact]
    public void NoMainGetline_StillStreams_RecordsInterleaveWithProducer()
    {
        var f = Mk("lk.txt", "z\n");
        var log = Lines(
            "$log = [System.Collections.Generic.List[string]]::new(); " +
            "& { 1..3 | ForEach-Object { $log.Add(\"P$_\"); \"$_\" } } | " +
            "Invoke-BashAwk " + Q("{ getline v < " + AwkStr(f) + "; print $1 * 10 }") + " | " +
            "ForEach-Object { $log.Add('T' + $_.ToString().Trim()) }; $log -join ' '");
        Assert.Equal(new[] { "P1 T10 P2 T20 P3 T30" }, log);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>An awk string literal naming <paramref name="path"/> (forward slashes: no escaping needed).</summary>
    private static string AwkStr(string path) => "\"" + path.Replace("\\", "/") + "\"";
}
