using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The file mutators called DIRECTLY from PowerShell (what the Pester gate does), where the binder sees
/// every bare dash token. A single-letter decoy switch rescues <c>-p -v</c>, <c>-a</c>, <c>-i</c>, ...;
/// but a MULTI-letter token that is a prefix or alias of a common parameter (<c>-pv</c> =
/// PipelineVariable, <c>-iv</c> = InformationVariable, <c>-pr</c> = ProgressAction, <c>-in</c> =
/// Information*, <c>-ve</c>/<c>-vb</c> = Verbose, <c>-de</c> = Debug) is taken by the binder, and cannot
/// be decoyed: declaring a parameter or alias with such a name makes PowerShell reject the whole cmdlet
/// ("conflicts with the parameter alias of the same name"). Those must be QUOTED in a direct call
/// (<c>'-pv'</c>); the transpiler always quotes, so bash scripts are unaffected.
/// </summary>
public class DirectCallBinderTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public DirectCallBinderTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), $"psb-dcb-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult InTmp(string body) => CmdResult.Run(_fixture.AcquireFresh(),
        $"Push-Location '{_tmp}'; try {{ {body} }} finally {{ Pop-Location }}");

    private string P(string rel) => Path.Combine(_tmp, rel);

    [Fact]
    public void Mkdir_SeparateDashP_DashV_Direct()
    {
        var r = InTmp("Invoke-BashMkdir -p -v a/b").AssertSuccess();
        Assert.True(Directory.Exists(P("a/b")));
        Assert.Contains("created directory", r.Stdout);
    }

    [Fact]
    public void Mkdir_QuotedPv_Direct()
    {
        var r = InTmp("Invoke-BashMkdir '-pv' c/d").AssertSuccess();
        Assert.True(Directory.Exists(P("c/d")));
        Assert.Contains("created directory", r.Stdout);
    }

    [Fact]
    public void Cp_DashA_Direct_IsArchive_NotSwallowedByArguments()
    {
        Directory.CreateDirectory(P("s"));
        File.WriteAllText(P("s/f"), "x\n");
        // Before the decoy this bound -Arguments, dropped the flag and failed "omitting directory".
        InTmp("Invoke-BashCp -a s t").AssertSuccess();
        Assert.True(File.Exists(P("t/f")));
    }

    [Fact]
    public void Cp_QuotedPr_Direct_IsPreserveRecursive()
    {
        Directory.CreateDirectory(P("s"));
        File.WriteAllText(P("s/f"), "x\n");
        InTmp("Invoke-BashCp '-pr' s t").AssertSuccess();
        Assert.True(File.Exists(P("t/f")));
    }

    [Fact]
    public void Cp_SeparateDashP_DashR_Direct()
    {
        Directory.CreateDirectory(P("s"));
        File.WriteAllText(P("s/f"), "x\n");
        InTmp("Invoke-BashCp -p -r s t").AssertSuccess();
        Assert.True(File.Exists(P("t/f")));
    }

    [Fact]
    public void Rm_QuotedIv_Direct_PromptsAndVerbose()
    {
        File.WriteAllText(P("f"), "x\n");
        var r = InTmp("'y' | Invoke-BashRm '-iv' f");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("removed 'f'", r.Stdout);
    }

    [Fact]
    public void Rm_SeparateDashI_DashV_Direct()
    {
        File.WriteAllText(P("f"), "x\n");
        var r = InTmp("'y' | Invoke-BashRm -i -v f");
        Assert.Equal(0, r.ExitCode);
        Assert.False(File.Exists(P("f")));
    }

    [Fact]
    public void Touch_DashA_DashC_Direct()
    {
        InTmp("Invoke-BashTouch -a -c nosuch").AssertSuccess();
        Assert.False(File.Exists(P("nosuch")));
    }

    [Fact]
    public void Mv_DashV_Direct()
    {
        File.WriteAllText(P("s"), "x\n");
        var r = InTmp("Invoke-BashMv -v s d").AssertSuccess();
        Assert.Equal("'s' -> 'd'", r.Stdout.Trim());
    }
}
