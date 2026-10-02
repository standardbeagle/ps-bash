using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <c>split -n</c> engine and argv. Expected values are GNU coreutils 9.4 output (the 24-byte
/// <c>aa bbbb c dd eeeeeeee f</c> file and a 10-digit file), and the random-input equivalence
/// against the real tool was run once in WSL (400 inputs x bytes/lines/round-robin, 0 mismatches).
/// </summary>
public class SplitChunksTests
{
    private static readonly byte[] Lines = Encoding.ASCII.GetBytes("aa\nbbbb\nc\ndd\neeeeeeee\nf\n");
    private static readonly byte[] Digits = Encoding.ASCII.GetBytes("0123456789");

    private static string[] Pieces(byte[] data, SplitChunkKind kind, int n) =>
        SplitChunks.Partition(data, kind, n).Select(c => Encoding.ASCII.GetString(c).Replace("\n", "|")).ToArray();

    [Theory]
    [InlineData(3, "0123,456,789")]
    [InlineData(4, "012,345,67,89")]
    [InlineData(10, "0,1,2,3,4,5,6,7,8,9")]
    [InlineData(11, "0,1,2,3,4,5,6,7,8,9,")]
    [InlineData(1, "0123456789")]
    public void Bytes_FirstChunksTakeTheRemainder(int n, string expected) =>
        Assert.Equal(expected.Split(','), Pieces(Digits, SplitChunkKind.Bytes, n));

    [Theory]
    [InlineData(2, "aa|bbbb|c|dd|;eeeeeeee|f|")]
    [InlineData(3, "aa|bbbb|;c|dd|eeeeeeee|;f|")]
    [InlineData(4, "aa|bbbb|;c|dd|;eeeeeeee|;f|")]
    [InlineData(5, "aa|bbbb|;c|;dd|eeeeeeee|;;f|")]
    [InlineData(6, "aa|bbbb|;;c|dd|;eeeeeeee|;;f|")]
    [InlineData(10, "aa|;bbbb|;c|;dd|;eeeeeeee|;;;;;f|")]
    public void Lines_ALineBelongsToThePartitionHoldingItsFirstByte(int n, string expected) =>
        Assert.Equal(expected.Split(';'), Pieces(Lines, SplitChunkKind.Lines, n));

    [Theory]
    [InlineData(2, "aa|c|eeeeeeee|;bbbb|dd|f|")]
    [InlineData(3, "aa|dd|;bbbb|eeeeeeee|;c|f|")]
    [InlineData(7, "aa|;bbbb|;c|;dd|;eeeeeeee|;f|;")]
    public void RoundRobin_LineJGoesToChunkJModN(int n, string expected) =>
        Assert.Equal(expected.Split(';'), Pieces(Lines, SplitChunkKind.RoundRobin, n));

    [Fact]
    public void Lines_MissingFinalNewline_KeepsTheLastLineAsIs()
    {
        var data = Encoding.ASCII.GetBytes("a\nb\ncc");
        Assert.Equal(new[] { "a|b|", "cc" }, Pieces(data, SplitChunkKind.Lines, 2));
    }

    [Fact]
    public void EmptyInput_StillYieldsNEmptyChunks()
    {
        foreach (var kind in new[] { SplitChunkKind.Bytes, SplitChunkKind.Lines, SplitChunkKind.RoundRobin })
            Assert.Equal(new[] { "", "", "" }, Pieces(Array.Empty<byte>(), kind, 3));
    }

    [Theory]
    [InlineData("3", 0, 0, 3)]
    [InlineData("+3", 0, 0, 3)]
    [InlineData("2/3", 0, 2, 3)]
    [InlineData("l/4", 1, 0, 4)]
    [InlineData("l/2/4", 1, 2, 4)]
    [InlineData("r/5", 2, 0, 5)]
    [InlineData("r/3/3", 2, 3, 3)]
    public void Spec_Parses(string text, int kind, int k, int n)
    {
        Assert.True(SplitChunks.TryParse(text, out var spec, out _));
        Assert.Equal(new SplitChunkSpec((SplitChunkKind)kind, k, n), spec);
    }

    [Theory]
    [InlineData("0", "invalid number of chunks: '0'")]
    [InlineData("x", "invalid number of chunks: 'x'")]
    [InlineData("", "invalid number of chunks: ''")]
    [InlineData("-3", "invalid number of chunks: '-3'")]
    [InlineData("1/", "invalid number of chunks: ''")]
    [InlineData("l/", "invalid number of chunks: ''")]
    [InlineData("l/0", "invalid number of chunks: '0'")]
    [InlineData("r/x", "invalid number of chunks: 'x'")]
    [InlineData("1/2/3", "invalid number of chunks: '2/3'")]
    [InlineData("L/3", "invalid number of chunks: 'L/3'")]
    [InlineData("/3", "invalid number of chunks: '/3'")]
    [InlineData("5/3", "invalid chunk number: '5'")]
    [InlineData("0/3", "invalid chunk number: '0'")]
    [InlineData("l/5/3", "invalid chunk number: '5'")]
    [InlineData("l/0/3", "invalid chunk number: '0'")]
    public void Spec_Errors_AreGnuWording(string text, string expected)
    {
        Assert.False(SplitChunks.TryParse(text, out _, out var error));
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData(26, 26, 1)]
    [InlineData(27, 26, 2)]
    [InlineData(676, 26, 2)]
    [InlineData(700, 26, 3)]
    [InlineData(100, 10, 2)]
    [InlineData(120, 10, 3)]
    [InlineData(20, 16, 2)]
    public void SuffixLengthNeeded(int n, int radix, int expected) =>
        Assert.Equal(expected, SplitChunks.SuffixLengthNeeded(n, radix));

    [Fact]
    public void Plan_NumberAndLines_IsMoreThanOneWay()
    {
        Assert.Contains("cannot split in more than one way", InvokeBashSplitCommand.Plan(new[] { "-n", "2", "-l", "1", "f" }).Error);
        Assert.Contains("cannot split in more than one way", InvokeBashSplitCommand.Plan(new[] { "-n", "2", "-b", "1", "f" }).Error);
        Assert.Contains("cannot split in more than one way", InvokeBashSplitCommand.Plan(new[] { "-n", "2", "-n", "3", "f" }).Error);
    }

    [Fact]
    public void Plan_SuffixIsSizedForTheChunkCount()
    {
        var p = InvokeBashSplitCommand.Plan(new[] { "-n", "700", "f" });
        Assert.Equal(3, p.SuffixLength);
        Assert.False(p.SuffixAuto);
        Assert.Equal(2, InvokeBashSplitCommand.Plan(new[] { "-n", "3", "f" }).SuffixLength);
        Assert.Equal(3, InvokeBashSplitCommand.Plan(new[] { "-d", "-n", "120", "f" }).SuffixLength);
        Assert.Equal("split: the suffix length needs to be at least 3",
            InvokeBashSplitCommand.Plan(new[] { "-n", "700", "-a", "2", "f" }).Error);
        Assert.Null(InvokeBashSplitCommand.Plan(new[] { "-n", "3", "-a", "1", "f" }).Error);
    }

    [Fact]
    public void Scan_AbbreviationsFollowGnuAmbiguity()
    {
        Assert.Equal("number", InvokeBashSplitCommand.ScanArgs(new[] { "--numb=2" }).Tokens[0].OptId);
        Assert.Contains("'--number' '--numeric-suffixes'", InvokeBashSplitCommand.ScanArgs(new[] { "--nu=2" }).Error!.Value.Message("split"));
    }
}

/// <summary>End to end through the cmdlet: files written / stdout for K/N.</summary>
public class SplitChunksCmdletTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public SplitChunksCmdletTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), $"psb-spc-{Guid.NewGuid():N}".Substring(0, 22));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "lines"), "aa\nbbbb\nc\ndd\neeeeeeee\nf\n");
        File.WriteAllText(Path.Combine(_dir, "digits"), "0123456789");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (CmdResult result, string work) Run(string script)
    {
        var work = Path.Combine(_dir, "w" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(work);
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            $"Set-Location -LiteralPath '{work.Replace("'", "''")}'; $in='{_dir.Replace("'", "''")}'; {script}");
        return (r, work);
    }

    private static string[] Names(string work) =>
        Directory.GetFiles(work).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    [Fact]
    public void Bytes_ThreeWay_WritesFourThreeThree()
    {
        var (r, w) = Run("Invoke-BashSplit '-n' 3 \"$in/digits\"");
        r.AssertSuccess();
        Assert.Equal(new[] { "xaa", "xab", "xac" }, Names(w));
        Assert.Equal("0123", File.ReadAllText(Path.Combine(w, "xaa")));
        Assert.Equal("456", File.ReadAllText(Path.Combine(w, "xab")));
        Assert.Equal("789", File.ReadAllText(Path.Combine(w, "xac")));
    }

    [Fact]
    public void Lines_FiveWay_CreatesTheEmptyChunkToo()
    {
        var (r, w) = Run("Invoke-BashSplit '-n' 'l/5' \"$in/lines\"");
        r.AssertSuccess();
        Assert.Equal(new[] { "xaa", "xab", "xac", "xad", "xae" }, Names(w));
        Assert.Equal("aa\nbbbb\n", File.ReadAllText(Path.Combine(w, "xaa")));
        Assert.Equal("c\n", File.ReadAllText(Path.Combine(w, "xab")));
        Assert.Equal("dd\neeeeeeee\n", File.ReadAllText(Path.Combine(w, "xac")));
        Assert.Equal("", File.ReadAllText(Path.Combine(w, "xad")));
        Assert.Equal("f\n", File.ReadAllText(Path.Combine(w, "xae")));
    }

    [Fact]
    public void RoundRobin_FromPipeline()
    {
        var (r, w) = Run("'a','b','c' | Invoke-BashSplit '-n' 'r/2'");
        r.AssertSuccess();
        Assert.Equal("a\nc\n", File.ReadAllText(Path.Combine(w, "xaa")));
        Assert.Equal("b\n", File.ReadAllText(Path.Combine(w, "xab")));
    }

    [Fact]
    public void KthChunk_GoesToStdout_AndMakesNoFiles()
    {
        var (r, w) = Run("Invoke-BashSplit '-n' 'l/2/4' \"$in/lines\"");
        Assert.Equal(new[] { "c", "dd" }, r.AssertSuccess().Lines);
        Assert.Empty(Names(w));
    }

    [Fact]
    public void Bytes_KthChunk_ExactBytes()
    {
        var (r, w) = Run("Invoke-BashSplit '-n' '2/3' \"$in/digits\"");
        Assert.Equal(new[] { "456" }, r.AssertSuccess().Lines);
        Assert.Empty(Names(w));
    }

    [Fact]
    public void Numeric_Prefix_AdditionalSuffix()
    {
        var (r, w) = Run("Invoke-BashSplit '-d' '-n' 3 '--additional-suffix=.p' \"$in/digits\" 'pre_'");
        r.AssertSuccess();
        Assert.Equal(new[] { "pre_00.p", "pre_01.p", "pre_02.p" }, Names(w));
    }

    [Fact]
    public void ThreeLetterSuffix_For700Chunks()
    {
        var (r, w) = Run("Invoke-BashSplit '-n' 700 \"$in/digits\"");
        r.AssertSuccess();
        var names = Names(w);
        Assert.Equal(700, names.Length);
        Assert.Equal("xaaa", names[0]);
        Assert.Equal("xbax", names[^1]);
    }

    [Fact]
    public void Errors_ExitOne_NothingWritten()
    {
        var (r, w) = Run("Invoke-BashSplit '-n' 5/3 \"$in/digits\"");
        r.AssertFailed(1, "invalid chunk number: '5'");
        Assert.Empty(Names(w));
        var (r2, w2) = Run("Invoke-BashSplit '-n' 2 '-l' 1 \"$in/digits\"");
        r2.AssertFailed(1, "cannot split in more than one way");
        Assert.Empty(Names(w2));
    }
}
