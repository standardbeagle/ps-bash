using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the run-time bash text invariant: text known only at run time (<c>eval "$c"</c>, <c>fc -s</c>) runs through ONE
/// emitted call, <c>PsBuild.RunBashTextCall</c> (made by <c>PsEmitter.RunTimeBashTextCall</c>), which carries the nesting
/// bound, the call site's loop mode, its function's locals and the return check.
/// <para>
/// The bug class: <c>fc -s</c> re-implemented eval's call site around <c>PsBuild.RunBashTextAtRunTime</c> and missed all
/// four (<c>history -s 'return 7'; f() { fc -s; echo notreached; }</c> printed notreached; <c>history -s break</c> in a
/// loop did not leave it; <c>history -s 'fc -s'; fc -s</c> had no bound). The runner is private to PsBuild; inside it
/// only the call and <c>BuiltinAsSourceScript</c> reach it (that one runs a static builtin command, <c>eval '…'</c>,
/// whose own emitted form makes the call).
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check and
/// an emitted-text check. Behavior vs bash: Differential <c>FcRerunDifferentialTests</c>.</para>
/// </summary>
public class RunBashTextCallGuardTests
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

    private static readonly Regex MemberHead = new(@"^\s*(?:public|private|internal)\s+(?:static\s+)?string\s+(\w+)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void OnlyTheSharedCallAndBuiltinAsSourceScriptReachTheRunner()
    {
        char sep = Path.DirectorySeparatorChar;
        var reached = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}") || file.Contains($".Tests{sep}")) continue;
            string? member = null;
            foreach (var line in File.ReadLines(file))
            {
                if (MemberHead.Match(line) is { Success: true } m) member = m.Groups[1].Value;
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (line.Contains("RunBashTextAtRunTime(", StringComparison.Ordinal))
                    reached.Add(Path.GetFileName(file) + ":" + member);
            }
        }
        Assert.Equal(
            ["PsBuild.cs:BuiltinAsSourceScript", "PsBuild.cs:RunBashTextAtRunTime", "PsBuild.cs:RunBashTextCall"],
            reached.ToArray());
    }

    private const string CallHead = "if (-not [string]::IsNullOrWhiteSpace($__psbash_eval_src)) { ";

    /// <summary>eval's blank-text branch: status 0. fc has none: its blank text keeps Invoke-BashFc's own status
    /// (<c>history -c; fc -s</c> is 1, <c>fc --bogus</c> 2; PsBuild.RunBashTextCall <c>callerStatus</c>).</summary>
    private const string EvalBlankElse = " else { $global:LASTEXITCODE = 0 }";

    /// <summary>eval and fc at the same call site emit the same call, but for the label in its messages and eval's
    /// blank-text status.</summary>
    [Theory]
    [InlineData("g() { local v=1; for i in 1 2; do CMD; done; }")]
    [InlineData("g() { CMD; }")]
    [InlineData("for i in 1 2; do CMD; done")]
    [InlineData("CMD")]
    [InlineData("( CMD )")]
    public void Transpile_EvalAndFc_RunTheirTextThroughTheSameCall(string shape)
    {
        static string Call(string ps) => ps[ps.IndexOf(CallHead, StringComparison.Ordinal)..];
        var eval = Call(PsEmitter.Transpile(shape.Replace("CMD", "eval \"$c\""))!);
        var fc = Call(PsEmitter.Transpile(shape.Replace("CMD", "fc -s"))!);
        int blank = eval.IndexOf(EvalBlankElse, StringComparison.Ordinal);
        Assert.True(blank > 0, "eval's call has its blank-text branch");
        Assert.Equal(eval.Remove(blank, EvalBlankElse.Length).Replace("eval: ", "fc: "), fc);
        Assert.Contains("__BashEvalDepth", fc);
    }

    [Fact]
    public void Transpile_FcInAFunctionLoop_CarriesLoopModeLocalsAndTheReturnCheck()
    {
        var ps = PsEmitter.Transpile("g() { local v=1; for i in 1 2; do fc -s; done; }")!;
        var call = ps[ps.IndexOf(CallHead, StringComparison.Ordinal)..];
        Assert.Contains(PsBuild.LoopModeSet(0), call);
        Assert.Contains(", [string[]]@('v')", call);
        Assert.Contains(PsBuild.EvalReturnCheck(inFunction: true), call);
    }
}
