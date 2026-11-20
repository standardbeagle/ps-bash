using System.Management.Automation;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// <see cref="LineAssembler"/>: pipeline records back into lines (oracle: the bytes bash's line reader would see —
/// end-to-end parity is LineAssemblyDifferentialTests; these pin the primitive's record contract).
/// </summary>
public class LineAssemblerTests
{
    private static PSObject Piece(string text) => (PSObject)BashRuntime.TextRecord(text, unterminated: true);
    private static PSObject Line(string text) => PSObject.AsPSObject(BashRuntime.NewBashObject(text));
    private static string Text(PSObject? o) => BashRuntime.GetBashText(o);

    [Fact]
    public void WholeLine_PassesThroughAsTheSameObject()
    {
        var a = new LineAssembler();
        var rec = Line("a b");
        Assert.Same(rec, a.Feed(rec));
        Assert.Null(a.Flush());
    }

    [Fact]
    public void Pieces_JoinIntoOneTerminatedLine()
    {
        var a = new LineAssembler();
        Assert.Null(a.Feed(Piece("q")));
        Assert.Null(a.Feed(Piece("a")));
        var joined = a.Feed(Line(" b"));
        Assert.Equal("qa b", Text(joined));
        Assert.False(BashRuntime.IsUnterminated(joined));
        Assert.False(a.HasPending);
    }

    [Fact]
    public void PieceThenEmbeddedNewlineRecord_JoinsWithoutAnExtraLine()
    {
        // printf 'q'; printf 'a\nb\n' : lines "qa", "b".
        var a = new LineAssembler();
        a.Feed(Piece("q"));
        var r = a.Feed(Piece("a\nb\n"));
        Assert.Equal("qa\nb", Text(r));
        Assert.False(BashRuntime.IsUnterminated(r));
    }

    [Fact]
    public void PieceThenRecordEndingInAnEmptyLine_KeepsTheEmptyLine()
    {
        // printf 'x'; printf 'a\n\n' : lines "xa", "" (bash: grep -c '' = 2). The join used to be the plain
        // record "xa\n", which every reader took as ONE line.
        var a = new LineAssembler();
        a.Feed(Piece("x"));
        var r = a.Feed(Piece("a\n\n"));
        Assert.Equal(new[] { "xa", "" }, BashRuntime.SplitRecord(r, out bool open));
        Assert.False(open);
        Assert.Equal("xa\n\n", BashRuntime.RecordStreamText(new object[] { r! }));
    }

    [Fact]
    public void Flush_ReturnsTheUnterminatedLastLine()
    {
        var a = new LineAssembler();
        a.Feed(Piece("x"));
        a.Feed(Piece("y"));
        var last = a.Flush();
        Assert.Equal("xy", Text(last));
        Assert.True(BashRuntime.IsUnterminated(last));
        Assert.Null(a.Flush());
    }

    [Fact]
    public void Null_IsIgnored()
    {
        var a = new LineAssembler();
        Assert.Null(a.Feed(null));
        a.Feed(Piece("x"));
        Assert.Null(a.Feed(null));
        Assert.Equal("xy", Text(a.Feed(Line("y"))));
    }

    [Fact]
    public void PassThrough_HoldsNothing()
    {
        // head -c / tail -c count bytes: a piece is handed on at once (an endless stream without a newline must flow).
        var a = new LineAssembler { PassThrough = true };
        var p = Piece("y");
        Assert.Same(p, a.Feed(p));
        Assert.False(a.HasPending);
        Assert.Null(a.Flush());
    }

    [Fact]
    public void NulTerminator_NulRecordsPass_ALineIsAPiece()
    {
        // -z: a record ends at NUL. `echo b` is the bytes "b\n" — part of the next NUL record, as bash reads it.
        var a = new LineAssembler { Terminator = '\0' };
        var nul = (PSObject)NulRecords.Record("a");
        Assert.Same(nul, a.Feed(nul));
        Assert.Null(a.Feed(Line("b")));
        var r = a.Feed((PSObject)NulRecords.Record("c"));
        Assert.Equal("b\nc\0", Text(r));
        Assert.Null(a.Feed(Line("d")));
        Assert.Equal("d\n", Text(a.Flush()));
    }
}

/// <summary>
/// Every cmdlet that takes pipeline input either reads it as LINES through <see cref="LineAssembler"/> or is listed
/// here with why it does not (it reads bytes, names, or the whole stream at once). REGRESSION: each line consumer
/// split each record on its own, so 25 commands took a piece without its newline as a line
/// (LineAssemblyDifferentialTests). A new pipeline cmdlet must pick a side.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant; behavior is pinned by the differential test.
/// </summary>
public class LineAssemblerGuardTests
{
    private static readonly Dictionary<string, string> NotLineConsumers = new(StringComparer.Ordinal)
    {
        ["ConvertToBashCaptureCommand.cs"] = "$(…) capture: the byte stream (RecordStreamText)",
        ["ConvertToBashGlobCommand.cs"] = "not stdin data",
        ["ConvertToBashNativeStdinCommand.cs"] = "bytes to a native program",
        ["ConvertToBashWordsCommand.cs"] = "not stdin data",
        ["ConvertToPowerShellCommand.cs"] = "objects",
        ["FormatCompactCommand.cs"] = "output formatting",
        ["FormatStyledCommand.cs"] = "output formatting",
        ["ShowStyledCommand.cs"] = "output formatting",
        ["TestBashSyntaxCommand.cs"] = "not stdin data",
        ["InvokeBashBase64Command.cs"] = "bytes",
        ["InvokeBashBashCommand.cs"] = "a nested shell's stdin (bytes)",
        ["InvokeBashBrowseCommand.cs"] = "whole stream",
        ["InvokeBashCatCommand.cs"] = "bytes (a piece is written as is)",
        ["InvokeBashChecksumCommands.cs"] = "bytes",
        ["InvokeBashCommandCommand.cs"] = "hands the pipeline to the command it runs",
        ["InvokeBashCpCommand.cs"] = "names",
        ["InvokeBashDiffCommand.cs"] = "whole stream (RecordStreamText)",
        ["InvokeBashEnvCommand.cs"] = "hands the pipeline to the command it runs",
        ["InvokeBashEvalCommand.cs"] = "hands the pipeline to the code it runs",
        ["InvokeBashExecFdCommand.cs"] = "bytes to a descriptor",
        ["InvokeBashFileCommand.cs"] = "whole stream (RecordStreamText)",
        ["InvokeBashFindCommand.cs"] = "names",
        ["InvokeBashFlockCommand.cs"] = "hands the pipeline to the command it runs (InnerCommand.Run)",
        ["InvokeBashFusedPipelineCommand.cs"] = "its stages are lines-only by certification (no unterminated producer)",
        ["InvokeBashGzipCommand.cs"] = "bytes",
        ["InvokeBashJqCommand.cs"] = "whole stream (RecordStreamText)",
        ["InvokeBashKeepStderrCommand.cs"] = "pipeline plumbing: passes records on (stderr kept apart after a capture)",
        ["InvokeBashLessCommand.cs"] = "pager: whole stream",
        ["InvokeBashLnCommand.cs"] = "names",
        ["InvokeBashMoreCommand.cs"] = "pager: whole stream",
        ["InvokeBashMvCommand.cs"] = "names",
        ["InvokeBashNativeCommand.cs"] = "bytes to a native program",
        ["InvokeBashNohupCommand.cs"] = "hands the pipeline to the command it runs (InnerCommand.Run)",
        ["InvokeBashOdCommand.cs"] = "bytes",
        ["InvokeBashPipeGateCommand.cs"] = "pipeline plumbing: passes records on",
        ["InvokeBashPipeStageCommand.cs"] = "pipeline plumbing: passes records on",
        ["InvokeBashPrintenvCommand.cs"] = "does not read stdin: piped input is discarded (GNU printenv)",
        ["InvokeBashReadCommand.cs"] = "its own byte reader (SharedStdin)",
        ["InvokeBashRedirectCommand.cs"] = "bytes to a file",
        ["InvokeBashRmCommand.cs"] = "names",
        ["InvokeBashTarCommand.cs"] = "bytes",
        ["InvokeBashTeeCommand.cs"] = "bytes",
        ["InvokeBashTestIntCommand.cs"] = "not stdin data",
        ["InvokeBashTimeoutCommand.cs"] = "hands the pipeline to the command it runs",
        ["InvokeBashTrCommand.cs"] = "bytes (a newline is an ordinary character)",
        ["InvokeBashTrueFalseCommand.cs"] = "does not read stdin: piped input is discarded (GNU true / false)",
        ["InvokeBashWcCommand.cs"] = "bytes (RecordStreamText)",
        ["InvokeBashXanCommand.cs"] = "whole stream",
        ["InvokeBashYqCommand.cs"] = "whole stream",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }

    private static readonly Regex PipelineParam = new(@"ValueFromPipeline\s*=\s*true", RegexOptions.Compiled);

    [Fact]
    public void EveryPipelineCmdlet_ReadsLinesThroughTheAssembler_OrSaysWhyNot()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        var stale = new HashSet<string>(NotLineConsumers.Keys, StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            if (!PipelineParam.IsMatch(text)) { stale.Remove(name); continue; }
            bool assembles = text.Contains("LineAssembler _lines", StringComparison.Ordinal)
                && text.Contains("_lines.Feed(InputObject)", StringComparison.Ordinal)
                && text.Contains("_lines.Flush()", StringComparison.Ordinal);
            if (NotLineConsumers.ContainsKey(name))
            {
                stale.Remove(name);
                if (assembles) offenders.Add($"{name}: uses LineAssembler but is listed as not a line consumer");
                continue;
            }
            if (!assembles) offenders.Add($"{name}: takes pipeline input but neither feeds a LineAssembler (and flushes it in EndProcessing) nor is listed");
        }
        foreach (var s in stale) offenders.Add($"{s}: listed but has no pipeline input (remove it)");
        Assert.True(offenders.Count == 0,
            "A command reading its input as lines must feed every record through LineAssembler (a piece without its newline is not a line):\n"
            + string.Join("\n", offenders));
    }
}
