using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>join</c> without <c>-t</c> separates fields by RUNS of blanks and ignores leading blanks (GNU
/// coreutils 9.4); with <c>-t CHAR</c> the split is exact. Output fields are joined by one space
/// (default) or the <c>-t</c> character. Expected text is GNU's (<c>wsl join</c>).
/// </summary>
public class JoinBlankFieldTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public JoinBlankFieldTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-jbf-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private void W(string name, string text) => File.WriteAllText(Path.Combine(_tmp, name), text);

    private string Join(string args)
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            $"Push-Location '{_tmp}'; try {{ Invoke-BashJoin {args} }} finally {{ Pop-Location }}").AssertSuccess();
        return r.Stdout;
    }

    [Fact]
    public void Default_RunsOfBlanksAndTabsSeparateFields_LeadingBlanksIgnored()
    {
        W("f1", "a  1   x\nb\t2\t y\n  c   3\nd 4 \n");
        W("f2", "a    P\nb \t Q\nc  R  S\nd    T\n");
        // GNU: a trailing blank leaves one EMPTY last field ("d 4 " = d,4,"").
        Assert.Equal("a 1 x P\nb 2 y Q\nc 3 R S\nd 4  T", Join("f1 f2"));
    }

    [Fact]
    public void Default_KeyColumnSelection_CountsBlankSeparatedFields()
    {
        W("g1", "x  a\ny   b\n");
        W("f2", "a    P\nb \t Q\n");
        Assert.Equal("a x P\nb y Q", Join("'-1' 2 '-2' 1 g1 f2"));
    }

    [Fact]
    public void Default_TabSeparatedInput_IsJoinedWithASingleSpace()
    {
        W("p1", "a\t1\n");
        W("p2", "a\tX\n");
        Assert.Equal("a 1 X", Join("p1 p2"));
    }

    [Fact]
    public void Default_KeyIsMatchedAcrossDifferentBlankRuns_AndIgnoreCaseWorks()
    {
        W("i1", "A  1\n");
        W("i2", "a   X\n");
        Assert.Equal("A 1 X", Join("'-i' i1 i2"));
    }

    [Fact]
    public void Default_UnpairedLine_IsPrintedWithSingleSpaces()
    {
        W("u1", "a  1\nz   9\n");
        W("u2", "a    X\n");
        Assert.Equal("a 1 X\nz 9", Join("'-a1' u1 u2"));
    }

    [Fact]
    public void ExplicitTab_SplitsExactly_AndJoinsWithTheTab()
    {
        W("t1", "a\t1\nb\t2\n");
        W("t2", "a\tX\nb\tY\n");
        Assert.Equal("a\t1\tX\nb\t2\tY", Join("'-t' \"`t\" t1 t2"));
    }

    [Fact]
    public void ExplicitComma_KeepsEmptyFields()
    {
        W("c1", "a,1,\nb,,2\n");
        W("c2", "a,X\nb,Y\n");
        Assert.Equal("a,1,,X\nb,,2,Y", Join("'-t,' c1 c2"));
    }

    [Fact]
    public void ExplicitSpace_IsAnExactSingleSpaceSplit()
    {
        // With -t ' ' two spaces make an EMPTY field, so key "a" (field 1) still matches but the
        // second field is empty and the third is the value.
        W("s1", "a  1\n");
        W("s2", "a  X\n");
        Assert.Equal("a  1  X", Join("'-t' ' ' s1 s2"));
    }
}
