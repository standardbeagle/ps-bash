using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Guard for the bug class "a bash builtin name falls through to a PowerShell command of the same name". The
/// runspace drops PowerShell's aliases (PowerShellAliasGuardTests) but keeps its default FUNCTIONS, and one of them is
/// <c>help</c> (Get-Help through a pager): bash's <c>help cd | head -1</c> printed "The handle is invalid." and the error
/// ended the sourcing script. A builtin emitted as its bare name runs whatever PowerShell finds under it.
/// <para>Oracle note (qa-rubric Directive 1): which command a name is EMITTED as has no bash equivalent; the behavior
/// is covered by Differential.Tests/ShellLanguageGapDifferentialTests (help).</para>
/// </summary>
public class BuiltinNamesNeverReachPowerShellTests
{
    /// <summary>PowerShell's default functions (InitialSessionState.CreateDefault2). A builtin with one of these names
    /// must never be emitted bare.</summary>
    private static readonly HashSet<string> PowerShellDefaultFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "prompt", "TabExpansion2", "PSConsoleHostReadLine", "Clear-Host", "help", "man", "more", "mkdir", "oss",
        "pause", "cd..", "cd\\", "cd~", "ImportSystemModules", "Get-Verb",
    };

    /// <summary>
    /// Builtins that are emitted as their bare name today, and why that is safe: the runtime's own alias, or a builtin
    /// ps-bash does not have (bash-feature-coverage.md "Missing builtins": it is "command not found", 127).
    /// Implementing one of the missing builtins removes it from here.
    /// </summary>
    private static readonly Dictionary<string, string> BareByDesign = new(StringComparer.Ordinal)
    {
        ["alias"] = "the runtime's alias for Invoke-BashAlias (kept by PowerShellAliases.IsBashRuntimeAlias)",
        ["bind"] = "missing builtin", ["caller"] = "missing builtin", ["disown"] = "missing builtin",
        ["enable"] = "missing builtin", ["logout"] = "missing builtin", ["suspend"] = "missing builtin",
    };

    private static bool EmittedBare(string name)
    {
        string emitted = PsEmitter.Transpile(name + " zz | head -1") ?? "";
        return emitted.StartsWith(name + " ", StringComparison.Ordinal);
    }

    [Fact]
    public void Transpile_BuiltinNamedLikeAPowerShellFunction_IsNeverBare()
    {
        var bare = BashBuiltins.Names.Where(n => PowerShellDefaultFunctions.Contains(n) && EmittedBare(n)).ToList();
        Assert.True(bare.Count == 0, "reaches a PowerShell function of its name: " + string.Join(", ", bare));
    }

    [Fact]
    public void Transpile_BuiltinsEmittedBare_AreExactlyTheListedOnes()
    {
        var bare = BashBuiltins.Names.Where(EmittedBare).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var listed = BareByDesign.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(bare.SequenceEqual(listed),
            $"emitted bare: {string.Join(" ", bare)}\nlisted:       {string.Join(" ", listed)}\n"
            + "A new bare builtin must say why it is safe; an implemented one leaves the list.");
        foreach (var name in BareByDesign.Keys) Assert.DoesNotContain(name, PowerShellDefaultFunctions);
    }

    [Fact]
    public void Transpile_Help_IsTheRuntimeCmdlet()
        => Assert.StartsWith("Invoke-BashHelp ", PsEmitter.Transpile("help cd | head -1"));
}
