using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// Removing old module-{version}-{hash} extraction directories: only ones no process uses.
/// Host-internal (no bash equivalent); hand-written asserts.
/// </summary>
public class ModuleExtractionPruneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "psb-prune-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IDisposable> _held = [];

    public ModuleExtractionPruneTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var h in _held) h.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // An extraction as a current build lays it down; `used` keeps its .inuse open the way a live host does.
    private string Extraction(string name, bool used)
    {
        var dir = Path.Combine(_root, name);
        var inUse = ModuleExtractor.OpenInUse(dir, TimeSpan.FromSeconds(1))!;
        File.WriteAllText(Path.Combine(dir, ".extracted"), "hash");
        File.WriteAllText(Path.Combine(dir, "PsBash.psm1"), "# module");
        File.WriteAllText(Path.Combine(dir, "PsBash.Cmdlets.dll"), "not really a dll");
        if (used) _held.Add(inUse); else inUse.Dispose();
        return dir;
    }

    // A release from before .inuse existed.
    private string LegacyExtraction(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".extracted"), "hash");
        File.WriteAllText(Path.Combine(dir, "PsBash.psm1"), "# module");
        return dir;
    }

    [Fact]
    public void Prune_ExtractionNoProcessUses_IsRemoved()
    {
        var keep = Extraction("module-1.0.0.0-aaaaaaaaaaaa", used: true);
        var old = Extraction("module-0.9.0.0-bbbbbbbbbbbb", used: false);

        int removed = ModuleExtractor.PruneStaleExtractions(_root, keep, ModuleExtractor.LegacyMinAge);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(old), "an extraction whose .inuse nobody holds should be removed");
        Assert.True(File.Exists(Path.Combine(keep, "PsBash.psm1")), "this process's own extraction is kept");
    }

    [Fact]
    public void Prune_ExtractionAnotherProcessUses_IsKept()
    {
        var keep = Extraction("module-1.0.0.0-aaaaaaaaaaaa", used: true);
        var live = Extraction("module-0.9.0.0-cccccccccccc", used: true);

        Assert.Equal(0, ModuleExtractor.PruneStaleExtractions(_root, keep, ModuleExtractor.LegacyMinAge));
        Assert.True(File.Exists(Path.Combine(live, "PsBash.psm1")), "a held .inuse means a host imports from it");
        Assert.True(File.Exists(Path.Combine(live, ".extracted")));
    }

    [Fact]
    public void Prune_LegacyExtraction_RemovedOnlyPastTheMinimumAge()
    {
        var keep = Extraction("module-1.0.0.0-aaaaaaaaaaaa", used: true);
        var legacy = LegacyExtraction("module-0.8.0.0-dddddddddddd");

        ModuleExtractor.PruneStaleExtractions(_root, keep, ModuleExtractor.LegacyMinAge);
        Assert.True(Directory.Exists(legacy), "a recent extraction without .inuse may belong to a running older release");

        ModuleExtractor.PruneStaleExtractions(_root, keep, TimeSpan.Zero);
        Assert.False(Directory.Exists(legacy), "past the minimum age (and with no DLL loaded) it is removed");
    }

    [Fact]
    public void Prune_DirectoryNotNamedLikeAnExtraction_IsKept()
    {
        var keep = Extraction("module-1.0.0.0-aaaaaaaaaaaa", used: true);
        var other = LegacyExtraction("module-build");

        ModuleExtractor.PruneStaleExtractions(_root, keep, TimeSpan.Zero);

        Assert.True(Directory.Exists(other));
    }

    [Theory]
    [InlineData("module-0.14.3.0-441c8624c3ec", true)]
    [InlineData("module-1-abcdef012345", true)]
    [InlineData("module-build", false)]
    [InlineData("module-0.14.3.0-441C8624C3EC", false)] // ExtractionDir lower-cases the hash
    [InlineData("module-0.14.3.0-441c8624c3", false)]
    [InlineData("module--441c8624c3ec", false)]
    [InlineData("module-0.1x-441c8624c3ec", false)]
    public void IsExtractionDirName_MatchesOnlyTheExtractionShape(string name, bool expected)
        => Assert.Equal(expected, ModuleExtractor.IsExtractionDirName(name));

    [Fact]
    public void OpenInUse_WhilePrunerHoldsTheDirectory_WaitsThenGivesUp()
    {
        var dir = Extraction("module-0.9.0.0-eeeeeeeeeeee", used: false);
        using var pruner = new FileStream(Path.Combine(dir, ".inuse"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Null(ModuleExtractor.OpenInUse(dir, TimeSpan.FromMilliseconds(200)));
    }
}
