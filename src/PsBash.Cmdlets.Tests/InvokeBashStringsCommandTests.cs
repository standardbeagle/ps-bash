using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of
/// <c>Invoke-BashStrings</c> from PsBash.psm1 to the binary cmdlet
/// <see cref="PsBash.Cmdlets.InvokeBashStringsCommand"/>.
///
/// Oracle: the original psm1 function's regex pattern
/// <c>[\x20-\x7E]{N,}</c> applied to the CRLF-normalized text of each input
/// file (or the <c>\n</c>-joined pipeline content). The expected outputs in
/// these tests are computed against that same regex on the same input, so the
/// cases double as ground-truth assertions and oracle-parity checks.
///
/// Failure-surface axes covered (per Directive 3): empty input (file with no
/// printable runs), unicode (multi-byte UTF-8 split by non-ASCII bytes),
/// multi-file emission, <c>-n</c> threshold, missing file (error
/// continuation), <c>--help</c>, alias resolution, and a quoting / injection
/// probe (Directive 12).
/// </summary>
public class InvokeBashStringsCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashStringsCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(),
            $"psb-strings-{Guid.NewGuid():N}".Substring(0, 25));
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
            // Output is a bare string (PsBash.TextOutput fast path) or a
            // PSObject with BashText.
            if (o == null) return "";
            var bashText = o.Properties["BashText"]?.Value as string;
            if (bashText != null) return bashText;
            return o.ToString() ?? "";
        }).ToArray();
    }

    private static string Esc(string path) => path.Replace("'", "''");

    [Fact]
    public void Strings_BinaryFile_ExtractsKnownPrintableRun()
    {
        // Embed "HELLO_WORLD" (11 printable ASCII chars) inside non-printable
        // bytes. Default -n 4 should extract exactly that run.
        var file = Path.Combine(_tmpDir, "bin.dat");
        var bytes = new byte[] { 0x00, 0x01, 0x02 }
            .Concat(System.Text.Encoding.ASCII.GetBytes("HELLO_WORLD"))
            .Concat(new byte[] { 0x00, 0x01, 0x02 })
            .ToArray();
        File.WriteAllBytes(file, bytes);

        var lines = RunLines($"Invoke-BashStrings '{Esc(file)}'");
        Assert.Contains("HELLO_WORLD", lines);
    }

    [Fact]
    public void Strings_NoPrintableRuns_EmitsNothing()
    {
        // File with only control bytes shorter than the default min run.
        var file = Path.Combine(_tmpDir, "ctrl.dat");
        File.WriteAllBytes(file, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });
        var lines = RunLines($"Invoke-BashStrings '{Esc(file)}'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Strings_NFlag_RaisesMinimumRunLength()
    {
        // "abc" (3) below -n 5; "abcdefgh" (8) above. Only the long run
        // should survive.
        var file = Path.Combine(_tmpDir, "mixed.dat");
        var bytes = System.Text.Encoding.ASCII.GetBytes("abc")
            .Concat(new byte[] { 0x00 })
            .Concat(System.Text.Encoding.ASCII.GetBytes("abcdefgh"))
            .ToArray();
        File.WriteAllBytes(file, bytes);

        var lines = RunLines($"Invoke-BashStrings -n 5 '{Esc(file)}'");
        Assert.Single(lines);
        Assert.Equal("abcdefgh", lines[0]);
    }

    [Fact]
    public void Strings_MultiFile_EmitsRunsFromAllFiles()
    {
        var f1 = Path.Combine(_tmpDir, "a.dat");
        var f2 = Path.Combine(_tmpDir, "b.dat");
        File.WriteAllBytes(
            f1,
            new byte[] { 0x00 }
                .Concat(System.Text.Encoding.ASCII.GetBytes("FIRST_RUN"))
                .Concat(new byte[] { 0x00 })
                .ToArray());
        File.WriteAllBytes(
            f2,
            new byte[] { 0x00 }
                .Concat(System.Text.Encoding.ASCII.GetBytes("SECOND_RUN"))
                .Concat(new byte[] { 0x00 })
                .ToArray());

        var lines = RunLines(
            $"Invoke-BashStrings '{Esc(f1)}' '{Esc(f2)}'");
        Assert.Contains("FIRST_RUN", lines);
        Assert.Contains("SECOND_RUN", lines);
    }

    [Fact]
    public void Strings_UnicodeContent_SplitsRunsAtNonAsciiChars()
    {
        // The non-ASCII char (é, decoded from UTF-8) is outside \x20-\x7E,
        // so the printable runs on each side are emitted separately.
        var file = Path.Combine(_tmpDir, "unicode.dat");
        File.WriteAllBytes(file, System.Text.Encoding.UTF8.GetBytes("helloéworld"));

        var lines = RunLines($"Invoke-BashStrings -n 3 '{Esc(file)}'");
        Assert.Contains("hello", lines);
        Assert.Contains("world", lines);
    }

    [Fact]
    public void Strings_PipelineInput_ScansJoinedContent()
    {
        // No operand → read from pipeline. The two items are joined with \n,
        // and the printable runs (\n is non-printable) are emitted separately.
        var lines = RunLines(
            "'alpha-bravo','charlie-delta' | Invoke-BashStrings -n 3");
        Assert.Contains("alpha-bravo", lines);
        Assert.Contains("charlie-delta", lines);
    }

    [Fact]
    public void Strings_MissingFile_ContinuesAndEmitsForRest()
    {
        var ghost = Path.Combine(_tmpDir, "ghost.dat");
        var ok = Path.Combine(_tmpDir, "ok.dat");
        File.WriteAllBytes(
            ok,
            new byte[] { 0x00 }
                .Concat(System.Text.Encoding.ASCII.GetBytes("VISIBLE_TEXT"))
                .ToArray());

        // Suppress the error stream so it doesn't fail the test runner; we
        // only care that the existing file still produces its run.
        var lines = RunLines(
            $"Invoke-BashStrings '{Esc(ghost)}' '{Esc(ok)}' 2>$null");
        Assert.Contains("VISIBLE_TEXT", lines);
    }

    [Fact]
    public void Strings_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashStrings --help");
        Assert.NotEmpty(lines);
        Assert.Contains(
            lines,
            l => l.Contains("strings", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Strings_ViaAlias_Works()
    {
        var file = Path.Combine(_tmpDir, "alias.dat");
        File.WriteAllBytes(
            file,
            new byte[] { 0x00 }
                .Concat(System.Text.Encoding.ASCII.GetBytes("ALIASED"))
                .ToArray());
        var lines = RunLines($"strings '{Esc(file)}'");
        Assert.Contains("ALIASED", lines);
    }

    [Fact]
    public void Strings_FilenameWithScriptblockChars_TreatedAsLiteralPath()
    {
        // Directive 12: a path containing $(...) and ;  must not re-parse as
        // a PowerShell scriptblock or command separator. The cmdlet binds
        // the path through ValueFromRemainingArguments; single-quoting the
        // literal in the test script body is the caller's job. The cmdlet
        // must then treat it as a literal file path.
        var weirdName = "$(throw'pwn');run.dat";
        var weirdPath = Path.Combine(_tmpDir, weirdName);
        File.WriteAllBytes(
            weirdPath,
            new byte[] { 0x00 }
                .Concat(System.Text.Encoding.ASCII.GetBytes("INJECT_SAFE"))
                .ToArray());

        var lines = RunLines($"Invoke-BashStrings '{Esc(weirdPath)}'");
        Assert.Contains("INJECT_SAFE", lines);
    }
}
