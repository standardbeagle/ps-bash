using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// GUARD (docs/specs/paths.md): a command that walks a tree, or prints a path it built from one, prints it with
/// <c>/</c>, as bash does (<c>src/sub/b.txt</c>), never the native <c>src\sub\b.txt</c>. Each walker joins its own
/// display path, so one cmdlet can regress alone: rg did (<c>RgWalker.Join</c> used Path.DirectorySeparatorChar). A
/// new tree-walking or path-listing command adds a row here. Oracle (bash 5.2 / GNU, Ubuntu 24.04): every row's real
/// output names <c>src/sub/b.txt</c> (or <c>src/sub</c>); the assertion is "no backslash", which holds on every OS.
/// </summary>
public class WalkedPathOutputGuardTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public WalkedPathOutputGuardTests(SharedPwshFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("Invoke-BashRg '-l' hello src")]
    [InlineData("Invoke-BashRg '--files' src")]
    [InlineData("Invoke-BashRg '--files'")]
    [InlineData("Invoke-BashRg hello")]
    [InlineData("Invoke-BashRg '-c' hello 'src/'")]
    [InlineData("Invoke-BashRg '--vimgrep' hello './src'")]
    [InlineData("Invoke-BashRg '--json' hello src")]
    [InlineData("Invoke-BashGrep '-rl' hello src")]
    [InlineData("Invoke-BashGrep '-r' hello .")]
    [InlineData("Invoke-BashFind src")]
    [InlineData("Invoke-BashFind . '-name' b.txt")]
    [InlineData("Invoke-BashLs '-R' src")]
    [InlineData("Invoke-BashDu '-a' src")]
    [InlineData("Invoke-BashCp '-rv' src dst")]
    [InlineData("Invoke-BashDiff '-rq' src other")]
    public void TreeWalkingCommand_PrintsForwardSlashPaths(string command)
    {
        string dir = Path.Combine(Path.GetTempPath(), "psb-walk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src", "sub"));
        Directory.CreateDirectory(Path.Combine(dir, "other", "sub"));
        File.WriteAllText(Path.Combine(dir, "src", "a.txt"), "hello\n");
        File.WriteAllText(Path.Combine(dir, "src", "sub", "b.txt"), "hello\n");
        File.WriteAllText(Path.Combine(dir, "other", "sub", "b.txt"), "other\n");
        try
        {
            var r = CmdResult.Run(_fixture.AcquireFresh(),
                $"Set-Location -LiteralPath '{dir.Replace("'", "''")}'; " +
                $"{command} | ForEach-Object {{ if ($_.PSObject.Properties['BashText']) {{ $_.BashText }} else {{ \"$_\" }} }}");
            Assert.Contains("sub", r.Stdout);
            // rg --json escapes its text (`hello\n`, a native separator would be `src\\sub`): check the path there.
            bool clean = command.Contains("--json")
                ? !r.Stdout.Contains("src\\\\", StringComparison.Ordinal) && r.Stdout.Contains("src/", StringComparison.Ordinal)
                : r.Stdout.IndexOf('\\') < 0;
            Assert.True(clean, $"`{command}` printed a native separator:\n{r.Stdout}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
