using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Spike test validating the module-scope binding assumption and its fallback.
///
/// Assumption tested:
///   ScriptBlock.Create(powershell) called from a binary cmdlet binds the
///   scriptblock to the cmdlet module's session state, so private functions
///   from a nested script module resolve even though they are not exported.
///
/// Finding:
///   ASSUMPTION FAILS. ScriptBlock.Create produces an unbound scriptblock.
///   InvokeCommand.InvokeScript(useLocalScope: false) executes in the caller's
///   scope and does NOT walk the cmdlet's private nested-module scope for
///   command resolution. Even InvokeCommand.NewScriptBlock and
///   SessionState.InvokeCommand.GetCommand cannot see nested-module functions.
///
/// Fallback implemented:
///   PsBash.Cmdlets.psd1 nests PsBash.psd1 and sets FunctionsToExport = @('*')
///   so all script-module functions become globally available when the binary
///   module is imported. AliasesToExport remains @() so user-facing aliases
///   like ls are not hijacked. This is slightly leakier (functions visible)
///   but still non-hijacking for aliases.
/// </summary>
public class ModuleScopeBindingTests
{
    [Fact]
    public void InvokeBashEval_ResolvesNestedModuleFunctions()
    {
        // Fallback: nested functions are re-exported globally, so
        // Invoke-BashEval transpiled scriptblocks can resolve them.
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript("Invoke-BashEval 'echo hello' -PassThru").Invoke();
        pwsh.Commands.Clear();

        var err = pwsh.AddScript("$error | Select-Object -First 1").Invoke();
        pwsh.Commands.Clear();

        Assert.True(err.Count == 0 || err[0] == null,
            $"Unexpected error: {(err.Count > 0 ? err[0].ToString() : "none")}");
        Assert.NotEmpty(result);
        Assert.Equal("hello", result[0].ToString());
    }

    [Fact]
    public void InvokeBashEval_Definitions_LandInCallerScope()
    {
        // ScriptBlock.Create + InvokeScript(useLocalScope: false) from a binary
        // cmdlet runs in the pipeline scope, so function definitions are visible
        // to subsequent commands in the same pipeline.
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript(
            "Invoke-BashEval 'greet_scope_test() { echo hi; }'; greet_scope_test")
            .Invoke();
        pwsh.Commands.Clear();

        var err = pwsh.AddScript("$error | Select-Object -First 1").Invoke();
        pwsh.Commands.Clear();

        Assert.True(err.Count == 0 || err[0] == null,
            $"Unexpected error: {(err.Count > 0 ? err[0].ToString() : "none")}");
        Assert.NotEmpty(result);
        Assert.Equal("hi", result[0].ToString());
    }

    [Fact]
    public void NestedModuleFunctions_AreGloballyAvailable()
    {
        // Fallback behavior: FunctionsToExport = @('*') re-exports nested
        // functions globally. This is acknowledged as "leakier" but required
        // for ScriptBlock.Create command resolution.
        using var pwsh = PwshTestFixture.Create();
        pwsh.AddScript("$error.Clear()").Invoke();
        pwsh.Commands.Clear();

        var result = pwsh.AddScript("Invoke-BashLs .").Invoke();
        pwsh.Commands.Clear();

        var err = pwsh.AddScript("$error | Select-Object -First 1").Invoke();
        pwsh.Commands.Clear();

        bool notRecognized = err.Count > 0 && err[0] != null &&
            err[0].ToString()!.Contains("not recognized");

        Assert.False(notRecognized,
            $"Invoke-BashLs should be globally available. Error: {(err.Count > 0 ? err[0] : "none")}");
    }

    [Fact]
    public void Aliases_AreNotExported_GloballyBlocked()
    {
        // AliasesToExport = @() in the parent manifest must block alias exports
        // from the nested script module so host aliases like ls are not hijacked.
        using var pwsh = PwshTestFixture.Create();

        var exportedAliases = pwsh.AddScript(
            "(Get-Module PsBash.Cmdlets).ExportedAliases.Keys")
            .Invoke();
        pwsh.Commands.Clear();

        Assert.Empty(exportedAliases);
    }
}
