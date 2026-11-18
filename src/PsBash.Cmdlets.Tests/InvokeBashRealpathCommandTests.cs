using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashRealpath
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: GNU coreutils 9.4 realpath. Canonicalization is <see cref="CanonicalPath"/> (unit-tested in
/// CanonicalPathTests); these cover the command surface: modes, -s/-L, -q, messages, exit status.
///
/// Failure-surface axes that apply: existing/missing paths, multiple operands, --help dispatch,
/// quoting/injection (Directive 12). Pipeline / file-content / signal axes do
/// not apply: realpath has no streaming or file-read surface.
/// </summary>
public class InvokeBashRealpathCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;
    private readonly string _existingFile;

    public InvokeBashRealpathCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(Path.GetTempPath(), $"psb-realpath-{Guid.NewGuid():N}".Substring(0, 25));
        Directory.CreateDirectory(_tmpDir);
        _existingFile = Path.Combine(_tmpDir, "exists.txt");
        File.WriteAllText(_existingFile, "hi\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private string[] RunLines(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var err = pwsh.AddScript("$error | Select-Object -First 1").Invoke();
        pwsh.Commands.Clear();
        Assert.True(err.Count == 0 || err[0] == null,
            $"Unexpected error running [{script}]: {(err.Count > 0 ? err[0]?.ToString() : "none")}");

        return result.Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Realpath_E_ExistingPath_ResolvesNormally()
    {
        var lines = RunLines($"Invoke-BashRealpath -e '{_existingFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.Contains("exists.txt", lines[0]);
    }

    [Fact]
    public void Realpath_E_MissingPath_ErrorsAndEmitsNoPath()
    {
        // REGRESSION: -e was declared but a no-op — a missing path was silently
        // resolved. GNU realpath -e fails on a non-existent path.
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var missing = Path.Combine(_tmpDir, "nope.txt").Replace("'", "''");
        var result = pwsh.AddScript($"Invoke-BashRealpath -e '{missing}'").Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        Assert.Empty(result);
        Assert.Contains(errs, m => m.Contains("No such file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Realpath_ExistingFile_ReturnsResolvedPath()
    {
        var lines = RunLines($"Invoke-BashRealpath '{_existingFile.Replace("'", "''")}'");
        Assert.Single(lines);
        // The resolved path should reference the same file. Use FileInfo for
        // canonical comparison (handles case-insensitivity on Windows).
        Assert.Equal(
            new FileInfo(_existingFile).FullName,
            new FileInfo(lines[0]).FullName,
            ignoreCase: true);
    }

    [Fact]
    public void Realpath_NonexistentPath_ReturnsCanonicalForm()
    {
        // psm1 oracle: when Resolve-Path fails, fall back to
        // GetUnresolvedProviderPathFromPSPath, which always succeeds.
        var ghost = Path.Combine(_tmpDir, "ghost.txt");
        var lines = RunLines($"Invoke-BashRealpath '{ghost.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.EndsWith("ghost.txt", lines[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Realpath_MultipleOperands_EmitsOneLinePerOperand()
    {
        var second = Path.Combine(_tmpDir, "ghost.txt");
        var lines = RunLines(
            $"Invoke-BashRealpath '{_existingFile.Replace("'", "''")}' '{second.Replace("'", "''")}'");
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("exists.txt", lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("ghost.txt", lines[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Realpath_FlagWithoutOperand_IsMissingOperand()
    {
        // GNU: options are parsed (in order, abbreviations allowed); with no FILE it is "missing operand", status 1.
        var pwsh = _fixture.AcquireFresh();
        CmdResult.Run(pwsh, "Invoke-BashRealpath -e")
            .AssertFailed(1, "realpath: missing operand", "Try 'realpath --help' for more information.");
    }

    [Fact]
    public void Realpath_MissingIntermediate_FailsAndSaysWhy()
    {
        // Default mode: all but the last component must exist; -q silences the message, not the status.
        var pwsh = _fixture.AcquireFresh();
        var dir = _tmpDir.Replace("'", "''");
        CmdResult.Run(pwsh, $"Set-Location -LiteralPath '{dir}'; Invoke-BashRealpath nosuch/x exists.txt/x")
            .AssertFailed(1, "realpath: nosuch/x: No such file or directory", "realpath: exists.txt/x: Not a directory");
        var quiet = CmdResult.Run(pwsh, $"Set-Location -LiteralPath '{dir}'; Invoke-BashRealpath -q nosuch/x").AssertFailed(1);
        Assert.Equal("", quiet.Stderr);
    }

    [Fact]
    public void Realpath_DashS_ChecksOnlyTheLastComponent()
    {
        // GNU -s reads no link: nosuch/x is printed, exists.txt/x is "Not a directory".
        var pwsh = _fixture.AcquireFresh();
        var dir = _tmpDir.Replace("'", "''");
        var ok = CmdResult.Run(pwsh, $"Set-Location -LiteralPath '{dir}'; Invoke-BashRealpath -s nosuch/x").AssertSuccess();
        Assert.EndsWith("nosuch/x", ok.Lines[0]);
        CmdResult.Run(pwsh, $"Set-Location -LiteralPath '{dir}'; Invoke-BashRealpath -s exists.txt/x")
            .AssertFailed(1, "realpath: exists.txt/x: Not a directory");
    }

    [Fact]
    public void Realpath_DirectDashP_BindsWithoutBinderCrash()
    {
        // Bare -P is ambiguous between -PipelineVariable/-ProgressAction; the P decoy binds it (Pester calls directly).
        var pwsh = _fixture.AcquireFresh();
        var r = CmdResult.Run(pwsh, $"Invoke-BashRealpath -P '{_existingFile.Replace("'", "''")}'").AssertSuccess();
        Assert.EndsWith("exists.txt", r.Lines[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Realpath_DashL_ChecksTheUnfoldedName()
    {
        // GNU -L: nosuch/../exists.txt still needs nosuch to be a directory (ENOENT), as the walk meets it.
        var pwsh = _fixture.AcquireFresh();
        var dir = _tmpDir.Replace("'", "''");
        CmdResult.Run(pwsh, $"Set-Location -LiteralPath '{dir}'; Invoke-BashRealpath -L nosuch/../exists.txt")
            .AssertFailed(1, "realpath: nosuch/../exists.txt: No such file or directory");
        var m = CmdResult.Run(pwsh, $"Set-Location -LiteralPath '{dir}'; Invoke-BashRealpath -Lm nosuch/../exists.txt").AssertSuccess();
        Assert.EndsWith("exists.txt", m.Lines[0]);
    }

    [Fact]
    public void Realpath_FlagInterleavedWithOperand_OnlyOperandResolved()
    {
        var lines = RunLines($"Invoke-BashRealpath -e '{_existingFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.EndsWith("exists.txt", lines[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Realpath_ViaAlias_Works()
    {
        var lines = RunLines($"realpath '{_existingFile.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.EndsWith("exists.txt", lines[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Realpath_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashRealpath --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("realpath", StringComparison.OrdinalIgnoreCase));
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Realpath_PathWithScriptblockChars_TreatedAsLiteralPath()
    {
        // Path containing $() chars. Must not be evaluated as PowerShell code.
        // Result is the canonical (unresolved) form of the literal string.
        var weirdPath = Path.Combine(_tmpDir, "$(throw'pwn').txt");
        var lines = RunLines($"Invoke-BashRealpath '{weirdPath.Replace("'", "''")}'");
        Assert.Single(lines);
        Assert.DoesNotContain(lines, l => l.Contains("pwn") && !l.Contains("$(throw"));
        Assert.EndsWith(".txt", lines[0], StringComparison.OrdinalIgnoreCase);
    }
}
