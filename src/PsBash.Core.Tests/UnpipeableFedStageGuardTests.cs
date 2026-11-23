using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the fed-stage invariant: a command fed records (the stage after a pipe, or after the <c>Invoke-BashCat f</c> of
/// its <c>&lt; f</c>) whose emission is no pipe element (a statement list such as exec's <c>cmd; return</c>, an env
/// prefix's save/restore, or a <c>$( )</c> expression) is re-emitted in a stdin scope, whose readers take the records.
/// Wrapped in <c>&amp; { }</c> (<c>PsEmitter.WrapPipelineStageIfStatementList</c>) its statements never see the block's
/// <c>$input</c>, so the wrap is only for a stage that is not fed.
/// <para>
/// REGRESSION (ec198f16): the input-redirect site went through the wrap so <c>true &lt; f</c> would parse, and
/// <c>( exec cat &lt; &lt;(echo q3) )</c> became <c>... | &amp; { Invoke-BashCat; return }</c>: nothing printed, and
/// <c>( exec cat &lt; f; echo after )</c> printed only "after". <c>echo q | { X=1 cat; }</c> printed nothing too (the
/// prefixed command in a stdin scope was never fed).
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): emitted-text shapes and a call-site inventory; behavior vs bash: Differential
/// <c>ShellLanguageGapDifferentialTests.FedStatementListStage_ReadsItsInput</c>.</para>
/// </summary>
public class UnpipeableFedStageGuardTests
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

    /// <summary>Members that may call the wrap, each with why the stage it wraps is not fed through it.</summary>
    private static readonly Dictionary<string, string> WrapCallers = new(StringComparer.Ordinal)
    {
        ["PsEmitter.cs:EmitPipelineStages"] = "the first stage, which no pipe feeds; a later stage that is neither simple nor compound "
            + "(nothing in it reads the pipe); and a later pipefail stage only when its text is a depth-0 statement list or an "
            + "expression, which a fed simple stage never is (EmitSimplePipeStage re-emits an unpipeable one in a stdin scope, "
            + "the drain / env / subshell wrappers are one `& { }` element): what is left is the constant-status no-op "
            + "`Out-Null; status`, which reads nothing. A command whose `; ` is nested in an argument (`head -n $(...; ...)`) "
            + "is one pipe element and is not wrapped (PsBuild.IsPipeElement)",
    };

    [Fact]
    public void WrapPipelineStageIfStatementList_IsCalledOnlyWhereTheStageIsNotFed()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser");
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "PsEmitter*.cs"))
        {
            string? member = null;
            foreach (var line in File.ReadLines(file))
            {
                if (MemberHead.Match(line) is { Success: true } m) member = m.Groups[1].Value;
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal)) continue;
                if (line.Contains("WrapPipelineStageIfStatementList(", StringComparison.Ordinal) && member != "WrapPipelineStageIfStatementList")
                    found.Add(Path.GetFileName(file) + ":" + member);
            }
        }
        var unknown = found.Where(f => !WrapCallers.ContainsKey(f)).ToList();
        Assert.True(unknown.Count == 0,
            "WrapPipelineStageIfStatementList hands its statements no input. A FED stage that is no pipe element "
            + "(IsUnpipeableStage) runs in a stdin scope instead; list a caller here only with the reason its stage is not fed:\n  "
            + string.Join("\n  ", unknown));
        var stale = WrapCallers.Keys.Where(k => !found.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "WrapCallers members that no longer call the wrap: " + string.Join(", ", stale));
    }

    /// <summary>The reader inside the statement list takes the shared stdin cursor.</summary>
    private static readonly string FedCat = PsBuild.StdinFeed + " | Invoke-BashCat";

    [Theory]
    [InlineData("( exec cat < f )")]
    [InlineData("( exec cat < <(echo q3) )")]
    [InlineData("( exec cat < f; echo after )")]
    [InlineData("exec cat < f")]
    [InlineData("y=$(exec cat < f)")]
    [InlineData("k() { exec cat < f; echo no; }")]
    [InlineData("( exec 2>/dev/null cat < f )")]
    [InlineData("echo q | { X=1 cat; }")]
    [InlineData("cat f | ( X=1 cat )")]
    [InlineData("while read -r l; do X=1 cat; done < f")]
    public void Transpile_FedStatementList_ReadsThroughTheStdinScope(string script)
    {
        var ps = PsEmitter.Transpile(script)!;
        Assert.Contains(FedCat, ps);
        Assert.DoesNotContain("& { Invoke-BashCat;", ps);
        Assert.DoesNotContain("& { Invoke-BashCat 2>$null;", ps);
    }

    /// <summary>
    /// An expression fed a file (<c>true &lt; f</c>, a return) is no pipe element either: it runs in the scope, never after
    /// a bare <c>|</c> (the whole script failed to parse, ec198f16).
    /// </summary>
    [Theory]
    [InlineData("true < f")]
    [InlineData(": < f")]
    [InlineData("false < f")]
    public void Transpile_ExpressionFedAFile_IsNotABarePipeElement(string script)
    {
        var ps = PsEmitter.Transpile(script)!;
        Assert.DoesNotContain("-RawStdinBytes | $(", ps);
        Assert.DoesNotContain("| ForEach-Object { Get-BashText $_ } | $(", ps);
    }

    /// <summary>A single-command stage after the file stays a plain pipe element (no scope, no wrap).</summary>
    [Theory]
    [InlineData("cat < f", "-RawStdinBytes | Invoke-BashCat")]
    [InlineData("sort -r < f", "-RawStdinBytes | Invoke-BashSort")]
    public void Transpile_SingleCommandFedAFile_StaysAPlainStage(string script, string expected)
    {
        var ps = PsEmitter.Transpile(script)!;
        Assert.Contains(expected, ps);
        Assert.DoesNotContain("__BashStdIn", ps);
    }

    /// <summary>
    /// A single reader whose ARGUMENTS hold a nested <c>; </c> (set -u's arithmetic guard <c>$($__psbash_av = …; …)</c>, a
    /// <c>&lt;( )</c> body's state scope, a guarded <c>"$x"</c>) is one pipe element: the file is piped straight into it.
    /// REGRESSION (3eb541b6): "any <c>; </c>" took it for a statement list and re-emitted it unfed in a scope, so
    /// <c>set -u; head -n $((n+1)) &lt; f</c> and <c>grep -Fxf &lt;(git ls-files) &lt; f</c> printed nothing.
    /// </summary>
    [Theory]
    [InlineData("set -u; n=1; head -n $((n+1)) < f", "-RawStdinBytes | Invoke-BashHead")]
    [InlineData("n=1; head -n $((n+1)) < f", "-RawStdinBytes | Invoke-BashHead")]
    [InlineData("grep -Fxf <(git ls-files) < f", "-RawStdinBytes | Invoke-BashGrep")]
    [InlineData("set -u; x=-; cat \"$x\" < f", "-RawStdinBytes | Invoke-BashCat")]
    [InlineData("set -u; cat \"${x:-}\" < f", "-RawStdinBytes | Invoke-BashCat")]
    public void Transpile_SingleReaderWithNestedSeparator_IsAPlainFedStage(string script, string expected)
    {
        var ps = PsEmitter.Transpile(script)!;
        Assert.Contains(expected, ps);
        Assert.DoesNotContain("New-BashFileStdin", ps);
    }

    /// <summary>
    /// The statement list re-emitted at the input site is FED (EmitSimpleFed: its reader takes the scope's cursor), and the
    /// scope is a lazily read cursor over the file (<c>New-BashFileStdin</c>), not <c>Invoke-BashCat f | . { … }</c>,
    /// which read the whole file before the command started (<c>exec head -c 5 &lt; /dev/zero</c> never ended).
    /// </summary>
    [Theory]
    [InlineData("exec cat < f")]
    [InlineData("X=1 exec cat < f")]
    [InlineData("( exec cat < f )")]
    [InlineData("eval cat < f")]
    [InlineData("set -u; n=1; eval 'head -n $((n+1))' < f")]
    [InlineData("exec cat < /dev/zero")]
    [InlineData("command exec cat < f")]
    public void Transpile_FedStatementListAtTheInputSite_ReadsALazyFileCursor(string script)
    {
        var ps = PsEmitter.Transpile(script)!;
        int scope = ps.IndexOf("New-BashFileStdin", StringComparison.Ordinal);
        Assert.True(scope >= 0, ps);
        Assert.True(ps.IndexOf(PsBuild.StdinFeed + " | Invoke-Bash", scope, StringComparison.Ordinal) > scope, ps);
        Assert.DoesNotContain("-RawStdinBytes | . {", ps);
    }

    /// <summary><c>( exec cat &lt; f; echo after )</c>: the fed cat sits INSIDE the file's scope, and what follows exec runs
    /// after the scope has restored the stdin (it is never reached at run time: exec ended the subshell).</summary>
    [Fact]
    public void Transpile_ExecFedAFileThenMore_TheFeedIsInsideTheFileScope()
    {
        var ps = PsEmitter.Transpile("( exec cat < f; echo after )")!;
        int open = ps.IndexOf("New-BashFileStdin", StringComparison.Ordinal);
        int feed = ps.IndexOf(PsBuild.StdinFeed + " | Invoke-BashCat", StringComparison.Ordinal);
        int restore = ps.IndexOf("$global:__BashStdIn = $__psbash_fin_prev", StringComparison.Ordinal);
        int after = ps.IndexOf("after", StringComparison.Ordinal);
        Assert.True(open >= 0 && open < feed && feed < restore && restore < after, ps);
    }

    /// <summary>
    /// A later pipefail stage whose arguments hold a nested <c>; </c> is one pipe element and is not wrapped in
    /// <c>&amp; { }</c>, whose statements never see the stage's records. REGRESSION: <c>set -euo pipefail; n=1;
    /// printf 'a\nb\nc\n' | head -n $((n+1))</c> printed nothing.
    /// </summary>
    [Theory]
    [InlineData("set -euo pipefail; n=1; printf 'a\\nb\\nc\\n' | head -n $((n+1))", "Invoke-BashHead")]
    [InlineData("set -o pipefail; n=1; printf 'a\\nb\\n' | head -n $((n+1)) | cat", "Invoke-BashHead")]
    [InlineData("set -uo pipefail; printf 'a\\n' | grep -f <(git ls-files)", "Invoke-BashGrep")]
    public void Transpile_PipefailStageWithNestedSeparator_IsNotWrapped(string script, string reader)
    {
        var ps = PsEmitter.Transpile(script)!;
        Assert.Contains(reader, ps);
        Assert.DoesNotContain("& { " + reader, ps);
    }

    /// <summary>
    /// An <c>exec</c> pipe stage is its command, so it streams (<c>tail -f log | exec grep x</c>): emitted as a state-builtin
    /// subshell its stdin scope collected every record before the command started, and an endless producer never let it.
    /// </summary>
    [Theory]
    [InlineData("cat /dev/zero | exec head -c 5", "cat /dev/zero | head -c 5")]
    [InlineData("tail -f log | exec grep x", "tail -f log | grep x")]
    [InlineData("echo q | command exec cat", "echo q | cat")]
    [InlineData("echo q | builtin exec -- sort -r | cat", "echo q | sort -r | cat")]
    [InlineData("echo q | X=1 exec cat", "echo q | X=1 cat")]
    public void Transpile_ExecPipeStage_IsTheCommandItself(string script, string same)
    {
        // The fused lane is an optimization of all-mapped pipelines that an exec stage does not take; off, the two
        // emissions are the plain stage chain.
        PsEmitter.FusionEnabledOverride = false;
        try
        {
            Assert.Equal(PsEmitter.Transpile(same), PsEmitter.Transpile(script));
        }
        finally
        {
            PsEmitter.FusionEnabledOverride = null;
        }
    }

    /// <summary>
    /// The command after <c>Read-BashFd N |</c> reads that pipe even inside a stdin scope (a script that reads the
    /// launcher's stdin, a group given <c>&lt; f</c>). REGRESSION: <c>exec 3&lt;f; eval cat &lt;&amp;3</c> inlined eval's
    /// cat fed from the scope: it printed nothing, and on a terminal waited for its input.
    /// </summary>
    [Theory]
    [InlineData("eval cat <&3", "Read-BashFd 3 | Invoke-BashCat")]
    [InlineData("builtin eval cat <&3", "Read-BashFd 3 | Invoke-BashCat")]
    [InlineData("eval 'cat <&3'", "Read-BashFd 3 | Invoke-BashCat")]
    [InlineData("fd=3; eval cat <&$fd", "Read-BashFd ([int]$__psbFd) | Invoke-BashCat")]
    public void Transpile_DescriptorPipeInAStdinScope_FeedsTheCommand(string script, string expected)
    {
        var launcher = PsBash.Core.Transpiler.BashTranspiler.TranspileWithLauncherStdin(script)!;
        Assert.Contains(expected, launcher);
        var group = PsEmitter.Transpile("{ " + script + "; } < f")!;
        Assert.Contains(expected, group);
    }

    /// <summary>
    /// eval / source / . through <c>builtin</c> or <c>command</c> are shell text: given <c>&lt; f</c> their readers take the
    /// file from a lazy scope. REGRESSION: <c>command . ./s &lt; f</c> piped the file into a bare <c>. $(...)</c> (the sourced
    /// read got nothing), and in a script that reads stdin <c>command eval cat &lt; f</c> fed cat from the script's scope.
    /// </summary>
    [Theory]
    [InlineData("command . ./s < f")]
    [InlineData("command -- source ./s < f")]
    [InlineData("builtin source ./s < f")]
    [InlineData("command eval cat < f")]
    [InlineData("builtin eval cat < f")]
    public void Transpile_ShellTextThroughAWrapperFedAFile_ReadsALazyFileCursor(string script)
    {
        foreach (var ps in new[] { PsEmitter.Transpile(script)!, PsBash.Core.Transpiler.BashTranspiler.TranspileWithLauncherStdin("read x; " + script)! })
        {
            Assert.Contains("New-BashFileStdin", ps);
            Assert.DoesNotContain("-RawStdinBytes |", ps);
        }
    }

    /// <summary>The command after a file's or a here-document's pipe reads that pipe inside a stdin scope.</summary>
    [Theory]
    [InlineData("{ cat < g; } < f", "Invoke-BashCat g -RawStdinBytes | ")]
    [InlineData("{ cat <<< hs; } < f", "| Emit-BashLine | Invoke-BashCat")]
    public void Transpile_PipeIntoACommandInAStdinScope_IsNotTheScopesFeed(string script, string pipe)
    {
        var ps = PsEmitter.Transpile(script)!;
        int at = ps.IndexOf(pipe, StringComparison.Ordinal);
        Assert.True(at >= 0, ps);
        Assert.False(ps.AsSpan(at + pipe.Length).StartsWith("& { $__psbash_q = $global:__BashStdIn"), ps);
    }
}
