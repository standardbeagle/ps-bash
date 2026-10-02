using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// GNU <c>head -v / -q / -z</c> and the "==> name &lt;==" headers of a multi-file head. Expected text is
/// coreutils 9.4 (<c>wsl</c>) on files a="1\n2\n3\n", b="x\ny" (no final newline), e="" and
/// z="a\0b\0"; bytes are compared exactly (a NUL is shown as \0, a newline as \n).
/// </summary>
public class HeadHeadersZeroTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public HeadHeadersZeroTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), "psb-headh-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
        File.WriteAllText(Path.Combine(_tmp, "a"), "1\n2\n3\n");
        File.WriteAllText(Path.Combine(_tmp, "b"), "x\ny");
        File.WriteAllText(Path.Combine(_tmp, "e"), "");
        File.WriteAllBytes(Path.Combine(_tmp, "z"), new byte[] { (byte)'a', 0, (byte)'b', 0 });
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>The exact bytes the command writes, with NUL shown as \0 and LF as \n.</summary>
    private string Bytes(string args)
    {
        var pwsh = _fixture.AcquireFresh();
        var res = pwsh.AddScript($"Push-Location '{_tmp.Replace("'", "''")}'; try {{ {args} }} finally {{ Pop-Location }}").Invoke();
        pwsh.Commands.Clear();
        pwsh.Streams.ClearStreams();
        var sb = new System.Text.StringBuilder();
        foreach (var o in res)
        {
            var bt = o?.Properties["BashText"]?.Value?.ToString() ?? o?.ToString() ?? "";
            bool exact = o?.Properties["NoTrailingNewline"]?.Value is true;
            sb.Append(exact ? bt : bt.TrimEnd('\n') + "\n");
        }
        return sb.ToString().Replace("\0", "\\0").Replace("\n", "\\n");
    }

    [Fact]
    public void TwoFiles_GetHeaders_BlankLineBetween() =>
        Assert.Equal("==> a <==\\n1\\n2\\n\\n==> b <==\\nx\\ny", Bytes("Invoke-BashHead '-n2' a b"));

    [Fact]
    public void LastLineWithoutNewline_IsCopied_AndTheSeparatorEndsIt() =>
        Assert.Equal("==> b <==\\nx\\ny\\n==> a <==\\n1\\n2\\n3\\n", Bytes("Invoke-BashHead b a"));

    [Fact]
    public void Quiet_SuppressesHeaders() =>
        Assert.Equal("1\\nx\\n", Bytes("Invoke-BashHead '-q' '-n1' a b"));

    [Fact]
    public void Verbose_ForcesHeaderOnASingleFile() =>
        Assert.Equal("==> a <==\\n1\\n", Bytes("Invoke-BashHead '-v' '-n1' a"));

    [Fact]
    public void QuietThenVerbose_LastWins_AndBack() =>
        Assert.Equal(Bytes("Invoke-BashHead '-v' '-n1' a b"), Bytes("Invoke-BashHead '-qv' '-n1' a b"));

    [Fact]
    public void VerboseThenQuiet_LastWins() =>
        Assert.Equal("1\\nx\\n", Bytes("Invoke-BashHead '-vq' '-n1' a b"));

    [Fact]
    public void EmptyFile_StillGetsItsHeader() =>
        Assert.Equal("==> e <==\\n\\n==> a <==\\n1\\n", Bytes("Invoke-BashHead '-n1' e a"));

    [Fact]
    public void ByteMode_Headers_AndNoTrailingNewlineBeforeSeparator() =>
        Assert.Equal("==> a <==\\n1\\n2\\n==> b <==\\nx\\ny", Bytes("Invoke-BashHead '-c3' a b"));

    [Fact]
    public void VerboseOnPipelineInput_UsesStandardInput() =>
        Assert.Equal("==> standard input <==\\n1\\n", Bytes("'1','2' | Invoke-BashHead '-v' '-n1'"));

    [Fact]
    public void VerboseOnEmptyPipeline_PrintsOnlyTheHeader() =>
        Assert.Equal("==> standard input <==\\n", Bytes("@() | Invoke-BashHead '-v'"));

    [Fact]
    public void DashOperand_ReadsThePipeline_NamedStandardInput() =>
        Assert.Equal("==> a <==\\n1\\n\\n==> standard input <==\\nS1\\n\\n==> b <==\\nx\\n",
            Bytes("'S1','S2' | Invoke-BashHead '-n1' a - b"));

    [Fact]
    public void Zero_FirstTwoNulTerminatedRecords() =>
        Assert.Equal("a\\0b\\0", Bytes("Invoke-BashPrintf 'a\\0b\\0c\\0' | Invoke-BashHead '-z' '-n2'"));

    [Fact]
    public void Zero_UnterminatedLastRecord_IsCopiedWithoutNul() =>
        Assert.Equal("a\\0b\\0c", Bytes("Invoke-BashPrintf 'a\\0b\\0c' | Invoke-BashHead '-z' '-n5'"));

    [Fact]
    public void Zero_AllButLastK() =>
        Assert.Equal("a\\0b\\0", Bytes("Invoke-BashPrintf 'a\\0b\\0c' | Invoke-BashHead '-z' '-n' '-1'"));

    [Fact]
    public void Zero_NewlineIsOrdinaryData() =>
        Assert.Equal("a\\nb\\0", Bytes("Invoke-BashPrintf 'a\\nb\\0c\\0' | Invoke-BashHead '-z' '-n1'"));

    [Fact]
    public void Zero_OnFile_WithHeaders() =>
        Assert.Equal("==> z <==\\na\\0\\n==> a <==\\n1\\n2\\n3\\n", Bytes("Invoke-BashHead '-z' '-n1' z a"));

    [Theory]
    [InlineData("a\0b\0c\0", 2, "a\0b\0")]
    [InlineData("a\0b\0c", 2, "a\0b\0")]
    [InlineData("a\0b\0c", 3, "a\0b\0c")]
    [InlineData("a\0b\0c", -1, "a\0b\0")]
    [InlineData("a\0b\0c\0", -1, "a\0b\0")]
    [InlineData("", 3, "")]
    [InlineData("a\0", 0, "")]
    public void ZeroHead_Pure(string input, int count, string expected) =>
        Assert.Equal(expected, InvokeBashHeadCommand.ZeroHead(input, count));

    [Fact]
    public void ScanArgs_AcceptsVerboseAndZero()
    {
        foreach (var a in new[] { "-v", "-z", "--verbose", "--zero-terminated", "--verb", "--zero" })
            Assert.Null(InvokeBashHeadCommand.ScanArgs(new[] { a }).Error);
        Assert.Equal(FileHeaders.Mode.Always, InvokeBashHeadCommand.Plan(new[] { "-qv" }).Headers);
        Assert.Equal(FileHeaders.Mode.Never, InvokeBashHeadCommand.Plan(new[] { "-vq" }).Headers);
        Assert.True(InvokeBashHeadCommand.Plan(new[] { "-z" }).Zero);
    }
}
