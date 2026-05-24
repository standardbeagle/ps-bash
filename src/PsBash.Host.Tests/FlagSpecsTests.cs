using PsBash.Host;
using Xunit;

namespace PsBash.Host.Tests;

/// <summary>
/// Verifies the host loads bash flag specs from the single canonical source
/// (PsBash.Module/BashFlagSpecs.json, embedded as PsBash.Host.Resources.FlagSpecs.json).
/// Oracle note (qa-rubric Directive 1): ps-bash-specific completion metadata, no bash oracle.
/// </summary>
public class FlagSpecsTests
{
    [Fact]
    public void LoadsCanonicalSpecs_FromSingleSource()
    {
        // The canonical file carries the full (psm1-sourced) command set.
        Assert.True(FlagSpecs.Commands.Count >= 50,
            $"expected >= 50 commands in the canonical flag-spec source, got {FlagSpecs.Commands.Count}");

        var grep = FlagSpecs.GetFlags("grep");
        Assert.NotNull(grep);
        Assert.Contains(grep!, f => f.Flag == "-i");

        // `tar -x` only existed in the richer psm1 table before unification — its presence here
        // proves the host now reads the merged canonical source, not the old host-only JSON.
        var tar = FlagSpecs.GetFlags("tar");
        Assert.NotNull(tar);
        Assert.Contains(tar!, f => f.Flag == "-x");
    }
}
