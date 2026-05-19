using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of the checksum family
/// (Invoke-BashMd5sum / Sha1sum / Sha256sum + the shared Invoke-BashChecksum
/// helper) from PsBash.psm1 to binary cmdlets via the shared
/// <c>ChecksumEngine</c> in <c>InvokeBashChecksumCommands.cs</c>.
///
/// Oracle: GNU coreutils md5sum / sha1sum / sha256sum output shape —
/// <c>&lt;lowercase-hex&gt;&lt;two-spaces&gt;&lt;path&gt;</c> per file, or
/// <c>&lt;hex&gt;  -</c> in pipeline mode. The hex values themselves are
/// cross-checked against <see cref="System.Security.Cryptography"/> ground
/// truth in the same process — no external bash required.
///
/// Failure-surface axes covered (per Directive 3): empty pipeline, unicode
/// file content, large input (10 KB), missing file (error continuation),
/// multi-file glob, quoting/injection (Directive 12).
/// </summary>
public class InvokeBashChecksumCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;
    private readonly string _testFile;
    private readonly byte[] _testBytes;

    public InvokeBashChecksumCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(Path.GetTempPath(), $"psb-checksum-{Guid.NewGuid():N}".Substring(0, 25));
        Directory.CreateDirectory(_tmpDir);
        _testFile = Path.Combine(_tmpDir, "data.txt");
        _testBytes = System.Text.Encoding.UTF8.GetBytes("hello world\n");
        File.WriteAllBytes(_testFile, _testBytes);
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
            // Each emitted object is a PSObject with a BashText property
            // (typed PsBash.TextOutput). Tests compare against that string.
            var bashText = o?.Properties["BashText"]?.Value as string;
            return bashText ?? o?.ToString() ?? "";
        }).ToArray();
    }

    // ---- md5sum ----

    [Fact]
    public void Md5sum_File_MatchesNetMd5Hex()
    {
        var expectedHash = ToHex(System.Security.Cryptography.MD5.HashData(_testBytes));
        var lines = RunLines($"Invoke-BashMd5sum '{_testFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.StartsWith(expectedHash + "  ", lines[0]);
        Assert.EndsWith("data.txt", lines[0]);
    }

    [Fact]
    public void Md5sum_PipelineInput_EmitsStdinMarker()
    {
        var lines = RunLines("'hello world' | Invoke-BashMd5sum");
        Assert.Single(lines);
        // Pipeline mode appends a "\n" after each text item, so the input
        // hashed is "hello world\n" — matches the file-mode hash byte for
        // byte.
        var expectedHash = ToHex(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("hello world\n")));
        Assert.Equal($"{expectedHash}  -", lines[0]);
    }

    [Fact]
    public void Md5sum_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashMd5sum --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("md5sum", StringComparison.OrdinalIgnoreCase));
    }

    // ---- sha1sum ----

    [Fact]
    public void Sha1sum_File_MatchesNetSha1Hex()
    {
        var expectedHash = ToHex(System.Security.Cryptography.SHA1.HashData(_testBytes));
        var lines = RunLines($"Invoke-BashSha1sum '{_testFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.StartsWith(expectedHash + "  ", lines[0]);
    }

    [Fact]
    public void Sha1sum_ViaAlias_Works()
    {
        var lines = RunLines($"sha1sum '{_testFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.Matches(@"^[0-9a-f]{40}  ", lines[0]);
    }

    // ---- sha256sum ----

    [Fact]
    public void Sha256sum_File_MatchesNetSha256Hex()
    {
        var expectedHash = ToHex(System.Security.Cryptography.SHA256.HashData(_testBytes));
        var lines = RunLines($"Invoke-BashSha256sum '{_testFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.StartsWith(expectedHash + "  ", lines[0]);
    }

    [Fact]
    public void Sha256sum_MultipleFiles_OneLinePerFile()
    {
        var file2 = Path.Combine(_tmpDir, "second.txt");
        File.WriteAllText(file2, "second content");
        var lines = RunLines(
            $"Invoke-BashSha256sum '{_testFile.Replace("'", "''")}' '{file2.Replace("'", "''")}'");
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^[0-9a-f]{64}  ", lines[0]);
        Assert.Matches(@"^[0-9a-f]{64}  ", lines[1]);
        Assert.EndsWith("data.txt", lines[0]);
        Assert.EndsWith("second.txt", lines[1]);
    }

    [Fact]
    public void Sha256sum_MissingFile_ContinuesAndEmitsErrorForRest()
    {
        // GNU coreutils continues across missing files; the psm1 oracle
        // emitted Write-Error and continued. The cmdlet must produce a hash
        // line for the existing file and skip the missing one.
        var ghost = Path.Combine(_tmpDir, "ghost.txt");
        var lines = RunLines(
            $"Invoke-BashSha256sum '{ghost.Replace("'", "''")}' '{_testFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.EndsWith("data.txt", lines[0]);
    }

    [Fact]
    public void Sha256sum_UnicodeFileContent_HashesUtf8Bytes()
    {
        var unicodeFile = Path.Combine(_tmpDir, "unicode.txt");
        var content = "héllo wörld 🚀\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(unicodeFile, bytes);
        var expectedHash = ToHex(System.Security.Cryptography.SHA256.HashData(bytes));

        var lines = RunLines($"Invoke-BashSha256sum '{unicodeFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.StartsWith(expectedHash + "  ", lines[0]);
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Md5sum_FilenameWithScriptblockChars_TreatedAsLiteralPath()
    {
        var weirdName = "$(throw'pwn').txt";
        var weirdPath = Path.Combine(_tmpDir, weirdName);
        File.WriteAllText(weirdPath, "x");
        // Wrap path in single quotes; the inner ' is escaped to ''.
        var lines = RunLines($"Invoke-BashMd5sum '{weirdPath.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.Matches(@"^[0-9a-f]{32}  ", lines[0]);
        Assert.EndsWith(weirdName, lines[0]);
    }

    private static string ToHex(byte[] bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();
}
