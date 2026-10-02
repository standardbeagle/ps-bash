using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// sort -z, -R/--random-sort/--random-source, --files0-from and the gnulib filevercmp -V. Oracle: GNU
/// coreutils 9.4 in the C locale (`wsl bash`, LC_ALL=C); every expected value below was read from it.
/// </summary>
public class SortZeroRandomFiles0Tests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public SortZeroRandomFiles0Tests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "sz_" + Guid.NewGuid().ToString("N")[..8]);
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

    private const string Seed = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const string Nums = "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n";

    [Fact]
    public void Z_SortsNulRecords_NewlineIsData()
    {
        Assert.Equal("a@b@c@", Bytes(@"Invoke-BashPrintf 'c\0a\0b\0' | Invoke-BashSort '-z'"));
        Assert.Equal("a@b~x@", Bytes(@"Invoke-BashPrintf 'b\nx\0a\0' | Invoke-BashSort '-z'"));   // oracle: a\0b\nx\0
        Assert.Equal("a@b@", Bytes(@"Invoke-BashPrintf 'b\0a\0b\0' | Invoke-BashSort '-zu'"));
        Assert.Equal("a@b@b@", Bytes(@"Invoke-BashPrintf 'b\0a\0b' | Invoke-BashSort '-z'"));      // a missing final NUL is supplied
    }

    [Fact]
    public void Z_NewlineIsABlankForFieldSplitting()   // oracle: -z -b -k2 puts "a b" before "a\nc"
    {
        Assert.Equal("a b@a~c@", Bytes(@"Invoke-BashPrintf 'a b\0a\nc\0' | Invoke-BashSort '-z' '-b' '-k2'"));
        Assert.Equal("a~c@a b@", Bytes(@"Invoke-BashPrintf 'a b\0a\nc\0' | Invoke-BashSort '-z' '-t' ' ' '-k2'"));
    }

    [Fact]
    public void Z_FileInput_Output_AndCheck()
    {
        string f = F("z.bin", "b\0a\0c\0"), o = Path.Combine(_dir, "o.bin").Replace('\\', '/');
        Assert.Equal("a@b@c@", Bytes($"Invoke-BashSort '-z' {f}"));
        Run($"Invoke-BashSort '-z' '-o' '{o}' {f}").AssertSuccess();
        Assert.Equal(new byte[] { (byte)'a', 0, (byte)'b', 0, (byte)'c', 0 }, File.ReadAllBytes(o));
        Run(@"Invoke-BashPrintf 'b\0a\0' | Invoke-BashSort '-zc'").AssertFailed(1, "disorder: a");
        Run(@"Invoke-BashPrintf 'a\0b\0' | Invoke-BashSort '-zc'").AssertSuccess();
    }

    [Fact]
    public void Random_FollowsGnuMd5OrderForTheSeed()
    {
        string src = F("seed", Seed), nums = F("nums", Nums);
        Assert.Equal(new[] { "6", "5", "9", "10", "4", "1", "2", "8", "7", "3" },
            Run($"Invoke-BashSort '-R' '--random-source={src.Trim('\'')}' {nums}".Replace("'--random-source=", "'--random-source=")).AssertSuccess().Lines);
        string[] rev = { "3", "7", "8", "2", "1", "4", "10", "9", "5", "6" };
        Assert.Equal(rev, Run($"Invoke-BashSort '-R' '-r' '--random-source={src.Trim('\'')}' {nums}").AssertSuccess().Lines);
        Assert.Equal(rev, Run($"Invoke-BashSort '-R' '--reverse' '--random-source={src.Trim('\'')}' {nums}").AssertSuccess().Lines);
        // spellings
        string[] fwd = { "6", "5", "9", "10", "4", "1", "2", "8", "7", "3" };
        Assert.Equal(fwd, Run($"Invoke-BashSort '--random-sort' '--random-source={src.Trim('\'')}' {nums}").AssertSuccess().Lines);
        Assert.Equal(fwd, Run($"Invoke-BashSort '--sort=random' '--random-source={src.Trim('\'')}' {nums}").AssertSuccess().Lines);
        Assert.Equal(fwd, Run($"Invoke-BashSort '-k1,1R' '--random-source={src.Trim('\'')}' {nums}").AssertSuccess().Lines);
    }

    [Fact]
    public void Random_GroupsIdenticalLines_AndUniqueKeepsOne()
    {
        string src = F("seed", Seed), dup = F("dup", "a\nb\na\nc\nb\n");
        Assert.Equal(new[] { "b", "b", "a", "a", "c" }, Run($"Invoke-BashSort '-R' '--random-source={src.Trim('\'')}' {dup}").AssertSuccess().Lines);
        Assert.Equal(new[] { "b", "a", "c" }, Run($"Invoke-BashSort '-R' '-u' '--random-source={src.Trim('\'')}' {dup}").AssertSuccess().Lines);
    }

    [Fact]
    public void Random_SourceIsIgnoredWithoutR_AndErrorsAreGnus()
    {
        string src = F("seed", Seed), nums = F("nums", Nums), tiny = F("tiny", "ab"), none = F("emptysrc", "");
        Assert.Equal(new[] { "1", "10", "2", "3", "4", "5", "6", "7", "8", "9" },
            Run($"Invoke-BashSort '--random-source={src.Trim('\'')}' {nums}").AssertSuccess().Lines);
        Run($"Invoke-BashSort '--random-source=/nonexistent/zz' {nums}").AssertSuccess();   // oracle: never opened without -R
        Run($"Invoke-BashSort '-R' '--random-source=/nonexistent/zz' {nums}").AssertFailed(2, "open failed: /nonexistent/zz: No such file or directory");
        Run($"Invoke-BashSort '-R' '--random-source={tiny.Trim('\'')}' {nums}").AssertFailed(2, "end of file");
        Run($"Invoke-BashSort '-R' '--random-source={none.Trim('\'')}' {nums}").AssertFailed(2, "end of file");
        Run($"Invoke-BashSort '-n' '-R' {nums}").AssertFailed(2, "options '-nR' are incompatible");
    }

    [Fact]
    public void Random_WithoutSource_IsAPermutation()
    {
        var r = Run("1..50 | ForEach-Object { \"$_\" } | Invoke-BashSort '-R'").AssertSuccess().Lines;
        Assert.Equal(50, r.Count);
        Assert.Equal(Enumerable.Range(1, 50).Select(i => i.ToString()).OrderBy(x => x), r.OrderBy(x => x));
    }

    [Fact]
    public void Files0From_ListFile_Stdin_AndErrors()
    {
        string fa = F("fa", "x\ny\n"), fb = F("fb", "b\na\n");
        string list = F("list0", fa.Trim('\'') + "\0" + fb.Trim('\'') + "\0");
        Assert.Equal(new[] { "a", "b", "x", "y" }, Run($"Invoke-BashSort '--files0-from={list.Trim('\'')}'").AssertSuccess().Lines);
        Assert.Equal(new[] { "x", "y" },
            Run($"Invoke-BashPrintf '{fa.Trim('\'')}\\0' | Invoke-BashSort '--files0-from=-'").AssertSuccess().Lines);
        Run($"Invoke-BashSort '--files0-from={list.Trim('\'')}' {fa}").AssertFailed(2, "extra operand", "cannot be combined with --files0-from");
        Run($"Invoke-BashPrintf '{fa.Trim('\'')}\\0\\0{fb.Trim('\'')}\\0' | Invoke-BashSort '--files0-from=-'").AssertFailed(2, "-:2: invalid zero-length file name");
        Run($"Invoke-BashPrintf '{fa.Trim('\'')}\\0/nonexistent/q\\0' | Invoke-BashSort '--files0-from=-'").AssertFailed(2, "nonexistent/q: No such file or directory");
        Run("Invoke-BashPrintf '' | Invoke-BashSort '--files0-from=-'").AssertFailed(2, "no input from '-'");
        Run("Invoke-BashSort '--files0-from=/nonexistent/l0'").AssertFailed(2, "open failed: /nonexistent/l0: No such file or directory");
        Run("Invoke-BashPrintf '--' '-\\0' | Invoke-BashSort '--files0-from=-'").AssertFailed(2, "when reading file names from stdin, no file name of '-' allowed");
        // a final name without NUL is still a name
        Assert.Equal(new[] { "x", "y" }, Run($"Invoke-BashPrintf '{fa.Trim('\'')}' | Invoke-BashSort '--files0-from=-'").AssertSuccess().Lines);
    }

    [Fact]
    public void Version_IsGnulibFilevercmp_WithSuffixStripping()
    {
        // Each row: input lines (one per element) -> GNU `sort -V` output, read from the oracle.
        void Case(string[] input, string[] expected)
        {
            string f = F("v" + Guid.NewGuid().ToString("N")[..6], string.Join("\n", input) + "\n");
            Assert.Equal(expected, Run($"Invoke-BashSort '-V' {f}").AssertSuccess().Lines);
        }
        Case(new[] { "a.tar.gz", "a-1.tar.gz", "a1.tar", "a.10", "a.9", "foo.1.2.3", "foo.1.10", "foo1.txt", "foo10.txt", "foo2.txt", "1.0", "1.0.0", "1.0~rc1", "1.0a", ".hid", "..", ".", "..a", "a1.b2", "a-1", "a_1" },
             new[] { ".", "..", "..a", ".hid", "1.0~rc1", "1.0", "1.0a", "1.0.0", "a.tar.gz", "a1.b2", "a1.tar", "a-1", "a-1.tar.gz", "a.9", "a.10", "a_1", "foo1.txt", "foo2.txt", "foo10.txt", "foo.1.2.3", "foo.1.10" });
        Case(new[] { "x.a", "x.b", "x", "x.a1", "x.1", "x.a.b", "x.A", "x.~", "x.~1", "x.a~" },
             new[] { "x", "x.~", "x.~1", "x.A", "x.a~", "x.a", "x.a1", "x.a.b", "x.b", "x.1" });
        Case(new[] { "img.jpg", "img1.jpg", "img.png", "img-1.jpg", "img1" }, new[] { "img.jpg", "img.png", "img1", "img1.jpg", "img-1.jpg" });
        Case(new[] { "a.x", "a.xy", "a.x.y", "a", "a.", "a.1" }, new[] { "a", "a.x", "a.xy", "a.x.y", "a.", "a.1" });
        Case(new[] { "abc.d.e.f1", "abc.d.e.f2", "abc.d.e", "abc.d" }, new[] { "abc.d", "abc.d.e", "abc.d.e.f1", "abc.d.e.f2" });
        Case(new[] { "1.a", "1.a1", "1.a2", "1a", "1" }, new[] { "1", "1.a", "1.a1", "1.a2", "1a" });
    }

    [Fact]
    public void Version_EmptyLineFirst_AndReverse()
    {
        string f = F("vr", "foo.1.10\nfoo.1.2.3\nfoo10.txt\nfoo2.txt\nfoo1.txt\n");
        Assert.Equal(new[] { "foo1.txt", "foo2.txt", "foo10.txt", "foo.1.2.3", "foo.1.10" }, Run($"Invoke-BashSort '-V' {f}").AssertSuccess().Lines);
        Assert.Equal(new[] { "foo.1.10", "foo.1.2.3", "foo10.txt", "foo2.txt", "foo1.txt" }, Run($"Invoke-BashSort '-V' '-r' {f}").AssertSuccess().Lines);
    }
}
