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
}
