using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashBase64</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="InvokeBashBase64Command"/>.
///
/// Oracle: the psm1 <c>Invoke-BashBase64</c> function. Failure-surface axes
/// covered (per Directive 3): empty input, unicode input, file mode, pipeline
/// mode, missing-file error continuation, alias resolution, <c>--help</c>,
/// <c>-w 0</c> no-wrap, <c>-w N</c> narrow wrap, <c>-d</c> decode round-trip,
/// and a quoting/injection probe per Directive 12.
/// </summary>
public class InvokeBashBase64CommandTests : IDisposable
{
    private readonly string _tmpDir;

    public InvokeBashBase64CommandTests()
    {
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-b64-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private static string[] RunLines(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result.Select(o =>
        {
            var bashText = o?.Properties["BashText"]?.Value as string;
            return bashText ?? o?.ToString() ?? "";
        }).ToArray();
    }

    [Fact]
    public void Base64_EmptyPipeline_NoOperands_EmitsNothing()
    {
        var lines = RunLines("Invoke-BashBase64");
        Assert.Empty(lines);
    }

    [Fact]
    public void Base64_EncodeAsciiPipeline_DefaultWrap_ProducesBase64()
    {
        // Pipeline mode appends a trailing \n, so the encoded output corresponds
        // to "hello\n" (= aGVsbG8K).
        var lines = RunLines("'hello' | Invoke-BashBase64");
        Assert.Single(lines);
        Assert.Equal("aGVsbG8K", lines[0]);
    }

    [Fact]
    public void Base64_DecodeRoundTrip_Pipeline_RestoresAscii()
    {
        // Encoding of "hello\n"; decoding then strips a single trailing \n
        // (the oracle's `$output -replace "`n$", ''`), so we should see "hello".
        var lines = RunLines("'aGVsbG8K' | Invoke-BashBase64 -d");
        Assert.Single(lines);
        Assert.Equal("hello", lines[0]);
    }

    [Fact]
    public void Base64_EncodeFileMode_ReadsBytesUnchanged()
    {
        // File mode uses File.ReadAllBytes — no trailing \n added. The raw
        // 5-byte payload "hello" encodes to "aGVsbG8=".
        var file = Path.Combine(_tmpDir, "in.bin");
        File.WriteAllBytes(file, Encoding.ASCII.GetBytes("hello"));
        var lines = RunLines($"Invoke-BashBase64 '{file.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.Equal("aGVsbG8=", lines[0]);
    }

    [Fact]
    public void Base64_EncodeUnicodeBytes_File_PreservesPayload()
    {
        // Write deliberate non-ASCII UTF-8 bytes ("héllo🌍") and verify the
        // base64 output matches Convert.ToBase64String over the same bytes.
        var raw = Encoding.UTF8.GetBytes("héllo🌍");
        var file = Path.Combine(_tmpDir, "uni.bin");
        File.WriteAllBytes(file, raw);
        var expected = Convert.ToBase64String(raw);
        var lines = RunLines($"Invoke-BashBase64 '{file.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.Equal(expected, lines[0]);
    }

    [Fact]
    public void Base64_WrapZero_DisablesLineBreaks()
    {
        // A long pipeline payload that would normally wrap at column 76;
        // -w 0 must emit one unbroken line.
        var payload = new string('A', 200);
        // Pipeline mode adds a trailing \n -> 201 input bytes -> 268 base64 chars
        // (ceil(201/3)*4) — well over the 76-col wrap.
        var lines = RunLines($"'{payload}' | Invoke-BashBase64 -w 0");
        Assert.Single(lines);
        Assert.DoesNotContain("\n", lines[0]);
        Assert.DoesNotContain("\r", lines[0]);
        // Sanity-check: at least the first 76 chars are 'A'-encoded
        // ("AAAA" 19 times = "QUFBQQ..." actually let's just check the
        // length matches expectation).
        var expectedLen = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload + "\n")).Length;
        Assert.Equal(expectedLen, lines[0].Length);
    }

    [Fact]
    public void Base64_WrapTen_NarrowWrap_EmbedsLineBreaks()
    {
        // Pipeline payload long enough to span multiple wrap-lines.
        // We can't pin the exact line ending (Environment.NewLine is platform-
        // specific by oracle design), so we strip CR/LF and check the chunks.
        var payload = new string('A', 30);
        var lines = RunLines($"'{payload}' | Invoke-BashBase64 -w 10");
        Assert.Single(lines);
        // BashText embeds the wrap lines as a single string with newline-
        // separated chunks. After normalization, each non-empty chunk must
        // be <= 10 chars.
        var chunks = lines[0]
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(chunks.Length >= 2, $"expected multi-chunk wrap, got {chunks.Length}");
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Length <= 10, $"chunk '{chunk}' exceeds wrap col");
        }
    }

    [Fact]
    public void Base64_DecodeFile_RoundTrip()
    {
        // Write base64 of "rosetta\n" (= cm9zZXR0YQo=) to a file and decode.
        var file = Path.Combine(_tmpDir, "encoded.txt");
        File.WriteAllText(file, "cm9zZXR0YQo=");
        var lines = RunLines($"Invoke-BashBase64 -d '{file.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.Equal("rosetta", lines[0]);
    }

    [Fact]
    public void Base64_MissingFile_EmitsErrorContinues_NoOutput()
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Path.Combine(_tmpDir, "no-such-file.txt").Replace("'", "''");
        var result = pwsh.AddScript($"Invoke-BashBase64 '{missing}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Base64_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashBase64 --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("base64", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Base64_AliasResolution_Base64Works()
    {
        // The psm1 module registers `Set-Alias base64 Invoke-BashBase64`. The
        // binary cmdlet loads before psm1 runs, so the alias resolves here.
        var lines = RunLines("'foo' | base64");
        Assert.Single(lines);
        // "foo\n" -> Zm9vCg==
        Assert.Equal("Zm9vCg==", lines[0]);
    }

    [Fact]
    public void Base64_InjectionProbe_OperandWithInjectionChars_LiteralPath()
    {
        // Directive 12: a file operand containing `; $(throw 'INJECTED')`
        // must be treated as a literal path string — never re-parsed as
        // PowerShell script. The cmdlet routes the operand through
        // SessionState.Path, then File.ReadAllBytes — there is no script
        // body concatenation. A missing file yields no output (the error
        // goes to the error stream, suppressed via 2>$null).
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var probe = "; $(throw 'INJECTED'); echo pwned";
        var result = pwsh.AddScript(
            $"Invoke-BashBase64 '{probe.Replace("'", "''")}' 2>$null").Invoke();
        pwsh.Commands.Clear();
        Assert.Empty(result);
    }

    [Fact]
    public void Base64_DecodeWithEmbeddedWhitespace_TrimsBeforeDecode()
    {
        // The oracle calls `.Trim()` on file/pipeline text before
        // Convert.FromBase64String so trailing newlines / spaces are stripped.
        // "aGVsbG8=" decodes to "hello" (no trailing newline in the encoded
        // payload).
        var lines = RunLines("'  aGVsbG8=  ' | Invoke-BashBase64 -d");
        Assert.Single(lines);
        Assert.Equal("hello", lines[0]);
    }
}
