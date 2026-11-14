using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary><c>printf %q</c> and <c>%(fmt)T</c> were unsupported.</summary>
public class PrintfQuoteTimeTests
{
    [Fact] public Task Q_Metacharacters() => AssertOracle.EqualAsync("printf '%q\\n' 'a b' \"it's\" 'x$y' 'a;b' 'a&b' 'a|b' 'a*b' 'a?b' 'a(b' 'a\"b' 'a`b' 'a\\b'");
    [Fact] public Task Q_Empty_AndSafe() => AssertOracle.EqualAsync("printf '[%q]\\n' '' abc a/b.c-d_e a:b a=b a+b a@b a%b 'a#b' 'a~b'");
    [Fact] public Task Q_LeadingSpecials() => AssertOracle.EqualAsync("printf '%q\\n' '#a' '~' '~a' '-a' '=a'");
    [Fact] public Task Q_ControlCharacters() => AssertOracle.EqualAsync("printf '%q\\n' \"$(printf 'a\\tb')\" \"$(printf 'x\\001y')\" $'l1\\nl2'");
    [Fact] public Task Q_RoundTrips() => AssertOracle.EqualAsync("v=$(printf '%q' 'we ird;$x'); eval \"w=$v\"; echo \"[$w]\"");
    [Fact] public Task Time_EpochSeconds() => AssertOracle.EqualAsync("printf '%(%Y-%m-%d %j %a %b)T\\n' 302400");
    // oils builtin-printf "%(fmt)T": the shell's TZ picks the zone (it used the machine's).
    [Fact] public Task Time_HonorsTz() => AssertOracle.EqualAsync("export TZ=Asia/Tokyo; printf '%(%Y-%m-%d %H:%M)T\\n' 0; TZ=UTC; printf '%(%H:%M)T\\n' 3600; TZ=:America/New_York; printf '%(%H)T\\n' 0");
    [Fact] public Task Time_Now_HasShape() => AssertOracle.EqualAsync("case $(printf '%(%Y)T' -1) in 20[0-9][0-9]) echo year;; esac; case $(printf '%(%s)T') in [0-9]*) echo secs;; esac");
}
