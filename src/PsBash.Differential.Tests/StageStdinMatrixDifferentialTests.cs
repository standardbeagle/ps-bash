using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// A pipe connects to the WHOLE next stage's stdin, whatever wraps the stage (emitter-strategy.md "Stage stdin"). ps-bash
/// lost the pipe whenever a wrapper sat between the stage's script block and its reader: <c>… | while read l; …; done &gt; f</c>
/// wrote an empty file, and a <c>read</c> / <c>cat</c> in a read loop's body saw no stdin
/// (<c>printf '1\n2\n3\n4\n' | while read a; do read b; echo "$a-$b"; done</c> printed <c>1-</c> <c>2-</c> … instead of
/// <c>1-2</c> <c>3-4</c>). The matrix runs every compound form × every wrapper context with readers inside, byte-diffed
/// against bash. Its structural twin is the Core <c>StageStdinMatrixTests</c>.
/// </summary>
public class StageStdinMatrixDifferentialTests
{
    private static Task Eq(string script) =>
        AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(40));

    private const string Producer = "printf '1\\n2\\n3\\n4\\n5\\n'";

    /// <summary>Three readers that must share one cursor in order: read takes a line, head the next, cat the rest.</summary>
    private const string Readers = "read r; echo \"r=$r\"; head -1; cat";

    /// <summary>Compound forms around <see cref="Readers"/> (<c>{0}</c>). <c>g</c> is a function defined by the context prelude.</summary>
    public static readonly string[] Forms =
    {
        "{{ {0}; }}",
        "( {0} )",
        "while true; do {0}; break; done",
        "until false; do {0}; break; done",
        "for i in 1; do {0}; done",
        "if true; then {0}; fi",
        "case x in x) {0};; esac",
        "while read l; do echo \"l=$l\"; {0}; done",
        "g",
    };

    /// <summary>Wrapper contexts; <c>{0}</c> is the producer piped into the stage.</summary>
    public static readonly string[] Contexts =
    {
        "{0}\necho \"rc=$?\"",
        "{0} | sed 's/^/>/'",
        "cd \"$(mktemp -d)\"\n{0} > out\necho \"rc=$?\"; cat out",
        "cd \"$(mktemp -d)\"\necho old > out\n{0} >> out\ncat out",
        "{0} 2>&1 | sed 's/^/>/'",
        "set -o pipefail\n{0}\necho \"rc=$? ps=${{PIPESTATUS[*]}}\"",
        "set -e\n{0}\necho \"rc=$?\"",
        "run() {{ {0}; }}\nrun\necho \"rc=$?\"",
        "x=$({0})\necho \"[$x]\"",
        "shopt -s lastpipe\n{0}\necho \"rc=$?\"",
    };

    private static string Script(string form, string context, string readers)
    {
        string stage = string.Format(form, readers);
        string prelude = "g() { " + readers + "; }\n";
        // The context's redirect / pipe applies to the stage: it goes after the stage text.
        string pipeline = Producer + " | " + stage;
        return prelude + string.Format(context, pipeline);
    }

    public static IEnumerable<object[]> FormByContext()
    {
        for (int f = 0; f < Forms.Length; f++)
            for (int c = 0; c < Contexts.Length; c++)
                yield return new object[] { f, c };
    }

    [SkippableTheory]
    [MemberData(nameof(FormByContext))]
    public Task Differential_StageReaders_ShareThePipeInEveryWrapper(int form, int context) =>
        Eq(Script(Forms[form], Contexts[context], Readers));

    public static IEnumerable<object[]> FormsOnly()
    {
        for (int f = 0; f < Forms.Length; f++)
            yield return new object[] { f };
    }

    /// <summary>A nested read loop as the reader: it takes every line after the ones its surroundings took.</summary>
    [SkippableTheory]
    [MemberData(nameof(FormsOnly))]
    public Task Differential_NestedReadLoopReader_TakesTheRest(int form) =>
        Eq(Script(Forms[form], Contexts[0], "while read m; do echo \"m=$m\"; done"));

    /// <summary>The reported bugs: a redirected read loop stage, and a reader in a read loop's body.</summary>
    [SkippableTheory]
    [InlineData("cd \"$(mktemp -d)\"\nprintf 'a\\nb\\n' | while read l; do echo \"[$l]\"; done > out.txt\ncat out.txt")]
    [InlineData("cd \"$(mktemp -d)\"\nprintf 'a\\nb\\n' | { while read l; do echo \"[$l]\"; done; } > out.txt\ncat out.txt")]
    [InlineData("cd \"$(mktemp -d)\"\necho z > out.txt\nprintf 'a\\nb\\n' | while read l; do echo \"[$l]\"; done >> out.txt\ncat out.txt")]
    [InlineData("printf 'a\\nb\\n' | while read l; do echo \"[$l]\"; ls /nonexistent-psb; done 2>&1 | sed 's/:.*//'")]
    [InlineData("cd \"$(mktemp -d)\"\nprintf 'a\\nb\\n' | while read l; do echo \"[$l]\"; done > mid.txt | cat\ncat mid.txt")]
    [InlineData("printf 'a\\nb\\n' | while read l; do echo \"[$l]\"; done < /dev/null; echo \"rc=$?\"")]
    [InlineData("printf '1\\n2\\n3\\n4\\n' | while read a; do read b; echo \"$a-$b\"; done")]
    [InlineData("printf '1\\n2\\n3\\n' | while read a; do echo \"a=$a\"; cat; done")]
    [InlineData("printf '1\\n2\\n3\\n' | while read a; do echo \"a=$a\"; head -1; done")]
    [InlineData("printf '1\\n2\\n3\\n4\\n' | while read a; do read b; echo \"$a-$b\"; done | sort -r")]
    public Task Differential_ReportedBugs(string script) => Eq(script);

    /// <summary>A read loop with its own input (`done &lt; f`, a here-string) shares it with the readers in its body.</summary>
    [SkippableTheory]
    [InlineData("cd \"$(mktemp -d)\"\nprintf '1\\n2\\n3\\n4\\n' > in\nwhile read a; do read b; echo \"$a-$b\"; done < in")]
    [InlineData("cd \"$(mktemp -d)\"\nprintf '1\\n2\\n3\\n' > in\nwhile read a; do echo \"a=$a\"; cat; done < in")]
    [InlineData("cd \"$(mktemp -d)\"\nprintf '1\\n2\\n3\\n4\\n' > in\nwhile read a; do read b; echo \"$a-$b\"; done < in > out\ncat out")]
    [InlineData("while read a; do read b; echo \"$a-$b\"; done <<< \"$(printf '1\\n2\\n3\\n4')\"")]
    [InlineData("while read a; do read b; echo \"$a-$b\"; done <<'EOF'\n1\n2\n3\nEOF")]
    [InlineData("f() { while read a; do read b; [ \"$a\" = 3 ] && return 4; echo \"$a-$b\"; done; echo no; }\nprintf '1\\n2\\n3\\n4\\n' | f; echo \"rc=$?\"")]
    public Task Differential_ReadLoopWithItsOwnInput_SharesItWithTheBody(string script) => Eq(script);

    /// <summary>Edge cases: empty / unterminated input, failing producers, errexit, break, an endless producer, IFS.</summary>
    [SkippableTheory]
    [InlineData("printf '' | while read a; do read b; echo \"$a-$b\"; done; echo \"rc=$?\"")]
    [InlineData("printf '1\\n2\\n3\\n' | while read a; do read b; echo \"$a-$b st=$?\"; done")]
    [InlineData("x=$(printf '1\\n2\\n3\\n' | while read a; do read b; echo \"$a$b\"; done); echo \"[$x]\"")]
    [InlineData("printf '1\\n' | { read b; read b; echo \"[$b] $?\"; }")]
    [InlineData("x=5; read x < /dev/null; echo \"[$x] $?\"; REPLY=r; read < /dev/null; echo \"[$REPLY]\"; y=7; z=8; read y z < /dev/null; echo \"[$y][$z]\"")]
    [InlineData("printf '1\\n2\\n3' | while read a; do read b; echo \"$a-$b st=$?\"; done; echo \"rc=$?\"")]
    [InlineData("printf '1\\n2' | while read a; do cat; echo; done; echo \"rc=$?\"")]
    [InlineData("{ printf 'a'; echo b; echo c; } | while read a; do read b; echo \"$a-$b\"; done")]
    [InlineData("set -o pipefail\n{ printf '1\\n2\\n'; exit 3; } | while read a; do read b; echo \"$a-$b\"; done\necho \"rc=$? ps=${PIPESTATUS[*]}\"")]
    [InlineData("{ printf '1\\n2\\n'; exit 3; } | while read a; do read b; echo \"$a-$b\"; done > /dev/null\necho \"rc=$? ps=${PIPESTATUS[*]}\"")]
    [InlineData("set -e\nprintf '1\\n2\\n3\\n4\\n' | while read a; do read b; echo \"$a-$b\"; false; echo no; done\necho \"rc=$?\"")]
    [InlineData("printf '1\\n2\\n3\\n4\\n' | while read a; do read b; echo \"$a-$b\"; break; done; echo \"rc=$?\"")]
    [InlineData("cd \"$(mktemp -d)\"\nyes | while read l; do read m; echo $l$m; break; done > f\necho \"rc=$?\"; cat f")]
    [InlineData("yes | while read l; do head -1; break; done; echo \"rc=$?\"")]
    [InlineData("printf 'a:b\\nc:d\\ne:f\\n' | while IFS=: read x y; do IFS=: read p q; echo \"$x$y|$p$q\"; done")]
    [InlineData("printf ' 1 \\n 2 \\n' | while IFS= read -r a; do IFS= read -r b; echo \"[$a][$b]\"; done")]
    [InlineData("printf 'a\\nb\\nc\\nd\\n' | while read a; do while read b; do echo \"$a$b\"; [ \"$b\" = c ] && break; done; done")]
    [InlineData("printf '1\\n2\\n3\\n' | { read x; while read a; do echo \"$x$a\"; done; }")]
    public Task Differential_EdgeCases(string script) => Eq(script);

    /// <summary>Wait (at most 10 s) until <c>[ -f seen ]</c> holds, then say whether it did.</summary>
    private const string WaitForSeen =
        "i=0; while [ ! -f seen ] && [ $i -lt 100 ]; do sleep 0.1; i=$((i+1)); done; " +
        "if [ -f seen ]; then echo b-after-loop-read-a; else echo b-loop-never-saw-a; fi";

    /// <summary>A redirected read-loop stage whose body reads nothing still streams: the handshake proves it ran per line.</summary>
    [SkippableTheory]
    [InlineData("> out")]
    [InlineData(">> out")]
    [InlineData("2>&1 > out")]
    public Task Differential_RedirectedReadLoopStage_StillStreams(string redirect) => Eq(
        "cd \"$(mktemp -d)\"\n" +
        "{ echo a; " + WaitForSeen + "; } | while read -r l; do echo \"got $l\"; touch seen; done " + redirect + "\n" +
        "echo rc=$?; cat out");
}
