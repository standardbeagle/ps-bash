using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 Phase 1d migration of
/// Invoke-BashLs from a PsBash.psm1 script function to a binary cmdlet
/// (PsBash.Cmdlets.dll / InvokeBashLsCommand.cs) — the final leaf of
/// REFACTOR-2 Phase 1.
///
/// Oracle: the original psm1 Invoke-BashLs and its pure helper web
/// (Get-LsEntryFromFsi, ConvertTo-PermissionString, Format-BashSize,
/// Format-BashDate, Format-LsLine, Test-IsExecutable), modeled on the bash
/// ls builtin. The helpers are reimplemented in C# inside the cmdlet; the
/// Tier 1 (custom $script:BashLsProviders) and Tier 3 (PS-provider fallback)
/// paths stay in psm1 behind the Get-BashLsProviderEntries shim.
///
/// ls has a directory + file surface (no pipeline input), so the applicable
/// failure-surface axes (per .claude/rules/qa-rubric.md Directive 3) are:
/// empty directory, unicode names, missing target, and large-ish directory.
/// Negative cases (Directive 7): missing target, empty directory. Mode
/// coverage: M5 (in-process cmdlet) is the surface under test here; the
/// M1/M2/M3 bash-oracle parity lives in PsBash.Differential.Tests.
///
/// The PwshTestFixture loads psm1 (which no longer defines Invoke-BashLs)
/// then imports PsBash.Cmdlets.dll, mirroring the host load order — so these
/// tests also prove the function-shadowing removal worked and the psm1
/// Set-Alias 'ls' line still resolves to the cmdlet.
/// </summary>
public class InvokeBashLsCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmpDir;
    private readonly SharedPwshFixture _fixture;

    public InvokeBashLsCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(), "psbash-1d-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_tmpDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string MakeDir(string name)
    {
        var path = Path.Combine(_tmpDir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Quotes a path for safe embedding in a single-quoted PS string.</summary>
    private static string Q(string path) => path.Replace("'", "''");

    /// <summary>Runs a script, asserts no error record was generated.</summary>
    private System.Collections.ObjectModel.Collection<PSObject> Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var err = pwsh.AddScript("$error | Select-Object -First 1").Invoke();
        pwsh.Commands.Clear();
        Assert.True(err.Count == 0 || err[0] == null,
            $"Unexpected error running [{script}]: " +
            $"{(err.Count > 0 ? err[0]?.ToString() : "none")}");

        return result;
    }

    /// <summary>Extracts the BashText payload of each emitted object.</summary>
    private string[] RunBashText(string script)
    {
        return Run(script)
            .Select(o =>
            {
                var prop = o?.Properties["BashText"];
                return prop != null ? prop.Value?.ToString() ?? "" : o?.ToString() ?? "";
            })
            .ToArray();
    }

    // --- Resolution: the cmdlet, not a psm1 function, backs `ls` ---

    [Fact]
    public void Ls_ResolvesToBinaryCmdlet_NotScriptFunction()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh
            .AddScript("(Get-Command Invoke-BashLs).CommandType.ToString()")
            .Invoke();
        pwsh.Commands.Clear();

        Assert.Single(result);
        Assert.Equal("Cmdlet", result[0]?.BaseObject?.ToString());
    }

    [Fact]
    public void LsAlias_StillResolvesToInvokeBashLs()
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh
            .AddScript("(Get-Command ls -CommandType Alias).Definition")
            .Invoke();
        pwsh.Commands.Clear();

        Assert.Single(result);
        Assert.Equal("Invoke-BashLs", result[0]?.BaseObject?.ToString());
    }

    // --- Basic listing ---

    [Fact]
    public void Ls_ListsFilesInDirectory_SortedCaseInsensitive()
    {
        WriteFile("banana.txt", "b");
        WriteFile("Apple.txt", "a");
        WriteFile("cherry.txt", "c");

        var names = RunBashText($"Invoke-BashLs '{Q(_tmpDir)}'");

        // Case-insensitive alphabetical: Apple, banana, cherry.
        Assert.Equal(
            new[] { "Apple.txt", "banana.txt", "cherry.txt" },
            names);
    }

    [Fact]
    public void Ls_EmptyDirectory_EmitsNothing()
    {
        var empty = MakeDir("empty");
        var result = Run($"Invoke-BashLs '{Q(empty)}'");
        Assert.Empty(result);
    }

    [Fact]
    public void Ls_DefaultTarget_IsCurrentDirectory()
    {
        WriteFile("only.txt", "x");
        var names = RunBashText(
            $"Set-Location '{Q(_tmpDir)}'; Invoke-BashLs");
        Assert.Equal(new[] { "only.txt" }, names);
    }

    // --- Hidden files: -a / -A ---

    [Fact]
    public void Ls_HidesDotfilesByDefault()
    {
        WriteFile(".hidden", "h");
        WriteFile("visible.txt", "v");

        var names = RunBashText($"Invoke-BashLs '{Q(_tmpDir)}'");
        Assert.Equal(new[] { "visible.txt" }, names);
    }

    [Fact]
    public void Ls_DashA_ShowsDotfiles()
    {
        WriteFile(".hidden", "h");
        WriteFile("visible.txt", "v");

        var names = RunBashText($"Invoke-BashLs -a '{Q(_tmpDir)}'");
        Assert.Equal(new[] { ".hidden", "visible.txt" }, names);
    }

    [Fact]
    public void Ls_DashA_ImpliesShowHidden()
    {
        WriteFile(".dotfile", "d");
        WriteFile("plain", "p");

        // -A is "almost all" — like -a for our temp dir (no . / .. entries
        // are enumerated by System.IO anyway).
        var names = RunBashText($"Invoke-BashLs -A '{Q(_tmpDir)}'");
        Assert.Equal(new[] { ".dotfile", "plain" }, names);
    }

    // --- Reverse / size / time sorting ---

    [Fact]
    public void Ls_DashR_ReversesSortOrder()
    {
        WriteFile("a.txt", "a");
        WriteFile("b.txt", "b");
        WriteFile("c.txt", "c");

        var names = RunBashText($"Invoke-BashLs -r '{Q(_tmpDir)}'");
        Assert.Equal(new[] { "c.txt", "b.txt", "a.txt" }, names);
    }

    [Fact]
    public void Ls_DashS_SortsBySizeDescending()
    {
        WriteFile("small.txt", "x");
        WriteFile("big.txt", new string('y', 5000));
        WriteFile("medium.txt", new string('z', 500));

        var names = RunBashText($"Invoke-BashLs -S '{Q(_tmpDir)}'");
        Assert.Equal(new[] { "big.txt", "medium.txt", "small.txt" }, names);
    }

    [Fact]
    public void Ls_DashSr_SortsBySizeAscending()
    {
        WriteFile("small.txt", "x");
        WriteFile("big.txt", new string('y', 5000));

        var names = RunBashText($"Invoke-BashLs -S -r '{Q(_tmpDir)}'");
        Assert.Equal(new[] { "small.txt", "big.txt" }, names);
    }

    // --- Single file target ---

    [Fact]
    public void Ls_SingleFileTarget_EmitsThatFile()
    {
        var f = WriteFile("target.txt", "data");
        var names = RunBashText($"Invoke-BashLs '{Q(f)}'");
        Assert.Equal(new[] { "target.txt" }, names);
    }

    // --- Long format: -l ---

    [Fact]
    public void Ls_DashL_EmitsLongFormatLine()
    {
        WriteFile("file.txt", "hello");

        var lines = RunBashText($"Invoke-BashLs -l '{Q(_tmpDir)}'");
        Assert.Single(lines);
        // Long line: permissions linkcount owner group size date name.
        // Permissions begin with the type char ('-' for a regular file) then
        // 9 rwx chars; the line ends with the file name.
        var line = lines[0];
        Assert.StartsWith("-", line);
        Assert.EndsWith("file.txt", line);
        // Size column carries the 5-byte content somewhere in the line.
        Assert.Contains("5", line);
    }

    [Fact]
    public void Ls_DashL_DirectoryPermissionsStartWithD()
    {
        MakeDir("subdir");

        var lines = RunBashText($"Invoke-BashLs -l '{Q(_tmpDir)}'");
        Assert.Single(lines);
        Assert.StartsWith("d", lines[0]);
        Assert.EndsWith("subdir", lines[0]);
    }

    // --- Classify: -p / -F ---

    [Fact]
    public void Ls_DashP_AppendsSlashToDirectories()
    {
        MakeDir("adir");
        WriteFile("afile.txt", "f");

        var names = RunBashText($"Invoke-BashLs -p '{Q(_tmpDir)}'");
        // Sorted: adir, afile.txt — directory gets a trailing slash.
        Assert.Equal(new[] { "adir/", "afile.txt" }, names);
    }

    [Fact]
    public void Ls_DashF_AppendsSlashToDirectories()
    {
        MakeDir("adir");
        WriteFile("afile.txt", "f");

        var names = RunBashText($"Invoke-BashLs -F '{Q(_tmpDir)}'");
        Assert.Equal(new[] { "adir/", "afile.txt" }, names);
    }

    // --- Dir-only: -d ---

    [Fact]
    public void Ls_DashD_ListsDirectoryItselfNotContents()
    {
        MakeDir("box");
        WriteFile(Path.Combine("box", "inner.txt"), "i");
        var box = Path.Combine(_tmpDir, "box");

        var names = RunBashText($"Invoke-BashLs -d '{Q(box)}'");
        Assert.Equal(new[] { "box" }, names);
    }

    // --- Recursive: -R ---

    [Fact]
    public void Ls_DashR_RecursesIntoSubdirectories()
    {
        MakeDir("nested");
        WriteFile("top.txt", "t");
        WriteFile(Path.Combine("nested", "deep.txt"), "d");

        var names = RunBashText($"Invoke-BashLs -R '{Q(_tmpDir)}'");
        // -R enumerates AllDirectories: the subdir entry, its file, and the
        // top-level file all appear.
        Assert.Contains("nested", names);
        Assert.Contains("deep.txt", names);
        Assert.Contains("top.txt", names);
    }

    // --- Negative: missing target (Directive 7) ---

    [Fact]
    public void Ls_MissingTarget_SetsExitCode2AndEmitsError()
    {
        var missing = Path.Combine(_tmpDir, "does-not-exist-xyz");
        var pwsh = _fixture.AcquireFresh();

        pwsh.AddScript(
            $"$global:LASTEXITCODE = 0; " +
            $"Invoke-BashLs '{Q(missing)}' 2>$null; " +
            "$global:LASTEXITCODE");
        var result = pwsh.Invoke();
        pwsh.Commands.Clear();

        // Last emitted value is the exit code — the oracle sets 2 for a
        // not-found target.
        Assert.NotEmpty(result);
        var exit = result[result.Count - 1]?.BaseObject;
        Assert.Equal(2, Convert.ToInt32(exit));
    }

    // --- Unicode names (Directive 3) ---

    [Fact]
    public void Ls_UnicodeFileNames_ListedCorrectly()
    {
        WriteFile("café.txt", "c");
        WriteFile("naïve.txt", "n");
        WriteFile("emoji-😀.txt", "e");

        var names = RunBashText($"Invoke-BashLs '{Q(_tmpDir)}'");
        Assert.Equal(3, names.Length);
        Assert.Contains("café.txt", names);
        Assert.Contains("naïve.txt", names);
        Assert.Contains("emoji-😀.txt", names);
    }

    // --- Larger directory (Directive 3: large-ish input) ---

    [Fact]
    public void Ls_ManyFiles_AllListedAndSorted()
    {
        for (int i = 0; i < 250; i++)
        {
            WriteFile($"f{i:D4}.txt", "x");
        }

        var names = RunBashText($"Invoke-BashLs '{Q(_tmpDir)}'");
        Assert.Equal(250, names.Length);
        // Case-insensitive ordinal sort keeps the zero-padded names ordered.
        Assert.Equal("f0000.txt", names[0]);
        Assert.Equal("f0249.txt", names[^1]);
    }

    // --- Typed object surface: LsEntry properties survive ---

    [Fact]
    public void Ls_EmitsTypedLsEntry_WithExpectedProperties()
    {
        WriteFile("typed.txt", "abc");

        var result = Run($"Invoke-BashLs '{Q(_tmpDir)}'");
        Assert.Single(result);
        var entry = result[0];

        Assert.Contains("PsBash.LsEntry", entry.TypeNames);
        Assert.Equal("typed.txt", entry.Properties["Name"]?.Value?.ToString());
        Assert.Equal(false, entry.Properties["IsDirectory"]?.Value);
        Assert.Equal(3L, Convert.ToInt64(entry.Properties["SizeBytes"]?.Value));
        Assert.NotNull(entry.Properties["Permissions"]?.Value);
        Assert.NotNull(entry.Properties["LastModified"]?.Value);
    }

    // --- Glob operand ---

    [Fact]
    public void Ls_GlobPattern_ExpandsToMatchingFiles()
    {
        WriteFile("keep1.log", "1");
        WriteFile("keep2.log", "2");
        WriteFile("skip.txt", "s");

        var names = RunBashText(
            $"Set-Location '{Q(_tmpDir)}'; Invoke-BashLs '*.log'");
        Assert.Equal(new[] { "keep1.log", "keep2.log" }, names);
    }
}
