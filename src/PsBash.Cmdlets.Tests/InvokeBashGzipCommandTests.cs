using System.IO.Compression;
using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashGzip</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashGzipCommand"/>.
///
/// Oracle: the psm1 <c>Invoke-BashGzip</c> function. Failure-surface axes
/// covered (per Directive 3): empty operands (missing-operand error), unicode
/// payload, file mode compress + decompress round-trip, missing-file error
/// continuation, alias resolution (<c>gunzip</c> and <c>zcat</c>), <c>--help</c>,
/// <c>-c</c> stdout, <c>-k</c> keep, <c>-l</c> list, <c>-9</c> and <c>-1</c>
/// level selection, and a quoting/injection probe per Directive 12.
/// </summary>
public class InvokeBashGzipCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmpDir;
    private readonly SharedPwshFixture _fixture;

    public InvokeBashGzipCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-gz-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o =>
        {
            var bashText = o?.Properties["BashText"]?.Value as string;
            return bashText ?? o?.ToString() ?? "";
        }).ToArray();
    }

    private System.Collections.ObjectModel.Collection<System.Management.Automation.PSObject> RunObjects(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    private static string PsQuote(string p) => p.Replace("'", "''");

    // GNU gzip refuses existing outputs without -f and leaves them intact on decode failure.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gzip_ExistingOutputWithoutForce_PreservesBothFiles(bool decompress)
    {
        string plain = Path.Combine(_tmpDir, "existing.txt");
        File.WriteAllText(plain, "valuable original");
        using (var output = File.Create(plain + ".gz"))
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            gzip.Write(Encoding.UTF8.GetBytes("archive content"));
        var archive = File.ReadAllBytes(plain + ".gz");

        CmdResult.Run(_fixture.AcquireFresh(),
            $"Invoke-BashGzip {(decompress ? "-d " : "")}'{PsQuote(decompress ? plain + ".gz" : plain)}'")
            .AssertFailed(2, "already exists");

        Assert.Equal("valuable original", File.ReadAllText(plain));
        Assert.Equal(archive, File.ReadAllBytes(plain + ".gz"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gzip_CorruptInput_PreservesDestinationAndSource(bool force)
    {
        string plain = Path.Combine(_tmpDir, "corrupt.txt");
        if (force) File.WriteAllText(plain, "valuable original");
        File.WriteAllText(plain + ".gz", "invalid gzip data");

        CmdResult.Run(_fixture.AcquireFresh(),
            $"Invoke-BashGzip -d {(force ? "-f " : "")}'{PsQuote(plain + ".gz")}'")
            .AssertFailed(1);

        if (force) Assert.Equal("valuable original", File.ReadAllText(plain));
        else Assert.False(File.Exists(plain));
        Assert.Equal("invalid gzip data", File.ReadAllText(plain + ".gz"));
        Assert.Equal(new[] { Path.GetFileName(plain + ".gz") }.Concat(force ? new[] { Path.GetFileName(plain) } : Array.Empty<string>()).Order(),
            Directory.GetFiles(_tmpDir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Gzip_Force_ReplacesExistingOutputAndRoundTrips()
    {
        string plain = Path.Combine(_tmpDir, "forced.txt");
        File.WriteAllText(plain, "new payload");
        File.WriteAllText(plain + ".gz", "old archive");
        CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashGzip -f '{PsQuote(plain)}'").AssertSuccess();
        Assert.False(File.Exists(plain));
        File.WriteAllText(plain, "old plain file");
        CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashGzip -d -f '{PsQuote(plain + ".gz")}'").AssertSuccess();
        Assert.Equal("new payload", File.ReadAllText(plain));
        Assert.False(File.Exists(plain + ".gz"));
    }

    [Fact]
    public void Gzip_EmptyArchive_DoesNotReplaceOutputEvenWithForce()
    {
        string plain = Path.Combine(_tmpDir, "empty-archive.txt");
        File.WriteAllText(plain, "valuable original");
        File.WriteAllBytes(plain + ".gz", Array.Empty<byte>());
        CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashGzip -d -f '{PsQuote(plain + ".gz")}'").AssertFailed(1);
        Assert.Equal("valuable original", File.ReadAllText(plain));
        Assert.True(File.Exists(plain + ".gz"));
    }

    [Fact]
    public void Gzip_EmptyFile_ProducesACompleteMemberAndRoundTrips()
    {
        string plain = Path.Combine(_tmpDir, "empty.txt");
        File.WriteAllBytes(plain, Array.Empty<byte>());
        CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashGzip '{PsQuote(plain)}'").AssertSuccess();
        Assert.True(new FileInfo(plain + ".gz").Length >= 20, "Even an empty gzip member needs its header, deflate block and trailer");
        CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashGzip -d '{PsQuote(plain + ".gz")}'").AssertSuccess();
        Assert.Empty(File.ReadAllBytes(plain));
        Assert.False(File.Exists(plain + ".gz"));
    }

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
    public void Gzip_CompressDecompress_RoundTrip_RestoresFile()
    {
        // Write a known payload, compress in-place, then decompress in-place.
        // The final file must match the original bytes.
        string file = Path.Combine(_tmpDir, "rt.txt");
        byte[] original = Encoding.UTF8.GetBytes("hello rosetta\nline 2\n");
        File.WriteAllBytes(file, original);

        RunLines($"Invoke-BashGzip '{PsQuote(file)}'");
        Assert.False(File.Exists(file), "default compress should remove the source");
        Assert.True(File.Exists(file + ".gz"));

        RunLines($"Invoke-BashGzip -d '{PsQuote(file + ".gz")}'");
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(file + ".gz"));
        Assert.Equal(original, File.ReadAllBytes(file));
    }

    [Fact]
    public void Gzip_StdoutFlag_EmitsTheExactGzipBytes()
    {
        // With -c the compressed stream goes to the pipeline as its EXACT bytes (escaped-byte markers,
        // RawBytes — the old base64 detour is gone). Source file must remain in place.
        string file = Path.Combine(_tmpDir, "c.txt");
        byte[] original = Encoding.UTF8.GetBytes("payload");
        File.WriteAllBytes(file, original);

        var records = RunObjects($"Invoke-BashGzip -c '{PsQuote(file)}'");
        Assert.True(File.Exists(file), "-c must not remove the source");
        byte[] decoded = PsBash.Core.RawBytes.GetBytes(
            PsBash.Cmdlets.BashRuntime.RecordStreamText(records.Cast<object>()));
        Assert.Equal(new byte[] { 0x1F, 0x8B }, decoded.Take(2).ToArray()); // gzip magic
        using var ms = new MemoryStream(decoded);
        using var gs = new GZipStream(ms, CompressionMode.Decompress);
        using var buf = new MemoryStream();
        gs.CopyTo(buf);
        Assert.Equal(original, buf.ToArray());
    }

    [Fact]
    public void Gzip_KeepFlag_PreservesSource()
    {
        string file = Path.Combine(_tmpDir, "k.txt");
        File.WriteAllText(file, "keepme");
        RunLines($"Invoke-BashGzip -k '{PsQuote(file)}'");
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(file + ".gz"));
    }

    [Fact]
    public void Gzip_DecompressToStdout_EmitsUtf8Text()
    {
        // Pre-compress a known payload so decompress can be exercised
        // independently of the compress path.
        string file = Path.Combine(_tmpDir, "d.gz");
        byte[] payload = Encoding.UTF8.GetBytes("hello stdout\n");
        using (var fs = File.OpenWrite(file))
        using (var gs = new GZipStream(fs, CompressionLevel.Optimal))
        {
            gs.Write(payload, 0, payload.Length);
        }

        string[] lines = RunLines($"Invoke-BashGzip -dc '{PsQuote(file)}'");
        Assert.Single(lines);
        // BashRuntime.NewBashObject normalizes a single trailing '\n' off
        // BashText (oracle parity with the psm1 New-BashObject helper).
        Assert.Equal("hello stdout", lines[0]);
        Assert.True(File.Exists(file), "-c must not remove the source");
    }

    [Fact]
    public void Gzip_ListFlag_EmitsTypedListingOutput()
    {
        string file = Path.Combine(_tmpDir, "l.gz");
        byte[] payload = Encoding.UTF8.GetBytes("aaaaaaaaaabbbbbbbbbb");
        using (var fs = File.OpenWrite(file))
        using (var gs = new GZipStream(fs, CompressionLevel.Optimal))
        {
            gs.Write(payload, 0, payload.Length);
        }

        var objs = RunObjects($"Invoke-BashGzip -l '{PsQuote(file)}'");
        // GNU's header line, then one row (no totals row for a single name).
        Assert.Equal(2, objs.Count);
        Assert.Equal("         compressed        uncompressed  ratio uncompressed_name", objs[0].BaseObject as string);   // a text record
        var o = objs[1];
        // GNU names the operand as typed, minus the suffix.
        Assert.Equal(file[..^3], o.Properties["FileName"].Value);
        Assert.Contains("PsBash.GzipListOutput", o.TypeNames);
        // Sizes are 64-bit so a >2 GB member can't overflow the listing.
        Assert.Equal((long)payload.Length, (long)o.Properties["UncompressedSize"].Value);
        Assert.True(((long)o.Properties["CompressedSize"].Value) > 0);
    }

    [Fact]
    public void Gzip_Level9_CompressesToValidGzip()
    {
        // Level 9 picks SmallestSize. We don't pin the exact compressed bytes
        // (compressor versioning is out of our control); we only verify the
        // produced file decompresses back to the source.
        string file = Path.Combine(_tmpDir, "nine.txt");
        byte[] original = Encoding.UTF8.GetBytes(new string('Z', 1000));
        File.WriteAllBytes(file, original);

        RunLines($"Invoke-BashGzip -9 -k '{PsQuote(file)}'");
        Assert.True(File.Exists(file + ".gz"));
        using var fs = File.OpenRead(file + ".gz");
        using var gs = new GZipStream(fs, CompressionMode.Decompress);
        using var buf = new MemoryStream();
        gs.CopyTo(buf);
        Assert.Equal(original, buf.ToArray());
    }

    [Fact]
    public void Gzip_Level1_CompressesToValidGzip()
    {
        // Level 1 picks Fastest. Same correctness check as -9.
        string file = Path.Combine(_tmpDir, "one.txt");
        byte[] original = Encoding.UTF8.GetBytes("fast level payload");
        File.WriteAllBytes(file, original);

        RunLines($"Invoke-BashGzip -1 -k '{PsQuote(file)}'");
        Assert.True(File.Exists(file + ".gz"));
        using var fs = File.OpenRead(file + ".gz");
        using var gs = new GZipStream(fs, CompressionMode.Decompress);
        using var buf = new MemoryStream();
        gs.CopyTo(buf);
        Assert.Equal(original, buf.ToArray());
    }

    [Fact]
    public void Gzip_GunzipAlias_DefaultsToDecompress()
    {
        // The `gunzip` alias names Invoke-BashGunzip (gzip -d).
        string file = Path.Combine(_tmpDir, "ga.txt");
        byte[] original = Encoding.UTF8.GetBytes("via gunzip\n");
        File.WriteAllBytes(file, original);
        RunLines($"Invoke-BashGzip '{PsQuote(file)}'");
        Assert.True(File.Exists(file + ".gz"));
        Assert.False(File.Exists(file));

        RunLines($"gunzip '{PsQuote(file + ".gz")}'");
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(file + ".gz"));
        Assert.Equal(original, File.ReadAllBytes(file));
    }

    [Fact]
    public void Gzip_ZcatAlias_EmitsDecompressedTextWithoutTouchingSource()
    {
        // The `zcat FILE.gz` alias boosts -dc — decompress to stdout, leave
        // the source in place.
        string file = Path.Combine(_tmpDir, "z.gz");
        byte[] payload = Encoding.UTF8.GetBytes("zcat works\n");
        using (var fs = File.OpenWrite(file))
        using (var gs = new GZipStream(fs, CompressionLevel.Optimal))
        {
            gs.Write(payload, 0, payload.Length);
        }

        string[] lines = RunLines($"zcat '{PsQuote(file)}'");
        Assert.Single(lines);
        // Trailing '\n' stripped by BashRuntime.NewBashObject.
        Assert.Equal("zcat works", lines[0]);
        Assert.True(File.Exists(file), "zcat must not remove the source");
    }

    // REGRESSION: gunzip / zcat were told apart by InvocationName, which is `&` under an indirect call
    // (`xargs gunzip`, `env zcat f`): gunzip then COMPRESSED (here: refused "already has .gz suffix").
    [Fact]
    public void Gunzip_IndirectCall_StillDecompresses()
    {
        string file = Path.Combine(_tmpDir, "ind.txt");
        File.WriteAllText(file, "indirect\n");
        RunLines($"Invoke-BashGzip '{PsQuote(file)}'");
        var r = CmdResult.Run(_fixture.AcquireFresh(), $"$c = 'gunzip'; & $c '{PsQuote(file + ".gz")}'");
        r.AssertSuccess();
        Assert.Equal("indirect\n", File.ReadAllText(file));
        Assert.False(File.Exists(file + ".gz"));
    }

    // Direct calls (Pester): -c / -d / -v bind the decoys, which the gunzip / zcat cmdlets inherit.
    [Fact]
    public void Zcat_DirectInvocation_WritesStdoutAndKeepsSource()
    {
        string file = Path.Combine(_tmpDir, "dz.txt");
        File.WriteAllText(file, "direct zcat\n");
        RunLines($"Invoke-BashGzip '{PsQuote(file)}'");
        Assert.Equal(new[] { "direct zcat" }, RunLines($"Invoke-BashZcat '{PsQuote(file + ".gz")}'"));
        Assert.Equal(new[] { "direct zcat" }, RunLines($"Invoke-BashGunzip -c '{PsQuote(file + ".gz")}'"));
        Assert.True(File.Exists(file + ".gz"));
    }

    // `gunzip NAME` with only NAME.gz on disk decompresses NAME.gz (GNU tries the suffix).
    [Fact]
    public void Gunzip_NameWithoutSuffix_TriesNameDotGz()
    {
        string file = Path.Combine(_tmpDir, "bare.txt");
        File.WriteAllText(file, "bare\n");
        RunLines($"Invoke-BashGzip '{PsQuote(file)}'");
        CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashGunzip '{PsQuote(file)}'").AssertSuccess();
        Assert.Equal("bare\n", File.ReadAllText(file));
    }

    // GNU status: a warning is 2 (even under -q), an error 1 and wins over a warning.
    [Fact]
    public void Gzip_DirectoryOperand_WarnsWithStatus2_ErrorWins()
    {
        string dir = Path.Combine(_tmpDir, "wd");
        Directory.CreateDirectory(dir);
        CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashGzip -q '{PsQuote(dir)}'").AssertFailed(2);
        CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashGzip '{PsQuote(dir)}' '{PsQuote(Path.Combine(_tmpDir, "nosuch"))}'")
            .AssertFailed(1, "is a directory -- ignored", "No such file or directory");
    }

    [Theory]
    [InlineData(-2, 6, "-33.3%")]
    [InlineData(1, 2, " 50.0%")]
    [InlineData(-2, 2, "-100.0%")]
    [InlineData(0, 0, "  0.0%")]
    [InlineData(-5, 3000, " -0.2%")]    // gzip -v of 3000 random bytes: rounded, sign beside the digits
    [InlineData(73, 81, " 90.1%")]
    [InlineData(-24, 12, "-200.0%")]
    public void Ratio_IsGnuDisplayRatio(long num, long den, string expected)
        => Assert.Equal(expected, InvokeBashGzipCommand.Ratio(num, den));

    // GNU's header_bytes: 10-byte header (+ stored name "a\0") + 8-byte trailer. Bytes from `gzip -k a` / `gzip -n`.
    [Theory]
    [InlineData("1f8b08080000000000036100cb48cdc9c9e702002030 3a3606000000", 20)]
    [InlineData("1f8b08000000000000 03cb48cdc9c95728cf2fca4951c840b0b900790d36831800 0000", 18)]
    [InlineData("1f8b", 18)]
    public void MemberOverhead_CountsHeaderFieldsAndTrailer(string hex, long expected)
    {
        using var s = new MemoryStream(Convert.FromHexString(hex.Replace(" ", "")));
        Assert.Equal(expected, InvokeBashGzipCommand.MemberOverhead(s));
    }

    [Fact]
    public void Gzip_HelpFlag_EmitsUsage()
    {
        string[] lines = RunLines("Invoke-BashGzip --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("gzip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Gzip_MissingFile_EmitsErrorContinues_NoOutput()
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        string missing = Path.Combine(_tmpDir, "no-such.txt").Replace("'", "''");
        var result = pwsh.AddScript($"Invoke-BashGzip '{missing}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Gzip_InjectionProbe_OperandWithThrowExpr_StaysLiteralPath()
    {
        // Directive 12: a file operand containing `$(throw 'pwn')` and a `.gz`
        // suffix must be treated as a literal path string. The cmdlet routes
        // it through SessionState.Path -> File.Exists -> no-such-file branch
        // (which writes to the error stream, suppressed via 2>$null) without
        // ever re-parsing the operand as PowerShell. The throw never fires.
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        string probe = "$(throw 'pwn').gz";
        var result = pwsh.AddScript(
            $"Invoke-BashGzip '{probe.Replace("'", "''")}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Gzip_Compress_ReadOnlySource_ForceDeletesOriginal()
    {
        // Default compress removes the source. If the source is read-only
        // (e.g. a file under a .git pack dir) a bare File.Delete throws on
        // Windows and the original lingers; the cmdlet must force-delete it.
        string file = Path.Combine(_tmpDir, "readonly.txt");
        File.WriteAllText(file, "force-delete-me");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            RunLines($"Invoke-BashGzip '{PsQuote(file)}'");
            Assert.True(File.Exists(file + ".gz"), "compressed output should exist");
            Assert.False(File.Exists(file),
                "compress should force-delete a read-only source");
        }
        finally
        {
            if (File.Exists(file)) { File.SetAttributes(file, FileAttributes.Normal); }
        }
    }

    // ===================== Full-flag support =====================

    [Fact]
    public void Gzip_Recursive_CompressesFilesUnderDirectory()
    {
        // -r/--recursive: a directory operand is expanded into its files, each
        // compressed in place. Was previously refused as "not supported".
        var sub = Path.Combine(_tmpDir, "tree", "nested");
        Directory.CreateDirectory(sub);
        var a = Path.Combine(_tmpDir, "tree", "a.txt");
        var b = Path.Combine(sub, "b.txt");
        File.WriteAllText(a, "aaa");
        File.WriteAllText(b, "bbb");

        RunLines($"Invoke-BashGzip -r '{PsQuote(Path.Combine(_tmpDir, "tree"))}'");

        Assert.True(File.Exists(a + ".gz"), "top-level file compressed");
        Assert.True(File.Exists(b + ".gz"), "nested file compressed");
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void Gzip_AlreadyHasSuffix_RefusedUnchanged()
    {
        // GNU refuses to compress a file already ending in the suffix. It must
        // NOT produce foo.gz.gz nor delete foo.gz.
        string file = Path.Combine(_tmpDir, "done.txt");
        File.WriteAllText(file, "payload");
        RunLines($"Invoke-BashGzip '{PsQuote(file)}'");
        Assert.True(File.Exists(file + ".gz"));

        var (_, errs) = RunWithErrors($"Invoke-BashGzip '{PsQuote(file + ".gz")}'");
        Assert.False(File.Exists(file + ".gz.gz"), "must not double-compress");
        Assert.True(File.Exists(file + ".gz"), "must not delete the already-compressed file");
        Assert.Contains(errs, m => m.Contains("already has", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Gzip_Recursive_SkipsAlreadyCompressedMembers()
    {
        // Regression: `gzip -r` over a tree containing a *.gz member must skip it
        // (not re-compress to *.gz.gz and delete the original).
        var dir = Path.Combine(_tmpDir, "rtree");
        Directory.CreateDirectory(dir);
        var plain = Path.Combine(dir, "p.txt");
        var already = Path.Combine(dir, "c.txt.gz");
        File.WriteAllText(plain, "plain");
        File.WriteAllText(already, "pretend-gz");

        RunLines($"Invoke-BashGzip -r -q '{PsQuote(dir)}'");

        Assert.True(File.Exists(plain + ".gz"), "plain file compressed");
        Assert.False(File.Exists(plain), "plain source removed");
        Assert.True(File.Exists(already), "existing .gz left in place");
        Assert.False(File.Exists(already + ".gz"), "existing .gz not double-compressed");
    }

    [Fact]
    public void Gzip_CustomSuffix_RoundTrips()
    {
        // -S SUF: compress appends the custom suffix; -d -S SUF strips it.
        string file = Path.Combine(_tmpDir, "s.txt");
        byte[] original = Encoding.UTF8.GetBytes("suffix payload");
        File.WriteAllBytes(file, original);

        RunLines($"Invoke-BashGzip -S .gzz '{PsQuote(file)}'");
        Assert.True(File.Exists(file + ".gzz"));
        Assert.False(File.Exists(file + ".gz"));

        RunLines($"Invoke-BashGzip -d -S .gzz '{PsQuote(file + ".gzz")}'");
        Assert.True(File.Exists(file));
        Assert.Equal(original, File.ReadAllBytes(file));
    }

    [Fact]
    public void Gzip_NoNameFlag_AcceptedAsNoOp()
    {
        // -n / --no-name has no faithful .NET header mapping → accept-and-ignore
        // (must NOT error). The compress still happens.
        string file = Path.Combine(_tmpDir, "n.txt");
        File.WriteAllText(file, "x");
        var (_, errs) = RunWithErrors($"Invoke-BashGzip -n '{PsQuote(file)}'");
        Assert.Empty(errs);
        Assert.True(File.Exists(file + ".gz"));
    }

    // ===================== Unsupported-flag classifier =====================

    [Fact]
    public void Gzip_UnrecognizedLongOption_BashParityMessage()
    {
        var (_, errs) = RunWithErrors("Invoke-BashGzip --bogus");
        Assert.Contains(errs, m =>
            m.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
            && m.Contains("--bogus", StringComparison.Ordinal));
    }

    [Fact]
    public void Gzip_UnrecognizedShortOption_BashParityMessage()
    {
        // A bundled unknown short char is rejected with "invalid option -- 'X'".
        var (_, errs) = RunWithErrors("Invoke-BashGzip -dQ");
        Assert.Contains(errs, m =>
            m.Contains("invalid option", StringComparison.OrdinalIgnoreCase)
            && m.Contains("Q", StringComparison.Ordinal));
    }
}
