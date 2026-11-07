using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>Tilde words must emit PowerShell that parses: a bareword `$HOME/x` in an array element or a test operand is a
/// division, and `$HOME:y` reads `$HOME:` as a drive reference (found by a differential sweep against bash 5.2).</summary>
public class TildeParseabilityTests
{
    [Theory]
    [InlineData("set -- ~ ~/z")]
    [InlineData("[ ~/x = /home/t/x ] && echo y")]
    [InlineData("a=(~/a ~/b)")]
    [InlineData("x=~:y")]
    [InlineData("echo a=~:y ~/")]
    [InlineData("echo ~'/q' ~\"/q\" ~\\root ~$u")]
    [InlineData("echo {~,~/x}")]
    [InlineData("case /h in ~) echo m;; esac")]
    [InlineData("cat <<< ~/x")]
    [InlineData("echo ${HOME#~} \"${u:-~}\"")]
    public void Transpile_TildeWords_ProduceParseablePowerShell(string bash) => ParseabilityContract.AssertNoCrash(bash);
}
