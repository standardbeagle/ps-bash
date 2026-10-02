using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// join as a GNU 9.4 merge join: -o FORMAT/auto, -e, --check-order/--nocheck-order, --header, -z.
/// Every expectation was read from the oracle (`wsl bash`, LC_ALL=C) on the same files.
/// </summary>
public class JoinGnuMergeTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public JoinGnuMergeTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "jg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string F(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, Encoding.UTF8.GetBytes(content));
        return "'" + p.Replace('\\', '/') + "'";
    }

    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(), script);

    private string Bytes(string cmd) =>
        Run($"$r = @({cmd}); [PsBash.Cmdlets.BashRuntime]::RecordStreamText($r).Replace([string][char]0,'@').Replace(\"`n\",'~')")
            .AssertSuccess().Stdout;

    private (string f1, string f2) Basic() => (F("f1", "a 1\nb 2\nd 4\n"), F("f2", "a x\nc y\nd z\n"));

    [Fact]
    public void O_Format_AndAccumulation()
    {
        var (f1, f2) = Basic();
        Assert.Equal(new[] { "a 1 x", "d 4 z" }, Run($"Invoke-BashJoin '-o' '0,1.2,2.2' {f1} {f2}").AssertSuccess().Lines);
        Assert.Equal(new[] { "a 1 x", "d 4 z" }, Run($"Invoke-BashJoin '-o' '0 1.2 2.2' {f1} {f2}").AssertSuccess().Lines);
        Assert.Equal(new[] { "a 1 x", "d 4 z" }, Run($"Invoke-BashJoin '-o' '0,1.2' '-o' '2.2' {f1} {f2}").AssertSuccess().Lines);
    }

    [Theory]
    [InlineData("", "invalid file number in field spec: ''")]
    [InlineData("1", "invalid field specifier: '1'")]
    [InlineData("1.x", "invalid field number: 'x'")]
    [InlineData("3.1", "invalid file number in field spec: '3.1'")]
    [InlineData("1.0", "invalid field number: '0'")]
    [InlineData("0.1", "invalid field specifier: '0.1'")]
    [InlineData("x", "invalid file number in field spec: 'x'")]
    public void O_Errors(string spec, string message)
    {
        var (f1, f2) = Basic();
        Run($"Invoke-BashJoin '-o' '{spec}' {f1} {f2}").AssertFailed(1, message);
    }

    [Fact]
    public void Auto_And_E_Filler()
    {
        string t1 = F("t1", "a 1 2\nb 3\n"), t2 = F("t2", "a x\nc y z w\n");
        Assert.Equal(new[] { "a 1 2 x", "b 3  ", "c   y" },
            Run($"Invoke-BashJoin '-o' 'auto' '-a1' '-a2' {t1} {t2}").AssertSuccess().Lines);
        Assert.Equal(new[] { "a 1 2 x", "b 3 - -", "c - - y" },
            Run($"Invoke-BashJoin '-o' 'auto' '-e' '-' '-a1' '-a2' {t1} {t2}").AssertSuccess().Lines);
        Assert.Equal(new[] { "a" }, Run($"Invoke-BashJoin '-o' 'auto' '-o' '0' {t1} {t2}").AssertSuccess().Lines);   // an explicit list beats auto
        var (f1, f2) = Basic();
        Assert.Equal(new[] { "a 1 x", "b 2 X", "c X y", "d 4 z" },
            Run($"Invoke-BashJoin '-e' 'X' '-a1' '-a2' '-o' '0,1.2,2.2' {f1} {f2}").AssertSuccess().Lines);
        // without -o, -e only fills fields that exist but are empty
        string s1 = F("s1", "a,,2\n"), s2 = F("s2", "a,,\n");
        Assert.Equal(new[] { "a,Z,2,Z,Z" }, Run($"Invoke-BashJoin '-t,' '-e' 'Z' {s1} {s2}").AssertSuccess().Lines);
    }

    [Fact]
    public void V_SuppressesPairs_EvenWithA()
    {
        var (f1, f2) = Basic();
        Assert.Equal(new[] { "b 2", "c y" }, Run($"Invoke-BashJoin '-a1' '-v2' {f1} {f2}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b 2", "c y" }, Run($"Invoke-BashJoin '-v1' '-v2' {f1} {f2}").AssertSuccess().Lines);
    }

    [Fact]
    public void Disorder_Default_WarnsOncePerFile_ContinuesAndFails()
    {
        string j1 = F("j1", "b 1\na 2\nb 3\na 4\nc 5\nb 6\n"), j2 = F("j2", "a x\nb y\nc z\nd w\n");
        var r = Run($"Invoke-BashJoin {j1} {j2}");
        Assert.Equal(new[] { "b 1 y", "c 5 z" }, r.Lines);
        Assert.Contains(":2: is not sorted: a 2", r.Stderr);
        Assert.Contains("join: input is not in sorted order", r.Stderr);
        Assert.Equal(1, r.ExitCode);
        Assert.Equal(1, r.Stderr.Split("is not sorted").Length - 1);
    }

    [Fact]
    public void Disorder_NotCheckedUntilAnUnpairableLine_LookaheadReadsAreFree()
    {
        // oracle: no warning and exit 0 although the tail is unsorted
        string d1 = F("d1", "a 1\nc 2\nb 3\nd 4\na 5\n"), d2 = F("d2", "a x\n");
        Run($"Invoke-BashJoin {d1} {d2}").AssertSuccess();
        string f1 = F("ff1", "b 1\na 2\n"), f2 = F("ff2", "c x\n");
        Run($"Invoke-BashJoin {f1} {f2}").AssertSuccess();
    }

    [Fact]
    public void Disorder_FoundInTheDrain()
    {
        string h1 = F("h1", "x 1\ny 2\nz 3\nb 4\n"), h2 = F("h2", "a x\n");
        var r = Run($"Invoke-BashJoin {h1} {h2}");
        Assert.Empty(r.Lines);
        Assert.Contains("is not sorted: b 4", r.Stderr);
        Assert.Equal(1, r.ExitCode);
        var a = Run($"Invoke-BashJoin '-a1' {h1} {h2}");
        Assert.Equal(new[] { "x 1", "y 2", "z 3", "b 4" }, a.Lines);
        Assert.Equal(1, a.ExitCode);
    }

    [Fact]
    public void CheckOrder_StopsAtTheFirstDisorder()
    {
        string d1 = F("e1", "a 1\nc 2\nb 3\n"), d2 = F("e2", "a x\n");
        var r = Run($"Invoke-BashJoin '--check-order' '-a1' {d1} {d2}");
        Assert.Equal(new[] { "a 1 x", "c 2" }, r.Lines);
        Assert.Contains("is not sorted: b 3", r.Stderr);
        Assert.DoesNotContain("input is not in sorted order", r.Stderr);
        Assert.Equal(1, r.ExitCode);
        Run($"Invoke-BashJoin '--nocheck-order' {d1} {d2}").AssertSuccess();
    }

    [Fact]
    public void CheckOrder_IgnoreCaseComparesCaseless()
    {
        string m1 = F("m1", "a 1\nB 2\n"), m2 = F("m2", "A x\nb y\n");
        Assert.Equal(new[] { "a 1 x", "B 2 y" }, Run($"Invoke-BashJoin '-i' '--check-order' {m1} {m2}").AssertSuccess().Lines);
        Run($"Invoke-BashJoin '--check-order' {m1} {m2}").AssertFailed(1, "is not sorted: B 2");
    }

    [Fact]
    public void Header_JoinsFirstLinesAndSkipsThemFromTheMerge()
    {
        string k1 = F("k1", "k v\na 1\nb 2\n"), k2 = F("k2", "k w\na x\nb y\n");
        Assert.Equal(new[] { "k v w", "a 1 x", "b 2 y" }, Run($"Invoke-BashJoin '--header' {k1} {k2}").AssertSuccess().Lines);
        string e = F("eh", ""), f2 = F("f2h", "a x\nc y\n");
        Assert.Equal(new[] { "a x" }, Run($"Invoke-BashJoin '--header' {e} {f2}").AssertSuccess().Lines);
        Assert.Empty(Run($"Invoke-BashJoin '--header' {e} {e}").AssertSuccess().Lines);
    }

    [Fact]
    public void Header_IsNotPartOfTheOrderCheck_ButCountsTowardsLineNumbers()
    {
        string l1 = F("l1", "zz 1\nb 1\na 2\n"), l2 = F("l2", "k w\nc x\nb y\n");
        var r = Run($"Invoke-BashJoin '--header' '--check-order' {l1} {l2}");
        Assert.Equal(new[] { "zz 1 w" }, r.Lines);
        Assert.Contains(":3: is not sorted: a 2", r.Stderr);
    }

    [Fact]
    public void Z_NulRecords_NewlineIsABlank()
    {
        string z1 = F("z1", "a 1\0b 2\0"), z2 = F("z2", "a x\0b y\0");
        Assert.Equal("a 1 x@b 2 y@", Bytes($"Invoke-BashJoin '-z' {z1} {z2}"));
        string z3 = F("z3", "a b\nc\0d e\0"), z4 = F("z4", "a b\nc\0d f\0");
        Assert.Equal("a b c b c@d e f@", Bytes($"Invoke-BashJoin '-z' {z3} {z4}"));
        Assert.Equal("a b~c b~c@d e f@", Bytes($"Invoke-BashJoin '-z' '-t' ' ' {z3} {z4}"));
    }

    [Fact]
    public void MissingJoinField_IsAnEmptyKey()
    {
        string m1 = F("mm1", "a\nb 2\n\n"), m2 = F("mm2", "a x\nb\n\n");
        Assert.Equal(new[] { "a x", "b 2", "" }, Run($"Invoke-BashJoin {m1} {m2}").AssertSuccess().Lines);
    }

    [Fact]
    public void Stdin_AndBothStdinError()
    {
        string d2 = F("sd2", "a x\n");
        Assert.Equal(new[] { "a 1 x" }, Run($"'a 1','c 2' | Invoke-BashJoin '-' {d2}").AssertSuccess().Lines);
        Run("Invoke-BashJoin '-' '-'").AssertFailed(1, "both files cannot be standard input");
    }

    [Fact]
    public void Dups_CrossProduct_AndTabIncompat()
    {
        string g1 = F("g1", "a 1\na 2\nb 3\n"), g2 = F("g2", "a x\na y\nb z\nb w\n");
        Assert.Equal(new[] { "a 1 x", "a 1 y", "a 2 x", "a 2 y", "b 3 z", "b 3 w" }, Run($"Invoke-BashJoin {g1} {g2}").AssertSuccess().Lines);
        Run($"Invoke-BashJoin '-t' ',' '-t' ';' {g1} {g2}").AssertFailed(1, "incompatible tabs");
        Run($"Invoke-BashJoin '-1' '1' '-1' '2' {g1} {g2}").AssertFailed(1, "incompatible join fields 0, 1");
    }
}
