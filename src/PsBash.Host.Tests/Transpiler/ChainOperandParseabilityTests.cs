using Xunit;

namespace PsBash.Host.Tests.Transpiler;

/// <summary>
/// PowerShell's <c>&amp;&amp;</c> / <c>||</c> chain only accepts pipelines as operands. A construct that emits a statement
/// (a leading assignment, <c>try</c>, <c>if</c>, a statement list) bare after the operator is "Unexpected token '='" for
/// the WHOLE script, so one odd operand kills an ordinary agent command. REGRESSIONS seen in Claude Code's Bash tool:
/// <c>cd /x &amp;&amp; time cmd</c> (<c>$__psbash_time0 = …</c>) and <c>cd /x &amp;&amp; a 2&gt;&amp;1 | sed …; echo ${PIPESTATUS[0]}</c>
/// (<c>$__pf = [int[]]…</c>). Every operand form is checked in every position a chain stands in, as the first, middle
/// and last operand of each operator. The oracle is PowerShell's parser (ParseabilityContract); statuses and output
/// against bash are the Differential TimeKeyword / ChainOperand tests.
/// </summary>
public class ChainOperandParseabilityTests
{
    /// <summary>Operand forms, chosen for emitting something other than one plain pipeline.</summary>
    public static TheoryData<string> Operands => new()
    {
        // time [-p] pipeline: `$mark = Start(); try { … } finally { report }`.
        "time echo a",
        "time -p echo a",
        "time -- echo a",
        "time echo a | cat",
        "time { echo a; }",
        "time ( echo a )",
        "time ! false",
        "time true > /dev/null",
        "time x=1",
        "time",
        // Pipelines that record each stage's status (PIPESTATUS read, pipefail).
        "echo a 2>&1 | sed -n '1,6p;/b/,/^c/p'",
        "false | true",
        "! false | true",
        "echo a |& cat",
        // Statement-emitting builtins.
        "cd /",
        "cd /nodir-psb",
        "set -- a b",
        "set --",
        "shift",
        "eval 'echo a'",
        "export X=1",
        "local v=1",
        "declare -a arr=(1 2)",
        "readonly R=1",
        "x=1",
        "x=$(echo a)",
        "a=(1 2)",
        "x+=1",
        "X=1 echo a",
        "read -r l <<< 'in'",
        "mapfile -t m <<< 'in'",
        "printf -v pv '%s' a",
        "let n=1",
        "((n++))",
        "[[ -n a ]]",
        "[ a = a ]",
        "test -n a",
        "getopts ab o",
        "unset x",
        "pushd / > /dev/null",
        "source /dev/null",
        ". /dev/null",
        "trap 'echo t' EXIT",
        "shopt -s nullglob",
        "exec 3>/dev/null",
        "echo *.zz-psb",
        "echo ${x:?}",
        "f() { :; }",
        // Compound commands.
        "{ echo a; }",
        "{ echo a; } 2>/dev/null",
        "( echo a )",
        "if true; then echo a; fi",
        "for i in 1; do echo $i; done",
        "while false; do :; done",
        "case a in a) echo a ;; esac",
        "cat <<< hs",
        "cat <(echo a)",
        // Statement-emitting commands as the first stage of a pipeline, or negated.
        "x=1 | cat",
        "cd / | cat",
        "set -- a | cat",
        "eval 'echo a' | cat",
        "export X=1 | cat",
        "a=(1) | cat",
        "X=1 cd / | cat",
        "time x=1 | cat",
        "[[ a ]] | cat",
        "((1)) | cat",
        "{ x=1; } | cat",
        "! cd /",
        "! x=1",
        "! time false",
        "! eval 'false'",
        // Other builtins and wrappers the emitter rewrites.
        "command cd /",
        "builtin cd /",
        "X=1 cd /",
        "X=1 x=2",
        "cd -",
        "popd",
        "dirs",
        "umask 022",
        "hash -r",
        "alias a=b",
        "declare -n r=x",
        "wait",
        "exit 0",
        "fc -s",
        "history -c",
        "nohup echo a",
        "exec >/dev/null",
        "function g { :; }",
        "select s in a; do break; done",
        "coproc cat",
        "echo $(cd / && time true)",
    };

    /// <summary>{0} is the operand; each position is a place a chain stands in.</summary>
    private static readonly string[] Positions =
    {
        "cd / && {0}",
        "false || {0}",
        "{0} && echo ok",
        "{0} || echo no",
        "true && {0} && echo ok",
        "false || {0} || echo no",
        "true && {0} || echo no",
        "cd / && {0}; echo \"st=$? ${{PIPESTATUS[0]}}\"",
        "if true && {0}; then echo t; fi",
        "while false || {0}; do break; done",
        "x=$(true && {0}); echo \"$x\"",
        "f() {{ true && {0}; }}; f && echo ok",
        "{{ true && {0}; }} 2>/dev/null",
        "true && {0} &",
        "set -e; true && {0}; echo after",
        "set -o pipefail; true && {0}",
        "set -euo pipefail; false || {0} && echo ok",
        "trap 'echo d' DEBUG; true && {0}",
        "for i in 1; do true && {0}; done",
    };

    [Theory]
    [MemberData(nameof(Operands))]
    public void Transpile_OperandInEveryChainPosition_EmitsParseablePowerShell(string operand)
    {
        var failures = new List<string>();
        foreach (var position in Positions)
        {
            string bash = string.Format(position, operand);
            try { ParseabilityContract.Assert(bash, "chain operand"); }
            catch (Exception e) { failures.Add(e.Message); }
        }
        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    /// <summary>A pipe stage is the other position that takes only a pipeline: every operand form as each stage.</summary>
    private static readonly string[] StagePositions =
    {
        "{0} | cat",
        "echo a | {0}",
        "echo a | {0} | cat",
        "cd / && echo a | {0}; echo \"${{PIPESTATUS[@]}}\"",
        "set -o pipefail; true && {0} | cat",
    };

    [Theory]
    [MemberData(nameof(Operands))]
    public void Transpile_OperandAsEveryPipeStage_EmitsParseablePowerShell(string operand)
    {
        var failures = new List<string>();
        foreach (var position in StagePositions)
        {
            string bash = string.Format(position, operand);
            try { ParseabilityContract.Assert(bash, "pipe stage"); }
            catch (Exception e) { failures.Add(e.Message); }
        }
        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    /// <summary>
    /// PsBuild.IsPipelineChainOperand, the one test the chain emitter applies to every operand, says what PowerShell's
    /// parser says after <c>a &amp;&amp; </c> (keyword statements such as <c>if</c> / <c>while</c> parse there as calls to
    /// a command of that name, so they are left out: the predicate wraps them on purpose).
    /// </summary>
    [Theory]
    [InlineData("Invoke-BashCat f")]
    [InlineData("$(cmd1; cmd2)")]
    [InlineData(". { $__pf = [int[]]::new(2); a; b }")]
    [InlineData("return 3")]
    [InlineData("throw 'x'")]
    [InlineData("$x -eq 1")]
    [InlineData("$x = 1")]
    [InlineData("$global:a[0] = 1")]
    [InlineData("$n += 1")]
    [InlineData("${x} = 1")]
    [InlineData("$x.y = 1")]
    [InlineData("foreach ($i in @(1)) { a }")]
    [InlineData("$__psbash_time0 = [PsBash.Cmdlets.BashTimeKeyword]::Start(); try { a } finally { b }")]
    public void IsPipelineChainOperand_AgreesWithPowerShellsParser(string psText)
    {
        System.Management.Automation.Language.Parser.ParseInput("a && " + psText, out _, out var errors);
        bool parsesAsOperand = errors.Length == 0 && !PsBash.Core.Parser.PsBuild.IsStatementList(psText);
        Assert.Equal(parsesAsOperand, PsBash.Core.Parser.PsBuild.IsPipelineChainOperand(psText));
    }

    /// <summary>The two command lines from the bug reports, exactly as typed into the Bash tool.</summary>
    [Theory]
    [InlineData("cd /some/path && time some-cmd")]
    [InlineData("cd ~/work/core/dev-setup && python agents-dashboard.py 2>&1 | sed -n '1,6p;/build3/,/^── /p'; echo \"EXIT ${PIPESTATUS[0]}\"")]
    public void Transpile_ReportedCommandLines_EmitParseablePowerShell(string bash)
        => ParseabilityContract.Assert(bash, "reported command line");
}
