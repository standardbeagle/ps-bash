using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>Tilde-prefix rules (bash manual 3.5.2), found by a differential sweep against bash 5.2: the emitted PowerShell
/// must PARSE (a bareword `$HOME/x` in an array element or test operand is a division), keep a trailing `/`, and expand
/// only an unquoted prefix.</summary>
public class TildeExpansionEmitterTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    [Fact] public void Transpile_TildeSlashAlone_KeepsTheSlash() => Assert.Contains("\"$HOME/\"", T("echo ~/"));

    [Theory]
    [InlineData("echo ~'/q'")]
    [InlineData("echo ~\"/q\"")]
    [InlineData("echo ~\\x")]
    public void Transpile_QuotedTildePrefix_StaysLiteral(string bash) => Assert.DoesNotContain("$HOME", T(bash));

    [Fact] public void Transpile_TildeBeforeVariable_StaysLiteral() => Assert.DoesNotContain("$HOME", T("echo ~$u"));

    [Fact] public void Transpile_CaseTildePattern_ExpandsHome() => Assert.Contains("$HOME", T("case /h in ~) echo m;; esac"));

    [Fact] public void Transpile_HereStringTilde_ExpandsHome() => Assert.Contains("$HOME/x", T("cat <<< ~/x"));

    [Fact] public void Transpile_QuotedHereStringTilde_StaysLiteral() => Assert.DoesNotContain("HOME", T("cat <<< '~/x'"));

    [Fact] public void Transpile_LeadingBraceTilde_ExpandsEachItem()
    {
        var ps = T("echo {~,~/x}");
        Assert.Contains("$HOME", ps);
    }

    [Fact] public void Transpile_RemovalPatternTilde_ExpandsHome() => Assert.Contains("$HOME", T("echo ${HOME#~}"));

    [Fact] public void Transpile_QuotedDefaultTilde_HasNoQuoteCharacters() => Assert.DoesNotContain("'''", T("echo \"${u:-~}\""));
}
