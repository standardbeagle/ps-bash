using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// Pathname expansion (bash "Filename Expansion") against real bash 5.2. The shell expands an unquoted
/// glob word AFTER word splitting and BEFORE the command runs, so every consumer — echo, printf, a function,
/// a for list, an array, the cmdlets that also glob themselves — receives file names: relative as written,
/// hidden names only for a literal-dot pattern, sorted, the literal word when nothing matches.
///
/// Fixture: <c>x</c>, <c>y.txt</c>, a hidden <c>.hid</c>, and <c>sub/{a1,a[1],b1}</c>
/// (<c>FsStateOracle.Tree</c>, so the resulting filesystem is compared too).
/// Failure-surface axes: quoting (Directive 3 axis 12: <c>"*"</c>, <c>\*</c>, <c>"$d"/*</c>), missing target
/// (axis 14: no match keeps the literal), unicode/special names (<c>a[1]</c> is a file, not a class),
/// working directory (relative results), and the shopt switches <c>dotglob</c> / <c>nullglob</c>.
/// Divergence documented in docs/specs/intentional-differences.md: <c>failglob</c> aborts only the failing
/// command here (bash discards the whole line), collation is byte order (the oracle runs C.UTF-8).
/// </summary>
public class PathnameExpansionDifferentialTests
{
    private static string Fx() =>
        Tree(("x", "x"), ("y.txt", "y"), (".hid", "h"), ("sub/a1", "a"), ("sub/b1", "b"), ("sub/a[1]", "c"));

    private static Task Case(string command) => EqualAsync(Fx(), command);

    // ───────────── the consumers that never expanded anything before ─────────────

    [SkippableFact] public Task Echo_Star_ListsNamesWithoutHiddenFiles() => Case("echo *");
    [SkippableFact] public Task Echo_StarDotTxt() => Case("echo *.txt");
    [SkippableFact] public Task Printf_PerWord() => Case("printf '%s\\n' *");
    [SkippableFact] public Task Function_ReceivesNamesNotThePattern() => Case("f() { echo \"$#\"; }; f *");
    [SkippableFact] public Task Array_AssignmentExpandsElements() => Case("arr=(*); echo \"${#arr[@]} ${arr[*]}\"");
    [SkippableFact] public Task Array_MixedLiteralAndGlobElements() => Case("arr=(a *.txt z); echo \"${#arr[@]}: ${arr[*]}\"");
    [SkippableFact] public Task ForList_StarIsRelativeAndSkipsHidden() => Case("for f in *; do echo \"[$f]\"; done");
    [SkippableFact] public Task ForList_GlobAndLiterals() => Case("for f in a *.txt x; do echo \"<$f>\"; done");
    [SkippableFact] public Task ForList_NoMatchIteratesOnceWithTheLiteral() => Case("for f in *.zz; do echo \"<$f>\"; done");

    // ───────────── pattern forms ─────────────

    [SkippableFact] public Task TrailingSlash_DirectoriesOnly() => Case("echo */");
    [SkippableFact] public Task DotStar_NeverMatchesDotOrDotDot() => Case("echo .*");
    [SkippableFact] public Task CharacterSet() => Case("echo [xy]*");
    [SkippableFact] public Task NegatedSet() => Case("echo [!x]*");
    [SkippableFact] public Task PosixClass() => Case("echo [[:alpha:]]*");
    [SkippableFact] public Task QuestionMark() => Case("echo ?.txt");
    [SkippableFact] public Task SubdirectoryPattern_KeepsThePathAsWritten() => Case("echo sub/*");
    [SkippableFact] public Task TwoLevelPattern() => Case("echo */*");
    [SkippableFact] public Task SetOfOneInTheMiddleOfAWord() => Case("echo sub/a[1]");
    [SkippableFact] public Task StarInsideQuotesMixedWithAStar() => Case("echo *\"y\"*");
    [SkippableFact] public Task AbsolutePattern_GivesAbsolutePaths() => Case("[ \"$(echo \"$PWD\"/*.txt)\" = \"$PWD/y.txt\" ] && echo same");

    // ───────────── quoting disables it ─────────────

    [SkippableFact] public Task DoubleQuotedStar_StaysLiteral() => Case("echo \"*\"");
    [SkippableFact] public Task SingleQuotedStar_StaysLiteral() => Case("echo '*.txt'");
    [SkippableFact] public Task EscapedStar_StaysLiteral() => Case("echo \\*");
    [SkippableFact] public Task EscapedBracketThenStar() => Case("echo sub/a\\[*");
    [SkippableFact] public Task QuotedVariablePrefix_DoesNotGlobItsOwnValue() => Case("d=sub; echo \"$d\"/*");
    [SkippableFact] public Task QuotedVariableHoldingStar_StaysLiteral() => Case("v='*'; echo \"$v\"");

    // ───────────── expansions that ARE subject to globbing ─────────────

    [SkippableFact] public Task UnquotedVariableHoldingStar_Expands() => Case("v='*'; echo $v");
    [SkippableFact] public Task UnquotedVariablePrefix_ThenStar() => Case("d=sub; echo $d/*");
    [SkippableFact] public Task UnquotedCommandSubstitutionResult_Expands() => Case("echo $(echo *) end");
    [SkippableFact] public Task UnquotedVariable_SplitsOnIfsThenGlobs() => Case("v='x y.t*'; echo $v");

    // ───────────── no match ─────────────

    [SkippableFact] public Task NoMatch_KeepsTheLiteralPattern() => Case("echo *.zz");
    [SkippableFact] public Task NoMatch_InASubdirectory_KeepsTheLiteralPattern() => Case("echo nosuch/*");
    [SkippableFact] public Task NoMatch_QuotedPartIsUnquotedInTheLiteral() => Case("echo *\"y\"zz*");

    // ───────────── shopt ─────────────

    [SkippableFact] public Task Dotglob_IncludesHiddenNames() => Case("shopt -s dotglob; echo *");
    [SkippableFact] public Task Dotglob_StillExcludesDotAndDotDot() => Case("shopt -s dotglob; echo .*");
    [SkippableFact] public Task Nullglob_NoMatchGivesNoWord() => Case("shopt -s nullglob; echo *.zz; echo n");
    [SkippableFact] public Task Nullglob_ForListRunsZeroTimes() => Case("shopt -s nullglob; for f in *.zz; do echo \"<$f>\"; done; echo done");

    // ───────────── commands that expand their own operands too: no double expansion ─────────────

    [SkippableFact] public Task Cat_StarDotTxt() => Case("cat *.txt");
    [SkippableFact] public Task Ls_StarSkipsHiddenAndListsDirectoryContents() => Case("ls *");
    [SkippableFact] public Task Wc_CountsTheMatchedFiles() => Case("wc -c *.txt x");
    // -h: grep prints the resolved ABSOLUTE path instead of the name as typed (pre-existing, unrelated).
    [SkippableFact] public Task Grep_SearchesTheMatchedFiles() => Case("grep -h y *.txt x; echo rc=$?");
    [SkippableFact] public Task Cat_StarMatchesAFileNamedLikeAClass() => Case("cat sub/a*");
    [SkippableFact] public Task Cat_ClassOperandMatchesTheOtherFile() => Case("cat sub/a[1]");
    [SkippableFact] public Task Cp_StarIntoDirectory_MatchesAndNothingElse() =>
        EqualAsync(Fx() + "\nmkdir -p d", "cp *.txt x d; echo \"rc=$?\"");
    [SkippableFact] public Task Touch_StarTouchesTheExistingMatches() => Case("touch *.txt; echo rc=$?");
    [SkippableFact] public Task Rm_StarRemovesOnlyNonHiddenFiles() =>
        EqualAsync(Fx(), "rm -f *; echo rc=$?");
}
