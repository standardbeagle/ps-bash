using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>Invoke-BashRedirect</c> (<c>&gt; file</c>) streams records to the file instead of buffering
/// the whole output. Streaming is proven by ORDER: the producer observes the target already
/// opened (and truncated) while it is still running, which a collect-then-write cmdlet cannot do.
/// </summary>
public class RedirectStreamingTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public RedirectStreamingTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psb-redir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private (string[] Out, PSDataCollection<ErrorRecord> Errors) Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        var errors = pwsh.Streams.Error;
        var copy = new PSDataCollection<ErrorRecord>(errors);
        pwsh.Commands.Clear();
        return (result.Select(o => o?.ToString() ?? "").ToArray(), copy);
    }

    [Fact]
    public void Redirect_TargetIsOpenedAndTruncatedBeforeTheProducerRuns()
    {
        var f = P("t.txt");
        File.WriteAllText(f, "old contents");
        // The producer samples the file length when its FIRST record is emitted: with streaming
        // the redirect has already truncated it (0); a buffering cmdlet would still show 12.
        var (output, errors) = Run(
            $"& {{ $global:psbSeen = (Get-Item -LiteralPath '{f}').Length; 'x' }} | Invoke-BashRedirect -Path '{f}'; $global:psbSeen");
        Assert.Empty(errors);
        Assert.Equal(new[] { "0" }, output);
        Assert.Equal("x\n", File.ReadAllText(f));
    }

    [Fact]
    public void Redirect_EmptyOutput_StillCreatesTruncatedFile()
    {
        var f = P("e.txt");
        File.WriteAllText(f, "old");
        Run($"@() | Invoke-BashRedirect -Path '{f}'");
        Assert.True(File.Exists(f));
        Assert.Equal(0, new FileInfo(f).Length);
    }

    [Fact]
    public void Redirect_Append_AddsToExistingFile()
    {
        var f = P("a.txt");
        File.WriteAllText(f, "one\n");
        Run($"'two' | Invoke-BashRedirect -Path '{f}' -Append");
        Assert.Equal("one\ntwo\n", File.ReadAllText(f));
    }

    [Fact]
    public void Redirect_ManyRecords_AreWrittenCompleteAndInOrder()
    {
        var f = P("big.txt");
        // ~6 MB across 300k records: well past the 64 KB buffer, so flushes happen mid-stream.
        Run($"1..300000 | ForEach-Object {{ \"line number $_\" }} | Invoke-BashRedirect -Path '{f}'");
        var lines = File.ReadLines(f).ToArray();
        Assert.Equal(300000, lines.Length);
        Assert.Equal("line number 1", lines[0]);
        Assert.Equal("line number 300000", lines[^1]);
    }

    [Fact]
    public void Redirect_NoTrailingNewlineRecord_WritesExactBytes()
    {
        var f = P("n.txt");
        Run($"Invoke-BashPrintf 'x' | Invoke-BashRedirect -Path '{f}'");
        Assert.Equal(new byte[] { (byte)'x' }, File.ReadAllBytes(f));
    }

    [Fact]
    public void Redirect_MissingDirectory_Fails()
    {
        var f = Path.Combine(_dir, "nodir", "x.txt");
        var (_, errors) = Run($"'x' | Invoke-BashRedirect -Path '{f}'");
        Assert.NotEmpty(errors);
        Assert.False(File.Exists(f));
    }
}
