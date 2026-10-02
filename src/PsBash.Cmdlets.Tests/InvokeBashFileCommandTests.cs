using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashFile</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="PsBash.Cmdlets.InvokeBashFileCommand"/>.
///
/// Oracle: the original psm1 function's magic-byte table (PNG/JPEG/PDF/Zip/
/// ELF/GIF/RIFF) plus its ASCII-text / data fallback (bytes &lt; 0x07 OR
/// (0x0E..0x1F excluding 0x1B) → non-text).
///
/// Failure-surface axes covered (per Directive 3): empty input (zero-byte
/// file → "ASCII text"), unicode (UTF-8 multi-byte body → "data" because
/// continuation bytes ≥ 0x80 do not match the text predicate? — actually
/// 0x80+ passes the test (predicate only rejects 0x00..0x06 and 0x0E..0x1F
/// excluding 0x1B), so unicode UTF-8 text registers as "ASCII text"; that
/// matches the psm1 oracle), multi-file emission, missing file (error
/// continuation), magic-byte branches, <c>-b</c> brief, <c>-i</c> MIME,
/// <c>--help</c>, alias resolution, and a Directive-12 injection probe.
/// </summary>
public class InvokeBashFileCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmpDir;
    private readonly SharedPwshFixture _fixture;

    public InvokeBashFileCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-file-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private (System.Management.Automation.PSObject[] objects, string[] texts) Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var texts = result.Select(o =>
        {
            if (o == null) return "";
            var bashText = o.Properties["BashText"]?.Value as string;
            if (bashText != null) return bashText;
            return o.ToString() ?? "";
        }).ToArray();
        return (result.ToArray(), texts);
    }

    private static string Esc(string path) => path.Replace("'", "''");

    private (string[] outLines, string[] errors) RunWithErrors(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(script).Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        var outLines = result.Select(o =>
            o?.Properties["BashText"]?.Value as string ?? o?.ToString() ?? "").ToArray();
        return (outLines, errs);
    }

    [Fact]
    public void File_PlainAsciiText_EmitsAsciiText()
    {
        var f = Path.Combine(_tmpDir, "plain.txt");
        File.WriteAllText(f, "hello world\n");
        var (_, lines) = Run($"Invoke-BashFile '{Esc(f)}'");
        Assert.Single(lines);
        Assert.EndsWith(": ASCII text", lines[0]);
    }

    [Fact]
    public void File_BinaryBytes_EmitsData()
    {
        // Control bytes (0x01 < 0x07) are the predicate's "non-text" trigger.
        var f = Path.Combine(_tmpDir, "bin.dat");
        File.WriteAllBytes(f, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
        var (_, lines) = Run($"Invoke-BashFile '{Esc(f)}'");
        Assert.Single(lines);
        Assert.EndsWith(": data", lines[0]);
    }

    [Fact]
    public void File_EmptyFile_EmitsEmpty()
    {
        // GNU file: an empty regular file is "empty" (the psm1 oracle said "ASCII text").
        var f = Path.Combine(_tmpDir, "empty.dat");
        File.WriteAllBytes(f, Array.Empty<byte>());
        var (_, lines) = Run($"Invoke-BashFile '{Esc(f)}'");
        Assert.Single(lines);
        Assert.EndsWith(": empty", lines[0]);
    }


    [Fact]
    public void File_PngMagic_EmitsPngImageData()
    {
        var f = Path.Combine(_tmpDir, "img.png");
        // PNG magic: 89 50 4E 47 0D 0A 1A 0A + the IHDR chunk header (libmagic needs both); a truncated IHDR reads as zeros.
        File.WriteAllBytes(f, new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x08, 0x08, 0x02, 0x00, 0x00, 0x00
        });
        var (_, lines) = Run($"Invoke-BashFile '{Esc(f)}'");
        Assert.Single(lines);
        Assert.EndsWith(": PNG image data, 16 x 8, 8-bit/color RGB, non-interlaced", lines[0]);
    }

    [Fact]
    public void File_BriefFlag_OmitsPathPrefix()
    {
        var f = Path.Combine(_tmpDir, "brief.txt");
        File.WriteAllText(f, "abc\n");
        var (_, lines) = Run($"Invoke-BashFile -b '{Esc(f)}'");
        Assert.Single(lines);
        Assert.Equal("ASCII text", lines[0]);
    }

    [Fact]
    public void File_MimeFlag_EmitsMimeType()
    {
        var f = Path.Combine(_tmpDir, "mime.txt");
        File.WriteAllText(f, "abc\n");
        var (_, lines) = Run($"Invoke-BashFile -i '{Esc(f)}'");
        Assert.Single(lines);
        Assert.EndsWith(": text/plain; charset=us-ascii", lines[0]); // GNU -i = --mime-type + --mime-encoding
    }

    [Fact]
    public void File_MimeAndBriefFlags_EmitJustMime()
    {
        var f = Path.Combine(_tmpDir, "both.png");
        File.WriteAllBytes(f, new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
        });
        var (_, lines) = Run($"Invoke-BashFile -b -i '{Esc(f)}'");
        Assert.Single(lines);
        Assert.Equal("image/png; charset=binary", lines[0]);
    }

    [Fact]
    public void File_MultiFile_EmitsOnePerOperand()
    {
        var f1 = Path.Combine(_tmpDir, "a.txt");
        var f2 = Path.Combine(_tmpDir, "b.bin");
        File.WriteAllText(f1, "alpha\n");
        File.WriteAllBytes(f2, new byte[] { 0x01, 0x02 });
        var (_, lines) = Run(
            $"Invoke-BashFile '{Esc(f1)}' '{Esc(f2)}'");
        Assert.Equal(2, lines.Length);
        Assert.EndsWith(": ASCII text", lines[0]);
        Assert.EndsWith(": data", lines[1]);
    }

    [Fact]
    public void File_MissingFile_ContinuesAndEmitsForRest()
    {
        var ghost = Path.Combine(_tmpDir, "ghost.txt");
        var ok = Path.Combine(_tmpDir, "ok.txt");
        File.WriteAllText(ok, "ok\n");
        var (_, lines) = Run(
            $"Invoke-BashFile '{Esc(ghost)}' '{Esc(ok)}' 2>$null");
        // file(1) reports the unopenable operand on stdout and carries on (exit 0)
        Assert.Equal(2, lines.Length);
        Assert.Contains("cannot open `", lines[0]);
        Assert.EndsWith(" ASCII text", lines[1]);   // type column is padded to the widest operand
    }

    [Fact]
    public void File_TypedOutput_CarriesSideProperties()
    {
        var f = Path.Combine(_tmpDir, "typed.txt");
        File.WriteAllText(f, "hi\n");
        var (objs, _) = Run($"Invoke-BashFile '{Esc(f)}'");
        Assert.Single(objs);
        var obj = objs[0];
        Assert.Equal("ASCII text", obj.Properties["FileType"]?.Value);
        Assert.Equal("text/plain", obj.Properties["MimeType"]?.Value);
        Assert.NotNull(obj.Properties["FileName"]?.Value);
        Assert.Contains("PsBash.TextOutput", obj.TypeNames);
    }

    [Fact]
    public void File_HelpFlag_EmitsUsage()
    {
        var (_, lines) = Run("Invoke-BashFile --help");
        Assert.NotEmpty(lines);
        Assert.Contains(
            lines,
            l => l.Contains("file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void File_ViaAlias_Works()
    {
        var f = Path.Combine(_tmpDir, "via-alias.txt");
        File.WriteAllText(f, "alias\n");
        var (_, lines) = Run($"file '{Esc(f)}'");
        Assert.Single(lines);
        Assert.EndsWith(": ASCII text", lines[0]);
    }

    [Fact]
    public void File_FilenameWithScriptblockChars_TreatedAsLiteralPath()
    {
        // Directive 12: a path containing $(...) and ; must not re-parse as
        // a PowerShell scriptblock or command separator.
        var weirdName = "$(throw'pwn');run.txt";
        var weirdPath = Path.Combine(_tmpDir, weirdName);
        File.WriteAllText(weirdPath, "safe\n");
        var (_, lines) = Run($"Invoke-BashFile -b '{Esc(weirdPath)}'");
        Assert.Single(lines);
        Assert.Equal("ASCII text", lines[0]);
    }

    // ===================== Unsupported-flag classifier =====================

    [Fact]
    public void File_ValidButUnsupportedFlag_ReportsNotSupported()
    {
        // --keep-going is a valid GNU file option ps-bash does not implement.
        // It must not be silently ignored or treated as a filename.
        var f = Path.Combine(_tmpDir, "kgsrc.txt");
        File.WriteAllText(f, "hello\n");
        var (_, errs) = RunWithErrors($"Invoke-BashFile --keep-going '{Esc(f)}'");
        Assert.Contains(errs, m => m.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void File_UnrecognizedLongOption_BashParityMessage()
    {
        var f = Path.Combine(_tmpDir, "unrecsrc.txt");
        File.WriteAllText(f, "hello\n");
        var (_, errs) = RunWithErrors($"Invoke-BashFile --bogus '{Esc(f)}'");
        Assert.Contains(errs, m =>
            m.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
            && m.Contains("--bogus", StringComparison.Ordinal));
    }

    [Fact]
    public void File_Directory_EmitsDirectoryAndMime()
    {
        var d = Path.Combine(_tmpDir, "adir"); Directory.CreateDirectory(d);
        Assert.Equal("directory", Run($"Invoke-BashFile -b '{Esc(d)}'").Item2[0]);
        Assert.Equal("inode/directory; charset=binary", Run($"Invoke-BashFile -b -i '{Esc(d)}'").Item2[0]);
    }

    [Fact]
    public void File_MimeTypeAndMimeEncoding_AreSeparateOutputs()
    {
        var f = Path.Combine(_tmpDir, "enc.txt");
        File.WriteAllBytes(f, System.Text.Encoding.UTF8.GetBytes("café\n"));
        Assert.Equal("text/plain", Run($"Invoke-BashFile -b --mime-type '{Esc(f)}'").Item2[0]);
        Assert.Equal("utf-8", Run($"Invoke-BashFile -b --mime-encoding '{Esc(f)}'").Item2[0]);
        Assert.Equal("text/plain; charset=utf-8", Run($"Invoke-BashFile -b -i '{Esc(f)}'").Item2[0]);
        Assert.Equal("Unicode text, UTF-8 text", Run($"Invoke-BashFile -b '{Esc(f)}'").Item2[0]);
    }

    [Fact]
    public void File_SeparatorAndPrint0_ShapeTheNamePrefix()
    {
        var f = Path.Combine(_tmpDir, "sep.txt");
        File.WriteAllText(f, "abc\n");
        Assert.EndsWith("sep.txt@ ASCII text", Run($"Invoke-BashFile -F '@' '{Esc(f)}'").Item2[0]);
        Assert.EndsWith("sep.txt\0: ASCII text", Run($"Invoke-BashFile -0 '{Esc(f)}'").Item2[0]);
    }

    [Fact]
    public void File_NoOperand_IsUsageError()
    {
        var (_, errs) = RunWithErrors("Invoke-BashFile");
        Assert.Contains(errs, m => m.Contains("Usage: file", StringComparison.Ordinal));
    }

    [Fact]
    public void File_Symlink_ReportedUnlessDereferenced()
    {
        var target = Path.Combine(_tmpDir, "tgt.txt"); File.WriteAllText(target, "abc\n");
        var link = Path.Combine(_tmpDir, "lnk");
        try { File.CreateSymbolicLink(link, "tgt.txt"); }
        catch (Exception) { return; } // no symlink privilege on this host (Windows without developer mode)
        Assert.Equal("symbolic link to tgt.txt", Run($"Invoke-BashFile -b '{Esc(link)}'").Item2[0]);
        Assert.Equal("ASCII text", Run($"Invoke-BashFile -b -L '{Esc(link)}'").Item2[0]);
        Assert.Equal("symbolic link to tgt.txt", Run($"Invoke-BashFile -b -h '{Esc(link)}'").Item2[0]);
    }
}