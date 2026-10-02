using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Every file name grep prints (the -H prefix, -l/-L lists, -c lines, context lines, the -r walk) is the
/// operand AS TYPED, never the resolved absolute path (oracle: GNU grep 3.11, `wsl bash`). Tree:
/// f, n, sub/g, sub/deep/h; f/sub/g/sub/deep/h contain "hello", n does not.
/// </summary>
public class GrepOperandNamesTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public GrepOperandNamesTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-grepn-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(Path.Combine(_dir, "sub", "deep"));
        File.WriteAllText(Path.Combine(_dir, "f"), "hello\nfoo\n");
        File.WriteAllText(Path.Combine(_dir, "n"), "nomatch\n");
        File.WriteAllText(Path.Combine(_dir, "sub", "g"), "hello\nbar\n");
        File.WriteAllText(Path.Combine(_dir, "sub", "deep", "h"), "x hello\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // grep's emitted shape: every dash word single-quoted. Sorted: the walk order is not the point.
    private string[] Run(string grepArgs, bool sort = false)
    {
        var script = $"Set-Location '{_dir.Replace("'", "''")}'; Invoke-BashGrep {grepArgs}";
        var lines = CmdResult.Run(_fixture.AcquireFresh(), script).Lines.ToArray();
        if (sort) Array.Sort(lines, StringComparer.Ordinal);
        return lines;
    }

    [Theory]
    [InlineData("'-H' hello f", "f:hello")]
    [InlineData("'-H' hello ./f", "./f:hello")]
    [InlineData("'-H' hello sub/g", "sub/g:hello")]
    [InlineData("'-n' '-H' hello sub/deep/h", "sub/deep/h:1:x hello")]
    public void Prefix_IsOperandAsTyped(string args, string expected)
        => Assert.Equal(new[] { expected }, Run(args));

    [Fact]
    public void ListFiles_AreOperandsAsTyped()
        => Assert.Equal(new[] { "f", "sub/g" }, Run("'-l' hello f sub/g ./n"));

    [Fact]
    public void ListNonMatching_IsOperandAsTyped()
        => Assert.Equal(new[] { "n" }, Run("'-L' hello f sub/g n"));

    [Fact]
    public void Count_MultipleFiles_UsesOperandsAsTyped()
        => Assert.Equal(new[] { "f:1", "sub/g:1", "n:0" }, Run("'-c' hello f sub/g n"));

    [Fact]
    public void MultipleFiles_PrefixIsOperandAsTyped()
        => Assert.Equal(new[] { "f:hello", "sub/g:hello" }, Run("hello f sub/g"));

    [Fact]
    public void ContextLines_UseOperandAsTyped()
        => Assert.Equal(new[] { "f:hello", "f-foo" }, Run("'-H' '-A1' hello f"));

    [Fact]
    public void Recursive_UsesTypedRootPlusRelativePath()
        => Assert.Equal(new[] { "sub/deep/h:x hello", "sub/g:hello" }, Run("'-r' hello sub", sort: true));

    [Fact]
    public void Recursive_DotRoot_KeepsDotSlash()
        => Assert.Equal(new[] { "./f:hello", "./sub/deep/h:x hello", "./sub/g:hello" }, Run("'-r' hello .", sort: true));

    [Fact]
    public void Recursive_NoOperand_HasNoPrefix()
        => Assert.Equal(new[] { "f:hello", "sub/deep/h:x hello", "sub/g:hello" }, Run("'-r' hello", sort: true));

    [Fact]
    public void Recursive_TrailingSlashRoot_NoDoubleSlash()
        => Assert.Equal(new[] { "sub/deep/h:x hello", "sub/g:hello" }, Run("'-r' hello sub/", sort: true));

    [Fact]
    public void Recursive_ListAndCount_UseTypedNames()
    {
        Assert.Equal(new[] { "f", "sub/deep/h", "sub/g" }, Run("'-rl' hello sub f", sort: true).OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "sub/deep/h:1", "sub/g:1" }, Run("'-rc' hello sub", sort: true));
    }

    [Fact]
    public void Recursive_FileAndDirOperands_MixTypedNames()
        => Assert.Equal(new[] { "f:hello", "sub/g:hello" }, Run("'-r' hello f sub/g"));
}
