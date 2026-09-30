using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Byte-fidelity of the <c>&gt; file</c> / <c>&gt;&gt; file</c> writer
/// (<see cref="InvokeBashRedirectCommand"/>). Expected bytes were confirmed against GNU bash
/// (<c>wsl bash -c '…; od -c f'</c>): a record marked <c>NoTrailingNewline</c> (printf / echo -n)
/// is written exactly; every other record gets its <c>\n</c> boundary; empty output still
/// creates/truncates the file. Regression: <c>printf x &gt; f</c> used to write <c>x\n</c>.
/// </summary>
public class InvokeBashRedirectCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashRedirectCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(Path.GetTempPath(), $"psb-redir-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private static string Q(string s) => s.Replace("'", "''");

    private CmdResult Run(string script) =>
        CmdResult.Run(_fixture.AcquireFresh(), script);

    private string File_(string name) => Path.Combine(_tmpDir, name);

    [Theory]
    [InlineData("x", "x")]
    [InlineData("a\\nb", "a\nb")]
    [InlineData("a\\n", "a\n")]
    [InlineData("", "")]
    public void Redirect_PrintfOutput_FileBytesAreExactlyTheFormatOutput(string format, string expected)
    {
        var f = File_("p.txt");
        Run($"Invoke-BashPrintf '{format}' | Invoke-BashRedirect -Path '{Q(f)}'").AssertSuccess();
        Assert.Equal(expected, File.ReadAllText(f));
    }

    [Fact]
    public void Redirect_EchoDashN_WritesNoTrailingNewline()
    {
        var f = File_("n.txt");
        Run($"Invoke-BashEcho '-n' 'x' | Invoke-BashRedirect -Path '{Q(f)}'").AssertSuccess();
        Assert.Equal("x", File.ReadAllText(f));
    }

    [Fact]
    public void Redirect_PlainEcho_KeepsTrailingNewline()
    {
        var f = File_("e.txt");
        Run($"Invoke-BashEcho 'x' | Invoke-BashRedirect -Path '{Q(f)}'").AssertSuccess();
        Assert.Equal("x\n", File.ReadAllText(f));
    }

    [Fact]
    public void Redirect_Append_PrintfThenPrintf_Concatenates()
    {
        var f = File_("a.txt");
        Run($"Invoke-BashPrintf 'a' | Invoke-BashRedirect -Path '{Q(f)}'").AssertSuccess();
        Run($"Invoke-BashPrintf 'b' | Invoke-BashRedirect -Path '{Q(f)}' -Append").AssertSuccess();
        Assert.Equal("ab", File.ReadAllText(f));
    }

    [Fact]
    public void Redirect_MixedRecords_OnlyTheMarkedRecordLosesItsNewline()
    {
        // { echo x; printf y; } > f  ->  "x\ny"
        var f = File_("m.txt");
        Run($"& {{ Invoke-BashEcho 'x'; Invoke-BashPrintf 'y' }} | Invoke-BashRedirect -Path '{Q(f)}'").AssertSuccess();
        Assert.Equal("x\ny", File.ReadAllText(f));
    }

    [Fact]
    public void Redirect_PlainStringRecords_GetNewlineBoundaries()
    {
        var f = File_("s.txt");
        Run($"'one','two' | Invoke-BashRedirect -Path '{Q(f)}'").AssertSuccess();
        Assert.Equal("one\ntwo\n", File.ReadAllText(f));
    }

    [Fact]
    public void Redirect_EmptyOutput_CreatesAndTruncatesTheFile()
    {
        var created = File_("new.txt");
        Run($"Invoke-BashPrintf '' | Invoke-BashRedirect -Path '{Q(created)}'").AssertSuccess();
        Assert.True(File.Exists(created));
        Assert.Equal("", File.ReadAllText(created));

        var existing = File_("old.txt");
        File.WriteAllText(existing, "OLD\n");
        Run($"@() | Invoke-BashRedirect -Path '{Q(existing)}'").AssertSuccess();
        Assert.Equal("", File.ReadAllText(existing));
    }
}
