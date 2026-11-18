using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for how line-reading commands see a stream whose pieces end mid-line. REGRESSION: every
/// line consumer took each pipeline RECORD as its own line(s), so a piece without its newline (<c>printf a</c>, a file
/// with no final newline, a <c>printf "%s,"</c> loop) became a line of its own: <c>{ printf a; printf 'b\n'; } | sed
/// s/x/y/</c> printed <c>a</c> and <c>b</c>, <c>grep -c .</c> counted pieces, <c>head -n1</c> printed only the first
/// piece, <c>sort</c> sorted fragments (25 commands). Fixed at the class: one primitive, <c>LineAssembler</c>, that
/// every line consumer feeds its records through (guard <c>LineAssemblerGuardTests</c>).
/// Also here: a quoted operand (<c>-d' '</c>, <c>'-d:'</c>) in a compound command's stdin scope counted as a file
/// operand, so <c>x | { cut -d' ' -f1; }</c> / <c>x | eval "cut '-d:' -f1"</c> read no stdin (<c>StdinOperands</c>).
/// </summary>
public class LineAssemblyDifferentialTests
{
    [SkippableFact]
    public Task LineConsumers_JoinPiecesIntoLines()
        => AssertOracle.EqualAsync(
            "cd \"$(mktemp -d)\" || exit\n"
            + "printf 'q' > u; printf 'a b\\nc d\\n' > f\n"
            + "for c in 'sed s/x/y/' 'awk 1' 'awk {print\\ NF}' 'grep -c .' 'grep a' 'cut -c1-3' 'rev' 'paste -sd+' 'nl' "
            + "'sort' 'uniq' 'uniq -c' 'wc -l' 'head -n1' 'tail -n1' 'tac' 'tr a-z A-Z' 'cat -n' 'fold -w2' 'expand' "
            + "'column -t' 'xargs echo' 'strings' 'grep -n .' 'rg -N .' "
            + "'mapfile -t a; echo ${#a[@]}:${a[0]}' 'while IFS= read -r l; do echo \"<$l>\"; done'; do\n"
            + "  echo \"## $c\"\n"
            + "  { printf q; printf 'a b\\n'; printf 'c'; printf ' d\\n'; } | eval \"$c\"; echo \"|rc=$?\"\n"
            + "  cat u f | eval \"$c\"; echo \"|rc=$?\"\n"
            + "  { printf 'x\\ny'; } | eval \"$c\"; echo \"|rc=$?\"\n"
            + "done 2>&1\n"
            + "cd / ",
            timeout: TimeSpan.FromSeconds(90));

    [SkippableFact]
    public Task LoopPieces_NulRecords_AndQuotedOperandsInAStdinScope()
        => AssertOracle.EqualAsync(
            "cd \"$(mktemp -d)\" || exit\n"
            + "for c in 'sed s/,$//' 'awk {print\\ NR\\ \\$0}' 'cut -d, -f2' 'rev' 'nl' 'grep -c ,'; do\n"
            + "  for i in 1 2 3; do printf '%s,' $i; done | eval \"$c\"; echo \"|$c\"\n"
            + "done\n"
            + "printf 'b\\0a\\0' | sort -z | tr '\\0' @; echo '|'\n"
            + "{ printf 'b'; printf 'x\\0a\\0'; } | sort -z | tr '\\0' @; echo '|'\n"
            + "printf 'a\\0b\\0' | head -zn1 | tr '\\0' @; echo '|'\n"
            + "{ printf 'a\\0a'; printf '\\0b\\0'; } | uniq -z | tr '\\0' @; echo '|'\n"
            + "yes | tr '\\n' x | head -c 5; echo '|'\n"
            + "printf 'f\\0g' | tr '\\0' @ | sed 's/@/+/'; echo '|'\n"
            + "echo 'a b:c' | { cut -d' ' -f1; }\n"
            + "echo 'a b:c' | { cut '-d:' -f2; }\n"
            + "echo 'a b:c' | { cut -d\":\" -f2; }\n"
            + "echo 'a b:c' | eval \"cut '-d:' -f1\"\n"
            + "echo 'a b:c' | eval \"'cat'\"\n"
            + "echo 'a b:c' | { 'sort' -t':' -k2; }\n"
            + "echo 'piped' | { cat '-'; }\n"
            + "cd / ",
            timeout: TimeSpan.FromSeconds(60));
}
