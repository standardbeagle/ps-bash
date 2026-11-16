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
