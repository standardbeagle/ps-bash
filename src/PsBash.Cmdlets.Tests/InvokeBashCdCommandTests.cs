using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Module-mode <c>cd</c> (<c>Invoke-BashCd</c>) must move BOTH halves of the working directory:
/// the PowerShell location and <see cref="Environment.CurrentDirectory"/>.
///
/// <para>The psm1 aliased <c>cd</c> straight to <c>Set-Location</c>, which moves only the first.
/// Under <c>Import-Module PsBash</c> in a plain pwsh that made every relative read through a .NET
/// path API resolve against the PRE-<c>cd</c> directory — a wrong file at exit 0, with no error
/// anywhere (see <c>LineStreamCatFileParityTests.Streamed_CatRelative_AfterModuleModeCdAlias_…</c>,
/// the end-to-end instance). These are the direct unit probes of the fix.</para>
///
/// <para>Oracle note (Directive 1): PowerShell-side working-directory state has no bash
/// equivalent to diff against — bash has one working directory, not two. Hand-written asserts.</para>
///
/// <para>Serialized with the other classes that move the process working directory (see
/// <see cref="ProcessWorkingDirectoryCollection"/>): it is per-process state, so two of them
/// running concurrently would corrupt each other.</para>
/// </summary>
[Collection(ProcessWorkingDirectoryCollection.Name)]
public class InvokeBashCdCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;
    private readonly string _sub;

    public InvokeBashCdCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-cd-" + Guid.NewGuid().ToString("N"));
        _sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(_sub);
    }

    public void Dispose()
    {
        // `cd` leaves the process cwd inside the tree; Windows cannot delete the directory a
        // process is sitting in, so step out before removing it.
        SharedPwshFixture.RestoreProcessWorkingDirectory(Path.GetTempPath());
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Cd_MovesBothHalvesOfTheWorkingDirectory()
    {
        // Seed BOTH halves inside the script: the fixture restores the process cwd right before
        // each invocation, so seeding from C# would be undone.
        var result = RunOneShot("cd 'sub'");

        Assert.Equal(_sub, result.Location, ignoreCase: true);
        Assert.Equal(_sub, result.ProcessCwd, ignoreCase: true);
    }

    [Fact]
    public void Cd_AliasResolvesToTheSyncingCommand_NotBareSetLocation()
    {
        // The whole fix is the alias target. If someone repoints it back at Set-Location the
        // behavior tests above still pass in isolation only by luck of ordering — this pins it.
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript("(Get-Alias cd).Definition").Invoke();

        Assert.Equal("Invoke-BashCd", result[0].ToString());
    }

    [Fact]
    public void Cd_Dash_ReturnsToThePreviousDirectory_BothHalves()
    {
        // `cd -` is Set-Location's own feature; splatting the operands through verbatim is what
        // keeps it working, and the sync must follow it back.
        var result = RunOneShot("cd 'sub'; cd -");

        Assert.Equal(_dir, result.Location, ignoreCase: true);
        Assert.Equal(_dir, result.ProcessCwd, ignoreCase: true);
    }

    [Fact]
    public void Cd_NoOperand_GoesHome_BothHalves()
    {
        var result = RunOneShot("cd");

        var home = result.Home;
        Assert.Equal(home, result.Location, ignoreCase: true);
        Assert.Equal(home, result.ProcessCwd, ignoreCase: true);
    }

    [Fact]
    public void Cd_MissingDirectory_FailsAndMovesNeitherHalf()
    {
        // A failed move must not sync: syncing on failure would be the same divergence with the
        // halves swapped.
        var result = RunOneShot("cd 'no-such-dir' -ErrorAction SilentlyContinue");

        Assert.Equal(_dir, result.Location, ignoreCase: true);
        Assert.Equal(_dir, result.ProcessCwd, ignoreCase: true);
    }

    /// <summary>
    /// Run a script with both halves seeded at <see cref="_dir"/>, and report where both ended up.
    /// One invocation: the fixture resets between invocations, so a seed and the command that
    /// depends on it must not be split apart.
    /// </summary>
    private (string Location, string ProcessCwd, string Home) RunOneShot(string script)
    {
        var q = _dir.Replace("'", "''");
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(
            $"[System.Environment]::CurrentDirectory = '{q}'; Set-Location -LiteralPath '{q}'; " +
            script + "; " +
            "$PWD.ProviderPath; [System.Environment]::CurrentDirectory; $HOME").Invoke();

        Assert.Equal(3, result.Count);
        return (Trim(result[0]), Trim(result[1]), Trim(result[2]));
    }

    private static string Trim(object value)
        => (value?.ToString() ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar);
}
