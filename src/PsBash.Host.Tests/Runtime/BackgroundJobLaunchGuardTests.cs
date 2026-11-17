using System.Management.Automation.Language;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// Guard for "launching a job lets the job run before the launcher resumes" (docs/specs/background-jobs.md section
/// 12.4): once a background job is started, the launcher's path back to the next bash command must be work-free. On
/// Windows every script-to-.NET method call is reported to AMSI under PowerShell's process-wide lock (slow when Defender
/// is loaded), and the job's own path to its first write has none, so a method call there let the job print first.
/// One primitive starts every job, <c>Start-BashBgJob</c>, and this checks its shape in PsBash.psm1:
/// every job start (<c>BeginInvoke</c>, <c>BeginLive</c>, <c>BashJobProcess]::Start</c>) is its <c>-StartJob</c>
/// block, nothing after the start in it calls a method, and nothing follows a <c>Start-BashBgJob</c> call but a return.
/// The behaviour itself is pinned by <c>BackgroundJobLiveOutputTests.LiveJobStart_*</c>.
/// <para>Oracle note (qa-rubric Directive 1): a structural rule about the runtime's own script; no bash oracle.</para>
/// </summary>
public class BackgroundJobLaunchGuardTests
{
    private const string Primitive = "Start-BashBgJob";
    private static readonly string[] StartMethods = ["BeginInvoke", "BeginLive"];

    private static ScriptBlockAst ParsePsm1()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "PsBash.Module", "PsBash.psm1");
            if (!File.Exists(path)) continue;
            var ast = Parser.ParseFile(path, out _, out var errors);
            Assert.Empty(errors);
            return ast;
        }
        throw new InvalidOperationException("src/PsBash.Module/PsBash.psm1 not found above " + AppContext.BaseDirectory);
    }

    private static bool IsJobStart(InvokeMemberExpressionAst call)
    {
        var name = (call.Member as StringConstantExpressionAst)?.Value;
        if (name is null) return false;
        if (StartMethods.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        return call.Static && string.Equals(name, "Start", StringComparison.OrdinalIgnoreCase)
            && call.Expression is TypeExpressionAst t && t.TypeName.FullName.EndsWith("BashJobProcess", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrimitiveCall(Ast? ast) =>
        ast is CommandAst c && string.Equals(c.GetCommandName(), Primitive, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void EveryJobStart_IsTheStartJobBlockOfStartBashBgJob()
    {
        var offenders = ParsePsm1()
            .FindAll(a => a is InvokeMemberExpressionAst m && IsJobStart(m), searchNestedScriptBlocks: true)
            .Where(a =>
            {
                // The start must sit in a { } passed to Start-BashBgJob.
                for (var p = a.Parent; p is not null; p = p.Parent)
                    if (p is ScriptBlockExpressionAst && IsPrimitiveCall(p.Parent)) return false;
                return true;
            })
            .Select(a => $"line {a.Extent.StartLineNumber}: {a.Extent.Text}")
            .ToList();
        Assert.True(offenders.Count == 0,
            "A background job is started outside Start-BashBgJob, so the launcher may do work (a method call that waits on "
            + "the AMSI lock) after the job runs. Start it through Start-BashBgJob -StartJob { ... }:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void StartBashBgJob_CallsNoMethodAfterTheStart()
    {
        var fn = (FunctionDefinitionAst)ParsePsm1().Find(
            a => a is FunctionDefinitionAst f && string.Equals(f.Name, Primitive, StringComparison.OrdinalIgnoreCase), true)!;
        Assert.NotNull(fn);
        var statements = fn.Body.EndBlock.Statements;
        int startAt = -1;
        for (int i = 0; i < statements.Count; i++)
            if (statements[i].Find(a => a is CommandAst c && c.InvocationOperator == TokenKind.Ampersand
                    && c.CommandElements[0] is VariableExpressionAst { VariablePath.UserPath: "StartJob" }, false) is not null)
                startAt = i;
        Assert.True(startAt >= 0, "Start-BashBgJob no longer runs `& $StartJob`");

        var startStatement = statements[startAt];
        var afterStart = new List<Ast>();
        // The try that starts the job: only its catch (the start failed, nothing runs) may call methods.
        if (startStatement is TryStatementAst tryStart) afterStart.AddRange(tryStart.Body.Statements);
        else afterStart.Add(startStatement);
        afterStart.AddRange(statements.Skip(startAt + 1));
        var calls = afterStart
            .SelectMany(s => s.FindAll(a => a is InvokeMemberExpressionAst, searchNestedScriptBlocks: true))
            .Select(a => $"line {a.Extent.StartLineNumber}: {a.Extent.Text}")
            .ToList();
        Assert.True(calls.Count == 0,
            "Start-BashBgJob calls a .NET method after starting the job; on Windows it waits on the AMSI lock (and the "
            + "AMSI call), and the job can print before the foreground's next command. Do it before the start:\n"
            + string.Join("\n", calls));
    }

    [Fact]
    public void NothingButReturn_FollowsAStartBashBgJobCall()
    {
        var offenders = new List<string>();
        foreach (var call in ParsePsm1().FindAll(IsPrimitiveCall, searchNestedScriptBlocks: true))
        {
            Ast node = call;
            // Climb statement by statement to the function body: at each level, what follows must be nothing or a return.
            while (node.Parent is not null and not FunctionDefinitionAst)
            {
                var parent = node.Parent;
                if (parent is NamedBlockAst or StatementBlockAst)
                {
                    var list = parent is NamedBlockAst nb ? nb.Statements : ((StatementBlockAst)parent).Statements;
                    var index = list.IndexOf((StatementAst)node);
                    if (index >= 0 && index + 1 < list.Count)
                    {
                        if (list[index + 1] is not ReturnStatementAst { Pipeline: null })
                            offenders.Add($"line {list[index + 1].Extent.StartLineNumber}: {list[index + 1].Extent.Text}");
                        break;
                    }
                }
                node = parent;
            }
        }
        Assert.True(offenders.Count == 0,
            "Work follows a Start-BashBgJob call: the job already runs, so it races (and may wait on) the job. Do it first:\n"
            + string.Join("\n", offenders));
    }
}
