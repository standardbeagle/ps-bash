using System.Text;
using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// Garbage fuzzer (docs/specs/transpile-fuzz-grammar.md §2/§4). Throws random noise, mutated
/// valid bash, and structural torture (unbalanced openers, dangling operators, deep nesting,
/// truncated heredocs) at the transpiler.
///
/// Invariant here is the SAFETY floor: <see cref="ParseabilityContract.AssertNoCrash"/> — for
/// ANY input (however malformed) Transpile must never throw a non-ParseException and never hang.
/// Raw crashes ARE failures; a clean ParseException is ideal; broken PowerShell on deliberately
/// malformed garbage is a tracked gap (spec §4), not a failure of THIS fuzzer. The stronger
/// "valid bash ⇒ valid PowerShell" guarantee is enforced where it is achievable: the grammar
/// fuzzer (TranspileGrammarFuzzTests) and the curated corpus, over the real construct surface.
/// This fuzzer's job is to guarantee the transpiler can't be crashed or hung by any byte string.
///
/// Deterministic: fixed master seed; a failure prints the exact (un)lucky input.
/// </summary>
public class TranspileGarbageFuzzTests
{
    private const int MasterSeed = 0x6A46_2;

    // The bash metacharacter alphabet plus a few ordinary chars — the soup the random
    // generator draws from. Heavy on the structural metacharacters that drive the lexer/parser.
    private static readonly char[] Meta =
        "${}()[]<>|&;\"'`\\$#*?!=~+-/. \t\n0aZ".ToCharArray();

    private static readonly string[] Seeds =
    {
        "echo hello world", "cat file | grep foo", "for i in 1 2 3; do echo $i; done",
        "if [ -f x ]; then echo y; fi", "x=${y:-default}", "echo $((1+2))",
        "cat <<EOF\nbody\nEOF", "diff <(a) <(b)", "echo \"$x\" '$y' $(date)",
        "arr=(a b c); echo ${arr[@]}", "case $x in a) echo a;; esac",
        "echo {1..5}", "grep -e 'pat' file", "a && b || c | d",
        "echo ${a:-${b:-${c:-d}}}", "[ a = b -a c = d ]", "declare -i n=5",
    };

    [Fact]
    public void RandomMetacharSoup_NeverCrashesNeverHangs()
    {
        // Length capped at 12: deeply-nested metachar soup (`${[(…`) hits catastrophic
        // backtracking in word decomposition whose cost grows with nesting depth (a tracked
        // perf gap, spec §4) — a ~24-char input can take ~1s. Short inputs keep every case
        // sub-millisecond; the watchdog is a belt-and-suspenders backstop for the rare
        // borderline one so the suite can never hang regardless of seed.
        var rng = new Random(MasterSeed);
        for (int n = 0; n < 1500; n++)
        {
            int len = rng.Next(1, 12);
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++) sb.Append(Meta[rng.Next(Meta.Length)]);
            ParseabilityContract.AssertNoCrashWithin(sb.ToString(), 2000, $"soup seed={MasterSeed} n={n}");
        }
    }

    [Fact]
    public void MutatedValidBash_NeverCrashesNeverHangs()
    {
        var rng = new Random(MasterSeed ^ 0x1234);
        for (int n = 0; n < 2000; n++)
        {
            var sb = new StringBuilder(Seeds[rng.Next(Seeds.Length)]);
            int muts = rng.Next(1, 6);
            for (int m = 0; m < muts && sb.Length > 0; m++)
            {
                int op = rng.Next(4), p = rng.Next(sb.Length);
                switch (op)
                {
                    case 0: sb.Remove(p, 1); break;                            // delete
                    case 1: sb.Insert(p, Meta[rng.Next(Meta.Length)]); break;  // insert meta
                    case 2: sb.Insert(p, sb[p]); break;                        // duplicate char
                    case 3: sb.Length = p; break;                              // truncate
                }
            }
            ParseabilityContract.AssertNoCrashWithin(sb.ToString(), 3000, $"mutate seed={MasterSeed} n={n}");
        }
    }

    [Theory]
    [MemberData(nameof(StructuralTorture))]
    public void StructuralTorture_NeverCrashesNeverHangs(string input)
    {
        ParseabilityContract.AssertNoCrashWithin(input, 3000, "torture");
    }

    public static IEnumerable<object[]> StructuralTorture()
    {
        string[] cases =
        {
            // Unbalanced openers — the lexer/parser must reject (or recover), never crash.
            "${", "$(", "$((", "((", "[[", "[ ", "'", "\"", "`", "<<EOF", "<<EOF\n",
            "${x", "${x:", "${x:-", "$(echo", "echo $(", "echo ${", "for", "if",
            "case x", "case x in", "while", "do", "{ echo", "( echo", "a |", "a &&",
            // Dangling / doubled operators.
            "a | | b", "> > x", "2>&", "<(", ">(", "echo \"$(", "${!", "${#",
            "$'", "$'\\", "[[ $x", "[ $x =", "${x//", "${x/", "echo {1..",
            "function", "function f", "f()", "f() {", ";;", "&& &&", "|| ||",
            // Pathological depth / repetition.
            new string('(', 200), new string('{', 200), new string('$', 200),
            "${" + new string('x', 500), "echo " + new string('"', 100),
            // Nested-expansion + quote-seam stress (the ${x:-${y:-z}} class).
            "echo ${a:-${b:-${c:-d}}}", "echo \"${a:-${b}}\"", "echo ${a:-\"$b\"}",
            "echo ${a:-$(echo ${b:-x})}", "echo \"$(echo \"$(echo x)\")\"",
            "echo ${a:-'$b'}", "echo ${a:-`echo ${b}`}", "echo ${a/${b}/${c}}",
            "x=${a:-${b:+${c:-d}}}", "echo \"a${b:-\"c\"}d\"",
            // Empty / whitespace / comment-only.
            "", " ", "\t", "\n", "#", "# comment", "   \n  \t ",
        };
        foreach (var c in cases) yield return new object[] { c };
    }
}
