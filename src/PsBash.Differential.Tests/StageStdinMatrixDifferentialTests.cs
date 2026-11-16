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

    // ───────────────────── Reader kinds the emitter cannot see into ──────────────────────
    // The third axis (emitter-strategy.md "Stage stdin", rule 4): a reader behind a function call, a sourced file, eval
    // text, a background job, a program or a run-time command name reads the stdin of the construct around it exactly as
    // a visible `read` does. ps-bash decided by LOOKING for a reader, so each of these read nothing:
    // `r() { read b; echo "b=$b"; }; printf '1\n2\n' | while read a; do r; echo "a=$a"; done` printed `b=` `a=1` `b=` `a=2`.

    /// <summary>Everything the reader kinds need: a sourced reader, a library function of another unit, a data file.</summary>
    private const string OpaquePrelude =
        "cd \"$(mktemp -d)\"\n" +
        "printf 'read b; echo \"b=$b\"\\n' > r.sh\n" +
        "printf 'libfn() { read b; echo \"lib=$b\"; }\\n' > lib.sh\n" +
        "printf '1\\n2\\n3\\n4\\n5\\n' > in\n" +
        ". ./lib.sh\n" +
        "rd() { read b; echo \"b=$b\"; }\n" +
        "outer() { rd; }\n" +
        "c='read b; echo \"b=$b\"'\n" +
        "cmd=rd\n";

    /// <summary>One reader per kind; each takes input the emitter cannot see it take.</summary>
    public static readonly string[] OpaqueReaders =
    {
        "rd",                                    // a function
        "outer",                                 // a function called from a function
        "libfn",                                 // a function of another unit (defined by a sourced file)
        "source ./r.sh",
        ". ./r.sh",
        "eval \"$c\"",
        "cat & wait",                            // a background job inherits the stdin
        "command cat",                           // the stand-in for a program: it takes the rest
        "read -u 0 b; echo \"b=$b\"",
        "read -n 1 ch; echo \"ch=$ch\"; read rest; echo \"rest=$rest\"",
        "$cmd",                                  // a run-time command name
        "x=$(rd); echo \"x=$x\"",                // inside a command substitution
        "echo \"[$(outer)]\"",
        "X=1 rd",                                // an env prefix
        "if rd; then echo ok; fi",
        "rd | sed 's/^/>/'",                     // the first stage of a pipeline in the body
    };

    /// <summary>Where the reader sits; <c>{0}</c> is the reader. <c>g</c> is a function whose body is the reader.</summary>
    public static readonly string[] OpaqueForms =
    {
        "while read a; do {0}; echo \"a=$a\"; done",
        "{{ {0}; {0}; }}",
        "( {0} )",
        "if true; then {0}; fi",
        "for i in 1 2; do {0}; done",
        "g",
    };

    private static string OpaqueScript(int form, int reader, bool fromFile)
    {
        string body = OpaqueReaders[reader];
        string stage = string.Format(OpaqueForms[form], body);
        return OpaquePrelude + "g() { " + body + "; }\n"
            + (fromFile ? stage + " < in" : Producer + " | " + stage)
            + "\necho \"rc=$?\"";
    }

    public static IEnumerable<object[]> OpaqueFormByReader()
    {
        for (int f = 0; f < OpaqueForms.Length; f++)
            for (int r = 0; r < OpaqueReaders.Length; r++)
                yield return new object[] { f, r };
    }

    /// <summary>Every reader kind, in every compound form, as a pipe stage.</summary>
    [SkippableTheory]
    [MemberData(nameof(OpaqueFormByReader))]
    public Task Differential_OpaqueReader_ReadsThePipe(int form, int reader) =>
        Eq(OpaqueScript(form, reader, fromFile: false));

    public static IEnumerable<object[]> OpaqueReadersOnly()
    {
        for (int r = 0; r < OpaqueReaders.Length; r++)
            yield return new object[] { r };
    }

    /// <summary>The same readers in a read loop over its own <c>&lt; file</c>, and in a redirected group.</summary>
    [SkippableTheory]
    [MemberData(nameof(OpaqueReadersOnly))]
    public Task Differential_OpaqueReader_ReadsTheLoopsOwnFile(int reader) =>
        Eq(OpaqueScript(0, reader, fromFile: true));

    [SkippableTheory]
    [MemberData(nameof(OpaqueReadersOnly))]
    public Task Differential_OpaqueReader_ReadsTheGroupsOwnFile(int reader) =>
        Eq(OpaqueScript(1, reader, fromFile: true));

    /// <summary>The cases as reported, and their neighbours.</summary>
    [SkippableTheory]
    // 1. a function that reads, called in a read loop's body
    [InlineData("r() { read b; echo \"b=$b\"; }; printf '1\\n2\\n' | while read a; do r; echo \"a=$a\"; done")]
    [InlineData("r() { read b; echo \"b=$b\"; }; seq 4 | while read a; do r; echo \"a=$a\"; done")]
    // 2. a sourced file as a pipe stage
    [InlineData("cd \"$(mktemp -d)\"\nprintf 'read x; echo \"x=$x\"\\n' > r.sh; echo hi | source ./r.sh")]
    [InlineData("cd \"$(mktemp -d)\"\nprintf 'read x; echo \"x=$x\"\\n' > r.sh; echo hi | . ./r.sh; echo \"rc=$?\"")]
    // 3. a background job inherits the stage's stdin
    [InlineData("echo hi | { cat & wait; }")]
    [InlineData("printf '1\\n2\\n3\\n' | while read a; do cat & wait; echo \"a=$a\"; done")]
    [InlineData("cd \"$(mktemp -d)\"\nprintf '1\\n2\\n' > two.txt; { cat & wait; } < two.txt")]
    // 4. read -u N in a loop condition, with the descriptor opened on the loop
    [InlineData("cd \"$(mktemp -d)\"\nprintf 'p\\nq\\n' > in.txt; while read -u 3 a; do echo \"a=$a\"; done 3< in.txt")]
    [InlineData("cd \"$(mktemp -d)\"\nprintf 'p\\nq\\n' > in.txt; printf 'x\\ny\\n' | while read -u 3 a; do read b; echo \"a=$a b=$b\"; done 3< in.txt")]
    // 5. read -n leaves the unterminated rest unterminated
    [InlineData("printf 'abc' | { read -n 2 x; echo \"x=$x\"; cat; echo; } | od -c")]
    [InlineData("printf 'abc\\ndef\\n' | { read -n 2 x; echo \"x=$x\"; cat; } | od -c")]
    [InlineData("printf 'abcdef' | { read -N 2 x; echo \"x=$x\"; cat; } | od -c")]
    // a function that only calls a reader, as a pipe stage; a program in a function
    [InlineData("inner() { read b; echo \"b=$b\"; }; outer() { inner; }; echo hi | outer")]
    [InlineData("nf() { command cat; }; echo hi | nf")]
    // a job at the top of a script reads /dev/null, not the pipe of an unrelated command
    [InlineData("echo hi | cat & wait")]
    // read flags that take a value are not loop variables
    [InlineData("printf 'a b\\nc d\\n' | while read -a arr; do echo \"${arr[1]}\"; done")]
    [InlineData("printf 'ab\\n' | while read -n 1 c; do echo \"[$c]\"; done")]
    // proven to read nothing: the loop keeps every line
    [InlineData("fr() { echo \"fr $1\"; }; seq 3 | while read a; do fr \"$a\"; done")]
    [InlineData("seq 3 | while read a; do command -v ls > /dev/null; echo \"a=$a\"; done")]
    [InlineData("rd() { read b; echo \"b=$b\"; }; seq 3 | while read a; do rd < /dev/null; echo \"a=$a\"; done")]
    public Task Differential_OpaqueReaders_ReportedCases(string script) => Eq(script);

    // ───────────────────── set -e in every read-loop form ──────────────────────
    // A read loop that is a pipe stage is a subshell that inherits -e: it ends at its first failing body command, with
    // that status. ps-bash lifted the pipeline's errexit suppression per stage by DECREMENTING a counter; with a compound
    // producer the two stages run interleaved, the counter went to -1 (read as "suppressed"), and
    // `set -e; { echo three; echo four; } | while read line; do echo "[$line]"; false; done` printed `[four]` too.

    /// <summary>Every read-loop form, with a body that fails after its first line. <c>{P}</c> is the producer.</summary>
    public static readonly string[] ErrexitLoopForms =
    {
        "{P} | while read a; do echo \"[$a]\"; false; echo no; done",                       // streamed per record
        "{P} | while read a; do read b; echo \"[$a$b]\"; false; echo no; done",             // shared cursor: the body reads
        "{P} | while read a; do echo \"[$a]\"; no_such_cmd_psb 2>/dev/null; echo no; done", // shared cursor: opaque body
        "{P} | while read a; do echo \"[$a]\"; false; echo no; done > out",                 // redirected stage
        "{P} | while read a; do echo \"[$a]\"; false; echo no; done | cat",                 // middle stage
        "{P} | { while read a; do echo \"[$a]\"; false; echo no; done; echo tail; }",       // in a group
        "{P} | ( while read a; do echo \"[$a]\"; false; echo no; done; echo tail )",        // in a subshell
        "{P} | while read -u 0 a; do echo \"[$a]\"; false; echo no; done",                  // read run by the loop itself
        "lf() { while read a; do echo \"[$a]\"; false; echo no; done; }; {P} | lf",         // in a function stage
        "shopt -s lastpipe\n{P} | while read a; do echo \"[$a]\"; false; echo no; done",    // lastpipe: the shell itself
        "while read a; do echo \"[$a]\"; false; echo no; done < in",                        // own file, inline records
        "while read a; do read b; echo \"[$a$b]\"; false; echo no; done < in",              // own file, shared cursor
    };

    /// <summary>A simple producer, and a compound one (a stage with its own errexit lift, interleaved with the loop's).</summary>
    public static readonly string[] ErrexitProducers =
    {
        "printf '1\\n2\\n3\\n4\\n'",
        "{ echo 1; echo 2; echo 3; echo 4; }",
    };

    public static IEnumerable<object[]> ErrexitFormByProducer()
    {
        for (int f = 0; f < ErrexitLoopForms.Length; f++)
            for (int p = 0; p < ErrexitProducers.Length; p++)
            {
                if (p > 0 && !ErrexitLoopForms[f].Contains("{P}", StringComparison.Ordinal)) continue;
                yield return new object[] { f, p, false };
                yield return new object[] { f, p, true };
            }
    }

    internal static string ErrexitScript(int form, int producer, bool topLevel)
    {
        string loop = ErrexitLoopForms[form].Replace("{P}", ErrexitProducers[producer], StringComparison.Ordinal);
        const string setup = "cd \"$(mktemp -d)\"\nprintf '1\\n2\\n3\\n4\\n' > in\n";
        return topLevel
            ? setup + "set -e\n" + loop + "\necho after"
            : setup + "( set -e\n" + loop + "\necho after ); echo \"rc=$?\"; cat out 2>/dev/null; echo end";
    }

    /// <summary>Under <c>set -e</c> the loop ends at its failing body command, in a subshell and at the top of the script.</summary>
    [SkippableTheory]
    [MemberData(nameof(ErrexitFormByProducer))]
    public Task Differential_ReadLoopUnderErrexit_EndsAtTheFailingBodyCommand(int form, int producer, bool topLevel) =>
        Eq(ErrexitScript(form, producer, topLevel));

    /// <summary>The case as reported (oils errexit.spec.sh "pipeline process respects errexit"), and its neighbours.</summary>
    [SkippableTheory]
    [InlineData("set -o errexit\n{ echo one; false; echo two; } | cat\n{ echo three; echo four; } | while read line; do\n  echo \"[$line]\"\n  false\ndone\necho four")]
    [InlineData("set -e\nif { echo 1; echo 2; } | while read a; do echo \"[$a]\"; false; echo no; done; then echo yes; fi; echo \"rc=$?\"")]
    [InlineData("set -e\nf() { { echo 1; echo 2; } | while read a; do echo \"[$a]\"; false; echo no; done; echo f-end; }\nif f; then echo yes; fi; echo \"rc=$?\"")]
    [InlineData("set -e\n{ echo 1; echo 2; } | while read a; do echo \"[$a]\"; false || echo or; done; echo \"rc=$?\"")]
    [InlineData("set -e\n{ echo 1; { echo x; false; echo y; } | cat; echo 2; } | while read a; do echo \"[$a]\"; done; echo \"rc=$?\"")]
    public Task Differential_ReadLoopUnderErrexit_ReportedCases(string script) => Eq(script);

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
