using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 Phase F6 migration of
/// Invoke-BashJq from a PsBash.psm1 script function (plus its Invoke-JqFilter,
/// ConvertTo-JqJson, and *-Jq* helper web) to a binary cmdlet
/// (PsBash.Cmdlets.dll / InvokeBashJqCommand.cs + JqEngine.cs).
///
/// Oracle: the original psm1 jq interpreter. The filter engine is
/// reimplemented in JqEngine; the cmdlet drives flag parsing, file / pipeline
/// collection, JSON parsing, slurp, and output emission. The surface under
/// test here is M5 (in-process cmdlet) — M1/M2/M3 bash-oracle parity lives in
/// PsBash.Differential.Tests / the canary suite.
///
/// Failure-surface axes (per .claude/rules/qa-rubric.md Directive 3):
/// empty input, unicode / emoji JSON, large-ish input, missing target,
/// malformed JSON, bad filter. Security (Directive 12): a filter / value
/// containing PowerShell scriptblock chars must be treated as literal jq
/// syntax, not executed at the PS layer.
///
/// The PwshTestFixture loads psm1 (which no longer defines Invoke-BashJq)
/// then imports PsBash.Cmdlets.dll — so these tests also prove the function
/// removal worked and the psm1 Set-Alias 'jq' line resolves to the cmdlet.
/// </summary>
public class InvokeBashJqCommandTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmpDir;
    private readonly SharedPwshFixture _fixture;

    public InvokeBashJqCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(
            Path.GetTempPath(), "psbash-jq-" + Guid.NewGuid().ToString("N"));
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

        var err = pwsh.AddScript("$error | Select-Object -First 1").Invoke();
        pwsh.Commands.Clear();
        Assert.True(err.Count == 0 || err[0] == null,
            $"Unexpected error running [{script}]: " +
            $"{(err.Count > 0 ? err[0]?.ToString() : "none")}");
        return result;
    }

    private System.Collections.ObjectModel.Collection<PSObject> RunAllowError(string script)
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

    // ===================== Identity + flags =====================

    [Fact]
    public void Jq_Identity_RoundTripsSimpleObject_Compact()
    {
        var lines = RunText("'{\"a\":1}' | Invoke-BashJq -c '.'");
        Assert.Single(lines);
        Assert.Equal("{\"a\":1}", lines[0]);
    }

    [Fact]
    public void Jq_FieldAccess_ReturnsScalar_RawOutput()
    {
        var lines = RunText("'{\"name\":\"hello\"}' | Invoke-BashJq -r '.name'");
        Assert.Single(lines);
        Assert.Equal("hello", lines[0]);
    }

    [Fact]
    public void Jq_FieldAccess_ReturnsScalar_JsonQuoted_NoRaw()
    {
        var lines = RunText("'{\"name\":\"hello\"}' | Invoke-BashJq -c '.name'");
        Assert.Single(lines);
        Assert.Equal("\"hello\"", lines[0]);
    }

    [Fact]
    public void Jq_SortKeys_OrdersKeysAlphabetically()
    {
        var lines = RunText("'{\"b\":2,\"a\":1}' | Invoke-BashJq -c -S '.'");
        Assert.Single(lines);
        Assert.Equal("{\"a\":1,\"b\":2}", lines[0]);
    }

    [Fact]
    public void Jq_NoSortKeys_PreservesInsertionOrder()
    {
        var lines = RunText("'{\"b\":2,\"a\":1}' | Invoke-BashJq -c '.'");
        Assert.Single(lines);
        Assert.Equal("{\"b\":2,\"a\":1}", lines[0]);
    }

    // ===================== Pipe / comma / // =====================

    [Fact]
    public void Jq_Pipe_ChainsFilters()
    {
        var lines = RunText(
            "'{\"a\":{\"b\":42}}' | Invoke-BashJq -c '.a | .b'");
        Assert.Single(lines);
        Assert.Equal("42", lines[0]);
    }

    [Fact]
    public void Jq_Comma_EmitsMultipleResults()
    {
        var lines = RunText(
            "'{\"a\":1,\"b\":2}' | Invoke-BashJq -c '.a, .b'");
        Assert.Equal(2, lines.Length);
        Assert.Equal("1", lines[0]);
        Assert.Equal("2", lines[1]);
    }

    [Fact]
    public void Jq_Alternative_UsesFallbackWhenLeftIsNull()
    {
        var lines = RunText(
            "'{}' | Invoke-BashJq -r '.missing // \"default\"'");
        Assert.Single(lines);
        Assert.Equal("default", lines[0]);
    }

    [Fact]
    public void Jq_Alternative_UsesLeftWhenPresent()
    {
        var lines = RunText(
            "'{\"a\":\"hit\"}' | Invoke-BashJq -r '.a // \"miss\"'");
        Assert.Single(lines);
        Assert.Equal("hit", lines[0]);
    }

    // ===================== Array iterate / index =====================

    [Fact]
    public void Jq_ArrayIterate_EmitsEach()
    {
        var lines = RunText("'[1,2,3]' | Invoke-BashJq -c '.[]'");
        Assert.Equal(new[] { "1", "2", "3" }, lines);
    }

    [Fact]
    public void Jq_IndexNumeric_ReturnsElement()
    {
        var lines = RunText("'[10,20,30]' | Invoke-BashJq -c '.[1]'");
        Assert.Single(lines);
        Assert.Equal("20", lines[0]);
    }

    [Fact]
    public void Jq_IndexNegative_CountsFromEnd()
    {
        var lines = RunText("'[10,20,30]' | Invoke-BashJq -c '.[-1]'");
        Assert.Single(lines);
        Assert.Equal("30", lines[0]);
    }

    [Fact]
    public void Jq_IndexOutOfRange_ReturnsNull()
    {
        var lines = RunText("'[1,2]' | Invoke-BashJq -c '.[10]'");
        Assert.Single(lines);
        Assert.Equal("null", lines[0]);
    }

    // ===================== map / select / length / type =====================

    [Fact]
    public void Jq_Map_IdentityCopiesArray()
    {
        // The oracle's jq surface does not implement arithmetic, so map's
        // useful test is identity-copy + map-of-field-access.
        var lines = RunText("'[1,2,3]' | Invoke-BashJq -c 'map(.)'");
        Assert.Single(lines);
        Assert.Equal("[1,2,3]", lines[0]);
    }

    [Fact]
    public void Jq_Map_FieldAccess_ExtractsValues()
    {
        var lines = RunText(
            "'[{\"k\":1},{\"k\":2}]' | Invoke-BashJq -c 'map(.k)'");
        Assert.Single(lines);
        Assert.Equal("[1,2]", lines[0]);
    }

    [Fact]
    public void Jq_Select_FiltersByEquality()
    {
        var lines = RunText(
            "'[{\"k\":1},{\"k\":2},{\"k\":1}]' | Invoke-BashJq -c '.[] | select(.k == 1)'");
        Assert.Equal(2, lines.Length);
        Assert.Equal("{\"k\":1}", lines[0]);
        Assert.Equal("{\"k\":1}", lines[1]);
    }

    [Fact]
    public void Jq_Length_OfArray()
    {
        var lines = RunText("'[1,2,3,4]' | Invoke-BashJq -c 'length'");
        Assert.Single(lines);
        Assert.Equal("4", lines[0]);
    }

    [Fact]
    public void Jq_Type_OfString()
    {
        var lines = RunText("'\"hi\"' | Invoke-BashJq -r 'type'");
        Assert.Single(lines);
        Assert.Equal("string", lines[0]);
    }

    [Fact]
    public void Jq_Keys_AreSorted()
    {
        var lines = RunText(
            "'{\"b\":1,\"a\":2}' | Invoke-BashJq -c 'keys'");
        Assert.Single(lines);
        Assert.Equal("[\"a\",\"b\"]", lines[0]);
    }

    // ===================== if / not =====================

    [Fact]
    public void Jq_If_ChoosesThenBranchOnTruthy()
    {
        // The psm1 oracle's Invoke-JqIf evaluates its condition through
        // Invoke-JqFilter, which has no top-level comparison operator handler
        // (== / != live only inside select()). The condition is therefore a
        // truthy check: a non-null, non-false value picks the 'then' branch.
        var lines = RunText(
            "'true' | Invoke-BashJq -r 'if . then \"yes\" else \"no\" end'");
        Assert.Single(lines);
        Assert.Equal("yes", lines[0]);
    }

    [Fact]
    public void Jq_If_ChoosesElseBranchOnFalsy()
    {
        var lines = RunText(
            "'false' | Invoke-BashJq -r 'if . then \"yes\" else \"no\" end'");
        Assert.Single(lines);
        Assert.Equal("no", lines[0]);
    }

    [Fact]
    public void Jq_Not_NegatesTruthy()
    {
        var lines = RunText("'true' | Invoke-BashJq -c 'not'");
        Assert.Single(lines);
        Assert.Equal("false", lines[0]);
    }

    // ===================== Slurp =====================

    [Fact]
    public void Jq_Slurp_WrapsSingleObjectInArray()
    {
        var lines = RunText("'{\"a\":1}' | Invoke-BashJq -c -s '.'");
        Assert.Single(lines);
        Assert.Equal("[{\"a\":1}]", lines[0]);
    }

    // ===================== Object construction =====================

    [Fact]
    public void Jq_ObjectConstruction_BuildsFromFilters()
    {
        var lines = RunText(
            "'{\"x\":1,\"y\":2}' | Invoke-BashJq -c '{a: .x, b: .y}'");
        Assert.Single(lines);
        Assert.Equal("{\"a\":1,\"b\":2}", lines[0]);
    }

    // ===================== File input =====================

    [Fact]
    public void Jq_FileInput_ParsesAndFilters()
    {
        var path = WriteFile("data.json", "{\"name\":\"alpha\"}");
        var lines = RunText($"Invoke-BashJq -r '.name' '{Esc(path)}'");
        Assert.Single(lines);
        Assert.Equal("alpha", lines[0]);
    }

    // ===================== Failure-surface axes =====================

    [Fact]
    public void Jq_EmptyInput_ProducesNoOutput()
    {
        var lines = RunText("'' | Invoke-BashJq -r '.'");
        Assert.Empty(lines);
    }

    [Fact]
    public void Jq_UnicodeEmoji_RoundTripsRaw()
    {
        // Emoji should pass through raw output unchanged.
        var lines = RunText("'{\"s\":\"hi 🎉\"}' | Invoke-BashJq -r '.s'");
        Assert.Single(lines);
        Assert.Equal("hi 🎉", lines[0]);
    }

    [Fact]
    public void Jq_LargeArray_FiltersWithoutCorruption()
    {
        // Build a 1000-element array, take .[999].
        string arr = "[" + string.Join(",", Enumerable.Range(0, 1000)) + "]";
        var lines = RunText($"'{arr}' | Invoke-BashJq -c '.[999]'");
        Assert.Single(lines);
        Assert.Equal("999", lines[0]);
    }

    // ===================== Negative cases =====================

    [Fact]
    public void Jq_MissingFile_EmitsError_AndNoOutput()
    {
        var result = RunAllowError(
            "Invoke-BashJq -r '.' 'nonexistent-file-xyz.json'");
        // No payload objects from the cmdlet; errors went through Write-BashError
        // which routes via Write-Host / Write-Error in the oracle. Just assert
        // no payload was emitted.
        var payload = result.Where(o =>
            o?.Properties["BashText"] != null
            || (o?.BaseObject is string s && s.Length > 0 && !s.StartsWith("jq:"))).ToList();
        Assert.Empty(payload);
    }

    [Fact]
    public void Jq_MalformedJson_EmitsErrorNoCrash()
    {
        var result = RunAllowError("'{not json' | Invoke-BashJq -r '.'");
        var payload = result.Where(o =>
            o?.Properties["BashText"] != null
            || (o?.BaseObject is string s && s.Length > 0 && !s.StartsWith("jq:"))).ToList();
        Assert.Empty(payload);
    }

    [Fact]
    public void Jq_UnknownFilter_EmitsErrorNoCrash()
    {
        var result = RunAllowError("'{}' | Invoke-BashJq -r 'totallyMadeUpFunction'");
        var payload = result.Where(o =>
            o?.Properties["BashText"] != null
            || (o?.BaseObject is string s && s.Length > 0 && !s.StartsWith("jq:"))).ToList();
        Assert.Empty(payload);
    }

    // ===================== Security probe =====================

    [Fact]
    public void Jq_FilterAndValueWithInjectionChars_AreLiteral()
    {
        // The value contains $(whoami) ; rm -rf / -style injection bait. It must
        // be treated as a literal string, not executed by the PS layer.
        var lines = RunText(
            "'{\"x\":\"$(whoami); rm -rf /\"}' | Invoke-BashJq -r '.x'");
        Assert.Single(lines);
        Assert.Equal("$(whoami); rm -rf /", lines[0]);
    }

    [Fact]
    public void Jq_ScriptBlockCharsInValue_AreLiteral()
    {
        // PowerShell scriptblock delimiters in the JSON value must not be
        // executed — they should appear verbatim in raw output.
        var lines = RunText(
            "'{\"x\":\"{ Write-Host PWNED }\"}' | Invoke-BashJq -r '.x'");
        Assert.Single(lines);
        Assert.Equal("{ Write-Host PWNED }", lines[0]);
    }

    // ===================== // alternative over a stream =====================

    [Fact]
    public void Jq_AlternativeOverStream_KeepsAllTruthyValues()
    {
        // `.[] // "x"` must yield every truthy element, not collapse to the
        // first. Oracle: echo '[1,2,3]' | jq -c '.[] // "x"' -> 1 2 3
        var lines = RunText("'[1,2,3]' | Invoke-BashJq -c '.[] // \"x\"'");
        Assert.Equal(new[] { "1", "2", "3" }, lines);
    }

    [Fact]
    public void Jq_AlternativeOverStream_DropsFalsyButKeepsRest()
    {
        // null/false are falsy and dropped; the surviving truthy values remain.
        // Oracle: echo '[1,null,false,2]' | jq -c '.[] // "x"' -> 1 2
        var lines = RunText("'[1,null,false,2]' | Invoke-BashJq -c '.[] // \"x\"'");
        Assert.Equal(new[] { "1", "2" }, lines);
    }

    [Fact]
    public void Jq_AlternativeOverStream_FallsBackWhenAllFalsy()
    {
        // When the left side yields no truthy value, fall back to the right.
        // Oracle: echo '[null,false]' | jq -c '.[] // "x"' -> "x"
        var lines = RunText("'[null,false]' | Invoke-BashJq -c '.[] // \"x\"'");
        Assert.Equal(new[] { "\"x\"" }, lines);
    }

    // ===================== object construction =====================

    [Fact]
    public void Jq_FromEntries_PreservesEntryOrder()
    {
        // from_entries must keep insertion order (b before a), not hash order.
        // Oracle: ... | jq -c from_entries -> {"b":1,"a":2}
        var lines = RunText(
            "'[{\"key\":\"b\",\"value\":1},{\"key\":\"a\",\"value\":2}]' | Invoke-BashJq -c from_entries");
        Assert.Equal(new[] { "{\"b\":1,\"a\":2}" }, lines);
    }

    [Fact]
    public void Jq_ComputedKey_StringConcat()
    {
        // {(expr): v} evaluates the key expression. Uses the spaced concat form
        // ("a" + "b") — the no-space form ("a"+"b") hits a separate pre-existing
        // arith-operator-needs-spaces gap unrelated to computed keys.
        // Oracle: echo '{}' | jq -c '{("a" + "b"): 1}' -> {"ab":1}
        var lines = RunText("'{}' | Invoke-BashJq -c '{(\"a\" + \"b\"): 1}'");
        Assert.Equal(new[] { "{\"ab\":1}" }, lines);
    }

    [Fact]
    public void Jq_ComputedKey_FromField()
    {
        // {(.k): .v} uses the value of .k as the key.
        // Oracle: echo '{"k":"name","v":5}' | jq -c '{(.k): .v}' -> {"name":5}
        var lines = RunText("'{\"k\":\"name\",\"v\":5}' | Invoke-BashJq -c '{(.k): .v}'");
        Assert.Equal(new[] { "{\"name\":5}" }, lines);
    }

    // ===================== Alias resolves to cmdlet =====================

    [Fact]
    public void Jq_AliasResolvesToCmdlet_AfterPsm1Removal()
    {
        // The psm1 no longer defines Invoke-BashJq; Set-Alias 'jq' must still
        // resolve to the binary cmdlet via the load order in PwshTestFixture.
        var result = Run("Get-Alias jq | Select-Object -ExpandProperty Definition");
        Assert.Single(result);
        Assert.Equal("Invoke-BashJq", result[0]?.BaseObject?.ToString());
    }
}
