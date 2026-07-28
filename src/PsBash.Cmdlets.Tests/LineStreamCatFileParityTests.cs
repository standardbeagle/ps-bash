using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Byte-parity tests for the S2 <c>cat FILE…</c> streaming PRODUCER
/// (<c>CatFileStage</c>), against the real <c>Invoke-BashCat</c>.
///
/// <para>This is the stage that actually delivers S2's headline criterion: with
/// <c>sort</c> cored but <c>cat FILE</c> still declining, <c>cat f | grep x | sort</c>
/// went on falling back all-or-nothing. The last test class here proves that chain now
/// STREAMS and is byte-identical to the unfused pipeline.</para>
///
/// <para>Coverage: empty file, no trailing newline (must DECLINE), CRLF, UTF-8 BOM,
/// unicode, multiple files, missing file (exit code + stderr stay with the cmdlet), and
/// a file plus a declined flag.</para>
/// </summary>
public class LineStreamCatFileParityTests : LineStreamParityHarness, IDisposable
{
    private readonly string _dir;

    public LineStreamCatFileParityTests(SharedPwshFixture fixture) : base(fixture)
    {
        _dir = Path.Combine(Path.GetTempPath(), "psbash-catstage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Write raw bytes — the point of most of these cases is the exact byte
    /// tail (BOM, CRLF, final newline), which a text helper would normalize away.</summary>
    private string WriteFile(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private string WriteUtf8(string name, string content, bool bom = false)
        => WriteFile(name, new UTF8Encoding(bom).GetBytes(content));

    /// <summary>Core output vs the real cmdlet's, line for line, for `cat FILE…`.</summary>
    private void AssertCatFile(params string[] paths)
        => AssertCoreMatchesCmdlet("cat", "Invoke-BashCat", paths, Array.Empty<string>());

    // ── certified: the file form ─────────────────────────────────────────────

    [Fact]
    public void CatFile_SimpleFile_MatchesCmdlet()
        => AssertCatFile(WriteUtf8("a.txt", "alpha\nbeta\ngamma\n"));

    [Fact]
    public void CatFile_EmptyFile_MatchesCmdlet()
        => AssertCatFile(WriteUtf8("empty.txt", ""));

    [Fact]
    public void CatFile_SingleLine_MatchesCmdlet()
        => AssertCatFile(WriteUtf8("one.txt", "solo\n"));

    [Fact]
    public void CatFile_BlankLines_MatchesCmdlet()
        => AssertCatFile(WriteUtf8("blanks.txt", "a\n\n\nb\n\n"));

    [Fact]
    public void CatFile_CrlfFile_MatchesCmdlet()
        // CRLF→LF normalization must come from BashFileSystem, not a re-derivation.
        => AssertCatFile(WriteUtf8("crlf.txt", "alpha\r\nbeta\r\ngamma\r\n"));

    [Fact]
    public void CatFile_Utf8BomFile_MatchesCmdlet()
        // A mishandled BOM corrupts the FIRST line of every file in the lane.
        => AssertCatFile(WriteUtf8("bom.txt", "alpha\nbeta\n", bom: true));

    [Fact]
    public void CatFile_UnicodeContent_MatchesCmdlet()
        => AssertCatFile(WriteUtf8("u.txt", "café\nnaïve\n🚀 rocket\ncombininǵ\n"));

    [Fact]
    public void CatFile_LoneCarriageReturn_MatchesCmdlet()
        // A lone \r stays IN the line (only \r immediately before \n is dropped).
        => AssertCatFile(WriteUtf8("cr.txt", "a\rb\nc\n"));

    [Fact]
    public void CatFile_MultipleFiles_ConcatenateInOrder_MatchCmdlet()
        => AssertCatFile(
            WriteUtf8("m1.txt", "one\ntwo\n"),
            WriteUtf8("m2.txt", "three\n"),
            WriteUtf8("m3.txt", "four\nfive\n"));

    [Fact]
    public void CatFile_AbsolutePath_IsCertified()
        => Assert.True(LineStreamRegistry.TryCreate(
            "cat", new[] { WriteUtf8("abs.txt", "x\n") }, out _));

    [Fact]
    public void CatFile_RelativePath_ResolvesLikeTheCmdlet()
    {
        // Pins the ONE assumption CatFileStage documents: a relative operand resolves
        // against Environment.CurrentDirectory, which the ps-bash host keeps equal to
        // the PowerShell location (SdkRunspace.cs:126 seeds it; the emitter's cd writes
        // both). The test mirrors that host invariant by setting BOTH, then asserts the
        // core and the cmdlet read the same file.
        WriteUtf8("rel.txt", "r1\nr2\n");
        var q = _dir.Replace("'", "''");
        // The cmdlet side must move the PS location and read in ONE invocation (the
        // harness resets location between invocations, as the host does not).
        var expected = RenderScript($"Set-Location -LiteralPath '{q}'; Invoke-BashCat 'rel.txt'");

        var prevEnv = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _dir;
            var actual = string.Concat(
                RunCore("cat", new[] { "rel.txt" }, Array.Empty<string>())
                    .Select(l => l + Environment.NewLine));
            Assert.Equal(expected, actual);
        }
        finally
        {
            Environment.CurrentDirectory = prevEnv;
        }
    }

    // ── decline matrix ───────────────────────────────────────────────────────

    [Fact]
    public void CatFile_NoTrailingNewline_Declines()
    {
        // The lane renders every yielded line as line + NewLine, so a file whose last
        // line has no terminator would GAIN a byte. Must decline, not guess.
        var f = WriteUtf8("nonl.txt", "alpha\nbeta");
        Assert.False(LineStreamRegistry.TryCreate("cat", new[] { f }, out _));
    }

    [Fact]
    public void CatFile_MissingFile_Declines()
        // The cmdlet owns the "No such file or directory" text and exit 1; the streaming
        // lane has no stderr, so it must not take the job.
        => Assert.False(LineStreamRegistry.TryCreate(
            "cat", new[] { Path.Combine(_dir, "nope.txt") }, out _));

    [Fact]
    public void CatFile_Directory_Declines()
        => Assert.False(LineStreamRegistry.TryCreate("cat", new[] { _dir }, out _));

    [Fact]
    public void CatFile_OneMissingAmongPresent_Declines()
    {
        var ok = WriteUtf8("ok.txt", "x\n");
        Assert.False(LineStreamRegistry.TryCreate(
            "cat", new[] { ok, Path.Combine(_dir, "gone.txt") }, out _));
    }

    [Theory]
    [InlineData("-n")]
    [InlineData("-b")]
    [InlineData("-s")]
    [InlineData("-E")]
    [InlineData("-T")]
    [InlineData("--number")]
    [InlineData("--")]
    public void CatFile_WithFlag_Declines(string flag)
    {
        var f = WriteUtf8("flagged.txt", "a\nb\n");
        Assert.False(LineStreamRegistry.TryCreate("cat", new[] { flag, f }, out _),
            $"cat core must DECLINE the flagged form '{flag}'");
    }

    [Fact]
    public void CatFile_MixedStdinMarkerAndFile_Declines()
    {
        var f = WriteUtf8("mixed.txt", "a\n");
        Assert.False(LineStreamRegistry.TryCreate("cat", new[] { "-", f }, out _));
        Assert.False(LineStreamRegistry.TryCreate("cat", new[] { f, "-" }, out _));
    }

    [Theory]
    [InlineData("*.txt")]
    [InlineData("a?.txt")]
    [InlineData("[ab].txt")]
    public void CatFile_GlobOperand_Declines(string pattern)
        // Expansion belongs to the PowerShell provider, which a stage cannot reach.
        => Assert.False(LineStreamRegistry.TryCreate("cat", new[] { pattern }, out _));

    [Fact]
    public void CatStage_BareAndStdinMarker_StillCertified()
    {
        Assert.True(LineStreamRegistry.TryCreate("cat", Array.Empty<string>(), out _));
        Assert.True(LineStreamRegistry.TryCreate("cat", new[] { "-" }, out _));
    }

    // ── THE criterion: cat f | grep x | sort streams, byte-identical ─────────

    [Fact]
    public void Streamed_CatGrepSort_TheNinetyPercentShape_ByteIdenticalToUnfused()
    {
        // S2's headline acceptance criterion. The -Fallback THROWS, so this passing
        // proves the chain took the STREAMING lane — no per-line PSObject — and the
        // equality proves it is byte-for-byte the unfused pipeline.
        var f = WriteUtf8("corpus.txt", BuildCorpus());
        var q = f.Replace("'", "''");
        AssertStreamedMatchesUnfused(
            $"Invoke-BashCat '{q}' | Invoke-BashGrep x | Invoke-BashSort",
            $"@(@('cat','{q}'),@('grep','x'),@('sort'))");
    }

    [Fact]
    public void Streamed_CatGrepSortUniq_ByteIdenticalToUnfused()
    {
        var f = WriteUtf8("corpus2.txt", BuildCorpus());
        var q = f.Replace("'", "''");
        AssertStreamedMatchesUnfused(
            $"Invoke-BashCat '{q}' | Invoke-BashGrep x | Invoke-BashSort -u | Invoke-BashUniq -c",
            $"@(@('cat','{q}'),@('grep','x'),@('sort','-u'),@('uniq','-c'))");
    }

    [Fact]
    public void Streamed_CatSortNumeric_ByteIdenticalToUnfused()
    {
        var sb = new StringBuilder();
        for (int i = 200; i > 0; i -= 7) sb.Append(i).Append('\n');
        var f = WriteUtf8("nums.txt", sb.ToString());
        var q = f.Replace("'", "''");
        AssertStreamedMatchesUnfused(
            $"Invoke-BashCat '{q}' | Invoke-BashSort -n",
            $"@(@('cat','{q}'),@('sort','-n'))");
    }

    [Fact]
    public void Streamed_CatWithNoTrailingNewline_FallsBackAndStaysByteIdentical()
    {
        // The decline path end-to-end: a no-final-newline file must produce the SAME
        // bytes as the unfused pipeline by going through the fallback, which here does
        // the real work.
        var f = WriteUtf8("nonl2.txt", "xb\nxa\nxc");
        var q = f.Replace("'", "''");
        var inner = $"Invoke-BashCat '{q}' | Invoke-BashGrep x | Invoke-BashSort";
        Assert.Equal(
            RenderScript(inner),
            RenderScript($"Invoke-BashFusedPipeline -Stages @(@('cat','{q}'),@('grep','x'),@('sort')) "
                       + $"-Fallback {{ {inner} }}"));
    }

    private static string BuildCorpus()
    {
        var sb = new StringBuilder();
        string[] words = { "xray", "alpha", "xenon", "beta", "xylem", "café", "x🚀", "xray" };
        for (int i = 0; i < words.Length; i++) sb.Append(words[i]).Append('\n');
        return sb.ToString();
    }
}
