using System.Text;
using PsBash.Core.Transpiler;
using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// Guard for the ONE "does this redirect failure end the shell?" decision (PsEmitter.EmitCheckedStatement ->
/// PsBuild.RedirectFailureChecked; docs/specs/emitter-strategy.md section Errexit). Every command kind x every
/// redirect operator x every descriptor, run in a real runspace under <c>set -e</c>: a redirection that cannot be
/// opened makes the command's status 1 and ends the (sub)shell, with an ERR trap run once first; in an exempt context
/// (<c>||</c> / <c>&amp;&amp;</c> operand, <c>if</c> / <c>while</c> condition, <c>!</c>, a function called as an
/// operand) the shell goes on. The failure is reported exactly once.
/// <para>Root cause guarded: each opener (the redirect cmdlet, the input-file guard, the fd opens, the statement
/// builtins' own redirects, eval and sourced text) decided errexit by itself, so <c>{ echo a; } 2&gt;/nodir/f</c>,
/// <c>x=1 &gt;/nodir/f</c>, <c>{ cat; } &lt;/nofile</c> and an eval'd <c>echo x &gt;/nodir/f</c> went on under
/// <c>set -e</c>, and a terminating throw ended the whole script from inside a subshell or an <c>||</c> operand.
/// Openers now only raise <c>$global:__BashRedirectFailed</c>; the statement's one check decides. A new command kind,
/// operator or context is a row or list entry here.</para>
/// Oracle note (qa-rubric Directive 1): every expectation below was confirmed against bash 5.2 and 5.3 by running the
/// generated script under bash; the end-to-end parity of representative shapes is
/// <c>Differential.Tests/ErrexitInputRedirectTests</c> and <c>ErrexitStatusDifferentialTests</c>.
/// </summary>
[Collection("SdkHost")]
public class ErrexitRedirectFailureMatrixTests : IAsyncLifetime
{
    private readonly HostWorkerFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private const string Missing = "/nonexistent_zz/f";

    /// <summary>Every redirect operator and descriptor, each opening <see cref="Missing"/> (the second of a pair).</summary>
    internal static readonly string[] Redirects =
    [
        ">", "2>", "3>", ">>", "2>>", ">|", "&>", "&>>", "<", "0<", "3<", "<>", "3<>",
        "2>/dev/null >", ">/dev/null 2>",
    ];

    /// <summary>Command kinds: (name, definitions run first, the command with <c>{R}</c> where its redirect goes, whether
    /// the failing command runs inside a function body). Inside a function an ERR trap set without <c>set -E</c> is not
    /// inherited, and <c>set -e</c> ends the shell from there, so bash runs no trap at all.</summary>
    internal static readonly (string Name, string Pre, string Command, bool InFunction)[] Kinds =
    [
        ("mapped", "", "echo x {R}", false),
        ("colon", "", ": {R}", false),
        ("true", "", "true {R}", false),
        ("printf", "", "printf x {R}", false),
        ("assignment", "", "x=1 {R}", false),
        ("export", "", "export y=1 {R}", false),
        ("local", "lf(){ local z=1 {R}; }; ", "lf", true),
        ("group", "", "{ echo x; } {R}", false),
        ("subshell", "", "( echo x ) {R}", false),
        ("for", "", "for i in 1; do echo $i; done {R}", false),
        ("while", "", "while false; do :; done {R}", false),
        ("if", "", "if true; then echo x; fi {R}", false),
        ("case", "", "case a in a) echo x;; esac {R}", false),
        ("call", "fn(){ echo x; }; ", "fn {R}", false),
        ("pipeline", "", "echo a | cat {R}", false),
        ("eval", "", "eval 'echo x {R}'", false),
        ("source", "printf '%s\\n' 'echo x {R}' > s.sh; ", ". ./s.sh", false),
        ("bare", "", "{R}", false),
    ];

    /// <summary>Contexts: (tag, shell head, the statement around <c>{C}</c>, the lines it prints before its tag line,
    /// whether the failure ends the subshell).</summary>
    internal static readonly (string Tag, string Head, string Body, string[] Prints, bool Ends)[] Contexts =
    [
        ("P", "set -e; ", "{C}; echo after", [], true),
        ("T", "trap 'echo ERR' ERR; set -e; ", "{C}; echo after", ["ERR"], true),
        ("O", "set -e; ", "{C} || echo caught; echo after", ["caught", "after"], false),
        ("A", "set -e; ", "{C} && echo no; echo after", ["after"], false),
        ("I", "set -e; ", "if {C}; then echo t; else echo e; fi; echo after", ["e", "after"], false),
        ("W", "set -e; ", "while {C}; do break; done; echo after", ["after"], false),
        ("N", "set -e; ", "! {C}; echo after", ["after"], false),
        ("F", "set -e; ", "h(){ {C}; echo in-h; }; h || echo caught; echo after", ["in-h", "after"], false),
    ];

    public static TheoryData<string> KindNames()
    {
        var data = new TheoryData<string>();
        foreach (var kind in Kinds) data.Add(kind.Name);
        return data;
    }

    /// <summary>The script for one command kind (one subshell per context x redirect, each followed by a tag line with
    /// its status and how many times the failed open was reported) and the stdout bash prints for it.</summary>
    internal static (string Script, string Expected) Case(string kindName)
    {
        var (_, pre, command, inFunction) = Kinds.Single(k => k.Name == kindName);
        var script = new StringBuilder("cd \"$(mktemp -d)\" || exit 1\n");
        var expected = new StringBuilder();
        foreach (var (tag, head, body, prints, ends) in Contexts)
            for (int i = 0; i < Redirects.Length; i++)
            {
                string redirect = Redirects[i] + " " + Missing;
                string cmd = command.Replace("{R}", redirect);
                string line = head + pre.Replace("{R}", redirect) + body.Replace("{C}", cmd);
                string name = tag + i;
                script.Append("( ").Append(line).Append(" ) 2>e; rc=$?; echo \"").Append(name)
                      .Append(" rc=$rc n=$(grep -c nonexistent_zz e)\"\n");
                foreach (var p in prints)
                    if (!(inFunction && p == "ERR")) expected.Append(p).Append('\n');
                int reported = Redirects[i].StartsWith("2>/dev/null", StringComparison.Ordinal) ? 0 : 1;
                expected.Append(name).Append(" rc=").Append(ends ? 1 : 0).Append(" n=").Append(reported).Append('\n');
            }
        return (script.ToString(), expected.ToString());
    }

    [Theory]
    [MemberData(nameof(KindNames))]
    public async Task FailedOpen_EveryOperatorAndContext_EndsTheShellUnlessExempt(string kind)
    {
        var (script, expected) = Case(kind);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var gate = new object();
        SdkWorker worker = _fixture.CreateWorker();
        int exit = await worker.ExecuteWithOutputAsync(BashTranspiler.Transpile(script)!,
            s => { lock (gate) stdout.Append(s); }, s => { lock (gate) stderr.Append(s); });

        // Every failure message goes to its case's `2>e`: anything on the script's own stderr escaped a redirection.
        var wrong = Mismatches(expected, stdout.ToString(), script);
        if (stderr.Length > 0) wrong.Add("  escaped to the script's stderr: " + stderr);
        Assert.True(wrong.Count == 0, $"{wrong.Count} case(s) differ from bash ({kind}):\n" + string.Join("\n", wrong));
        Assert.Equal(0, exit);
    }

    /// <summary>
    /// How a function defined INSIDE a subshell leaves the shell (PsEmitter.InSubshellBody): <c>exit</c>, an errexit
    /// failure, a fatal error and a run-time <c>return</c> there are the function's, decided at run time
    /// (PsBuild.DynamicExit), not the subshell body's own <c>return</c>, which left only the function. REGRESSION:
    /// <c>( f(){ exit 3; echo no; }; f; echo after )</c> printed after with status 0, <c>x=$( f(){ exit 7; }; f; echo no )</c>
    /// captured no, and <c>( trap 'echo E' ERR; set -e; f(){ false; }; f )</c> ran the trap bash never runs. The expected
    /// stdout is bash 5.2's and 5.3's for this script.
    /// </summary>
    [Fact]
    public async Task ExitFromAFunctionDefinedInASubshell_EndsThatSubshell()
    {
        const string script = """
            ( f(){ exit 3; echo no; }; f; echo after ); echo "s1 $?"
            ( set -e; f(){ false; echo no; }; f; echo after ); echo "s2 $?"
            ( set -e; f(){ false; echo no; }; f || echo caught; echo after ); echo "s3 $?"
            ( f(){ g(){ exit 4; }; g; echo no; }; f; echo after ); echo "s4 $?"
            ( trap 'echo ERR4' ERR; set -e; lf2(){ false; }; lf2; echo no ); echo "s5 $?"
            ( ( f(){ exit 5; }; f; echo no ); echo "in $?"; exit 6 ); echo "s6 $?"
            x=$( f(){ exit 7; }; f; echo no ); echo "s7 $? [$x]"
            ( f(){ eval 'exit 8'; echo no; }; f; echo after ); echo "s8 $?"
            ( f(){ shift 5; }; set -e; f; echo after ); echo "s9 $?"
            ( f(){ exit; }; false; f; echo after ); echo "s10 $?"
            echo a | { f(){ exit 9; }; f; echo no; }; echo "s11 $?"
            ( f(){ eval 'return 3'; echo no; }; f; echo "r1 $?" ); echo "t1 $?"
            ( f(){ r=return; $r 4; echo no; }; f; echo "r2 $?" ); echo "t2 $?"
            ( f(){ set -u; echo "$undefined_zz"; echo no; }; f; echo after ) 2>/dev/null; echo "t3 $?"
            ( f(){ ( exit 5 ); echo "in $?"; exit 6; }; f; echo no ); echo "t5 $?"
            ( f(){ return 7; }; f; echo "r3 $?"; exit 8 ); echo "t6 $?"
            ( set -e; f(){ false; }; if f; then echo t; else echo e; fi; f || echo c; echo after ); echo "t7 $?"
            f(){ ( g(){ exit 9; }; g; echo no ); echo "u $?"; }; f; echo "t8 $?"
            """;
        const string expected = """
            s1 3
            s2 1
            no
            after
            s3 0
            s4 4
            s5 1
            in 5
            s6 6
            s7 7 []
            s8 8
            s9 1
            s10 1
            s11 9
            r1 3
            t1 0
            r2 4
            t2 0
            t3 1
            in 5
            t5 6
            r3 7
            t6 8
            e
            c
            after
            t7 0
            u 9
            t8 0
            """;
        var stdout = new StringBuilder();
        var gate = new object();
        SdkWorker worker = _fixture.CreateWorker();
        await worker.ExecuteWithOutputAsync(BashTranspiler.Transpile(script)!,
            s => { lock (gate) stdout.Append(s); }, _ => { });
        Assert.Equal(expected.Replace("\r", ""), stdout.ToString().Replace("\r", "").TrimEnd('\n'));
    }

    /// <summary>
    /// The status an assignment or declaration builtin reads (<c>$?</c> in its value) is the one from before it, and a
    /// redirection it cannot open is its status 1 as an <c>||</c> / <c>&amp;&amp;</c> operand or a later pipeline stage
    /// (PsEmitter.EmitShAssignmentStatementCore, PsBuild.SignalFailIfOpenFailed, PsEmitter.OpensDescriptorsAroundItself).
    /// REGRESSION: the assignment's 0 was set before its value, so <c>false; local rc=$?</c> stored 0, as did every
    /// <c>rc=$?</c> in a script that sources a file; <c>x=1 &gt;/nodir/f || echo c</c> fell through to the <c>&amp;&amp;</c>
    /// side; <c>echo a | cat 3&gt;/nodir/f</c> wrote PowerShell's warning stream. The expected stdout is bash's.
    /// </summary>
    [Fact]
    public async Task AssignmentStatusAndFailedOpens_MatchBash()
    {
        const string script = """
            cd "$(mktemp -d)" || exit 1
            echo 'true' > s.sh
            f(){ false; local rc=$?; echo "l $rc"; }; f
            false; rc=$?; echo "a $rc"
            . ./s.sh; false; rc2=$?; echo "b $rc2"
            false; y=$(($?+1)); echo "c $y"
            false; declare d=$?; echo "d $d"
            false; export e="x$?"; echo "e $e"
            false; y=$((2)); echo "g $?"
            false; local z=1 2>/dev/null; echo "h $?"
            x=1 >/nonexistent_zz/f 2>/dev/null || echo "i caught"
            export w=1 3>/nonexistent_zz/f 2>/dev/null && echo "j no"; echo "j $?"
            echo a | cat 3>/nonexistent_zz/f 2>/dev/null; echo "k $?"
            """;
        const string expected = "l 1\na 1\nb 1\nc 2\nd 1\ne x1\ng 0\nh 1\ni caught\nj 1\nk 1";
        var stdout = new StringBuilder();
        var gate = new object();
        SdkWorker worker = _fixture.CreateWorker();
        await worker.ExecuteWithOutputAsync(BashTranspiler.Transpile(script)!,
            s => { lock (gate) stdout.Append(s); }, _ => { });
        Assert.Equal(expected, stdout.ToString().Replace("\r", "").TrimEnd('\n'));
    }

    /// <summary>Each case's block (its printed lines and its tag line) that differs, with the bash line that made it.</summary>
    private static List<string> Mismatches(string expected, string actual, string script)
    {
        var want = Blocks(expected);
        var got = Blocks(actual);
        var lines = script.Split('\n');
        var wrong = new List<string>();
        foreach (var (name, block) in want)
        {
            string have = got.TryGetValue(name, out var b) ? b : "(missing)";
            if (have == block) continue;
            string source = lines.FirstOrDefault(l => l.Contains("echo \"" + name + " ", StringComparison.Ordinal)) ?? "";
            wrong.Add($"  {source}\n    bash:     {block}\n    ps-bash:  {have}");
        }
        return wrong;
    }

    /// <summary>The output split after each tag line (<c>NAME rc=..</c>), keyed by the tag.</summary>
    private static Dictionary<string, string> Blocks(string output)
    {
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        var current = new List<string>();
        foreach (var line in output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            current.Add(line);
            int space = line.IndexOf(" rc=", StringComparison.Ordinal);
            if (space <= 0) continue;
            blocks[line[..space]] = string.Join(" | ", current);
            current.Clear();
        }
        return blocks;
    }
}
