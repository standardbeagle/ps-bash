using System.Management.Automation;
using System.Management.Automation.Language;
using Xunit;
using PsParser = System.Management.Automation.Language.Parser;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// An in-process job compiles nothing on its way to its first statement (docs/specs/background-jobs.md section 12.5,
/// cause 7): the job function (<see cref="JobScript.Text"/>) and the job's body are compiled before the launch, and the
/// body is the same compiled script block for every job that runs it. On Windows every compile is an AMSI scan under a
/// process-wide lock (40-130 ms per job on a loaded host): jobs that compiled their own <c>AddScript</c> text began that
/// late, and the oils cases "wait -n" / "wait for N parallel jobs" (sleeps of 10-90 ms) saw them end in launch order.
/// <para>Oracle note (qa-rubric Directive 1): when a script block is compiled is invisible to bash; the bash-visible
/// result is the spec cases' (spec/oils/background.spec.sh) and BackgroundJobLiveOutputTests'.</para>
/// </summary>
public class JobScriptTests
{
    // Each test's own body text: PowerShell's script-block cache is process-wide.
    private static string Unique(string what) => $"Invoke-BashEcho {what}_{Guid.NewGuid():N}";

    /// <summary>A PowerShell upgrade that renames the internal compile entry point must fail here, not slow every job.</summary>
    [Fact]
    public void PowerShell_HasTheCompileEntryPoint()
    {
        Assert.True(JobScript.CanCompile, "ScriptBlock.Compile(bool) not found: jobs would compile their bodies after their start");
        Assert.NotNull(JobScript.IsCompiled(ScriptBlock.Create(Unique("probe"))));
    }

    [Fact]
    public void Body_IsCompiledWhenTheLauncherGetsIt()
    {
        string text = Unique("body");
        Assert.False(JobScript.IsCompiled(ScriptBlock.Create(text)));

        Assert.True(JobScript.IsCompiled(JobScript.Body(text)));
    }

    /// <summary>The second job with a body shares the first one's compile: the same text finds it compiled.</summary>
    [Fact]
    public void Body_SameText_SharesOneCompile()
    {
        string text = Unique("shared");
        JobScript.Body(text);

        Assert.True(JobScript.IsCompiled(ScriptBlock.Create(text)));
    }

    [Fact]
    public void CompileFunction_CompilesTheTextEveryJobRuns()
    {
        JobScript.CompileFunction();

        Assert.True(JobScript.IsCompiled(ScriptBlock.Create(JobScript.Text)));
    }

    /// <summary>
    /// The host compiles, before the command runs, exactly the bodies its jobs will ask for: the text
    /// <c>$Command.ToString()</c> gives (the body between the braces), in-process jobs only, nested ones included.
    /// </summary>
    [Fact]
    public void Prepare_CompilesEveryInProcessBodyTheCommandCanStart()
    {
        string live = Unique("live"), plain = Unique("plain"), nested = Unique("nested"), isolated = "$global:x = '" + Guid.NewGuid() + "'";
        string script = $"Invoke-BashBackground -Live {{ {live} }} -Text 'a'; function f {{ Invoke-BashBackground {{ {nested} }} }}; " +
                        $"Invoke-BashBackground -Isolated {{ {isolated} }}; if ($true) {{ Invoke-BashBackground {{ {plain} }} }}";

        Assert.Equal(3, JobScript.Prepare(script, includeIsolated: false));

        Assert.True(JobScript.IsCompiled(ScriptBlock.Create($" {live} ")));
        Assert.True(JobScript.IsCompiled(ScriptBlock.Create($" {nested} ")));
        Assert.True(JobScript.IsCompiled(ScriptBlock.Create($" {plain} ")));
        Assert.False(JobScript.IsCompiled(ScriptBlock.Create($" {isolated} ")));
    }

    /// <summary>The body text the host finds is the text the job gets: a script block's own <c>ToString()</c>.</summary>
    [Fact]
    public void BodyTexts_AreWhatTheScriptBlockReports()
    {
        string body = Unique("same") + "; $a = @{ k = 'v' }";
        string script = "Invoke-BashBackground -Live { " + body + " }";
        var literal = (ScriptBlockExpressionAst)PsParser.ParseInput(script, out _, out _).Find(a => a is ScriptBlockExpressionAst, true)!;

        Assert.Equal([literal.ScriptBlock.GetScriptBlock().ToString()], JobScript.BodyTexts(script, includeIsolated: false));
        Assert.Equal(" " + body + " ", JobScript.BodyTexts(script, includeIsolated: false).Single());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Invoke-BashEcho x")]
    [InlineData("Invoke-BashBackground { unclosed")]
    public void Prepare_NoJobOrUnparsable_CompilesNothing(string script)
    {
        Assert.Equal(0, JobScript.Prepare(script, includeIsolated: false));
    }

    /// <summary>
    /// Nothing before the body calls a .NET method (an AMSI report under the same lock, section 12.4), and the body runs
    /// dot-sourced (the compile <see cref="JobScript.Compile"/> made is for that mode).
    /// </summary>
    [Fact]
    public void Text_CallsNoMethodBeforeTheBody_AndDotSourcesIt()
    {
        var ast = PsParser.ParseInput(JobScript.Text, out _, out var errors);
        Assert.Empty(errors);
        var body = (CommandAst?)ast.Find(a => a is CommandAst c && c.InvocationOperator == TokenKind.Dot
            && c.CommandElements.Count == 1 && c.CommandElements[0].Extent.Text == "$__bgBody", true);
        Assert.NotNull(body);

        var early = ast.FindAll(a => a is InvokeMemberExpressionAst && a.Extent.StartOffset < body!.Extent.StartOffset, true)
            .Select(a => a.Extent.Text).ToList();
        Assert.Empty(early);
    }

    /// <summary>
    /// Guard: the job's pipeline holds no script text. <c>AddScript</c> text is parsed and compiled (AMSI-scanned) by every
    /// invocation, even when it repeats, after the job's start.
    /// </summary>
    [Fact]
    public void JobRunspaces_AddNoScriptText()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets", "BashJobRunspaces.cs"));
        Assert.DoesNotContain("AddScript(", src);
        Assert.Contains("AddCommand(JobScript.FunctionName, useLocalScope: false)", src);
        Assert.Contains("JobScript.Body(body.ToString())", src);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }
}
