using PsBash.Core.Runtime.Ipc;
using PsBash.Core.Transpiler;
using Xunit;

namespace PsBash.Core.Tests.Transpiler;

/// <summary>
/// The launcher's own stdin as the stdin of a <c>-c</c> script: the transpile scope
/// (<see cref="BashTranspiler.TranspileWithLauncherStdin"/>), the wire protocol (STDIN-FEED header, STDIN: frames)
/// and the lazy <see cref="StdinCursor"/> the host hands the runspace.
/// </summary>
public class LauncherStdinTests
{
    // ---- transpile scope -------------------------------------------------------------------------------

    [Theory]
    [InlineData("sort")]
    [InlineData("while read l; do echo $l; done")]
    [InlineData("cat | wc -l")]
    [InlineData("head -n1")]
    public void LauncherStdin_ScriptThatReadsStdin_IsScopedAndFedFromTheCursorVariable(string bash)
    {
        var ps = BashTranspiler.TranspileWithLauncherStdin(bash);

        Assert.NotNull(ps);
        Assert.Contains("$global:__BashStdIn", ps);
        Assert.DoesNotContain("in $input", ps); // the host fills the cursor; nothing drains $input eagerly
    }

    [Theory]
    [InlineData("read x; echo $x")]
    [InlineData("mapfile -t a; echo ${#a[@]}")]
    public void LauncherStdin_RuntimeReaderBuiltins_AreForwardedToToo(string bash)
    {
        // read / mapfile take their lines from the shared stdin at RUN time, so the text never names the variable
        Assert.NotNull(BashTranspiler.TranspileWithLauncherStdin(bash));
    }

    [Theory]
    [InlineData("echo hi")]
    [InlineData("ls -la")]
    [InlineData("sort file.txt")]
    [InlineData("cat a b | wc -l")]
    [InlineData("x=1; echo $x")]
    public void LauncherStdin_ScriptWithNoStdinReader_ReturnsNullSoNothingIsForwarded(string bash)
    {
        Assert.Null(BashTranspiler.TranspileWithLauncherStdin(bash));
    }

    [Fact]
    public void LauncherStdin_OnlyTheFirstPipelineStageReadsTheLauncherStdin()
    {
        var ps = BashTranspiler.TranspileWithLauncherStdin("sort | uniq -c")!;

        // the first stage is fed; `uniq` reads the pipe
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ps, "__BashStdIn.Count"));
    }

    [Fact]
    public void LauncherStdin_FunctionBody_ReadsItsOwnPipeNotTheLauncherStdin()
    {
        // `echo x | f` must feed f from the pipe: a function is a filter, not a reader of the shell's stdin.
        var ps = BashTranspiler.TranspileWithLauncherStdin("f() { cat; }; echo x | f; sort")!;
        var fnEnd = ps.IndexOf("} finally { $global:BashPositional = $__bp }", StringComparison.Ordinal);

        Assert.True(fnEnd > 0);
        Assert.DoesNotContain("__BashStdIn", ps[..fnEnd]);
        Assert.Contains("__BashStdIn", ps[fnEnd..]); // the top-level `sort` still reads it
    }

    [Fact]
    public void PlainTranspile_IsUnchangedByTheLauncherStdinScope()
    {
        // the scope flag must not leak into later plain transpiles on the same thread
        _ = BashTranspiler.TranspileWithLauncherStdin("sort");
        Assert.DoesNotContain("__BashStdIn", BashTranspiler.Transpile("sort"));
    }

    // ---- wire protocol ---------------------------------------------------------------------------------

    [Fact]
    public async Task Protocol_CommandWithStdinFollows_RoundTripsTheFeedHeader()
    {
        var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Command("sort", SessionMode.Framed, null, StdinFollows: true));
        ms.Position = 0;

        var mode = Assert.IsType<Mode.Command>(await HostProtocol.ReadRequestAsync(ms));

        Assert.Equal("sort", mode.Body);
        Assert.True(mode.StdinFollows);
    }

    [Fact]
    public async Task Protocol_CommandWithoutStdin_KeepsTheOldWireFormat()
    {
        var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Command("sort"));

        var wire = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        Assert.DoesNotContain("STDIN", wire);
        ms.Position = 0;
        Assert.False(Assert.IsType<Mode.Command>(await HostProtocol.ReadRequestAsync(ms)).StdinFollows);
    }

    [Fact]
    public async Task Protocol_BodyLineThatLooksLikeTheFeedHeader_IsCommandData()
    {
        // only a header-block line is a header: the same text inside the body must stay data (as for ENV:)
        var ms = new MemoryStream();
        await HostProtocol.WriteRequestAsync(ms, new Mode.Command("echo a\nSTDIN-FEED:1\necho b"));
        ms.Position = 0;

        var mode = Assert.IsType<Mode.Command>(await HostProtocol.ReadRequestAsync(ms));

        Assert.False(mode.StdinFollows);
        Assert.Equal("echo a\nSTDIN-FEED:1\necho b", mode.Body);
    }

    [Fact]
    public async Task Protocol_StdinFrames_RoundTripRawBytesAndEof()
    {
        var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var ms = new MemoryStream();
        await HostProtocol.WriteStdinFrameAsync(ms, all);
        await HostProtocol.WriteStdinEofAsync(ms);

        var lines = System.Text.Encoding.ASCII.GetString(ms.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.True(HostProtocol.TryParseStdinFrame(lines[0], out var bytes, out var eof0));
        Assert.False(eof0);
        Assert.Equal(all, bytes);
        Assert.True(HostProtocol.TryParseStdinFrame(lines[1], out _, out var eof1));
        Assert.True(eof1);
        Assert.False(HostProtocol.TryParseStdinFrame("garbage", out _, out _));
    }

    // ---- cursor ----------------------------------------------------------------------------------------

    [Fact]
    public void Cursor_PullsTheSourceOneRecordAtATime()
    {
        var pulled = 0;
        IEnumerable<object> Source() { for (var i = 1; i <= 100; i++) { pulled++; yield return "r" + i; } }
        var cursor = new StdinCursor(Source());

        Assert.Equal(0, pulled);              // nothing read until asked
        Assert.Equal(1, cursor.Count);        // asking "is there more?" reads exactly one
        Assert.Equal("r1", cursor.Dequeue());
        Assert.Equal("r2", cursor.Dequeue());
        Assert.Equal(2, pulled);              // a reader that stops early leaves the rest unread
    }

    [Fact]
    public void Cursor_PushFront_ReturnsRecordsInOrderBeforeTheSource()
    {
        var cursor = new StdinCursor(new object[] { "c" }.AsEnumerable());
        cursor.PushFront(new object[] { "a", "b" });

        Assert.Equal(new object[] { "a", "b", "c" }, new[] { cursor.Dequeue(), cursor.Dequeue(), cursor.Dequeue() });
        Assert.Equal(0, cursor.Count);
        Assert.Throws<InvalidOperationException>(() => cursor.Dequeue());
    }

    [Fact]
    public void Cursor_EmptySource_CountIsZero()
    {
        Assert.Equal(0, new StdinCursor(Enumerable.Empty<object>()).Count);
        Assert.Equal(0, new StdinCursor().Count);
    }
}
