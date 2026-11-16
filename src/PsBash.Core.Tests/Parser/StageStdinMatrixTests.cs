using System.Collections.Generic;
using Xunit;
using PsBash.Core.Parser;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// The stage-stdin model (emitter-strategy.md "Stage stdin"): a pipe stage's records reach only <c>$input</c> of the
/// stage's OWN script block, so that block must hand them to the shared stdin cursor at once, before any wrapper (a
/// redirect's <c>. { }</c>, a guard, a loop, a function, a <c>$( )</c>) could hide them. Five bugs of one family came from a
/// wrapper between the stage block and the reader (a quoted <c>;</c> wrapping the stage, a dropped here-doc, a collected
/// read loop, a redirected read loop with an empty <c>$input</c>, a body <c>read</c> that saw no stdin).
/// <para>
/// This matrix pins every compound form × every wrapper context to that one primitive. The behaviour itself is checked
/// against bash in <c>StageStdinMatrixDifferentialTests</c>; this is the structural half (ps-bash-specific: the emitted
/// shape), so a new wrapper that sneaks between the stage and its stdin fails here for every form at once.
/// </para>
/// </summary>
public class StageStdinMatrixTests
{
    private static string T(string bash) => PsEmitter.Transpile(bash)!;

    /// <summary>The opening of <see cref="PsBuild.StdinScope"/> (and <see cref="PsBuild.StdinScopeLazy"/>): what a stage block must start with.</summary>
    private static readonly string ScopeOpen = PsBuild.StdinScope("\u0001").Split('\u0001')[0];

    /// <summary>Compound forms, each with a stdin reader (<c>cat</c>) inside.</summary>
    public static readonly string[] Forms =
    {
        "{ cat; }",
        "( cat )",
        "while true; do cat; break; done",
        "until false; do cat; break; done",
        "for i in 1; do cat; done",
        "for ((i=0; i<1; i++)); do cat; done",
        "if true; then cat; fi",
        "case x in x) cat;; esac",
        "while read l; do echo \"$l\"; cat; done",
        "[[ $(cat) == a ]]",
        "(( $(cat) > 0 ))",
    };

    /// <summary>The contexts a stage runs in; <c>{0}</c> is the stage.</summary>
    public static readonly string[] Contexts =
    {
        "printf 'a\\nb\\n' | {0}",
        "printf 'a\\nb\\n' | {0} | sort",
        "printf 'a\\nb\\n' | {0} > out",
        "printf 'a\\nb\\n' | {0} >> out",
        "printf 'a\\nb\\n' | {0} 2>&1 | sort",
        "set -o pipefail\nprintf 'a\\nb\\n' | {0}\necho \"${{PIPESTATUS[*]}}\"",
        "set -e\nprintf 'a\\nb\\n' | {0}",
        "f() {{ printf 'a\\nb\\n' | {0}; }}\nf",
        "x=$(printf 'a\\nb\\n' | {0})",
        "shopt -s lastpipe\nprintf 'a\\nb\\n' | {0}",
    };

    public static IEnumerable<object[]> FormByContext()
    {
        foreach (var form in Forms)
            foreach (var context in Contexts)
                yield return new object[] { form, context };
    }

    [Theory]
    [MemberData(nameof(FormByContext))]
    public void Transpile_CompoundStageWithAReader_TheStageBlockOpensTheSharedStdinFirst(string form, string context)
    {
        var bash = string.Format(context, form);
        var ps = T(bash);

        // The stage's own block turns its input into the shared cursor before anything else runs in it.
        Assert.True(ps.Contains("& { " + ScopeOpen, System.StringComparison.Ordinal),
            $"`{bash}`: the stage block does not open with the stage stdin scope:\n{ps}");
        // The reader inside takes from that cursor.
        Assert.Contains(PsBuild.StdinFeed, ps);
        AssertEveryInputIsAPrimitive(bash, ps);
    }

    /// <summary>A <c>while read</c> stage whose body reads nothing streams per record behind the gate, in every context but lastpipe.</summary>
    [Theory]
    [MemberData(nameof(ContextsOnly))]
    public void Transpile_ReadLoopStageWithNoReader_StreamsOrReadsTheScopeLineByLine(string context)
    {
        var bash = string.Format(context, "while read l; do echo \"$l\"; done");
        var ps = T(bash);

        if (bash.StartsWith("shopt -s lastpipe", System.StringComparison.Ordinal))
        {
            Assert.DoesNotContain(PsBuild.PipeGate, ps);
            Assert.Contains("& { " + ScopeOpen, ps);
            Assert.Contains(PsBuild.SharedStdinNextLine, ps);
        }
        else
        {
            Assert.Contains(PsBuild.PipeGate + " | ", ps);
            Assert.Contains(" in @($_))", ps);
        }
        AssertEveryInputIsAPrimitive(bash, ps);
    }

    public static IEnumerable<object[]> ContextsOnly()
    {
        foreach (var context in Contexts)
            yield return new object[] { context };
    }

    /// <summary>
    /// The read loop's own lines come from the same cursor its body's readers advance (bug e): one line per pass, never a
    /// whole record, and never <c>$input</c>.
    /// </summary>
    [Theory]
    [InlineData("printf '1\\n2\\n' | while read a; do read b; echo \"$a-$b\"; done")]
    [InlineData("printf '1\\n2\\n' | while read a; do echo \"$a\"; cat; done > out")]
    [InlineData("printf '1\\n2\\n' | while read a; do head -1; done 2>&1 | sort")]
    [InlineData("printf '1\\n2\\n' | while read a; do while read b; do echo $b; done; done")]
    [InlineData("while read a; do read b; done < f")]
    [InlineData("while read a; do read b; done <<< \"$s\"")]
    [InlineData("while read a; do cat; done < f > out")]
    [InlineData("{ while read a; do echo $a; done; } < f")]
    public void Transpile_ReadLoopWhoseBodyReads_TakesItsLinesFromTheSharedCursor(string bash)
    {
        var ps = T(bash);

        Assert.Contains(PsBuild.SharedStdinNextLine, ps);
        Assert.DoesNotContain(PsBuild.PipeGate, ps);
        AssertEveryInputIsAPrimitive(bash, ps);
    }

    // ───────────────────── Reader kinds the emitter cannot see into ──────────────────────
    // The third axis (emitter-strategy.md "Stage stdin", rule 4). Whether a body reads the stage's stdin was decided by
    // LOOKING for a reader in its emitted text, so a reader behind a function call, a sourced file, eval text, a
    // background job or a program read nothing: `r() { read b; }; printf '1\n2\n' | while read a; do r; done` streamed
    // per record and r's read saw no stdin. The question is now "is the body PROVEN to read nothing?"
    // (PsEmitter.ProbeStdinScope), and every kind below must answer "no" in every construct that asks it.

    /// <summary>Body commands that may consume the stdin they inherit although no reader shows in their text.</summary>
    public static readonly string[] OpaqueReaders =
    {
        "other_unit_fn",                         // a function of another unit (a sourced file, an earlier command line)
        "other_unit_fn \"$l\" x",
        "unit_reader",                           // a function of this unit whose body reads (see ReaderPrelude)
        "unit_outer",                            // ... and one that only calls it
        "source ./r.sh",
        ". ./r.sh",
        "eval \"$c\"",
        "eval 'read b'",
        "cat & wait",                            // a background job inherits the stdin
        "unit_reader & wait",
        "command cat",                           // a program
        "ssh host true",
        "python3 x.py",
        "/usr/bin/sort",
        "read -u 0 b",
        "read -n 1 c",
        "read -r b",
        "mapfile -t arr",
        "$cmd",                                  // a run-time command name
        "\"$tool\" --flag",
        "X=1 other_unit_fn",                     // an env prefix (a statement list: never fed, still a reader)
        "x=$(other_unit_fn)",                    // inside a command substitution
        "echo \"$(unit_reader)\"",
        "bash -c 'read z'",
        "sh ./s.sh",
        "env other_unit_fn",
        "timeout 5 other_unit_fn",
        "xargs echo",
        "if other_unit_fn; then :; fi",
        "other_unit_fn && :",
        "! other_unit_fn",
        "other_unit_fn | cat",                   // the first stage of a pipeline reads the stdin
        "( other_unit_fn )",
        "{ other_unit_fn; } 2>&1",
        "while other_unit_fn; do break; done",
        "case $(other_unit_fn) in *) :;; esac",
        "for i in $(other_unit_fn); do :; done",
        "other_unit_fn > out",
        "other_unit_fn 2>/dev/null",
        "tar xf -",
    };

    private const string ReaderPrelude = "unit_reader() { read b; echo \"$b\"; }\nunit_outer() { unit_reader; }\n";

    public static IEnumerable<object[]> OpaqueReadersOnly()
    {
        foreach (var reader in OpaqueReaders)
            yield return new object[] { reader };
    }

    /// <summary>A read-loop pipe stage whose body MAY read never streams per record: the stage opens the shared stdin and the loop takes its lines from the cursor.</summary>
    [Theory]
    [MemberData(nameof(OpaqueReadersOnly))]
    public void Transpile_ReadLoopStageWithAnOpaqueReader_ReadsTheSharedCursor(string reader)
    {
        var bash = ReaderPrelude + "printf 'a\\nb\\n' | while read l; do " + reader + "; echo \"$l\"; done";
        var ps = T(bash);

        Assert.False(ps.Contains(PsBuild.PipeGate, System.StringComparison.Ordinal),
            $"`{reader}` may read the loop's stdin, but the loop streams per record (its reader would see none of it):\n{ps}");
        Assert.Contains("& { " + ScopeOpen, ps);
        Assert.Contains(PsBuild.SharedStdinNextLine, ps);
        AssertEveryInputIsAPrimitive(bash, ps);
    }

    /// <summary>The same for a loop over its own <c>&lt; file</c>: the file becomes the shared stdin instead of being iterated inline.</summary>
    [Theory]
    [MemberData(nameof(OpaqueReadersOnly))]
    public void Transpile_ReadLoopOverAFileWithAnOpaqueReader_ReadsTheSharedCursor(string reader)
    {
        var ps = T(ReaderPrelude + "while read l; do " + reader + "; echo \"$l\"; done < in.txt");

        Assert.True(ps.Contains(PsBuild.SharedStdinNextLine, System.StringComparison.Ordinal),
            $"`{reader}` may read the loop's input, but the loop iterates the file's records inline:\n{ps}");
    }

    /// <summary>A function whose body MAY read takes the pipe it is called with as its stdin.</summary>
    [Theory]
    [MemberData(nameof(OpaqueReadersOnly))]
    public void Transpile_FunctionWithAnOpaqueReader_TakesThePipeItIsCalledWith(string reader)
    {
        var ps = T(ReaderPrelude + "probe() { " + reader + "; }\necho x | probe");

        // The prelude's functions carry the scope too: look at `probe` itself.
        string probe = ps.Substring(ps.IndexOf("function probe ", System.StringComparison.Ordinal));
        Assert.True(probe.Contains(PsBuild.FunctionStdinScope("\u0001").Split('\u0001')[0], System.StringComparison.Ordinal),
            $"`{reader}` may read the function's stdin, but `echo x | probe` hands it none:\n{probe}");
    }

    /// <summary>A script whose only reader is opaque still asks the launcher for its stdin.</summary>
    [Theory]
    [MemberData(nameof(OpaqueReadersOnly))]
    public void Transpile_ScriptWithAnOpaqueReader_AsksForTheLauncherStdin(string reader)
    {
        var ps = PsBash.Core.Transpiler.BashTranspiler.TranspileWithLauncherStdin(ReaderPrelude + reader);

        Assert.True(ps is not null, $"`{reader}` may read the script's stdin, but the launcher would forward none.");
    }

    /// <summary>
    /// The other direction: bodies PROVEN to read nothing keep the per-record streaming stage (ddec2a10), so the proof
    /// is not simply "always no". A builtin, a file operand, a command with a stdin of its own, a lookup, a job that reads
    /// nothing, and a function of this unit whose one definition reads nothing.
    /// </summary>
    [Theory]
    [InlineData("echo \"$l\"")]
    [InlineData("printf '%s\\n' \"$l\"; touch seen")]
    [InlineData("n=$((n+1)); [ $n -ge 3 ] && break")]
    [InlineData("cat \"$l\"")]
    [InlineData("grep -q x \"$l\" && echo hit")]
    [InlineData("other_unit_fn < /dev/null")]
    [InlineData("ssh host true < /dev/null")]
    [InlineData("other_unit_fn <<< \"$l\"")]
    [InlineData("command -v ls > /dev/null")]
    [InlineData("sleep 1 & wait")]
    [InlineData("unit_free \"$l\"")]
    [InlineData("unit_free_outer")]
    [InlineData("x=$(unit_free); echo \"$x\"")]
    [InlineData("printf 'q\\n' | other_unit_fn")]
    [InlineData("cd /; pwd")]
    public void Transpile_ReadLoopStageProvenToReadNothing_StillStreams(string body)
    {
        var ps = T("unit_free() { echo \"f $1\"; }\nunit_free_outer() { unit_free x; }\n"
            + "printf 'a\\nb\\n' | while read l; do " + body + "; done");

        Assert.True(ps.Contains(PsBuild.PipeGate + " | ", System.StringComparison.Ordinal),
            $"`{body}` reads no stdin, but the loop no longer streams per record:\n{ps}");
    }

    /// <summary>
    /// A function is trusted to read nothing only when this unit defines it once, before the call, with a body proven to
    /// read nothing: a second definition, a later one, or a recursive one may read.
    /// </summary>
    [Theory]
    [InlineData("f() { echo a; }\nf() { read b; }\nprintf 'a\\n' | while read l; do f; done")]
    [InlineData("f() { echo a; }\nmain() { printf 'a\\n' | while read l; do f; done; }\nf() { read b; }\nmain")]
    [InlineData("main() { printf 'a\\n' | while read l; do f; done; }\nf() { echo a; }\nmain")]
    [InlineData("f() { [ -n \"$1\" ] && f; }\nprintf 'a\\n' | while read l; do f; done")]
    [InlineData("f() { g; }\nprintf 'a\\n' | while read l; do f; done")]
    public void Transpile_ReadLoopCallingAFunctionNotProvenFree_DoesNotStream(string bash)
    {
        var ps = T(bash);

        Assert.DoesNotContain(PsBuild.PipeGate, ps);
        Assert.Contains(PsBuild.SharedStdinNextLine, ps);
    }

    /// <summary>
    /// <c>read</c> flags that take a value (<c>-u FD</c>, <c>-n N</c>, <c>-t SECS</c>, <c>-a ARR</c>, …) are not the plain
    /// line loop: the value was taken for a variable name (<c>while read -u 3 a</c> read stdin into <c>$3</c>).
    /// </summary>
    [Theory]
    [InlineData("while read -u 3 a; do echo \"$a\"; done 3< in.txt")]
    [InlineData("while read -n 1 c; do echo \"$c\"; done < in.txt")]
    [InlineData("while read -N 2 c; do echo \"$c\"; done < in.txt")]
    [InlineData("while read -t 5 line; do echo \"$line\"; done < in.txt")]
    [InlineData("while read -ra parts; do echo \"${parts[0]}\"; done < in.txt")]
    [InlineData("printf 'a\\n' | while read -u 0 a; do echo \"$a\"; done")]
    public void Transpile_WhileReadWithAValueFlag_RunsReadItself(string bash)
    {
        var ps = T(bash);

        Assert.Contains("Invoke-BashRead", ps);
        Assert.DoesNotContain(PsBuild.PipeGate, ps);
    }

    /// <summary><c>… | source f</c> / <c>… | . f</c>: the stage opens the shared stdin the file's commands read, as <c>… | eval</c> does.</summary>
    [Theory]
    [InlineData("echo hi | source ./r.sh")]
    [InlineData("echo hi | . ./r.sh")]
    [InlineData("echo hi | eval \"$c\"")]
    public void Transpile_SourceOrEvalAsAPipeStage_OpensTheSharedStdin(string bash)
    {
        var ps = T(bash);

        Assert.Contains("& { " + ScopeOpen, ps);
        AssertEveryInputIsAPrimitive(bash, ps);
    }

    /// <summary>Every read-loop form as a pipe stage, with a body that fails; <c>{P}</c> is the producer.</summary>
    public static readonly string[] ErrexitLoopForms =
    {
        "{P} | while read a; do echo \"[$a]\"; false; echo no; done",
        "{P} | while read a; do read b; echo \"[$a$b]\"; false; echo no; done",
        "{P} | while read a; do echo \"[$a]\"; no_such_cmd_psb; echo no; done",
        "{P} | while read a; do echo \"[$a]\"; false; echo no; done > out",
        "{P} | while read a; do echo \"[$a]\"; false; echo no; done | cat",
        "{P} | { while read a; do echo \"[$a]\"; false; echo no; done; echo tail; }",
        "{P} | ( while read a; do echo \"[$a]\"; false; echo no; done; echo tail )",
        "{P} | while read -u 0 a; do echo \"[$a]\"; false; echo no; done",
        "lf() { while read a; do echo \"[$a]\"; false; echo no; done; }; {P} | lf",
        "shopt -s lastpipe\n{P} | while read a; do echo \"[$a]\"; false; echo no; done",
    };

    public static IEnumerable<object[]> ErrexitFormByProducer()
    {
        foreach (var form in ErrexitLoopForms)
            foreach (var producer in new[] { "printf '1\\n2\\n'", "{ echo 1; echo 2; }" })
                yield return new object[] { form.Replace("{P}", producer) };
    }

    /// <summary>
    /// Under <c>set -e</c> a read-loop stage inherits <c>-e</c>: it lifts the pipeline's errexit suppression through
    /// <see cref="PsBuild.ErrexitLifted"/>, which sets the counter back to its value before the pipeline. Stages run
    /// interleaved, so a per-stage decrement took it below zero (read as "suppressed") and the loop ran past a failing
    /// command (oils errexit.spec.sh "pipeline process respects errexit"). Behaviour: StageStdinMatrixDifferentialTests.
    /// </summary>
    [Theory]
    [MemberData(nameof(ErrexitFormByProducer))]
    public void Transpile_ReadLoopStageUnderErrexit_LiftsTheSuppressionToItsValueBeforeThePipeline(string loop)
    {
        var ps = T("set -e\n" + loop + "\necho after");

        const string mark = "$__psbash_eb0";
        Assert.Contains(PsBuild.ErrexitSuppressedFrom(mark, "\u0001").Split('\u0001')[0], ps);
        Assert.Contains(PsBuild.ErrexitLifted("\u0001", mark).Split('\u0001')[0], ps);
        Assert.DoesNotContain(PsBuild.ErrexitSuppressVar + "--; try", ps);
    }

    /// <summary>Simple stages in a script block forward or drain the block's input through the primitives.</summary>
    [Theory]
    [InlineData("echo a | X=1 cat")]                 // env prefix: forwarded
    [InlineData("echo a | cat < f")]                 // own stdin: drained
    [InlineData("echo a | cat <<< b")]
    [InlineData("echo a | seq 2")]                   // never reads stdin: drained
    [InlineData("echo a | echo \"${u=x}\"")]         // assigning stage: guarded + forwarded
    [InlineData("set -o pipefail\necho a | cat >/dev/null")]
    public void Transpile_SimpleStageInABlock_UsesTheStageStdinPrimitives(string bash)
    {
        var ps = T(bash);

        Assert.Contains("$input", ps);
        AssertEveryInputIsAPrimitive(bash, ps);
    }

    /// <summary>Every <c>$input</c> in the emitted text is one of the PsBuild stage-stdin primitives.</summary>
    private static void AssertEveryInputIsAPrimitive(string bash, string ps)
    {
        string rest = ps
            .Replace(ScopeOpen, "")
            .Replace(PsBuild.DrainStageInput("\u0001").Split('\u0001')[0], "")
            .Replace(PsBuild.FunctionStdinScope("\u0001").Split('\u0001')[0], "")
            .Replace(PsBuild.ForwardedStageInput, "");
        Assert.False(rest.Contains(PsBuild.StageInput, System.StringComparison.Ordinal),
            $"`{bash}`: `$input` used outside the stage-stdin primitives:\n{ps}");
    }
}
