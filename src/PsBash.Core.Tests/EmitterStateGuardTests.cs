using System.Reflection;
using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the emitter's thread state: every <c>[ThreadStatic]</c> field of the Transpiler assembly (<c>PsEmitter</c>,
/// <c>PsBuild</c>, <c>BashParser</c>, <c>NestingGuard</c>, ... found by reflection, so a new one is checked too) goes back to
/// its first value at the start of a top-level transpile (<c>PsEmitter.ResetUnitState</c>), or is listed here as the
/// caller's input to that transpile, with the reason. The state of one nested emission scope (<c>$( )</c>, <c>&lt;( )</c>, a
/// pipe stage, a <c>&amp;</c> job, a subshell, a function body) is <c>PsEmitter.EmitterScope</c>'s.
/// <para>
/// The bug class: <c>_leavingReturns</c> was never reset and not restored after a command substitution, a pipe stage or a
/// job, so the same script emitted different PowerShell depending on what had been transpiled before it on that thread
/// (and an errexit and-or operand was buffered in <c>$( )</c> after any earlier <c>|| return 1</c>). The behavior guard is
/// <c>PsBash.Spec.Tests.TranspileDeterminismTests</c>: the same text gives the same PowerShell whatever came before.
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): an inventory of the emitter's own fields; no bash behavior is asserted here.</para>
/// </summary>
public class EmitterStateGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }

    /// <summary>Fields a top-level transpile must NOT reset, each with why.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["PsEmitter.InheritedPipefail"] = "input: the running shell's pipefail, set by the caller (RuntimeOptionHints, BashTranspiler) before it transpiles",
        ["PsEmitter.InheritedNounset"] = "input: the running shell's nounset, set by the caller before it transpiles",
        ["PsEmitter.TrapActionSignal"] = "input: the trap whose action is being transpiled (BashTranspiler), read and cleared by Transpile itself",
        ["PsEmitter.FusionEnabledOverride"] = "input: the fused-lane switch of the caller (BashTranspiler, tests)",
        ["PsEmitter.EvalLocalNames"] = "input: the locals of the function an eval runs in (BashTranspiler's eval entry)",
        ["PsEmitter._inStdinScope"] = "input: whether the text runs in a stdin scope (BashTranspiler, TranspileWithLauncherStdin)",
        ["PsEmitter._launcherStdin"] = "input: set by TranspileWithLauncherStdin around its Transpile",
        ["PsEmitter._transpileNesting"] = "the nesting count that decides a top-level transpile: 0 there by definition",
        ["NestingGuard.t_depth"] = "the parser's recursion depth, raised and lowered by each NestingGuard.Scope (using); a transpile can start inside one",
        ["NestingGuard.t_onLargeStack"] = "a fact about the thread (set on the large-stack worker OnLargeStack starts), not about a unit",
        ["NestingGuard.t_stackExhausted"] = "cleared by WithStackFallback before the work it wraps, which may be several units or part of one",
        ["CommandHashTable._probeDepth"] = "run-time command lookup (hash), not emission: raised and lowered by each CommandHashTable.ProbeScope (using)",
        ["ShellVariables.t_launcherParent"] = "shell startup's own value, set and cleared in one call's finally; never read by the emitter",
    };

    /// <summary>Every <c>[ThreadStatic]</c> field of the Transpiler assembly (the emitter, the parser, and what they call).</summary>
    private static IEnumerable<(string Name, FieldInfo Field)> ThreadStaticFields()
    {
        foreach (var type in typeof(PsEmitter).Assembly.GetTypes())
            foreach (var f in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (f.IsDefined(typeof(ThreadStaticAttribute), inherit: false))
                    yield return (type.Name + "." + f.Name, f);
    }

    /// <summary>The text of <c>PsEmitter.ResetUnitState</c>.</summary>
    private static string ResetHelperText()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser", "PsEmitter.cs"));
        int start = text.IndexOf("private static void ResetUnitState(", StringComparison.Ordinal);
        Assert.True(start >= 0, "PsEmitter.ResetUnitState not found");
        int end = text.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return text[start..end];
    }

    [Fact]
    public void EveryThreadStaticField_IsResetPerUnitOrExempt()
    {
        string reset = ResetHelperText();
        var fields = ThreadStaticFields().ToList();
        Assert.True(fields.Count > 50, "expected the emitter's [ThreadStatic] fields, found " + fields.Count);
        var missing = new List<string>();
        foreach (var (name, field) in fields)
        {
            if (Exempt.ContainsKey(name)) continue;
            // PsEmitter's own fields by name; another type's through the class (`PsBuild.TrapLine = 0`, `BashParser.SawBadSubstitution = false`).
            string written = field.DeclaringType == typeof(PsEmitter) ? field.Name : field.DeclaringType!.Name + "." + field.Name;
            if (!Regex.IsMatch(reset, @"(?<![\w.])" + Regex.Escape(written) + @"\s*=[^=]"))
                missing.Add(name);
        }
        Assert.True(missing.Count == 0,
            "A [ThreadStatic] emitter field a top-level transpile does not reset: an earlier unit on the thread would change the "
            + "text emitted for the next. Reset it in PsEmitter.ResetUnitState, or add it to Exempt with the reason:\n  "
            + string.Join("\n  ", missing));
        var known = fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var stale = Exempt.Keys.Where(k => !known.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Exempt names no [ThreadStatic] field any more: " + string.Join(", ", stale));
    }

    /// <summary>An exempt field is never reset: that would discard the caller's input.</summary>
    [Fact]
    public void ExemptFields_AreNotReset()
    {
        string reset = ResetHelperText();
        foreach (var name in Exempt.Keys)
        {
            string field = name[(name.IndexOf('.') + 1)..];
            Assert.False(Regex.IsMatch(reset, @"(?<![\w.])" + Regex.Escape(field) + @"\s*=[^=]"), name + " is an input; ResetUnitState must keep it");
        }
    }

    /// <summary>
    /// A top-level transpile starts from what a fresh thread has: after a unit that leaves counters raised, the same text
    /// still gives the same PowerShell (the corpus-wide check is TranspileDeterminismTests).
    /// </summary>
    [Theory]
    [InlineData("set -e\nf() { x || return 1; }")]
    [InlineData("set -e\nv=$(foo || exit 1)")]
    [InlineData("set -e\nx | while :; do exit 1; done")]
    [InlineData("eval \"$c\" <<< x; cat > >(tac)")]
    public void Transpile_AfterAUnitThatLeavesStateRaised_GivesTheSameText(string before)
    {
        const string script = "set -e\ng() { [[ 1 ]] && { echo a; }; eval \"$c\" <<< x; seq 3 > >(tac); }\ntrue && { echo b; }";
        string first = OnFreshThread(script);
        PsEmitter.Transpile(before);
        Assert.Equal(first, PsEmitter.Transpile(script));
        Assert.Equal(first, PsEmitter.Transpile(script));
    }

    private static string OnFreshThread(string script)
    {
        string? text = null;
        var t = new Thread(() => text = PsEmitter.Transpile(script), 16 * 1024 * 1024);
        t.Start();
        t.Join();
        return text!;
    }
}
