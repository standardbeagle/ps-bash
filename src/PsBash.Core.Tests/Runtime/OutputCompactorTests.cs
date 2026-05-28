using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

public class OutputCompactorTests
{
    [Fact]
    public void CompactCommandOutput_CollapsesRepeatedLines()
    {
        var frames = Enumerable.Repeat(new OutputFrame(StreamTag.Stdout, "restore ok\n"), 25).ToArray();

        var compacted = OutputCompactor.CompactCommandOutput("dotnet build", 0, false, frames);

        Assert.Contains("exit=0", compacted);
        Assert.Contains("stdout_lines=25", compacted);
        Assert.Contains("repeated 24 more times", compacted);
        Assert.True(compacted.Length < string.Concat(frames.Select(f => f.Text)).Length);
    }

    [Fact]
    public void CompactCommandOutput_KeepsErrorsAndFileLocations()
    {
        var frames = new[]
        {
            new OutputFrame(StreamTag.Stdout, "line 1\nline 2\nline 3\n"),
            new OutputFrame(StreamTag.Stderr, "src/App.cs:42: error CS1002: ; expected\n"),
            new OutputFrame(StreamTag.Stdout, "tail context\n"),
        };

        var compacted = OutputCompactor.CompactCommandOutput("dotnet test", 1, false, frames, maxLines: 4);

        Assert.Contains("exit=1", compacted);
        Assert.Contains("[err] src/App.cs:42: error CS1002: ; expected", compacted);
        Assert.Contains("[out] tail context", compacted);
    }

    [Fact]
    public void CompactCommandOutput_DistinguishesMixedStdoutAndStderr()
    {
        var frames = new[]
        {
            new OutputFrame(StreamTag.Stdout, "normal output\n"),
            new OutputFrame(StreamTag.Stderr, "warning: noisy stderr\n"),
        };

        var compacted = OutputCompactor.CompactCommandOutput("tool", 0, false, frames);

        Assert.Contains("[out] normal output", compacted);
        Assert.Contains("[err] warning: noisy stderr", compacted);
        Assert.Contains("stderr_lines=1", compacted);
    }

    [Fact]
    public void CompactCommandOutput_RecordsTimeout()
    {
        var compacted = OutputCompactor.CompactCommandOutput("sleep 99", 124, true, []);

        Assert.Contains("exit=124", compacted);
        Assert.Contains("timeout=true", compacted);
    }
}
