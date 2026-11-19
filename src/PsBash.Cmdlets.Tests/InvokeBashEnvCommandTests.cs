using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Behavioral-parity tests for the REFACTOR-2 migration of Invoke-BashEnv
/// from PsBash.psm1 to a binary cmdlet (PsBash.Cmdlets.dll).
///
/// Typed PsBash.EnvEntry PSObjects with Name / Value / BashText="NAME=VALUE" properties, sorted by name. GNU
/// semantics for options, statuses (125 usage, 126 cannot run, 127 not found) and `env NAME` (runs NAME); the
/// wording is the oracle's (RuntimeCommandParityDifferentialTests.Env_*).
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
    public void Env_NameValueCommand_RunsCommandWithVarSet()
    {
        // env's PRIMARY form: run a command with a modified environment. REGRESSION: printenv was Invoke-BashEnv told
        // apart by InvocationName, which is `&` here, so this printed `PSB_ENVRUN=zzz9` (env's shape).
        var lines = RunLines(
            "env PSB_ENVRUN=zzz9 printenv PSB_ENVRUN | ForEach-Object { $_.BashText }");
        Assert.Equal(new[] { "zzz9" }, lines);
    }
    [Fact]
    public void Env_NameValueCommand_RestoresEnvironmentAfter()
    {
        // The temporary assignment must not leak past the command.
        var lines = RunLines(
            "env PSB_RUNX=hi printenv PSB_RUNX | Out-Null; " +
            "\"after=$([string]::IsNullOrEmpty($env:PSB_RUNX))\"");
        Assert.Contains("after=True", lines);
    }

    [Fact]
    public void Env_AssignmentNoCommand_PrintsEnvIncludingAssignment()
    {
        var lines = RunLines("env PSB_PRINTV=qqq | ForEach-Object { $_.BashText }");
        Assert.Contains(lines, l => l == "PSB_PRINTV=qqq");
    }

    [Fact]
    public void Env_IgnoreEnvironment_PrintsOnlyAssignments()
    {
        // `env -i NAME=VALUE` starts from an empty environment.
        var lines = RunLines("env -i PSB_ONLY=1 | ForEach-Object { $_.BashText }");
        Assert.Equal(new[] { "PSB_ONLY=1" }, lines);
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
    public void Printenv_NamedVar_EmitsTypedEntry()
    {
        var raw = RunRaw(
            "$env:PSB_ENV_TARGETED = 'hello-target'; " +
            "Invoke-BashPrintenv PSB_ENV_TARGETED");
        Assert.Single(raw);
        var entry = raw[0];
        Assert.Equal("PSB_ENV_TARGETED", entry.Properties["Name"]?.Value?.ToString());
        Assert.Equal("hello-target", entry.Properties["Value"]?.Value?.ToString());
        Assert.Equal("hello-target", entry.Properties["BashText"]?.Value?.ToString());
        Assert.Contains("PsBash.EnvEntry", entry.TypeNames);
    }

    [Fact]
    public void Printenv_MissingName_Status1Silent_UsageError2()
    {
        var name = "PSB_ENV_DEFINITELY_NOT_SET_" + Guid.NewGuid().ToString("N");
        var r = CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashPrintenv {name}");
        r.AssertFailed(1);
        Assert.Empty(r.Lines);
        CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashPrintenv -Q")
            .AssertFailed(2, "printenv: invalid option -- 'Q'", "Try 'printenv --help'");
    }

    // GNU `env NAME` RUNS NAME: the old ps-bash-ism printed the variable, so a COMMAND that is not found was reported
    // as an unset variable at status 1 instead of 127.
    [Fact]
    public void Env_CommandNotFound_Exit127()
    {
        var name = "psb-nosuch-" + Guid.NewGuid().ToString("N");
        CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashEnv {name}")
            .AssertFailed(127, $"env: ‘{name}’: No such file or directory");
    }

    [Fact]
    public void Env_Directory_Exit126()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psb-envdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            CmdResult.Run(_fixture.AcquireFresh(), $"Set-Location '{dir}'; Invoke-BashEnv ./sub; New-Item -ItemType Directory sub | Out-Null; Invoke-BashEnv ./sub")
                .AssertFailed(126, "env: ‘./sub’: Permission denied");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("-Q", "env: invalid option -- 'Q'")]
    [InlineData("-u", "env: option requires an argument -- 'u'")]
    [InlineData("--bogus", "env: unrecognized option '--bogus'")]
    public void Env_UsageError_Exit125(string arg, string message)
        => CmdResult.Run(_fixture.AcquireFresh(), $"Invoke-BashEnv '{arg}'")
            .AssertFailed(125, message, "Try 'env --help' for more information.");

    // -u NAME / -uNAME / --unset=NAME, `-` = -i, `--` ends options; several assignments.
    [Fact]
    public void Env_UnsetIgnoreAndAssignments()
    {
        var lines = RunLines(
            "$env:PSB_U = 'set'; " +
            "Invoke-BashEnv -u PSB_U Invoke-BashPrintenv PSB_U; " +
            "Invoke-BashEnv -uPSB_U Invoke-BashPrintenv PSB_U; " +
            "Invoke-BashEnv --unset=PSB_U Invoke-BashPrintenv PSB_U; " +
            "Invoke-BashEnv PSB_A=1 PSB_B=2 Invoke-BashPrintenv PSB_A PSB_B | ForEach-Object { $_.BashText }; " +
            "Invoke-BashEnv - PSB_Z=3 Invoke-BashPrintenv PSB_Z PSB_U | ForEach-Object { $_.BashText }; " +
            "Invoke-BashEnv -- Invoke-BashPrintenv PSB_U | ForEach-Object { $_.BashText }");
        Assert.Equal(new[] { "1", "2", "3", "set" }, lines);
    }

    // `-i` collides with -InformationAction: a DIRECT call binds the decoy (Pester calls cmdlets directly).
    [Fact]
    public void Env_DirectInvocation_IgnoreEnvironmentDecoy()
    {
        var r = CmdResult.Run(_fixture.AcquireFresh(),
            "$env:PSB_GONE = 'x'; Invoke-BashEnv -i PSB_ONLY=1 Invoke-BashPrintenv PSB_ONLY PSB_GONE | ForEach-Object { $_.BashText }");
        r.AssertFailed(1);
        Assert.Equal(new[] { "1" }, r.Lines);
    }

    // `env false` / `xargs false`: false and true are commands too (Invoke-BashFalse / Invoke-BashTrue).
    [Fact]
    public void Env_FalseAndTrue_AreCommands()
    {
        CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashEnv false").AssertFailed(1);
        CmdResult.Run(_fixture.AcquireFresh(), "Invoke-BashEnv true").AssertSuccess();
    }
    [Fact]
    public void Env_ViaPrintenvAlias_NamedVar_EmitsValueOnly()
    {
        // bash `printenv NAME` prints the VALUE only (no `NAME=` prefix): its own cmdlet, Invoke-BashPrintenv.
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
