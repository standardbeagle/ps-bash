using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Compaction;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

public class FailureTeeTests
{
    [Fact]
    public void TryWriteFailureTee_WritesUnfilteredFramesAndAllowsConcurrentReadWrite()
    {
        var frames = new[]
        {
            new OutputFrame(StreamTag.Stdout, "ordinary output\n"),
            new OutputFrame(StreamTag.Stderr, "fatal: complete diagnostic\n")
        };

        var path = IpcWorker.TryWriteFailureTee("git push", frames);

        Assert.NotNull(path);
        try
        {
            Assert.Contains(Path.Combine("ps-bash", "tee"), path!);
            using var stream = new FileStream(path!, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            Assert.Equal("ordinary output\nfatal: complete diagnostic\n", reader.ReadToEnd().Replace("\r\n", "\n"));
        }
        finally
        {
            if (path is not null) File.Delete(path);
        }
    }
}
