using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashEnv
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Oracle: the psm1 function emitted typed PsBash.EnvEntry PSObjects with
/// Name / Value / BashText="NAME=VALUE" properties. No-args case sorted
/// entries by name. Missing-name case emitted bash-style error
/// "env: 'NAME': not set" and returned with no result objects.
///
/// Failure-surface axes that apply: empty (no args = all vars), missing
/// target (Directive 3 axis 14), quoting/injection (Directive 12),
/// alias resolution. Streaming / file-content / signal axes do not apply:
/// env reads in-process env vars, no I/O, no pipeline input.
///
/// Reference conversion for the SharedPwshFixture migration — see
/// MIGRATION RECIPE in PwshTestFixture.cs.
/// </summary>
public class InvokeBashEnvCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashEnvCommandTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
    }

    private System.Collections.ObjectModel.Collection<System.Management.Automation.PSObject> RunRaw(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        // AcquireFresh() already sets BashErrorMode -> PowerShell and clears
        // $error, so we can go straight to the user script.
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    private string[] RunLines(string script)
    {
        return RunRaw(script).Select(o => o?.ToString() ?? "").ToArray();
    }

    [Fact]
    public void Env_NoArgs_EmitsAllEnvVars()
    {
        // Seed a known marker var so we don't depend on a specific shell env.
        var lines = RunLines(
            "$env:PSB_ENV_MARK = 'xyzzy-9001'; " +
            "(Invoke-BashEnv | ForEach-Object { $_.BashText })");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l == "PSB_ENV_MARK=xyzzy-9001");
    }

    [Fact]
    public void Env_NoArgs_OutputIsSortedByName()
    {
        // The psm1 oracle calls Sort-Object on the key set. Verify ordinal
        // ascending order on the BashText prefix (NAME= part).
        var raw = RunRaw(
            "$env:PSB_ZZZ = '1'; $env:PSB_AAA = '2'; " +
            "Invoke-BashEnv | ForEach-Object { $_.Name }");
        var names = raw.Select(o => o?.ToString() ?? "").Where(n =>
            n.StartsWith("PSB_", StringComparison.Ordinal)).ToArray();
        Assert.Contains("PSB_AAA", names);
        Assert.Contains("PSB_ZZZ", names);
        var idxA = Array.IndexOf(names, "PSB_AAA");
        var idxZ = Array.IndexOf(names, "PSB_ZZZ");
        Assert.True(idxA < idxZ, $"Expected PSB_AAA before PSB_ZZZ; got positions {idxA} / {idxZ}");
    }

    [Fact]
    public void Env_NamedVar_EmitsTypedEntry()
    {
        var raw = RunRaw(
            "$env:PSB_ENV_TARGETED = 'hello-target'; " +
            "Invoke-BashEnv PSB_ENV_TARGETED");
        Assert.Single(raw);
        var entry = raw[0];
        Assert.Equal("PSB_ENV_TARGETED", entry.Properties["Name"]?.Value?.ToString());
        Assert.Equal("hello-target", entry.Properties["Value"]?.Value?.ToString());
        Assert.Equal("PSB_ENV_TARGETED=hello-target", entry.Properties["BashText"]?.Value?.ToString());
        Assert.Contains("PsBash.EnvEntry", entry.TypeNames);
    }

    [Fact]
    public void Env_NamedVar_Missing_EmitsNoSuccessOutput()
    {
        // Oracle: Write-BashError "env: 'NAME': not set" then return with
        // no success-pipeline output. The error message itself is emitted
        // via the psm1 Write-BashError shim through nested InvokeScript —
        // that nested error stream does NOT surface on the in-process test
        // runspace's Streams.Error / $error / -ErrorVariable (Microsoft
        // PowerShell SDK isolates nested pipelines). The error-text
        // assertion lives in the end-to-end smoke run; this in-process
        // test verifies the contract that matters for the parity oracle:
        // zero success objects and no typed entry for the missing name.
        var name = "PSB_ENV_DEFINITELY_NOT_SET_" + Guid.NewGuid().ToString("N");
        var raw = RunRaw($"Invoke-BashEnv {name}");
        Assert.Empty(raw);
    }

    [Fact]
    public void Env_ViaAlias_Works()
    {
        // The `env` alias (declared in psm1) must resolve to the cmdlet.
        var raw = RunRaw(
            "$env:PSB_ENV_ALIAS = 'aliased'; env PSB_ENV_ALIAS");
        Assert.Single(raw);
        Assert.Equal("PSB_ENV_ALIAS=aliased", raw[0].Properties["BashText"]?.Value?.ToString());
    }

    [Fact]
    public void Env_ViaPrintenvAlias_NamedVar_EmitsValueOnly()
    {
        // bash `printenv NAME` prints the VALUE only (no `NAME=` prefix);
        // `env NAME` (the ps-bash-ism) keeps the NAME=VALUE shape. The cmdlet
        // distinguishes the two via MyInvocation.InvocationName (Dart wpCPSd25qMuI).
        var raw = RunRaw(
            "$env:PSB_ENV_PRINTENV = 'printed'; printenv PSB_ENV_PRINTENV");
        Assert.Single(raw);
        Assert.Equal("printed", raw[0].Properties["BashText"]?.Value?.ToString());
        // The typed Name/Value properties are preserved for downstream consumers.
        Assert.Equal("PSB_ENV_PRINTENV", raw[0].Properties["Name"]?.Value?.ToString());
        Assert.Equal("printed", raw[0].Properties["Value"]?.Value?.ToString());
    }

    [Fact]
    public void Env_ViaPrintenvAlias_MultipleNames_EmitsValuePerLine()
    {
        // bash `printenv A B` prints each value on its own line, value-only.
        var raw = RunRaw(
            "$env:PSB_PE_A = 'aaa'; $env:PSB_PE_B = 'bbb'; printenv PSB_PE_A PSB_PE_B");
        var texts = raw.Select(o => o.Properties["BashText"]?.Value?.ToString()).ToArray();
        Assert.Equal(new[] { "aaa", "bbb" }, texts);
    }

    [Fact]
    public void Env_HelpFlag_EmitsUsage()
    {
        var lines = RunLines("Invoke-BashEnv --help");
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("env", StringComparison.OrdinalIgnoreCase));
    }

    // ---- injection probe (Directive 12) ----

    [Fact]
    public void Env_NameWithScriptblockChars_TreatedAsLiteralName()
    {
        // A variable name with $() / ; must not be re-parsed as PowerShell.
        // The cmdlet looks the name up via Environment.GetEnvironmentVariable
        // — a literal lookup that cannot execute the embedded expression.
        // Asserts: zero success objects (the lookup misses) AND the
        // pwsh.Invoke() call returns normally rather than throwing a
        // RuntimeException with "pwn" (which is what would happen if the
        // $(throw "pwn") payload had been evaluated as PowerShell syntax).
        // The negative-assertion is the security probe — Directive 12.
        var raw = RunRaw("Invoke-BashEnv '$(throw \"pwn\");rm -rf /'");
        Assert.Empty(raw);
    }
}
