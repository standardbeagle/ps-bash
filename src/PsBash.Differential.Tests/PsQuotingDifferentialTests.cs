using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Differential oracle for bash text that PowerShell would read as syntax although bash does not.
/// <list type="bullet">
/// <item>Unicode quote marks (U+2018-U+201E: a pasted ’ or “”) closed the emitted PS string: a parse error for the
/// whole script, or a silently different value (<c>'a‘’b'</c> printed <c>a’b</c>); a leading en/em dash made the word a
/// -parameter (<c>echo –n x</c> ran <c>echo -n</c>). Guard: PsQuoteMarkGuardTests.</item>
/// <item>Inside a PS string's <c>$( )</c>, PowerShell rewrites double-quote marks even in a nested literal, so
/// <c>echo "${u:-a\"\"b}"</c> printed <c>a"b</c> and an assoc subscript with <c>""</c> read another key.</item>
/// <item>A static for-list word was re-quoted from its command-mode text: <c>for c in grep\ x</c> iterated over
/// <c>grep` x</c> (the backtick kept), and a word that parsed as a PS number lost its spelling (<c>007</c> became <c>7</c>). An assoc compound key kept its backslashes (<c>["k\"j"]=v</c>).</item>
/// </list>
/// </summary>
public class PsQuotingDifferentialTests
{
    private const string Marks = "‘’‚‛“”„";

    [SkippableFact]
    public Task QuoteMarks_InEveryWordContext()
        => AssertOracle.EqualAsync(
            "cd \"$(mktemp -d)\" || exit\n"
            + "q='" + Marks + "'\n"
            + ("echo X; echo 'X'; echo \"X\"; echo \"X$q\"; echo a\\X\n"
            + "x=X; echo \"$x\"; x='X'; echo \"$x\"; x=\"X$q\"; echo \"$x\"\n"
            + "for c in X \"X\" 'X' a\\ X; do echo \"[$c]\"; done\n"
            + "echo \"$(echo X)\" `echo X`; a=(X \"X\" 'X'); echo \"${a[@]}\" ${#a[@]}\n"
            + "case X in X) echo hit;; esac; [[ \"X\" == X ]] && echo eq\n"
            + "printf '%s|' X 'X' \"X\"; echo\n"
            + "echo ${q:-X} \"${u:-X}\" ${u:-'X'}; y=${u:-X}; echo \"$y\"\n"
            + "f() { echo \"X$1\"; }; f X; eval \"echo X\"; eval 'echo X'\n"
            + "echo X > o; cat o; echo X | sed s/X/Y/; echo X | awk '{print \"X\" $0}'; echo X | grep -c X\n"
            + "test \"X\" = X && echo t; declare -A m=([X]=X); echo \"${m[X]}\" ${m[X]}\n"
            + "read -r v <<< \"X\"; echo \"$v\"; export EV='X'; printenv EV; bash -c \"echo X\"; bash -c 'echo X'\n"
            + "[[ \"aXb\" == a*X* ]] && echo m1; case \"aXb\" in *X*) echo m2;; esac\n"
            + "cat <<EOF\nhXd$q\nX@\nEOF\ncat <<'EOF'\nX@\n'@\nEOF\n").Replace("X", "a" + Marks + "b")
            + "cd / ",
            timeout: TimeSpan.FromSeconds(60));

    [SkippableFact]
    public Task DashMarks_DoubleQuotesInExpansions_AndStaticListWords()
        => AssertOracle.EqualAsync(
            "cd \"$(mktemp -d)\" || exit\n"
            + "echo –n x; echo —e; echo –; printf '%s\\n' –e; x=–n; echo \"$x\"\n"
            + "for w in –n x’; do echo \"<$w>\"; done; a=(–n); echo \"${a[0]}\"\n"
            + "echo \"${u:-a\\\"\\\"b}\"; y=\"${u:-a\\\"\\\"b}\"; echo \"$y\"; echo \"${u:-x'\"\"'y}\"; echo \"pre${u:-a\\\"\\\"b}post\"\n"
            + "declare -A m=([\"k\\\"\\\"j\"]=v); echo \"${m[\"k\\\"\\\"j\"]}\"; declare -A n=([\"a\\\"b\"]=1 ['c d']=2); echo \"${!n[@]}\" | tr ' ' '\\n' | sort | tr '\\n' ' '; echo\n"
            + "for c in a\\ b e\\\"f 'x y' \"p q\" grep\\ x; do echo \"[$c]\"; done\n"
            + "for c in grep\\ -c; do printf 'a\\nb\\n' | $c a; done\n"
            + "for i in 007 1.0 1e3 0x10 -5 +3; do printf '%s,' \"$i\"; done; echo\n"
            + "cd / ",
            timeout: TimeSpan.FromSeconds(60));
}
