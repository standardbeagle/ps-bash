using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// file-5.45 (`wsl file`) layout rules: an operand it cannot open is reported on STDOUT as
/// <c>NAME: cannot open `NAME' (No such file or directory)</c> with exit 0 (<c>-b</c> drops the name),
/// and the type column is padded to the widest operand (missing ones included) unless <c>-N</c>:
/// <c>NAME</c> + separator + <c>(widest - len(NAME))</c> spaces + one space + text. <c>-F STR</c> and
/// <c>-0</c> change only the separator / add a NUL after the name; names are the operands as typed.
/// </summary>
public class FileMissingAndPaddingTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public FileMissingAndPaddingTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), $"psb-fmp-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "a"), "hello\n");
        File.WriteAllText(Path.Combine(_dir, "longername.txt"), "abcdefgh\n");
        Directory.CreateDirectory(Path.Combine(_dir, "dd"));
        File.WriteAllBytes(Path.Combine(_dir, "empty"), Array.Empty<byte>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult Run(string call) =>
        CmdResult.Run(_fixture.AcquireFresh(), $"Set-Location -LiteralPath '{_dir.Replace("'", "''")}'; {call}");

    [Fact]
    public void Missing_IsOnStdout_Exit0_NoErrorRecord()
    {
        var r = Run("Invoke-BashFile nosuch").AssertSuccess();
        Assert.Equal(new[] { "nosuch: cannot open `nosuch' (No such file or directory)" }, r.Lines);
    }

    [Fact]
    public void Missing_Brief_DropsTheName()
    {
        var r = Run("Invoke-BashFile '-b' nosuch").AssertSuccess();
        Assert.Equal(new[] { "cannot open `nosuch' (No such file or directory)" }, r.Lines);
    }

    [Fact]
    public void Missing_InMimeMode_IsTheSameText()
    {
        var r = Run("Invoke-BashFile '-i' nosuch").AssertSuccess();
        Assert.Equal(new[] { "nosuch: cannot open `nosuch' (No such file or directory)" }, r.Lines);
    }

    [Fact]
    public void TypeColumn_PaddedToTheWidestOperand_MissingOnesCount()
    {
        var r = Run("Invoke-BashFile a longername.txt dd empty nosuch").AssertSuccess();
        Assert.Equal(new[]
        {
            "a:              ASCII text",
            "longername.txt: ASCII text",
            "dd:             directory",
            "empty:          empty",
            "nosuch:         cannot open `nosuch' (No such file or directory)",
        }, r.Lines);
        // the missing operand sets the width too (6 chars): 'a' then 5 pad + 1
        var two = Run("Invoke-BashFile nosuch a").AssertSuccess();
        Assert.Equal(new[]
        {
            "nosuch: cannot open `nosuch' (No such file or directory)",
            "a:      ASCII text",
        }, two.Lines);
    }

    [Fact]
    public void NoPad_Brief_AndSeparator()
    {
        Assert.Equal(new[] { "a: ASCII text", "longername.txt: ASCII text" },
            Run("Invoke-BashFile '-N' a longername.txt").AssertSuccess().Lines);
        Assert.Equal(new[] { "ASCII text", "ASCII text" },
            Run("Invoke-BashFile '-b' a longername.txt").AssertSuccess().Lines);
        Assert.Equal(new[] { "a=              ASCII text", "longername.txt= ASCII text" },
            Run("Invoke-BashFile '-F' '=' a longername.txt").AssertSuccess().Lines);
        Assert.Equal(new[] { "a              ASCII text", "longername.txt ASCII text" },
            Run("Invoke-BashFile '-F' '' a longername.txt").AssertSuccess().Lines);
    }

    [Fact]
    public void Print0_PutsTheNulBeforeTheSeparator_AndStillPads()
    {
        var lines = Run("Invoke-BashFile '-0' a longername.txt").AssertSuccess().Lines;
        Assert.Equal("a\0:              ASCII text", lines[0]);
        Assert.Equal("longername.txt\0: ASCII text", lines[1]);
    }

    [Fact]
    public void GlobMatches_AreListedRelativeAndPaddedTogether()
    {
        var lines = Run("Invoke-BashFile 'l*' 'a'").AssertSuccess().Lines;
        Assert.Equal(new[] { "longername.txt: ASCII text", "a:              ASCII text" }, lines);
    }
}
