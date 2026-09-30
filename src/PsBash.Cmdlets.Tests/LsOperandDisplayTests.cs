using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A FILE / <c>-d</c> operand is printed exactly as typed. Oracle (bash 5.2, Ubuntu-24.04):
/// <c>ls -d . .. ./sub sub/ ./a.txt</c> -> <c>.</c> <c>..</c> <c>./a.txt</c> <c>./sub</c> <c>sub/</c>;
/// <c>ls -d</c> -> <c>.</c>; <c>ls -ld .</c> last column <c>.</c>; <c>ls -ld ./sub/</c> -> <c>./sub/</c>;
/// <c>ls ./a.txt</c> -> <c>./a.txt</c>. A directory LISTING still prints entry names.
/// </summary>
public class LsOperandDisplayTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public LsOperandDisplayTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-lsop-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string[] Run(string bash)
    {
        var ps = PsEmitter.Transpile(bash)!;
        var r = CmdResult.Run(_fixture.AcquireFresh(), $"Set-Location '{_dir}'; {ps}");
        Assert.True(r.ExitCode == 0, r.Stderr);
        return r.Lines.Select(l => l.TrimEnd('\n')).ToArray();
    }

    [Theory]
    [InlineData("ls -d .", ".")]
    [InlineData("ls -d ..", "..")]
    [InlineData("ls -d ./sub", "./sub")]
    [InlineData("ls -d sub/", "sub/")]
    [InlineData("ls -d", ".")]
    [InlineData("ls ./a.txt", "./a.txt")]
    [InlineData("ls a.txt", "a.txt")]
    public void Operand_PrintedAsTyped(string bash, string expected)
        => Assert.Equal(new[] { expected }, Run(bash));

    [Fact]
    public void LongDirOperand_LastColumnIsOperand()
    {
        Assert.EndsWith(" .", Assert.Single(Run("ls -ld .")));
        Assert.EndsWith(" ./sub/", Assert.Single(Run("ls -ld ./sub/")));
    }

    [Fact]
    public void DirectoryListing_StillPrintsEntryNames()
        => Assert.Equal(new[] { "a.txt", "sub" }, Run("ls ."));

    [Fact]
    public void TypedEntry_KeepsRealName()
    {
        var ps = PsEmitter.Transpile("ls -d .")!;
        var r = CmdResult.Run(_fixture.AcquireFresh(), $"Set-Location '{_dir}'; ({ps}).Name");
        Assert.Equal(Path.GetFileName(_dir), r.Lines.Single().TrimEnd('\n'));
    }
}
