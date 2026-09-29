using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure-helper tests for the cp/mv pre-mutation validation. Each case mirrors a
/// GNU coreutils message verified against the Ubuntu oracle.
/// </summary>
public class TransferValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"psb-tv-{Guid.NewGuid():N}".Substring(0, 18));

    public TransferValidationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string P(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    [Fact]
    public void OperandShape_OneSource_AnyDestination_Ok() =>
        Assert.Null(TransferValidation.CheckOperandShape("cp", 1, "x", P("missing")));

    [Fact]
    public void OperandShape_SeveralSources_MissingDest_NoSuchFile()
    {
        var err = TransferValidation.CheckOperandShape("cp", 2, "result", P("result"));
        Assert.Equal("cp: target 'result': No such file or directory", err);
    }

    [Fact]
    public void OperandShape_SeveralSources_FileDest_NotADirectory()
    {
        File.WriteAllText(P("f"), "x");
        Assert.Equal("mv: target 'f': Not a directory", TransferValidation.CheckOperandShape("mv", 3, "f", P("f")));
    }

    [Fact]
    public void OperandShape_SeveralSources_DirDest_Ok()
    {
        Directory.CreateDirectory(P("d"));
        Assert.Null(TransferValidation.CheckOperandShape("cp", 5, "d", P("d")));
    }

    [Fact]
    public void ResolveTarget_ExistingDir_AppendsBasename_TrailingSeparatorIgnored()
    {
        var target = TransferValidation.ResolveTarget(P("a", "src") + Path.DirectorySeparatorChar, P("d"), destIsDir: true);
        Assert.Equal(P("d", "src"), target);
        Assert.Equal(P("new"), TransferValidation.ResolveTarget(P("a"), P("new"), destIsDir: false));
    }

    [Fact]
    public void IsStrictlyInside_RequiresSeparatorBoundary()
    {
        Assert.True(TransferValidation.IsStrictlyInside(P("src", "sub"), P("src")));
        Assert.False(TransferValidation.IsStrictlyInside(P("src2"), P("src")));   // prefix, not child
        Assert.False(TransferValidation.IsStrictlyInside(P("src"), P("src")));    // equal is not "inside"
    }

    [Fact]
    public void Identity_SamePath_IsSameFile_EvenWithTrailingSeparator()
    {
        var err = TransferValidation.CheckIdentity("mv", P("p", "src") + Path.DirectorySeparatorChar, srcIsDir: true, P("p", "src"));
        Assert.Contains("are the same file", err);
    }

    [Fact]
    public void Identity_DirIntoOwnSubdir_MessagesDifferPerCommand()
    {
        var mv = TransferValidation.CheckIdentity("mv", P("s"), true, P("s", "sub", "s"));
        var cp = TransferValidation.CheckIdentity("cp", P("s"), true, P("s", "sub", "s"));
        Assert.StartsWith("mv: cannot move ", mv);
        Assert.Contains("to a subdirectory of itself", mv);
        Assert.StartsWith("cp: cannot copy a directory, ", cp);
        Assert.Contains("into itself", cp);
    }

    [Fact]
    public void Identity_SiblingWithSharedPrefix_IsFine() =>
        Assert.Null(TransferValidation.CheckIdentity("mv", P("src"), true, P("src2", "src")));

    [Fact]
    public void Occupancy_MissingTarget_Ok() =>
        Assert.Null(TransferValidation.CheckOccupancy("mv", P("s"), true, P("nothing"), replaceEmptyDirOnly: true));

    [Fact]
    public void Occupancy_DirOverFile_Refused()
    {
        File.WriteAllText(P("t"), "x");
        Assert.Equal($"cp: cannot overwrite non-directory '{P("t")}' with directory '{P("s")}'",
            TransferValidation.CheckOccupancy("cp", P("s"), true, P("t"), false));
    }

    [Fact]
    public void Occupancy_FileOverDir_Refused()
    {
        Directory.CreateDirectory(P("t"));
        Assert.Equal($"mv: cannot overwrite directory '{P("t")}' with non-directory",
            TransferValidation.CheckOccupancy("mv", P("f"), false, P("t"), true));
    }

    [Fact]
    public void Occupancy_DirOverNonEmptyDir_MvRefusesCpMerges()
    {
        Directory.CreateDirectory(P("t"));
        File.WriteAllText(P("t", "keep"), "x");
        Assert.Equal($"mv: cannot overwrite '{P("t")}': Directory not empty",
            TransferValidation.CheckOccupancy("mv", P("s"), true, P("t"), replaceEmptyDirOnly: true));
        Assert.Null(TransferValidation.CheckOccupancy("cp", P("s"), true, P("t"), replaceEmptyDirOnly: false));
    }

    [Fact]
    public void Occupancy_DirOverEmptyDir_MvAllowed()
    {
        Directory.CreateDirectory(P("t"));
        Assert.Null(TransferValidation.CheckOccupancy("mv", P("s"), true, P("t"), replaceEmptyDirOnly: true));
    }

    [Fact]
    public void Occupancy_FileOverFile_Allowed()
    {
        File.WriteAllText(P("t"), "x");
        Assert.Null(TransferValidation.CheckOccupancy("mv", P("f"), false, P("t"), true));
    }
}
