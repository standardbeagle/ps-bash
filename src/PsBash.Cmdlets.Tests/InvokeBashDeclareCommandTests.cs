using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The print forms of <c>declare</c>/<c>typeset</c> (<c>-f</c>, <c>-F</c>, <c>-p</c>). Oracle: bash 5.2
/// (`f(){ :; }; declare -f f` → "f () ", "{ ", "    :", "}", exit 0; `declare -f nosuch` → no output, exit 1;
/// `declare -F f` → "f"; `declare -F` → "declare -f f"; `declare -p x` → `declare -- x="1"`).
/// The body is the function's emitted PowerShell (bash source is not kept) — hand-asserted for that reason.
/// </summary>
public class InvokeBashDeclareCommandTests : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    public InvokeBashDeclareCommandTests(SharedPwshFixture fixture) => _fixture = fixture;

    /// <summary>A function exactly as the transpiler defines a bash function.</summary>
    private static string BashFunction(string name, string body) =>
        "function " + name + " {" + PsBuild.FunctionPrologue + body + PsBuild.FunctionEpilogue + "}; ";

    // The last statement's records are projected to their BashText (what the shell prints).
    private CmdResult Run(string script) => CmdResult.Run(_fixture.AcquireFresh(),
        script + " | ForEach-Object { if ($_.PSObject.Properties['BashText']) { $_.BashText } else { \"$_\" } }");

    private static string[] Texts(CmdResult r) => r.Lines.ToArray();

    [Fact]
    public void DeclareF_BashFunction_PrintsItsDefinition_ExitZero()
    {
        var r = Run(BashFunction("psbfn_a", "Invoke-BashEcho hi") + "Invoke-BashDeclare '-f' psbfn_a").AssertSuccess();

        Assert.Equal(new[] { "psbfn_a () ", "{ ", "    Invoke-BashEcho hi", "}" }, Texts(r));
    }

    [Fact]
    public void DeclareF_MissingFunction_PrintsNothing_ExitOne()
    {
        // The stock probe `declare -f fn >/dev/null 2>&1`: bash is silent and only the status says "absent".
        var r = Run("Invoke-BashDeclare '-f' psbfn_definitely_missing").AssertFailed(1);

        Assert.Empty(r.Lines);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void DeclareF_PowerShellFunctionThatIsNotABashFunction_CountsAsMissing()
    {
        // The runtime's own PowerShell functions are not shell functions.
        var r = Run("function psbfn_plain { 1 }; Invoke-BashDeclare '-f' psbfn_plain").AssertFailed(1);

        Assert.Empty(r.Lines);
    }

    [Fact]
    public void DeclareBigF_Name_PrintsJustTheName()
    {
        var r = Run(BashFunction("psbfn_b", "Invoke-BashEcho x") + "Invoke-BashDeclare '-F' psbfn_b").AssertSuccess();

        Assert.Equal(new[] { "psbfn_b" }, Texts(r));
    }

    [Fact]
    public void DeclareBigF_NoNames_ListsOnlyBashFunctionsAsDeclareLines()
    {
        var r = Run(BashFunction("psbfn_c", "Invoke-BashEcho x") + "Invoke-BashDeclare '-F'").AssertSuccess();

        Assert.Contains("declare -f psbfn_c", Texts(r));
        Assert.DoesNotContain(Texts(r), l => l.Contains("Show-BashHelp", StringComparison.Ordinal));
    }

    [Fact]
    public void DeclareP_Variable_PrintsADeclareLine()
    {
        var r = Run("$global:psbvar_x = '1'; Invoke-BashDeclare '-p' psbvar_x").AssertSuccess();

        Assert.Equal(new[] { "declare -- psbvar_x=\"1\"" }, Texts(r));
    }

    [Fact]
    public void DeclareP_DirectCall_DecoySwitchStillWorks()
    {
        // Pester / interactive PowerShell bind a bare -p to the P decoy (-p prefix-matches -PipelineVariable).
        var r = Run("$global:psbvar_y = 'v'; Invoke-BashDeclare -p psbvar_y").AssertSuccess();

        Assert.Equal(new[] { "declare -- psbvar_y=\"v\"" }, Texts(r));
    }

    [Fact]
    public void DeclareP_MissingVariable_IsNotFound_ExitOne()
    {
        Run("Invoke-BashDeclare '-p' psbvar_definitely_missing")
            .AssertFailed(1, "bash: declare: psbvar_definitely_missing: not found");
    }

    [Fact]
    public void Declare_InvalidOption_ExitTwo()
    {
        Run("Invoke-BashDeclare '-fz' x").AssertFailed(2, "declare: -z: invalid option");
    }

    [Fact]
    public void DeclareF_Output_IsNewlineTerminated()
    {
        var output = _fixture.AcquireFresh().AddScript("$global:psbvar_z = '1'; Invoke-BashDeclare '-p' psbvar_z").Invoke();

        Assert.Single(output);
        Assert.NotEqual(true, output[0].Properties["NoTrailingNewline"]?.Value);
    }
}
