using System.Management.Automation.Language;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// Guard for "a builtin resolves a jobspec its own way" (docs/specs/background-jobs.md, job table): <c>kill</c>,
/// <c>wait</c> and <c>fg</c> name a job by <c>%N</c>, <c>%%</c>/<c>%+</c> (the current job), <c>%-</c>; only
/// <c>Find-BashBgJob</c> in PsBash.psm1 maps such a spec to a job-table entry. <c>fg</c> once indexed the table itself
/// and took "the newest job still running" as the current job, so a job <c>kill %%</c> had just signalled (still in the
/// table until reported) was invisible to it: <c>kill %%; fg</c> said "no current job" where bash prints the job's
/// command and returns 143. Here: no function but <c>Find-BashBgJob</c> indexes the job table by position.
/// <para>Oracle note (qa-rubric Directive 1): a structural rule about the runtime's own script; the behaviour is pinned
/// by <c>BackgroundJobDifferentialTests.Differential_Background_FgOfASignalledJob</c>.</para>
/// </summary>
public class JobSpecResolutionGuardTests
{
    private const string Resolver = "Find-BashBgJob";

    [Fact]
    public void JobTable_IsIndexedOnlyByTheJobspecResolver()
    {
        var ast = ParsePsm1();
        var offenders = new List<string>();
        foreach (var fn in ast.FindAll(a => a is FunctionDefinitionAst, true).Cast<FunctionDefinitionAst>())
        {
            if (string.Equals(fn.Name, Resolver, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var index in fn.Body.FindAll(a => a is IndexExpressionAst, true).Cast<IndexExpressionAst>())
                if (index.Target.Extent.Text.Contains("BashBgJobs", StringComparison.Ordinal))
                    offenders.Add($"{fn.Name}: {index.Extent.Text}");
        }
        Assert.True(offenders.Count == 0,
            "Resolve a jobspec with " + Resolver + ", not by indexing the job table: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Fg_ResolvesThroughTheResolver()
    {
        var fg = ParsePsm1().FindAll(a => a is FunctionDefinitionAst { Name: "Invoke-BashFg" }, true).Single();
        Assert.Contains(fg.FindAll(a => a is CommandAst, true).Cast<CommandAst>(), c => c.GetCommandName() == Resolver);
    }

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
}
