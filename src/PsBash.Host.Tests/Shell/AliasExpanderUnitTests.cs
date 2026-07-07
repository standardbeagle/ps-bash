using PsBash.Host.Shell;
using Xunit;

namespace PsBash.Host.Tests.Shell;

/// <summary>
/// Unit tests for <see cref="AliasExpander.ExpandAliases"/> command-position logic —
/// which words re-arm alias expansion (a command) vs. which do not (arguments and
/// redirect targets). Regression guards for the 2026-07 review:
///   • `&gt;` / `&amp;&gt;` redirect targets must NOT be alias-expanded (they are filenames);
///   • a newline (multi-line input) DOES re-arm, so an aliased command on line 2 expands.
///
/// Oracle note (qa-rubric Directive 1): interactive-shell alias resolution is a
/// ps-bash-specific surface with no bash-transpile equivalent — hand-asserted per the
/// exception list. The static <see cref="AliasExpander.Aliases"/> table is cleared
/// around each test.
/// </summary>
public class AliasExpanderUnitTests : IDisposable
{
    public AliasExpanderUnitTests() => AliasExpander.Aliases.Clear();
    public void Dispose() => AliasExpander.Aliases.Clear();

    [Fact]
    public void CommandStart_Expands()
    {
        AliasExpander.Aliases["ll"] = "ls -la";
        Assert.Equal("ls -la", AliasExpander.ExpandAliases("ll"));
    }

    [Fact]
    public void RedirectTargetAfterAmpGt_NotExpanded()
    {
        AliasExpander.Aliases["ll"] = "ls -la";
        // `echo hi &>ll` — ll is a both-streams redirect FILENAME, not a new command.
        var result = AliasExpander.ExpandAliases("echo hi &>ll");
        Assert.DoesNotContain("ls -la", result);
        Assert.Contains("&>ll", result);
    }

    [Fact]
    public void RedirectTargetAfterGt_NotExpanded()
    {
        AliasExpander.Aliases["out"] = "SHOULD-NOT-EXPAND";
        Assert.DoesNotContain("SHOULD-NOT-EXPAND", AliasExpander.ExpandAliases("echo hi >out"));
    }

    [Fact]
    public void BareBackgroundAmp_StillReArms()
    {
        AliasExpander.Aliases["ll"] = "ls -la";
        // `sleep 1 & ll` — bare & is the background operator, so ll IS a new command.
        Assert.Contains("ls -la", AliasExpander.ExpandAliases("sleep 1 & ll"));
    }

    [Fact]
    public void SecondLineOfMultilineInput_Expands()
    {
        AliasExpander.Aliases["ll"] = "ls -la";
        // A newline starts a new command line — ll on line 2 re-arms and expands.
        Assert.Contains("ls -la", AliasExpander.ExpandAliases("echo hi\nll"));
    }

    [Fact]
    public void ArgumentPosition_NotExpanded()
    {
        AliasExpander.Aliases["ll"] = "ls -la";
        // ll as an argument (not command position) stays literal.
        Assert.Equal("echo ll", AliasExpander.ExpandAliases("echo ll"));
    }
}
