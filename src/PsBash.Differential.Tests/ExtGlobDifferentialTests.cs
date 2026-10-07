using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for extglob MATCHING (`shopt -s extglob`): case patterns, the RHS of
/// <c>[[ == / != ]]</c>, and the <c>${v#pat}</c> / <c>%</c> / <c>/pat/rep</c> patterns.
///
/// Covered regressions (all pre-existing): <c>case b in @(a|b))</c> never matched (the pattern was
/// compared as a literal); <c>[[ $w == @(a|b) ]]</c> emitted a PowerShell array (<c>-eq @(a|b)</c>);
/// <c>[[ $w == !(*.log) ]]</c> lexed <c>!</c> as negation ("unsupported test operator");
/// <c>${v%.@(gz|bz2)}</c> / <c>${v//@(a|z)/Y}</c> matched the extglob text literally.
/// </summary>
public class ExtGlobDifferentialTests
{
    // shopt on its OWN line: bash parses a whole line before running it, so a same-line`n    // `shopt -s extglob; case … @(…)` is a bash syntax error (the oracle, not ps-bash, fails).
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task ExtGlob_DoubleBracket_AllOperators()
        => Eq("shopt -s extglob\nfor s in '' a aa ab abab b; do "
            + "[[ $s == ?(a) ]] && printf q; [[ $s == *(ab) ]] && printf s; [[ $s == +(ab) ]] && printf p; "
            + "[[ $s == @(a|b) ]] && printf a; [[ $s != @(a|b) ]] && printf n; echo \"|$s\"; done");

    [SkippableFact]
    public Task ExtGlob_Negation_ExactSplitSemantics()
        => Eq("shopt -s extglob\nfor p in 'a:!(a)*' 'ab:!(a)b' 'b:!(a)b' 'ab:@(a)!(c)' 'ac:@(a)!(c)' "
            + "'foo.log.txt:!(*.log)' 'foo.log:!(*.log)' 'zoo:!(f*|b*)' 'foo:!(f*|b*)'; do "
            + "s=${p%%:*}; pat=${p#*:}; eval \"[[ \\$s == $pat ]]\" && echo \"1 $p\" || echo \"0 $p\"; done");

    [SkippableFact]
    public Task ExtGlob_QuotedEscapedNestedAndClasses()
        => Eq("shopt -s extglob\n[[ 'a)' == @('a)'|z) ]] && echo q1; [[ 'x(y' == @(x\\(y) ]] && echo q2; "
            + "[[ 'a|b' == @('a|b') ]] && echo q3; [[ axc == @(a@(b|x)c) ]] && echo q4; "
            + "[[ file.c == *.@(c|h) ]] && echo q5; [[ file.o == *.@(c|h) ]] || echo q6; "
            + "[[ A == @(a) ]] || echo q7; [[ 7 == @([[:digit:]]) ]] && echo q8; [[ b == @([!a-c]) ]] || echo q9");

    [SkippableFact]
    public Task ExtGlob_CasePatterns()
        => Eq("shopt -s extglob\nfor w in b c foo.log foo.txt xyz 'x(y' A; do case $w in "
            + "@(a|b)) echo \"$w arm1\" ;; @(x\\(y)) echo \"$w esc\" ;; !(*.log|xyz|A)) echo \"$w arm2\" ;; "
            + "*) echo \"$w default\" ;; esac; done");

    [SkippableFact]
    public Task ExtGlob_ParameterPatterns()
        => Eq("shopt -s extglob\nv=aaa.tar.gz; "
            + "echo \"${v#+(a)}|${v##+(a)}|${v#*(a)}|${v##*(a).}\"; "
            + "echo \"${v%.@(gz|bz2)}|${v%%.@(tar|gz)*}|${v%?(.gz)}|${v%%?(.gz)}\"; "
            + "echo \"${v#!(a)}|${v##!(z)}|${v%!(z)}|${v%%!(z)}\"; "
            + "echo \"${v/+(a)/X}|${v//@(a|z)/Y}|${v/!(a)/N}\"; w=foo.log.txt; echo \"${w%.!(txt)}|${w%%.!(log)}\"");

    [SkippableFact]
    public Task ExtGlob_BangAtCommandPositionIsStillNegation()
        => Eq("!(false); echo \"rc=$?\"; if !(false); then echo neg; fi; x=1; [[ $x == !(2) ]] && echo pat");
}
