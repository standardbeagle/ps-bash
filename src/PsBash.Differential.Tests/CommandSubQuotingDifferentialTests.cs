using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for command substitution whose text holds raw regions — heredoc bodies,
/// quoted or escaped parens — in quoted, assignment, and glued contexts.
///
/// Covered regressions:
///   - a heredoc body inside <c>$( )</c> was lexed as code: an apostrophe (<c>it's</c>) made the
///     script a parse error, a <c>)</c> ended the substitution early. This is the
///     <c>git commit -m "$(cat &lt;&lt;'EOF' … EOF)"</c> idiom agents use for every commit.
///   - PowerShell finds the end of a <c>$( )</c> inside a string or bareword by naive paren
///     counting, so ONE odd paren in quoted command text failed the whole script.
///   - <c>\)</c> inside <c>$( )</c> was taken as the closing paren.
/// </summary>
public class CommandSubQuotingDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    [SkippableFact]
    public Task CommandSub_HeredocBody_ApostropheAndParen()
        => Eq("m=\"$(cat <<'EOF'\n- rc kept: ps-bash's own shell :) (mostly\nEOF\n)\"\nprintf '[%s]\\n' \"$m\"");

    [SkippableFact]
    public Task CommandSub_HeredocForms_UnquotedDashMultiple()
        => Eq("a=$(cat <<EOF\nit's $((1+1))\nEOF\n)\n"
            + "b=\"$(cat <<-EOF\n\tit's ) tabbed\n\tEOF\n)\"\n"
            + "c=\"$(cat <<A; cat <<'B'\nit's a\nA\nit's ) b\nB\n)\"\n"
            + "printf '[%s]\\n' \"$a\" \"$b\" \"$c\"");

    [SkippableFact]
    public Task CommandSub_QuotedParensInQuotedContext()
        => Eq("echo \"1 [$(echo 'a ) b')]\"; echo \"2 [$(echo \"a ( b\")]\"; x=\"$(printf '%s' ':)')\"; echo \"3 [$x]\"; echo \"4 [${HOME:+$(echo 'x)')}]\"");

    [SkippableFact]
    public Task CommandSub_ParensInAssignmentAndGluedWords()
        => Eq("y=$(echo 'bare ) ok'); echo \"[$y]\"; echo [$(echo ')')] pre$(echo '(')post; echo \"a $(echo \"b $(echo 'c ) d')\") e\"");

    [SkippableFact]
    public Task CommandSub_EscapedParen()
        => Eq("echo \"[$(echo \\))]\" [$(echo \\()]");

    /// <summary>
    /// Backticks inside double quotes also strip the backslash of \" ("x `echo \"hi\"`" is x hi; it printed x "hi"), and
    /// a word that starts with an escaped space keeps it (`echo \ ` printed an empty line: PowerShell drops a
    /// backtick-escaped space at the start of an argument).
    /// </summary>
    [SkippableFact]
    public Task Backtick_InDoubleQuotes_AndLeadingEscapedSpace()
        => Eq("echo \"x `echo \\\"hi\\\"`\" \"x $(echo \\\"hi\\\")\"\n"
            + "echo \"[1 `echo \\ `]\" \"[3 `echo \\\\\\\\ `]\" \"3 `echo \\\\ \\\\(`\"\n"
            + "echo \\ | od -c | head -1; echo \\ x; printf '[%s]' \\ ; echo; v=(\\ a \\ ); echo \"${#v[@]}:${v[0]}:${v[1]}:\"");

    /// <summary>
    /// An unquoted substitution glued to text is word-split, the text glued to its first and last field: it was one
    /// argument, so `[$(echo " ")]` kept the space and `x$(printf 'a\nb')y` was one word.
    /// </summary>
    [SkippableFact]
    public Task CommandSub_GluedToText_IsWordSplit()
        => Eq("cd \"$(mktemp -d)\"; mkdir d2; touch d2/a.txt d2/b.txt\n"
            + "echo [1 $(echo \" \")] [2 x$(echo \" a b \")y]\n"
            + "for w in pre$(printf 'a\\nb')post; do echo \"w=$w\"; done\n"
            + "ls d2/$(echo '*.txt'); cat $(echo d2)/a.txt; echo \"st:$?\"\n"
            + "x=pre$(echo \" a b \")post; echo \"[$x]\"; arr=(a$(echo \"1 2\")b); echo \"${#arr[@]}\"\n"
            + "cnt() { echo \"n=$#\"; }; cnt x$(echo)y; cnt $(echo \"a b\")/c; echo \"q$(echo \" a \")q\" [$(echo)]");

    [SkippableFact]
    public Task CommandSub_ArithShiftIsNotHeredoc()
        => Eq("v=$( (( n = 1 << 3 )); echo \"$n it's\" ); echo \"[$v]\"");
}
