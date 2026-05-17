using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// <c>Invoke-BashMktemp</c> from PsBash.psm1 to a binary cmdlet
/// (<see cref="PsBash.Cmdlets.InvokeBashMktempCommand"/>).
///
/// Oracle: the psm1 function. The cmdlet preserves the exact oracle
/// branches — default file creation, <c>-d</c> directory creation, the
/// trailing-<c>X</c> template prefix, and the
/// <c>{TempPath}/ps-bash/proc-sub/</c> destination (the oracle always
/// plants the result there regardless of the template's directory
/// portion).
///
/// Failure-surface axes covered (per Directive 3):
/// empty input (no args), unicode/template, alias resolution, <c>--help</c>,
/// quoting/injection (Directive 12). Pipeline / large-input / streaming
/// axes do not apply: mktemp produces a single name and exits.
/// </summary>
public class InvokeBashMktempCommandTests : IDisposable
{
    private readonly List<string> _createdPaths = new();

    public void Dispose()
    {
        foreach (var p in _createdPaths)
        {
            try
            {
                if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                else if (File.Exists(p)) File.Delete(p);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    private string[] RunLines(string script)
    {
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();

        var lines = result.Select(o => o?.ToString() ?? "").ToArray();
        // Track every emitted path for cleanup.
        foreach (var l in lines)
        {
            if (!string.IsNullOrEmpty(l) && (File.Exists(l) || Directory.Exists(l)))
            {
                _createdPaths.Add(l);
            }
        }
        return lines;
    }

    private static readonly string ExpectedRoot =
        Path.Combine(Path.GetTempPath(), "ps-bash", "proc-sub");

    [Fact]
    public void Mktemp_NoArgs_CreatesEmptyFileInProcSubDir()
    {
        var lines = RunLines("(Invoke-BashMktemp).Path");
        Assert.Single(lines);
        var path = lines[0];
        Assert.True(File.Exists(path), $"expected file to exist: {path}");
        Assert.Equal(0, new FileInfo(path).Length);
        // Path lives under {Temp}/ps-bash/proc-sub/...
        Assert.StartsWith(ExpectedRoot, path,
            // case-insensitive on Windows
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    [Fact]
    public void Mktemp_DashD_CreatesDirectory()
    {
        var lines = RunLines("(Invoke-BashMktemp -d).Path");
        Assert.Single(lines);
        var path = lines[0];
        Assert.True(Directory.Exists(path), $"expected directory: {path}");
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Mktemp_Template_PrefixesNameWithBasename()
    {
        // Oracle: 'myapp.XXXXXX' -> prefix 'myapp.' + random.
        var lines = RunLines("(Invoke-BashMktemp 'myapp.XXXXXX').Path");
        Assert.Single(lines);
        var path = lines[0];
        Assert.True(File.Exists(path));
        var name = Path.GetFileName(path);
        Assert.StartsWith("myapp.", name, StringComparison.Ordinal);
        // The random suffix from Path.GetRandomFileName is non-empty.
        Assert.True(name.Length > "myapp.".Length);
    }

    [Fact]
    public void Mktemp_TemplateWithDirPath_StillPlantsInProcSubDir()
    {
        // Oracle: prefix = [Path]::GetFileName($trimmed) — the directory
        // portion of a template like 'sub/x.XXXX' is discarded and the
        // result still lives under ps-bash/proc-sub/.
        var lines = RunLines("(Invoke-BashMktemp 'whatever/leaf.XXX').Path");
        Assert.Single(lines);
        var path = lines[0];
        var parent = Path.GetDirectoryName(path);
        Assert.NotNull(parent);
        Assert.Equal(ExpectedRoot.TrimEnd(Path.DirectorySeparatorChar),
            parent!.TrimEnd(Path.DirectorySeparatorChar),
            ignoreCase: OperatingSystem.IsWindows());
        Assert.StartsWith("leaf.", Path.GetFileName(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Mktemp_DashU_TreatedAsTemplate_PerOracle()
    {
        // The psm1 oracle has no -u flag; any non '-d' token becomes the
        // template (last wins). We assert oracle parity here: '-u' becomes
        // a literal prefix on the generated filename.
        var lines = RunLines("(Invoke-BashMktemp -u).Path");
        Assert.Single(lines);
        var name = Path.GetFileName(lines[0]);
        // The trailing-X regex strips nothing (no X+ run at end); basename
        // of '-u' is '-u'. The resulting prefix is '-u'.
        Assert.StartsWith("-u", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Mktemp_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashMktemp --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("mktemp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Mktemp_ViaAlias_Works()
    {
        var lines = RunLines("(mktemp).Path");
        Assert.Single(lines);
        Assert.True(File.Exists(lines[0]));
    }

    [Fact]
    public void Mktemp_EmitsTypedMktempOutput()
    {
        // The PSObject's typename must be 'PsBash.MktempOutput' so any
        // ps1xml view or downstream consumer depending on the type marker
        // keeps working. Probe via PSTypeNames.
        using var pwsh = PwshTestFixture.Create();
        var result = pwsh.AddScript(
            "(Invoke-BashMktemp).PSTypeNames -join ','").Invoke();
        Assert.Single(result);
        var types = result[0]?.ToString() ?? "";
        Assert.Contains("PsBash.MktempOutput", types);

        // Cleanup: also fetch and remove the created file.
        pwsh.Commands.Clear();
        var pathRes = pwsh.AddScript("(Invoke-BashMktemp).Path").Invoke();
        foreach (var r in pathRes)
        {
            var p = r?.ToString();
            if (p != null && File.Exists(p)) _createdPaths.Add(p);
        }
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Mktemp_TemplateWithScriptblockChars_TreatedAsLiteralPrefix()
    {
        // A template containing $(throw 'pwn') chars must not be evaluated
        // as PowerShell — the prefix is taken as a literal basename string.
        var lines = RunLines("(Invoke-BashMktemp '$(throw).XXX').Path");
        Assert.Single(lines);
        var name = Path.GetFileName(lines[0]);
        Assert.StartsWith("$(throw).", name, StringComparison.Ordinal);
        // No "pwn" execution and no error: the path exists.
        Assert.True(File.Exists(lines[0]));
    }
}
