using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashReadlink
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 function emitted a typed PsBash.ReadlinkOutput object whose
/// BashText / Path is either FileSystemInfo.LinkTarget (when the operand is a
/// symlink) or item.FullName (regular file/dir). On a missing operand or a
/// missing path, it called Write-Error -ErrorAction Continue with a
/// bash-style "readlink: PATH: No such file or directory" message. The -f
/// flag switched to a Resolve-Path branch.
///
/// Failure-surface axes that apply: empty input (missing operand), unicode
/// path, missing target (Directive 3 #14), alias resolution, --help dispatch,
/// injection probe (Directive 12). Pipeline / large-input / signal axes do
/// not apply: readlink has no streaming surface.
/// </summary>
public class InvokeBashReadlinkCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;
    private readonly string _regularFile;

    public InvokeBashReadlinkCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(Path.GetTempPath(), $"psb-readlink-{Guid.NewGuid():N}".Substring(0, 25));
        Directory.CreateDirectory(_tmpDir);
        _regularFile = Path.Combine(_tmpDir, "regular.txt");
        File.WriteAllText(_regularFile, "hi\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private string[] RunBashText(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        return result.Select(o =>
        {
            if (o == null) return "";
            var bt = o.Properties["BashText"]?.Value as string;
            return bt ?? o.ToString();
        }).ToArray();
    }

    /// <summary>
    /// Attempt to create a symlink. Requires Developer Mode / admin on Windows.
    /// Returns null if symlink creation is not permitted on this host.
    /// </summary>
    private static string? TryCreateSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return linkPath;
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
    }

    [Fact]
    public void Readlink_RegularFile_ReturnsFullName()
    {
        // psm1 oracle: when item.Target is empty, emit item.FullName.
        var lines = RunBashText($"Invoke-BashReadlink '{_regularFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.True(new FileInfo(lines[0]).FullName.Equals(
            new FileInfo(_regularFile).FullName,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Readlink_DashE_CanonicalizesWithoutBinderCrash()
    {
        // Regression: bare -e prefix-collides with -ErrorAction/-ErrorVariable and the
        // binder crashed ("ambiguous") before the cmdlet ran. It must bind (E decoy)
        // and canonicalize like -f. A passing invocation proves no binder crash.
        var lines = RunBashText($"Invoke-BashReadlink -e '{_regularFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.True(new FileInfo(lines[0]).FullName.Equals(
            new FileInfo(_regularFile).FullName,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Readlink_DashV_AcceptedWithoutBinderCrash()
    {
        // Regression: bare -v silently bound -Verbose. It must be accepted (V decoy)
        // and produce the normal output for the operand.
        var lines = RunBashText($"Invoke-BashReadlink -v '{_regularFile.Replace("'", "''")}'");
        Assert.Single(lines);
    }

    [SkippableFact]
    public void Readlink_Symlink_ReturnsLinkTarget()
    {
        var linkPath = Path.Combine(_tmpDir, "link-to-regular");
        var created = TryCreateSymlink(linkPath, _regularFile);
        Skip.If(created == null, "Symlink creation not permitted on this host");

        var lines = RunBashText($"Invoke-BashReadlink '{linkPath.Replace("'", "''")}'");
        Assert.Single(lines);
        // .NET's FileSystemInfo.LinkTarget returns whatever was passed at
        // creation. We compare canonicalized FullNames so case/separator
        // differences don't break the assertion on Windows.
        Assert.True(new FileInfo(lines[0]).FullName.Equals(
            new FileInfo(_regularFile).FullName,
            StringComparison.OrdinalIgnoreCase),
            $"Expected link target to match {_regularFile}, got {lines[0]}");
    }

    [Fact]
    public void Readlink_MissingPath_EmitsNoOutput()
    {
        // psm1 oracle: Get-Item fails, Write-Error -ErrorAction Continue is
        // raised, foreach continues. No BashText is emitted for the missing
        // operand.
        var ghost = Path.Combine(_tmpDir, "ghost.txt");
        var lines = RunBashText($"Invoke-BashReadlink '{ghost.Replace("'", "''")}'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Readlink_MissingOperand_EmitsNoOutput()
    {
        // psm1 oracle: zero operands -> "readlink: missing operand" error +
        // return. No output objects.
        var lines = RunBashText("Invoke-BashReadlink");
        Assert.Empty(lines);
    }

    [Fact]
    public void Readlink_DashFCanonicalize_ResolvesPath()
    {
        // -f branch: Resolve-Path on an existing file. Should emit the
        // canonical path.
        var lines = RunBashText($"Invoke-BashReadlink -f '{_regularFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.True(new FileInfo(lines[0]).FullName.Equals(
            new FileInfo(_regularFile).FullName,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Readlink_DashFMissing_EmitsNoOutput()
    {
        var ghost = Path.Combine(_tmpDir, "ghost-f.txt");
        var lines = RunBashText($"Invoke-BashReadlink -f '{ghost.Replace("'", "''")}'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Readlink_MultipleOperands_OneOutputPerExisting()
    {
        var ghost = Path.Combine(_tmpDir, "ghost-multi.txt");
        var lines = RunBashText(
            $"Invoke-BashReadlink '{_regularFile.Replace("'", "''")}' '{ghost.Replace("'", "''")}'");
        Assert.Single(lines); // only the existing one emits
        Assert.True(new FileInfo(lines[0]).FullName.Equals(
            new FileInfo(_regularFile).FullName,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Readlink_UnicodeFilename_Roundtrips()
    {
        var unicodeFile = Path.Combine(_tmpDir, "файл-📄.txt");
        File.WriteAllText(unicodeFile, "hi\n");
        var lines = RunBashText($"Invoke-BashReadlink '{unicodeFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.True(new FileInfo(lines[0]).FullName.Equals(
            new FileInfo(unicodeFile).FullName,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Readlink_ViaAlias_Works()
    {
        var lines = RunBashText($"readlink '{_regularFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.True(new FileInfo(lines[0]).FullName.Equals(
            new FileInfo(_regularFile).FullName,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Readlink_HelpFlag_EmitsUsage()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript("Invoke-BashReadlink --help").Invoke();
        var lines = result.Select(o => o?.ToString() ?? "").ToArray();
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("readlink", StringComparison.OrdinalIgnoreCase));
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Readlink_PathWithScriptblockChars_TreatedAsLiteralPath()
    {
        // A literal path string containing $() must never be evaluated as
        // PowerShell code. The path doesn't exist so we expect zero output
        // and zero side-effects (no "pwn" anywhere).
        var weirdPath = Path.Combine(_tmpDir, "$(throw'pwn').txt");
        var lines = RunBashText($"Invoke-BashReadlink '{weirdPath.Replace("'", "''")}'");
        Assert.Empty(lines);
        Assert.DoesNotContain(lines, l => l.Contains("pwn"));
    }
}
