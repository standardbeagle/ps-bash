using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="RedirectFileWriter"/>: a redirect's buffered output reaches the file while the command still runs —
/// a slow producer's record at once, a burst's tail on the idle timer, and anything buffered before a ps-bash read.
/// </summary>
public class RedirectFileWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "psb-rfw-" + Guid.NewGuid().ToString("N")[..8]);

    public RedirectFileWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private RedirectFileWriter Open(string path)
        => new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, bufferSize: 64 * 1024));

    // What another process sees: a plain shared read, not BashFileSystem (which flushes first).
    private static string Peek(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void FirstWrite_IsInTheFileAtOnce()
    {
        var f = Path.Combine(_dir, "first.txt");
        using var w = Open(f);
        w.Write("a\n"u8.ToArray());
        Assert.Equal("a\n", Peek(f));
    }

    [Fact]
    public void BurstTail_ReachesTheFileOnIdle()
    {
        var f = Path.Combine(_dir, "burst.txt");
        using var w = Open(f);
        for (int i = 0; i < 100; i++) w.Write(Encoding.UTF8.GetBytes(i + "\n"));
        var expected = string.Concat(Enumerable.Range(0, 100).Select(i => i + "\n"));
        // The idle timer runs on its own thread: wait for its effect (bounded), not for a fixed time.
        SpinWait.SpinUntil(() => Peek(f) == expected, TimeSpan.FromSeconds(5));
        Assert.Equal(expected, Peek(f));
    }

    [Fact]
    public void FlushPending_WritesWhatIsBuffered()
    {
        var f = Path.Combine(_dir, "pending.txt");
        using var w = Open(f);
        w.Write("a\n"u8.ToArray());
        w.Write("b\n"u8.ToArray());
        RedirectFileWriter.FlushPending();
        Assert.Equal("a\nb\n", Peek(f));
    }

    [Fact]
    public void BashFileSystemRead_SeesBufferedOutput()
    {
        var f = Path.Combine(_dir, "read.txt");
        using var w = Open(f);
        w.Write("a\n"u8.ToArray());
        w.Write("b\n"u8.ToArray());
        Assert.Equal("a\nb\n", BashFileSystem.ReadAllTextRaw(f));
    }

    [Fact]
    public void Dispose_WritesTheRestAndLetsGo()
    {
        var f = Path.Combine(_dir, "close.txt");
        var w = Open(f);
        w.Write("a\n"u8.ToArray());
        w.Write("b\n"u8.ToArray());
        w.Dispose();
        w.Write("c\n"u8.ToArray()); // after close: ignored, never throws
        Assert.Equal("a\nb\n", File.ReadAllText(f));
        File.Delete(f);
    }
}
