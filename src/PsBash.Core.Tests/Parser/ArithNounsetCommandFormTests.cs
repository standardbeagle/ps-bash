using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// bash: <c>(( 1/0 )); echo "st=$?"</c> prints the error, then <c>st=1</c>: the arithmetic COMMAND (and <c>let</c>) fails
/// with status 1 and the line goes on; only a failed WORD (<c>$(( ))</c>, a slice bound) abandons its command. Under
/// <c>set -u</c> anywhere in the script, every evaluation was wrapped as <c>$__psbash_av = Invoke-BashArith</c>, which
/// the cmdlet reads as a word, so the echo was lost (PsBuild.ArithNounsetGuard).
/// </summary>
public class ArithNounsetCommandFormTests
{
    [Theory]
    [InlineData("set -u; (( 1/0 )); echo \"st=$?\"")]
    [InlineData("set -u; let '1/0'; echo \"st=$?\"")]
    [InlineData("set -u; if (( 1/0 )); then echo y; fi")]
    [InlineData("( set -u; true ); (( 3 + x = 4 )); echo \"st=$?\"")]
    public void Transpile_ArithCommandUnderNounset_IsNotAWord(string bash)
    {
        var ps = PsEmitter.Transpile(bash)!;
        Assert.Contains("$__psbash_ac = Invoke-BashArith", ps);
        Assert.DoesNotContain("$__psbash_av = Invoke-BashArith", ps);
    }

    [Theory]
    [InlineData("set -u; echo $(( 1/0 ))")]
    [InlineData("set -u; a=(a b); i=1; echo \"${a[@]:i+0}\"")]
    public void Transpile_ArithWordUnderNounset_StaysAWord(string bash)
        => Assert.Contains("$__psbash_av = Invoke-BashArith", PsEmitter.Transpile(bash)!);
}
