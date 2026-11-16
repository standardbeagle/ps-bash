using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A <c>case</c> pattern undergoes parameter/command expansion; an unquoted expansion's value is pattern text,
/// a quoted one is literal. The pattern was emitted as the literal text <c>$i</c>, so none of these matched.
/// </summary>
public class CasePatternExpansionTests
{
    [SkippableTheory]
    [InlineData("i=a; case a in $i) echo y;; *) echo n;; esac")]
    [InlineData("i=a; case a in \"$i\") echo y;; *) echo n;; esac")]
    [InlineData("i=a; case ' a ' in *\" $i \"*) echo y;; *) echo n;; esac")]
    [InlineData("i=a; case \" $i \" in *\" $i \"*) echo y;; *) echo n;; esac")]
    [InlineData("p='a*'; case abc in $p) echo y;; *) echo n;; esac")]
    [InlineData("p='a*'; case abc in \"$p\") echo y;; *) echo n;; esac")]
    [InlineData("p='a*'; case 'a*' in \"$p\") echo y;; *) echo n;; esac")]
    [InlineData("x=b; case b in a|$x|c) echo y;; *) echo n;; esac")]
    [InlineData("case a in $(echo a)) echo y;; *) echo n;; esac")]
    [InlineData("i=a; case zz in $i) echo y;; *) echo n;; esac")]
    [InlineData("case a in '$i') echo y;; *) echo n;; esac")]
    public Task Pattern_Expands(string script) => AssertOracle.EqualAsync(script);
}
