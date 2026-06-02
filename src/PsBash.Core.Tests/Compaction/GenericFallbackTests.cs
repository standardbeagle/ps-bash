using PsBash.Core.Runtime.Compaction;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Compaction;

/// <summary>
/// The <see cref="GenericFallback.ErrorExtract"/> catch-all: trims a failing command with
/// no dedicated filter to its error/summary lines, while leaving success and the
/// default (<see cref="GenericFallback.None"/>) byte-identical to the plain digest.
/// </summary>
public class GenericFallbackTests
{
    private static OutputFrame Out(string text) => new(StreamTag.Stdout, text);
    private static OutputFrame Err(string text) => new(StreamTag.Stderr, text);

    [Fact]
    public void ErrorExtract_Failure_KeepsErrorLinesDropsNoise()
    {
        var frames = new[]
        {
            Out("building step 1\n"),
            Out("building step 2\n"),
            Out("fatal: the build broke\n"),
            Out("cleaning up\n"),
        };

        var r = FilterEngine.Apply("mytool", 1, false, frames, filters: null, fallback: GenericFallback.ErrorExtract);

        Assert.Contains("fatal: the build broke", r);
        Assert.DoesNotContain("building step 1", r);
        Assert.DoesNotContain("cleaning up", r);
    }

    [Fact]
    public void ErrorExtract_KeepsAllStderr()
    {
        var frames = new[]
        {
            Out("noise\n"),
            Err("something went sideways\n"),
        };

        var r = FilterEngine.Apply("mytool", 1, false, frames, filters: null, fallback: GenericFallback.ErrorExtract);

        Assert.Contains("[err] something went sideways", r);
        Assert.DoesNotContain("[out] noise", r);
    }

    [Fact]
    public void ErrorExtract_Success_EqualsPlainDigest()
    {
        var frames = new[] { Out("all good\n"), Out("done\n") };

        var viaFallback = FilterEngine.Apply("mytool", 0, false, frames, filters: null, fallback: GenericFallback.ErrorExtract);
        var viaCompactor = OutputCompactor.CompactCommandOutput("mytool", 0, false, frames);

        Assert.Equal(viaCompactor, viaFallback);
    }

    [Fact]
    public void ErrorExtract_FailureWithNoSignal_FallsBackToDigest()
    {
        // Failing command but nothing matches the importance/summary patterns and no stderr:
        // must NOT be silently emptied — fall back to the plain digest.
        var frames = new[] { Out("plain line one\n"), Out("plain line two\n") };

        var viaFallback = FilterEngine.Apply("mytool", 1, false, frames, filters: null, fallback: GenericFallback.ErrorExtract);
        var viaCompactor = OutputCompactor.CompactCommandOutput("mytool", 1, false, frames);

        Assert.Equal(viaCompactor, viaFallback);
    }

    [Fact]
    public void DefaultFallbackNone_OnFailure_IsByteIdenticalToDigest()
    {
        var frames = new[] { Out("step\n"), Err("error: boom\n") };

        // No fallback argument -> None -> plain digest, preserving the P0 regression guard.
        var viaEngine = FilterEngine.Apply("mytool", 1, false, frames);
        var viaCompactor = OutputCompactor.CompactCommandOutput("mytool", 1, false, frames);

        Assert.Equal(viaCompactor, viaEngine);
    }
}
