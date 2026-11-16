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

    // OpenText is the writer awk `print > f`, find -fprint, sed `w`, uniq OUTPUT and `exec 3> f` use: same policy.
    [Fact]
    public void OpenText_FirstWriteIsInTheFileAtOnce()
    {
        var f = Path.Combine(_dir, "text.txt");
        using var w = RedirectFileWriter.OpenText(f, append: false);
        w.Write("a\n");
        Assert.Equal("a\n", Peek(f));
    }

    [Fact]
    public void OpenText_FlushTextWritesABurstNow_AndDisposeClosesTheFile()
    {
        var f = Path.Combine(_dir, "textburst.txt");
        var w = RedirectFileWriter.OpenText(f, append: false);
        for (int i = 0; i < 50; i++) w.Write(i + "\n");
        RedirectFileWriter.FlushText(w);
        var expected = string.Concat(Enumerable.Range(0, 50).Select(i => i + "\n"));
        Assert.Equal(expected, Peek(f));
        w.Dispose();
        File.Delete(f); // closed: nothing holds the file
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void OpenText_AppendKeepsWhatIsThere()
    {
        var f = Path.Combine(_dir, "append.txt");
        File.WriteAllText(f, "old\n");
        using (var w = RedirectFileWriter.OpenText(f, append: true)) w.Write("new\n");
        Assert.Equal("old\nnew\n", File.ReadAllText(f));
    }

    // Regression: awk's `print > "f"` held its output in a private 64 KB StreamWriter until the program ended.
    [Fact]
    public void AwkFileSink_PrintIsInTheFileWhileAwkRuns()
    {
        var f = Path.Combine(_dir, "awk.txt");
        var sink = AwkFileSink.Open("awk.txt", f, append: false);
        sink.Write("a\n");
        Assert.Equal("a\n", Peek(f));
        Assert.Equal(0, sink.Close());
    }

    [Fact]
    public void AwkFileSink_MissingDirectory_IsGawksCantRedirect()
    {
        var f = Path.Combine(_dir, "nodir", "x.txt");
        var ex = Assert.Throws<AwkInterpreter.AwkRuntimeException>(() => AwkFileSink.Open("nodir/x.txt", f, append: false));
        Assert.Contains("can't redirect to `nodir/x.txt'", ex.Message);
    }
}
