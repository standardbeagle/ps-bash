using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the redirect-block invariant: a body the emitter puts in a script block for its redirects (<c>{ ...; } 2&gt;x</c>,
/// <c>while ... done &lt;f</c>, <c>if ... fi &gt;x</c>, <c>eval '...' 3&gt;x</c>) goes through ONE primitive,
/// <c>PsEmitter.RedirectGroup</c>. A PowerShell <c>return</c> leaves only the block it is in, so when the body has a
/// return that leaves a function, eval text or a subshell, the block passes it on (<c>PsBuild.GroupPassingReturn</c>).
/// <para>
/// The bug class: each site built its own <c>. { }</c> / <c>&amp; { }</c>, so <c>f() { { return 6; } 2&gt;/dev/null; echo
/// no; }</c> printed no and returned 0 (bash 5.2: 6), and the same for while/until/for/if/case, <c>&lt;file</c>,
/// <c>&lt;&lt;&lt; x</c>, <c>3&gt;x</c>, <c>{fd}&gt;x</c>, a here-document on eval, and <c>( { exit 3; } 2&gt;x; echo no )</c>;
/// under set -e or an ERR trap the final operand of <c>[[ 1 ]] &amp;&amp; { return 6; }</c> too, and <c>return 6 &lt;/dev/null</c>
/// (<c>return 6 &lt;f</c> / <c>eval 'return 7' &lt;f</c> failed the whole script's parse). Only eval and fc had the fix
/// (WithRedirectTailPassingReturn). break/continue are not affected (PowerShell's labelled
/// break leaves blocks); <c>source f 2&gt;x</c> and a function call with a redirect end only themselves in bash too.
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): a source inventory and emitted-text shapes; behavior vs bash: Differential
/// <c>RedirectGroupReturnDifferentialTests</c>.</para>
/// </summary>
public class RedirectGroupGuardTests
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

    private static readonly Regex MemberHead = new(@"^\s*(?:public|private|internal)\s+(?:static\s+)?[\w<>\[\]?,.]+\s+(\w+)\s*[(<]", RegexOptions.Compiled);

    // The text that builds a block around a body: dot-sourced bodies, the redirect tail around a statement list, scoped
    // fds, and the PsBuild forms that wrap a command in one (errexit's final and-or operand, `< /dev/null`, `< file`, a
    // command tested in expression position, the errexit forms of a condition).
    private static readonly string[] BlockMakers =
    [
        "\". { \"", "$\". {{", "PsBuild.WithRedirectTail(", "WithScopedHighFds(", "new StringBuilder(\". { \")",
        "PsBuild.ErrexitFinalOperand", "PsBuild.EmptyStdinScope(", "PsBuild.SimpleInputRedirectGuard(", "PsBuild.RunTimeNameBranch(",
        "PsBuild.WithPipeEntryStatus(", "PsBuild.ExitCodeTest(", "PsBuild.ExitCodeTestFlaggingBad(", "\"(& { \"", "$\"(& {{",
    ];

    /// <summary>
    /// The errexit / ERR-trap wrappers of PsBuild: each must wrap its statements in try/finally (a return passes through),
    /// never in a script block, or be a block maker listed above (and so routed through RedirectGroup or exempt).
    /// REGRESSION class: a condition run suppressed in <c>(&amp; { })</c> (ErrexitSuppressedExpr) would have kept
    /// <c>if return 6; then</c> from leaving the function under set -e.
    /// </summary>
    [Fact]
    public void ErrexitWrappersPassAReturnOrAreBlockMakers()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser", "PsBuild.cs"));
        var heads = Regex.Matches(text, @"public static string (\w*Errexit\w*|ErrTrap\w*)\s*\(([^)]*)\)\s*(?:=>|\{)");
        Assert.NotEmpty(heads);
        var blocky = new List<string>();
        foreach (Match h in heads)
        {
            string name = h.Groups[1].Value;
            int end = text.IndexOf("\n\n", h.Index, StringComparison.Ordinal);
            string body = text[h.Index..(end < 0 ? text.Length : end)];
            bool makesBlock = body.Contains("\"(& { \"", StringComparison.Ordinal) || body.Contains("\"& { \"", StringComparison.Ordinal)
                || body.Contains("\". { \"", StringComparison.Ordinal) || body.Contains("(& {", StringComparison.Ordinal);
            if (makesBlock && !BlockMakers.Contains("PsBuild." + name) && !BlockMakers.Contains("PsBuild." + name + "("))
                blocky.Add(name);
        }
        Assert.True(blocky.Count == 0,
            "An errexit/ERR wrapper that puts the command in a script block (a return in it stops at the block): use try/finally, "
            + "or list it in BlockMakers so every call site goes through PsEmitter.RedirectGroup:\n  " + string.Join("\n  ", blocky));
    }

    /// <summary>
    /// Members that build such a block NOT through RedirectGroup on that line, each with why its body cannot hold a return
    /// that must leave a caller (or why its caller already went through RedirectGroup).
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["PsEmitter.cs:RedirectGroup"] = "the primitive",
        ["PsEmitter.cs:DotStdinBlock"] = "the eval/source here-document block: built inside RedirectGroup (eval); source's own return ends only the file",
        ["PsEmitter.cs:WithScopedHighFds"] = "the scoped-fd block itself; its callers pass it to RedirectGroup",
        ["PsEmitter.cs:EmitSubshell"] = "a subshell's body returns from its own block (its exits are the subshell's, counted apart)",
        ["PsEmitter.cs:ApplyCompoundRedirectsCore"] = "the construct ApplyCompoundRedirects hands to RedirectGroup",
        ["PsEmitter.cs:TryEmitInvalidIdentifiers"] = "diagnostics only (`declare 1a=3 2>/dev/null`): no return in the body",
        ["PsEmitter.cs:TryEmitInvalidBuiltinOption"] = "diagnostics only (`read -Z 2>/dev/null`): no return in the body",
        ["PsEmitter.cs:TryEmitMappedCommand"] = "`builtin NAME 2>x` when NAME is not a builtin: a diagnostic only",
        ["PsEmitter.cs:EmitPipeline"] = "a pipeline: each stage is a subshell, whose return ends only the stage",
        ["PsEmitter.ShellOptions.cs:TryEmitRunTimeExecRedirect"] = "`$c >x`: a run-time `return` raises the eval-return flag, checked after the statement (EmitStatement)",
        ["PsEmitter.cs:EmitCondition"] = "an expression-position condition runs no command: EmitExpressionCondition refuses one that does (it is hoisted)",
        ["PsEmitter.cs:EmitConditionAsExpr"] = "an operand of such a condition: a test, no command (EmitExpressionCondition)",
        ["PsEmitter.cs:TranslateTestCondition"] = "`[[ -v x ]]` / a device test: Invoke-BashTest, which cannot return",
    };

    [Fact]
    public void EveryRedirectBlockGoesThroughRedirectGroupOrIsExempt()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser");
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "PsEmitter*.cs"))
        {
            string? member = null;
            string previous = "";
            foreach (var line in File.ReadLines(file))
            {
                if (MemberHead.Match(line) is { Success: true } m) member = m.Groups[1].Value;
                var code = line.TrimStart();
                bool isComment = code.StartsWith("//", StringComparison.Ordinal);
                // The construct is RedirectGroup's lambda, on its line or the next (a wrapped argument list).
                bool inRedirectGroup = line.Contains("RedirectGroup(", StringComparison.Ordinal) || previous.Contains("RedirectGroup(", StringComparison.Ordinal);
                if (!isComment) previous = line;
                if (isComment || inRedirectGroup || !BlockMakers.Any(b => line.Contains(b, StringComparison.Ordinal))) continue;
                found.Add(Path.GetFileName(file) + ":" + member);
            }
        }
        var unknown = found.Where(f => !Exempt.ContainsKey(f)).ToList();
        Assert.True(unknown.Count == 0,
            "A redirect's script block built outside PsEmitter.RedirectGroup (a return in its body would stop at the block). "
            + "Build it through RedirectGroup, or add the member to Exempt with the reason no return can be in it:\n  "
            + string.Join("\n  ", unknown));
        var stale = Exempt.Keys.Where(k => !found.Contains(k) && k != "PsEmitter.cs:RedirectGroup").ToList();
        Assert.True(stale.Count == 0, "Exempt members that build no block any more: " + string.Join(", ", stale));
    }

    private static readonly string PassesReturn = "if (" + PsBuild.GroupReturnVar + ") { return }";

    /// <summary>Each shape whose redirect makes a block, with a return in it: the block passes the return on.</summary>
    [Theory]
    [InlineData("f() { { return 6; } 2>/dev/null; echo no; }")]
    [InlineData("f() { while true; do return 6; done 2>/dev/null; echo no; }")]
    [InlineData("f() { until false; do return 6; done >x; echo no; }")]
    [InlineData("f() { for i in 1; do return 6; done 2>x; echo no; }")]
    [InlineData("f() { for ((i=0;i<1;i++)); do return 6; done 2>x; echo no; }")]
    [InlineData("f() { if true; then return 6; fi >x; echo no; }")]
    [InlineData("f() { case a in a) return 6;; esac 2>x; echo no; }")]
    [InlineData("f() { { return 6; } <x; echo no; }")]
    [InlineData("f() { { return 6; } <<< x; echo no; }")]
    [InlineData("f() { { return 6; } 3>x; echo no; }")]
    [InlineData("f() { { return 6; } {fd}>x; echo no; }")]
    [InlineData("f() { { { return 6; } 2>x; } >y; echo no; }")]
    [InlineData("f() { eval 'return 6' <<< x; echo no; }")]
    [InlineData("f() { builtin eval 'return 6' 2>/dev/null; echo no; }")]
    [InlineData("f() { eval 'return 6' 9>x; echo no; }")]
    [InlineData("c=x; f() { eval \"$c\" 2>/dev/null; }")]
    [InlineData("f() { fc -s >out; }")]
    [InlineData("( { exit 3; } 2>/dev/null; echo no )")]
    [InlineData("( if true; then exit 4; fi >x; echo no )")]
    [InlineData("( set -e; { false; } 2>x; echo no )")]
    [InlineData("f() { [[ 1 ]] && { return 6; }; echo no; }\nset -e")]            // errexit's final and-or operand
    [InlineData("trap : ERR\nf() { true || { return 6; } 2>x; echo no; }")]
    [InlineData("f() { return 6 </dev/null; echo no; }")]
    [InlineData("f() { eval 'return 7' </dev/null; echo no; }")]
    [InlineData("f() { eval 'return 7' <rc; echo no; }")]
    [InlineData("f() { return 6 <rc; echo no; }")]
    public void Transpile_RedirectBlockWithAReturn_PassesItOn(string script)
        => Assert.Contains(PassesReturn, PsEmitter.Transpile(script)!);

    /// <summary>
    /// A return in a condition, a <c>!</c> pipeline or an and-or left operand, under set -e and under an ERR trap: it is a
    /// statement of the function (no expression block holds it), so it leaves the function (bash 5.2: status 6).
    /// </summary>
    [Theory]
    [InlineData("f() { if return 6; then echo t; fi; echo no; }")]
    [InlineData("f() { if false; then :; elif return 6; then :; fi; echo no; }")]
    [InlineData("f() { while return 6; do :; done; echo no; }")]
    [InlineData("f() { until return 6; do :; done; echo no; }")]
    [InlineData("f() { if ! return 6; then :; fi; echo no; }")]
    [InlineData("f() { if [[ 1 ]] && return 6; then :; fi; echo no; }")]
    [InlineData("f() { if { return 6; }; then :; fi; echo no; }")]
    [InlineData("f() { ! return 6; echo no; }")]
    [InlineData("f() { return 6 && echo and; echo no; }")]
    [InlineData("f() { return 6 || echo or; echo no; }")]
    [InlineData("f() { [[ 1 ]] && ! return 6; echo no; }")]
    public void Transpile_ReturnInAnErrexitCondition_StaysAStatement(string fn)
    {
        foreach (var prologue in new[] { "set -e\n", "trap : ERR\n", "" })
        {
            var ps = PsEmitter.Transpile(prologue + fn)!;
            Assert.Contains("$global:LASTEXITCODE = 6; return", ps);
            Assert.DoesNotContain("(& {", ps);
        }
    }

    /// <summary>No return to pass on: the plain block, no flag.</summary>
    [Theory]
    [InlineData("f() { { echo a; } 2>x; }")]
    [InlineData("for i in 1; do { break; } 2>x; done")]                 // break leaves blocks by itself
    [InlineData("f() { source ./rc 2>/dev/null; }")]                   // a sourced file's return ends only the file
    [InlineData("eval 'return 7' 2>/dev/null")]                        // top of a script: a report, not a return
    [InlineData("{ f() { return 1; }; } 2>x")]                         // the return leaves f, not the group
    [InlineData("{ ( exit 3 ); echo $?; } 2>x")]                       // the exit leaves the subshell, not the group
    [InlineData("{ return 6; } 2>/dev/null")]                          // top of a script: bash refuses it (status 2)
    [InlineData("f() { [[ 1 ]] && echo a; }\nset -e")]                 // errexit's final operand, no return in it
    [InlineData("f() { read -r l </dev/null; cat <rc; }")]             // `< /dev/null`, `< file` with no return
    public void Transpile_RedirectBlockWithNoReturnToPass_KeepsThePlainBlock(string script)
        => Assert.DoesNotContain(PsBuild.GroupReturnVar, PsEmitter.Transpile(script)!);

    /// <summary>errexit's final and-or operand as emitted: <c>&amp;&amp; . { suppress--; try { OPERAND</c>.</summary>
    private static readonly string FinalOperandBlock = "&& . { " + PsBuild.ErrexitSuppressVar + "--; try { ";

    /// <summary>
    /// errexit's final and-or operand with a return in it: the check after the whole list passes the return on, and the
    /// operand stays the dot-sourced block, a chain operand whose output streams. REGRESSION: the passing form was put in
    /// <c>$( )</c>, which held back every line of <c>set -e; cond &amp;&amp; npm start</c> / <c>tail -f</c> until it ended.
    /// </summary>
    [Theory]
    [InlineData("set -e\nf() { [[ 1 ]] && { return 6; }; echo no; }")]
    [InlineData("trap : ERR\nf() { true && { return 6; } 2>x; echo no; }")]
    [InlineData("set -e\nf() { false || return 1; [[ 1 ]] && { echo a; return 6; }; echo no; }")]
    public void Transpile_ErrexitFinalOperandWithAReturn_StreamsAndPassesItOn(string script)
    {
        var ps = PsEmitter.Transpile(script)!;
        Assert.Contains(PassesReturn, ps);
        Assert.Contains(FinalOperandBlock + PsBuild.GroupReturnVar + " = $true", ps);
        Assert.DoesNotContain("$(" + PsBuild.GroupReturnVar, ps);
    }

    /// <summary>
    /// errexit's final operand that emits a statement list (a redirected compound, a loop, an eval, an env-pair command) sits
    /// in the operand's own block as is. REGRESSION: it was first wrapped in <c>$( )</c> to be a chain operand, which the block
    /// already is, so <c>set -e; true &amp;&amp; { tail -f log; } 2&gt;x</c> showed nothing until it ended.
    /// </summary>
    [Theory]
    [InlineData("set -e\ntrue && { echo a; } 2>x")]
    [InlineData("set -e\ntrue && { echo a; } > out")]
    [InlineData("trap : ERR\ntrue && while false; do :; done")]
    [InlineData("set -e\ntrue && eval 'echo a'")]
    [InlineData("set -e\ntrue && X=1 printenv X")]
    [InlineData("set -e\nf() { true && { return 6; } 2>x; }")]
    public void Transpile_ErrexitFinalOperandThatIsAStatementList_IsNotASubexpression(string script)
    {
        var ps = PsEmitter.Transpile(script)!;
        int block = ps.IndexOf(FinalOperandBlock, StringComparison.Ordinal);
        Assert.True(block >= 0, ps);
        Assert.False(ps.AsSpan(block + FinalOperandBlock.Length).StartsWith("$("), ps);
    }

    /// <summary>
    /// errexit's final and-or operand with no return of its own is the plain block, whatever came before it. REGRESSION: the
    /// operand compared the return count against the enclosing statement's mark, so one earlier <c>x || return 1</c> (or a
    /// <c>$( exit )</c>, a pipe stage's exit, a function's return) made every later final operand "pass", in <c>$( )</c>.
    /// </summary>
    [Theory]
    [InlineData("set -e\nf() { false || return 1; true && { sleep 1; echo a; sleep 1; echo b; }; }")]
    [InlineData("set -e\nf() { { return 6; } 2>x; true && { echo a; }; }")]
    [InlineData("set -e\nf() { return 1; }\ntrue && { echo a; }")]
    [InlineData("set -e\nv=$(foo || exit 1)\ntrue && { echo a; }")]
    [InlineData("set -e\necho q | while :; do exit 1; done\ntrue && { echo a; }")]
    [InlineData("set -e\n( exit 2 ) || :\ntrue && { echo a; }")]
    [InlineData("set -e\n{ exit 3; } &\ntrue && { echo a; }")]
    public void Transpile_ErrexitFinalOperandAfterAnEarlierReturn_KeepsThePlainBlock(string script)
    {
        var ps = PsEmitter.Transpile(script)!;
        Assert.Contains(FinalOperandBlock, ps);
        Assert.DoesNotContain(FinalOperandBlock + PsBuild.GroupReturnVar, ps);
        Assert.DoesNotContain("$(" + PsBuild.GroupReturnVar, ps);
    }

    /// <summary>The statement check after a redirected compound (PsBuild.RedirectFailureChecked).</summary>
    private static readonly string RedirectFailedCheck = "; if (" + PsBuild.RedirectFailedVariable + ") { ";

    public static TheoryData<string> RedirectedCompounds()
    {
        string[] compounds =
        [
            "{ cat; } R", "if true; then cat; fi R", "while false; do :; done R", "until true; do :; done R",
            "for i in 1; do cat; done R", "for ((i=0; i<1; i++)); do cat; done R", "case x in x) cat ;; esac R",
            "while read -r l; do echo \"$l\"; done R",
        ];
        string[] redirects = ["< /nonexistent/zz", "> /nonexistent/d/o", "2>/dev/null < /nonexistent/zz", ">> /nonexistent/d/o 2>&1",
            "2> /nonexistent/d/e", "2>> /nonexistent/d/e", "&> /nonexistent/d/o"];
        string[] around = ["set -e\nX\necho no", "set -e\n{ X; } 2>/dev/null\necho no", "set -e\nf() { X; echo no; }", "set -e\n( X; echo no )",
            "trap 'echo E' ERR\nX\necho no", "set -e\nf() { { X; } 2>/dev/null; echo no; }; ( f )"];
        var data = new TheoryData<string>();
        foreach (var c in compounds)
            foreach (var r in redirects)
                foreach (var a in around)
                    data.Add(a.Replace("X", c.Replace("R", r)));
        return data;
    }

    /// <summary>
    /// Under errexit / an ERR trap every compound command whose redirect opens a file (stdin or stdout), at statement
    /// position, nested in another redirected compound or not, in a function or a subshell, is followed by the statement
    /// check of the open-failure flag; a compound's input guard raises it. bash 5.2: <c>set -e; { { cat; } &lt; missing; }
    /// 2&gt;/dev/null; echo no</c> ends with status 1 (behavior: ErrexitInputRedirectTests). REGRESSION: the input guard lost its errexit exit and nothing checked
    /// after the compound, so it printed no.
    /// </summary>
    [Theory]
    [MemberData(nameof(RedirectedCompounds))]
    public void Transpile_RedirectedCompoundUnderErrexit_IsFollowedByTheOpenFailureCheck(string script)
    {
        var ps = PsEmitter.Transpile(script)!;
        Assert.Contains(RedirectFailedCheck, ps);
        if (script.Contains("< /nonexistent", StringComparison.Ordinal) && !script.Contains("while read", StringComparison.Ordinal))
            Assert.Contains(PsBuild.RedirectFailedVariable + " = $true", ps);
    }

    /// <summary>
    /// A builtin whose own status errexit ignores (<c>:</c>, <c>true</c>, <c>export</c>, <c>local</c>) still fails when its
    /// redirect cannot open (bash 5.2: <c>set -e; : &gt; /nodir/x; echo no</c> ends with status 1): it is followed by the
    /// open-failure check, and a simple command's input guard raises the flag. A redirect that cannot fail adds nothing.
    /// </summary>
    [Theory]
    [InlineData("set -e\n: > /nonexistent/d/o\necho no", false)]
    [InlineData("set -e\ntrue < /nonexistent/zz\necho no", true)]
    [InlineData("set -e\nexport Y=1 2> /nonexistent/d/e\necho no", false)]
    [InlineData("set -e\nf() { local v=1 > /nonexistent/d/o; echo no; }", false)]
    [InlineData("trap 'echo E' ERR\n: 2>> /nonexistent/d/e\necho no", false)]
    [InlineData("set -e\n( : < /nonexistent/zz; echo no )", true)]
    public void Transpile_NeutralBuiltinWithAFileRedirect_IsFollowedByTheOpenFailureCheck(string script, bool input)
    {
        var ps = PsEmitter.Transpile(script)!;
        Assert.Contains(RedirectFailedCheck, ps);
        if (input) Assert.Contains(PsBuild.RedirectFailedVariable + " = $true", ps);
    }

    [Theory]
    [InlineData("set -e\n: > /dev/null\ntrue < /dev/null\n{ :; } 2>/dev/null\n: 2>&1\necho ok")]
    [InlineData(": > /nonexistent/d/o\n{ :; } < /nonexistent/zz\necho no")]   // no errexit, no ERR trap
    public void Transpile_RedirectThatCannotFailOrNoErrexit_HasNoOpenFailureCheck(string script)
        => Assert.DoesNotContain(RedirectFailedCheck, PsEmitter.Transpile(script)!);

    /// <summary>The failure of a compound's input guard is the flag, never an exit/return inside its <c>&amp; { }</c> block
    /// (it would leave only the block in a function or subshell): the check is the statement after the command.</summary>
    [Fact]
    public void InputRedirectGuard_RaisesTheFlagAndClearsANestedOne()
    {
        string guarded = PsBuild.InputRedirectGuard("'f'", "OK");
        Assert.Contains("OK; " + PsBuild.RedirectFailedVariable + " = $false }", guarded);
        Assert.Contains("; $global:LASTEXITCODE = 1; " + PsBuild.RedirectFailedVariable + " = $true } }", guarded);
        Assert.DoesNotContain("exit", guarded);
        Assert.DoesNotContain("return", guarded);
        Assert.DoesNotContain("OK; " + PsBuild.RedirectFailedVariable, PsBuild.InputRedirectGuard("'f'", "OK", clearsNested: false));
    }
}
