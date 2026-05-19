using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 follow-on migration of
/// Invoke-BashYq from a PsBash.psm1 script function to a binary cmdlet
/// (PsBash.Cmdlets.dll / InvokeBashYqCommand.cs). The cmdlet delegates
/// YAML parse + jq filter + JSON/YAML output to the surviving psm1
/// helpers (ConvertFrom-SimpleYaml / Invoke-JqFilter / ConvertTo-JqJson
/// / ConvertTo-SimpleYaml) via parameter-bound InvokeCommand.InvokeScript,
/// so the observable shape matches the psm1 oracle byte-for-byte.
///
/// Failure-surface axes (per .claude/rules/qa-rubric.md Directive 3):
/// empty input, unicode YAML, missing file, pipeline mode, file mode.
/// Security (Directive 12): a filter containing PowerShell scriptblock
/// metacharacters must be treated as a literal jq filter, not executed
/// at the PS layer.
/// </summary>
public class InvokeBashYqCommandTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashYqCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(), "psbash-yq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_tmpDir, name);
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

    private string[] RunText(string script)
    {
        return Run(script)
            .Select(o =>
            {
                var prop = o?.Properties["BashText"];
                return prop != null
                    ? prop.Value?.ToString() ?? string.Empty
                    : o?.ToString() ?? string.Empty;
            })
            .Select(s => s.TrimEnd('\n'))
            .ToArray();
    }

    private static string Esc(string path) => path.Replace("\\", "\\\\");

    // --- Basic JSON output ---

    [Fact]
    public void Yq_FieldFilter_QuotedString_Default()
    {
        var path = WriteFile("a.yaml", "foo: bar\n");
        var lines = RunText($"Invoke-BashYq '.foo' '{Esc(path)}'");
        Assert.Single(lines);
        Assert.Equal("\"bar\"", lines[0]);
    }

    [Fact]
    public void Yq_FieldFilter_RawOutput_StripsQuotes()
    {
        var path = WriteFile("a.yaml", "foo: bar\n");
        var lines = RunText($"Invoke-BashYq -r '.foo' '{Esc(path)}'");
        Assert.Single(lines);
        Assert.Equal("bar", lines[0]);
    }

    [Fact]
    public void Yq_IdentityFilter_EmitsJsonObject()
    {
        var path = WriteFile("a.yaml", "foo: bar\n");
        var lines = RunText($"Invoke-BashYq '.' '{Esc(path)}'");
        // Pretty-printed JSON: {\n  "foo": "bar"\n}
        Assert.Single(lines);
        Assert.Contains("\"foo\"", lines[0]);
        Assert.Contains("\"bar\"", lines[0]);
    }

    [Fact]
    public void Yq_YamlOutput_RoundTripsField()
    {
        var path = WriteFile("a.yaml", "foo: hello\n");
        var lines = RunText($"Invoke-BashYq -o yaml '.foo' '{Esc(path)}'");
        Assert.Single(lines);
        Assert.Equal("hello", lines[0]);
    }

    [Fact]
    public void Yq_PipelineInput_FieldFilter()
    {
        // Single pipeline item carrying YAML text.
        var lines = RunText("'foo: piped' | Invoke-BashYq -r '.foo'");
        Assert.Single(lines);
        Assert.Equal("piped", lines[0]);
    }

    [Fact]
    public void Yq_NestedFilter_Default()
    {
        var path = WriteFile("n.yaml", "a:\n  b: c\n");
        var lines = RunText($"Invoke-BashYq -r '.a.b' '{Esc(path)}'");
        Assert.Single(lines);
        Assert.Equal("c", lines[0]);
    }

    [Fact]
    public void Yq_IntegerValue_PreservesNumericJson()
    {
        var path = WriteFile("i.yaml", "n: 42\n");
        var lines = RunText($"Invoke-BashYq '.n' '{Esc(path)}'");
        Assert.Single(lines);
        Assert.Equal("42", lines[0]);
    }

    [Fact]
    public void Yq_UnicodeValue_FileMode()
    {
        var path = WriteFile("u.yaml", "g: \"héllo 你好 🎉\"\n");
        var lines = RunText($"Invoke-BashYq -r '.g' '{Esc(path)}'");
        Assert.Single(lines);
        Assert.Equal("héllo 你好 🎉", lines[0]);
    }

    [Fact]
    public void Yq_MissingFile_EmitsErrorNoOutput()
    {
        var missing = Path.Combine(_tmpDir, "nope.yaml");
        var lines = RunText($"Invoke-BashYq '.foo' '{Esc(missing)}' 2>$null");
        Assert.Empty(lines);
    }

    [Fact]
    public void Yq_EmptyPipeline_NoOutput()
    {
        var lines = RunText("@() | Invoke-BashYq '.foo'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Yq_AliasResolution()
    {
        var path = WriteFile("a.yaml", "foo: bar\n");
        // 'yq' alias should resolve to the binary cmdlet after psm1 removal.
        var lines = RunText($"yq -r '.foo' '{Esc(path)}'");
        Assert.Single(lines);
        Assert.Equal("bar", lines[0]);
    }

    [Fact]
    public void Yq_HelpFlag_EmitsHelpDoesNotThrow()
    {
        // --help delegates to psm1 Show-BashHelp; should not throw.
        var result = Run("Invoke-BashYq --help");
        // Help may produce 0+ lines depending on Show-BashHelp output; just
        // assert it doesn't throw and the cmdlet returned without error.
        Assert.NotNull(result);
    }

    // --- Directive 12 injection probe ---

    [Fact]
    public void Yq_InjectionProbe_FilterWithDollarParenStaysLiteral()
    {
        // A jq filter containing PowerShell scriptblock metacharacters must
        // not be re-evaluated at the PS layer. The filter $(throw 'PWNED')
        // is not valid jq syntax so the cmdlet should emit a filter error
        // (no exception leaking out, no PWNED throw observable).
        var path = WriteFile("i.yaml", "foo: bar\n");
        // Use the cmdlet form so any leaking PS expansion would throw.
        var result = Run($"Invoke-BashYq \"`$(throw 'PWNED')\" '{Esc(path)}' 2>$null");
        // Either filter-error path (no output) or empty result is acceptable;
        // the critical assertion is no 'PWNED' exception propagated.
        Assert.NotNull(result);
    }
}
