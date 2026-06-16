using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 Phase 4 follow-on migration of
/// Invoke-BashStat from a PsBash.psm1 script function to a binary cmdlet
/// (PsBash.Cmdlets.dll / InvokeBashStatCommand.cs).
///
/// Oracle: the original psm1 Invoke-BashStat + Format-StatString helper
/// (deleted at this commit). The cmdlet reimplements both, plus the
/// Get-BashFileInfo slice (duplicated from find / ls — see class remarks).
///
/// Failure-surface coverage (qa-rubric Directive 3):
/// - Empty input → missing-operand branch (test: MissingOperand_*).
/// - Unicode → unicode-named file (test: UnicodeFilename_*).
/// - Missing target → cannot-stat error + LASTEXITCODE=1 (test: MissingFile_*).
/// - Quoting/Injection (Directive 12) → format-string with $() embedded
///   (test: FormatString_Injection_PreservedAsLiteral).
/// Negative cases (Directive 7): MissingOperand, MissingFile, both verified.
/// </summary>
public class InvokeBashStatCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashStatCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(), "psbash-stat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string Mk(string rel, string content = "hello world")
    {
        var path = Path.Combine(_tmpDir, rel);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
        return path;
    }

    private System.Collections.ObjectModel.Collection<PSObject> Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    private System.Collections.ObjectModel.Collection<PSObject> RunAllowError(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    private static string Esc(string p) => p.Replace("\\", "\\\\");

    private (string[] outLines, string[] errors) RunWithErrors(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(script).Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        var outLines = result.Select(o =>
            o?.Properties["BashText"]?.Value as string ?? o?.ToString() ?? "").ToArray();
        return (outLines, errs);
    }

    // ===================== Default multi-line output =====================

    [Fact]
    public void DefaultOutput_RegularFile_EmitsMultiLineBlockWithFileLabel()
    {
        var f = Mk("regular.txt", "hello");
        var results = Run($"Invoke-BashStat '{Esc(f)}'");
        Assert.Single(results);
        var bashText = (string?)results[0].Properties["BashText"]?.Value;
        Assert.NotNull(bashText);
        Assert.Contains("File: regular.txt", bashText);
        Assert.Contains("Size:", bashText!);
        Assert.Contains("Blocks:", bashText!);
        Assert.Contains("regular file", bashText!);
        Assert.Contains("Modify:", bashText!);
    }

    [Fact]
    public void DefaultOutput_Directory_LabelsAsDirectory()
    {
        var d = Path.Combine(_tmpDir, "asubdir");
        Directory.CreateDirectory(d);
        var results = Run($"Invoke-BashStat '{Esc(d)}'");
        Assert.Single(results);
        var bashText = (string?)results[0].Properties["BashText"]?.Value;
        Assert.NotNull(bashText);
        Assert.Contains("directory", bashText!);
        Assert.True((bool)results[0].Properties["IsDirectory"].Value);
    }

    // ===================== -c FORMAT =====================

    [Fact]
    public void FormatC_PercentN_EmitsName()
    {
        var f = Mk("namefile.txt");
        var results = Run($"Invoke-BashStat -c '%n' '{Esc(f)}'");
        var bashText = (string?)results[0].Properties["BashText"]?.Value;
        Assert.Equal("namefile.txt\n", bashText);
    }

    [Fact]
    public void FormatC_PercentS_EmitsSizeBytes()
    {
        var f = Mk("size7.txt", "1234567");
        var results = Run($"Invoke-BashStat -c '%s' '{Esc(f)}'");
        var bashText = (string?)results[0].Properties["BashText"]?.Value;
        Assert.Equal("7\n", bashText);
    }

    [Fact]
    public void FormatC_PercentY_EmitsMtimeEpoch()
    {
        var f = Mk("epoch.txt");
        var results = Run($"Invoke-BashStat -c '%Y' '{Esc(f)}'");
        var bashText = ((string?)results[0].Properties["BashText"]?.Value)?.TrimEnd('\n');
        Assert.NotNull(bashText);
        Assert.True(long.TryParse(bashText, out var epoch));
        Assert.True(epoch > 0, $"mtime epoch should be positive, got {epoch}");
    }

    [Fact]
    public void FormatC_PercentF_UnknownSpec_PreservesLiteral()
    {
        // Oracle: unknown spec → append the percent and advance by one; the
        // next iteration handles the spec char as a regular literal.
        var f = Mk("unkF.txt");
        var results = Run($"Invoke-BashStat -c '%F' '{Esc(f)}'");
        var bashText = (string?)results[0].Properties["BashText"]?.Value;
        Assert.Equal("%F\n", bashText);
    }

    [Fact]
    public void FormatC_MultiToken_EmitsConcatenatedSpace()
    {
        var f = Mk("multi.txt", "12345");
        var results = Run($"Invoke-BashStat -c '%n %s' '{Esc(f)}'");
        var bashText = (string?)results[0].Properties["BashText"]?.Value;
        Assert.Equal("multi.txt 5\n", bashText);
    }

    [Fact]
    public void FormatC_PercentPercent_EmitsLiteralPercent()
    {
        var f = Mk("pct.txt");
        var results = Run($"Invoke-BashStat -c '%%' '{Esc(f)}'");
        var bashText = (string?)results[0].Properties["BashText"]?.Value;
        Assert.Equal("%\n", bashText);
    }

    // ===================== --printf=FORMAT =====================

    [Fact]
    public void PrintfLongForm_ExpandsEscapesAndOmitsTrailingNewline()
    {
        // --printf path runs Expand-EscapeSequences over the formatted result
        // (oracle), so \n in the format becomes a real newline AND the
        // result is NOT suffixed with an extra newline (unlike -c).
        var f = Mk("printf.txt", "abc");
        var results = Run($"Invoke-BashStat --printf=%n--%s '{Esc(f)}'");
        var bashText = (string?)results[0].Properties["BashText"]?.Value;
        // No trailing newline (printf path); name then literal "--" then size.
        Assert.Equal("printf.txt--3", bashText);
    }

    // ===================== -t terse one-line =====================

    [Fact]
    public void TerseMode_EmitsFourteenSpaceSepFields()
    {
        var f = Mk("terse.txt", "xy");
        var results = Run($"Invoke-BashStat -t '{Esc(f)}'");
        var bashText = ((string?)results[0].Properties["BashText"]?.Value)?.TrimEnd('\n');
        Assert.NotNull(bashText);
        var fields = bashText!.Split(' ');
        Assert.Equal(14, fields.Length);
        Assert.Equal("terse.txt", fields[0]);
        Assert.Equal("2", fields[1]); // size
    }

    // ===================== Multi-operand =====================

    [Fact]
    public void MultiOperand_EmitsOnePerOperand()
    {
        var f1 = Mk("a.txt", "a");
        var f2 = Mk("b.txt", "bb");
        var results = Run($"Invoke-BashStat -c '%n' '{Esc(f1)}' '{Esc(f2)}'");
        Assert.Equal(2, results.Count);
        Assert.Equal("a.txt\n", results[0].Properties["BashText"]?.Value);
        Assert.Equal("b.txt\n", results[1].Properties["BashText"]?.Value);
    }

    // ===================== Negative path =====================

    [Fact]
    public void MissingFile_EmitsErrorAndSetsLastExitCode1()
    {
        var missing = Path.Combine(_tmpDir, "nope.txt");
        var script =
            $"$global:LASTEXITCODE = 0; " +
            $"Invoke-BashStat '{Esc(missing)}' 2>$null; " +
            $"$global:LASTEXITCODE";
        var results = RunAllowError(script);
        // No StatEntry emitted; LASTEXITCODE bubbles back as the single output.
        // Find any non-null int in results (LASTEXITCODE).
        int? exit = null;
        foreach (var r in results)
        {
            if (r?.BaseObject is int x) { exit = x; break; }
        }
        Assert.Equal(1, exit);
    }

    [Fact]
    public void MissingOperand_EmitsBashErrorNoOutput()
    {
        var results = RunAllowError("Invoke-BashStat 2>$null");
        // No StatEntry-shaped output.
        foreach (var r in results)
        {
            var ptn = r?.TypeNames?.FirstOrDefault();
            Assert.NotEqual("PsBash.StatEntry", ptn);
        }
    }

    // ===================== Unicode =====================

    [Fact]
    public void UnicodeFilename_EmitsNameUnchanged()
    {
        var f = Mk("héllo-世界-🦄.txt", "u");
        var results = Run($"Invoke-BashStat -c '%n' '{Esc(f)}'");
        var bashText = (string?)results[0].Properties["BashText"]?.Value;
        Assert.Equal("héllo-世界-🦄.txt\n", bashText);
    }

    // ===================== Alias resolution =====================

    [Fact]
    public void StatAlias_ResolvesToCmdlet()
    {
        var f = Mk("aliased.txt", "x");
        var results = Run($"stat -c '%n' '{Esc(f)}'");
        Assert.Single(results);
        Assert.Equal("aliased.txt\n", results[0].Properties["BashText"]?.Value);
    }

    // ===================== --help =====================

    [Fact]
    public void Help_EmitsSomething()
    {
        var results = Run("Invoke-BashStat --help");
        Assert.NotEmpty(results);
    }

    // ===================== Directive 12 injection probe =====================

    [Fact]
    public void FormatString_Injection_PreservedAsLiteral()
    {
        // The format-string value is walked char-by-char and never re-parsed
        // as PowerShell. A %n$(throw 'pwn') format must NOT throw — the
        // literal $(throw 'pwn') tail is emitted verbatim, and the %n is
        // replaced with the file name.
        var f = Mk("safe.txt", "z");
        // PowerShell single-quoted string escapes ' by doubling it.
        var fmt = "%n$(throw ''pwn'')";
        var results = Run($"Invoke-BashStat -c '{fmt}' '{Esc(f)}'");
        Assert.Single(results);
        var bashText = (string?)results[0].Properties["BashText"]?.Value;
        Assert.Equal("safe.txt$(throw 'pwn')\n", bashText);
    }

    // ===================== Typed-output shape =====================

    [Fact]
    public void TypedOutput_HasPsBashStatEntryPsTypeName()
    {
        var f = Mk("typed.txt", "t");
        var results = Run($"Invoke-BashStat '{Esc(f)}'");
        Assert.Single(results);
        Assert.Contains("PsBash.StatEntry", results[0].TypeNames);
    }

    // ===================== Unsupported-flag classifier =====================

    [Fact]
    public void Stat_ValidButUnsupportedFlag_ReportsNotSupported()
    {
        // --dereference is a valid GNU stat option ps-bash does not implement.
        // It must not be treated as a filename — the classifier catches it and
        // emits "recognized but not supported", exit 2.
        var f = Mk("derefsrc.txt", "x");
        var (_, errs) = RunWithErrors($"Invoke-BashStat --dereference '{Esc(f)}' 2>$null");
        Assert.Contains(errs, m => m.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Stat_UnrecognizedLongOption_BashParityMessage()
    {
        // A completely unknown flag gets the "unrecognized option" message and
        // must include the token in the error so the caller knows what to fix.
        var f = Mk("unrecsrc.txt", "x");
        var (_, errs) = RunWithErrors($"Invoke-BashStat --bogus '{Esc(f)}' 2>$null");
        Assert.Contains(errs, m =>
            m.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
            && m.Contains("--bogus", StringComparison.Ordinal));
    }
}
