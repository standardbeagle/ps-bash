using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// One GNU regex translator (GnuRegex) for grep, sed, expr and find, diffed against the GNU tools.
///
/// Covered regressions:
///   - each tool had its own BRE/ERE pass: grep read <c>a$b</c>'s <c>$</c> as an anchor and <c>\d</c> as a digit
///   - <c>a\{1</c>, <c>[[:foo:]]</c>, <c>a**</c> (sed) were .NET's "Invalid pattern ..." or silently accepted, not
///     GNU's <c>Unmatched \{</c> / <c>Invalid character class name</c> / <c>Invalid preceding regular expression</c>
///   - <c>[:space:]</c> is GNU's "character class syntax is [[:space:]], not [:space:]" (sed status 4, grep 2)
///   - grep -E reads a malformed interval as text and warns about a leading repetition
/// </summary>
public class GnuRegexDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    private const string In = @"cd ""$(mktemp -d)"" || exit; printf '%s\n' 'a$b' ab '*a' '{1}a' 'a{1,2' 'x)' aa > in; ";

    [SkippableFact]
    public Task Grep_Bre_PositionalAnchorsAndLeadingRepetitionAreText()
        => Eq(In + @"grep 'a$b' in; echo ""s=$?""; grep -c '*a' in; grep -c '\{1\}a' in; grep -c 'a**' in; "
            + @"grep -c '\(^a$\)\|^ab' in; grep -c 'x\{1\}\{2\}' in");

    [SkippableFact]
    public Task Grep_BadRegex_IsGnusMessage_Status2()
        => Eq(In + @"grep 'a\{1' in; echo ""s=$?""; grep '[[:foo:]]' in; echo ""s=$?""; grep '[:space:]' in; echo ""s=$?""; "
            + @"grep '[b-a]' in; echo ""s=$?""; grep '\(a\)\2' in; echo ""s=$?""; grep 'a\' in; echo ""s=$?""; "
            + @"grep 'a\{2,1\}' in; echo ""s=$?""");

    [SkippableFact]
    public Task GrepE_MalformedIntervalIsText_LeadingRepetitionWarns()
        => Eq(In + @"grep -E -c 'a{1,2' in; grep -E -c 'x)' in; grep -E 'a{2,1}' in; echo ""s=$?""; "
            + @"grep -E -c '*a' in; echo ""s=$?""; grep -E -c '+a' in; grep -E -c 'b|?a' in");

    [SkippableFact]
    public Task Sed_BadRegex_IsGnusMessage()
        => Eq(In + @"sed 's/a\{1/x/' in; echo ""s=$?""; sed 's/[[:foo:]]/x/' in; echo ""s=$?""; sed 's/a**/x/' in; echo ""s=$?""; "
            + @"sed 's/\{1\}/x/' in; echo ""s=$?""; sed -E 's/a{1,2/x/' in; echo ""s=$?""; sed -E 's/*a/x/' in; echo ""s=$?""; "
            + @"sed 's/[[.ab.]]/x/' in; echo ""s=$?""");

    [SkippableFact]
    public Task Sed_ColonClassMistake_IsStatus4()
        => Eq(In + @"sed 's/[:space:]/X/' in; echo ""s=$?""");

    [SkippableFact]
    public Task Sed_Ere_StackedRepetitionIsAccepted()
        => Eq(In + @"sed -E 's/a**/X/' in; sed -E 's/(a)*{2}/Y/' in; sed -E 's/a{1}{2}/Z/' in");

    [SkippableFact]
    public Task Expr_BadRegex_IsGnusMessage()
        => Eq(@"expr ab : '\(a\)\2'; echo ""s=$?""; expr ab : 'a\{1'; echo ""s=$?""; expr ab : 'a\{2,1\}'; echo ""s=$?""; "
            + @"expr aa : 'a**'; expr '{1}a' : '\{1\}a'; expr 'a$b' : 'a$b'");

    [SkippableFact]
    public Task Find_RegexFlavors_GnuRules()
        => Eq(@"cd ""$(mktemp -d)"" || exit; mkdir d; touch 'd/aa' 'd/a)' 'd/a{'; "
            + @"find d -regextype posix-extended -regex '.*a)' ; echo ""s=$?""; "
            + @"find d -regextype posix-extended -regex '.*/*a'; echo ""s=$?""; "
            + @"find d -regextype posix-basic -regex '.*/\{1\}a'; echo ""s=$?""; "
            + @"find d -regex '.*/a\{'; echo ""s=$?""; find d -regex '.*/a+'; echo ""s=$?""");
}
