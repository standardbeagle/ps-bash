using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// file-5.45 (`wsl file`) standard-input and name-list behavior: the `-` operand reads the pipeline and is
/// listed as <c>/dev/stdin</c> but padded by the typed <c>-</c> (width 1); <c>-f NAMEFILE</c> names one file per
/// line (verbatim, an empty line is a file called "", CR kept and shown as <c>\015</c>), <c>-f -</c> reads the
/// names from stdin, each list is its own padding batch and is processed before the plain operands with the
/// options that PRECEDED it; an unopenable name file is <c>file: Cannot open `X' (No such file or directory)</c>, exit 1.
/// </summary>
public class FileStdinAndNameFileTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public FileStdinAndNameFileTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), $"psb-fsn-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "hello\n");
        File.WriteAllBytes(Path.Combine(_dir, "e"), Array.Empty<byte>());
        File.WriteAllText(Path.Combine(_dir, "names"), "a.txt\ne\nnosuch\n");
        File.WriteAllText(Path.Combine(_dir, "blank"), "a.txt\n\ne\n");
        File.WriteAllBytes(Path.Combine(_dir, "crlf"), Encoding.ASCII.GetBytes("a.txt\r\n"));
        File.WriteAllText(Path.Combine(_dir, "one"), "a.txt\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private CmdResult Run(string call) =>
        CmdResult.Run(_fixture.AcquireFresh(), $"Set-Location -LiteralPath '{_dir.Replace("'", "''")}'; {call}");

    [Fact]
    public void Stdin_IsListedAsDevStdin()
    {
        var r = Run("'hello' | Invoke-BashFile '-'").AssertSuccess();
        Assert.Equal(new[] { "/dev/stdin: ASCII text" }, r.Lines);
    }

    [Fact]
    public void Stdin_Brief_AndMime()
    {
        Assert.Equal(new[] { "ASCII text" }, Run("'hello' | Invoke-BashFile '-b' '-'").AssertSuccess().Lines);
        Assert.Equal(new[] { "/dev/stdin: text/plain; charset=us-ascii" }, Run("'hello' | Invoke-BashFile '-i' '-'").AssertSuccess().Lines);
    }

    [Fact]
    public void Stdin_NoInput_IsEmpty()
    {
        Assert.Equal(new[] { "/dev/stdin: empty" }, Run("Invoke-BashFile '-'").AssertSuccess().Lines);
    }

    [Fact]
    public void Stdin_IsPaddedByTheTypedDash_NotByDevStdin()
    {
        // oracle: `printf hello | file a.txt -` -> "a.txt: ASCII text" / "/dev/stdin:     ASCII text"
        var r = Run("'hello' | Invoke-BashFile a.txt '-'").AssertSuccess();
        Assert.Equal(new[] { "a.txt: ASCII text", "/dev/stdin:     ASCII text" }, r.Lines);
    }

    [Fact]
    public void Stdin_TwoDashes_BothClassified()
    {
        var r = Run("'hello' | Invoke-BashFile '-' '-'").AssertSuccess();
        Assert.Equal(new[] { "/dev/stdin: ASCII text", "/dev/stdin: ASCII text" }, r.Lines);
    }

    [Fact]
    public void Stdin_SeparatorApplies()
    {
        Assert.Equal(new[] { "/dev/stdin= ASCII text" }, Run("'hello' | Invoke-BashFile '-F' '=' '-'").AssertSuccess().Lines);
    }

    [Fact]
    public void NameFile_ListsEachName_PaddedToTheWidest_MissingIsStdoutText()
    {
        var r = Run("Invoke-BashFile '-f' names").AssertSuccess();
        Assert.Equal(new[]
        {
            "a.txt:  ASCII text",
            "e:      empty",
            "nosuch: cannot open `nosuch' (No such file or directory)",
        }, r.Lines);
    }

    [Fact]
    public void NameFile_EmptyLine_IsAFileCalledEmptyString()
    {
        var r = Run("Invoke-BashFile '-f' blank").AssertSuccess();
        Assert.Equal(new[]
        {
            "a.txt: ASCII text",
            ":      cannot open `' (No such file or directory)",
            "e:     empty",
        }, r.Lines);
    }

    [Fact]
    public void NameFile_Cr_IsKeptAndShownAsOctal()
    {
        var r = Run("Invoke-BashFile '-f' crlf").AssertSuccess();
        Assert.Equal(new[] { "a.txt\\015: cannot open `a.txt\\015' (No such file or directory)" }, r.Lines);
    }

    [Fact]
    public void NameFile_OptionsBeforeApply_OptionsAfterDoNot()
    {
        Assert.Equal(new[] { "ASCII text", "empty", "cannot open `nosuch' (No such file or directory)" },
            Run("Invoke-BashFile '-b' '-f' names").AssertSuccess().Lines);
        Assert.Equal(new[] { "a.txt:  ASCII text", "e:      empty", "nosuch: cannot open `nosuch' (No such file or directory)" },
            Run("Invoke-BashFile '-f' names '-b'").AssertSuccess().Lines);
        Assert.Equal(new[] { "a.txt=  ASCII text", "e=      empty", "nosuch= cannot open `nosuch' (No such file or directory)" },
            Run("Invoke-BashFile '-F' '=' '-f' names").AssertSuccess().Lines);
    }

    [Fact]
    public void NameFile_IsItsOwnBatch_ThenTheOperands()
    {
        var r = Run("Invoke-BashFile '-f' one a.txt e").AssertSuccess();
        Assert.Equal(new[] { "a.txt: ASCII text", "a.txt: ASCII text", "e:     empty" }, r.Lines);
    }

    [Fact]
    public void NameFile_FromStdin()
    {
        var r = Run("'a.txt' | Invoke-BashFile '-f' '-' e").AssertSuccess();
        Assert.Equal(new[] { "a.txt: ASCII text", "e: empty" }, r.Lines);
    }

    [Fact]
    public void NameFile_Unopenable_IsFatalExit1()
    {
        var r = Run("Invoke-BashFile '-f' nonames a.txt");
        r.AssertFailed(1, "file: Cannot open `nonames' (No such file or directory)");
        Assert.Empty(r.Lines);
    }
}