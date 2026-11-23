using System.Text;
using PsBash.Core.Transpiler;
using PsBash.Host.Runtime;
using PsBash.Testing;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// Text a script builds at run time is bash data: no door may run it as PowerShell. Each door (a nested <c>bash -c</c> /
/// <c>sh -c</c>, <c>flock -c</c>, <c>trap</c>, <c>source &lt;(...)</c>, <c>eval</c>, an alias body) gets a payload PowerShell
/// would run and bash never does:
/// <list type="bullet">
/// <item>a comment with a lone CR (PowerShell ends a <c>#</c> comment at CR; bash reads one comment, no command), which
/// transpiled to null and came back as the raw text;</item>
/// <item>text bash cannot parse but PowerShell can (<c>Write-Output ('SENT'+'INEL')</c>, a PowerShell <c>if (..) { }</c>),
/// which flock, trap and a nested launcher ran as PowerShell.</item>
/// </list>
/// The output must never hold SENTINEL (the payload spells it in two halves, so only PowerShell evaluation joins them).
/// Real bash prints no SENTINEL for any of these scripts (oracle: bash 5.2.21, every line is a syntax error or a comment).
///
/// <para>Oracle note (qa-rubric Directive 1): "never runs as PowerShell" is ps-bash-specific (bash has no PowerShell to
/// fall back to); the bash-visible status/messages are left to the differential suite.</para>
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)] // NestedShell_* sets PSBASH_NESTED_EXE; runs alone, so off the SdkHost lane too
public class BashTextNeverRunsAsPowerShellTests : IAsyncLifetime
{
    private const string Payload = "Write-Output ('SENT'+'INEL')";
    private const string Sentinel = "SENTINEL";

    private readonly HostWorkerFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>The run-time values each door is handed: a comment with a lone CR, PowerShell bash cannot parse.</summary>
    public static TheoryData<string, string> Values => new()
    {
        { "comment-cr", "cr=$'\\r'; x=\"#${cr}" + Payload + "\"" },
        { "comment-cr-lead", "cr=$'\\r'; x=\"  # note${cr}" + Payload + "${cr}\"" },
        { "ps-call", "x=\"" + Payload + "\"" },
        { "ps-if-block", "x=\"if (1) { " + Payload + " }\"" },
        // Pipeline output, not [Console]::WriteLine: that writes past the captured streams and would pass unseen.
        { "ps-static", "x=\"[string]::Concat('SENT','INEL')\"" },
    };

    private static readonly (string Door, string Script)[] InHostDoors =
    {
        ("flock -c", "flock LOCKFILE -c \"$x\" 2>/dev/null"),
        ("trap EXIT", "( trap \"$x\" EXIT; true ) 2>/dev/null"),
        ("trap ERR", "( trap \"$x\" ERR; false ) 2>/dev/null"),
        ("source <()", "source <(printf '%s' \"$x\") 2>/dev/null"),
        ("eval", "eval \"$x\" 2>/dev/null"),
        ("alias", "shopt -s expand_aliases; alias psbguard=\"$x\"; psbguard 2>/dev/null"),
    };

    private static async Task<string> RunBashAsync(SdkWorker worker, string bash)
    {
        var sb = new StringBuilder();
        var gate = new object();
        string ps;
        try { ps = BashTranspiler.Transpile(bash); }
        catch (PsBash.Core.Parser.ParseException ex) { return "transpile error: " + ex.Message; }
        await worker.ExecuteWithOutputAsync(ps,
            s => { lock (gate) sb.Append(s); },
            s => { lock (gate) sb.Append(s); });
        lock (gate) return sb.ToString();
    }

    [Theory]
    [MemberData(nameof(Values))]
    public async Task InHostDoor_RunTimeText_NeverRunsAsPowerShell(string kind, string assign)
    {
        var dir = Directory.CreateTempSubdirectory("psb-guard-");
        try
        {
            foreach (var (door, script) in InHostDoors)
            {
                // No `cd`: the process cwd is shared by every worker, and this directory is deleted at the end.
                var lockFile = "'" + PsBash.Core.BashPath.ToShell(Path.Combine(dir.FullName, "lk")) + "'";
                var bash = assign + "\n" + script.Replace("LOCKFILE", lockFile, StringComparison.Ordinal) + "\necho done";
                var output = await RunBashAsync(_fixture.CreateWorker(), bash);
                Assert.True(output.Contains("done", StringComparison.Ordinal), $"{door} / {kind}: the script did not finish:\n{output}");
                Assert.False(output.Contains(Sentinel, StringComparison.Ordinal), $"{door} / {kind} ran bash data as PowerShell:\n{output}");
            }
        }
        finally { try { dir.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>A nested launcher (<c>bash -c "$x"</c> from a script) runs THIS build's ps-bash: its text was built at run time,
    /// so the launcher's PowerShell fallback for typed text must not apply. Skips when the launcher is not built.</summary>
    [SkippableTheory]
    [MemberData(nameof(Values))]
    public async Task NestedShell_RunTimeText_NeverRunsAsPowerShell(string kind, string assign)
    {
        var launcher = PsBashLocator.Resolve();
        Skip.If(launcher is null, "the ps-bash launcher is not built");
        using var env = ProcessEnv.Set(("PSBASH_NESTED_EXE", launcher), ("PSBASH_PER_INVOCATION", "1"));
        foreach (var shell in new[] { "bash", "sh" })
        {
            var bash = assign + "\n" + shell + " -c \"$x\" 2>/dev/null\necho done";
            var output = await RunBashAsync(_fixture.CreateWorker(), bash);
            Assert.True(output.Contains("done", StringComparison.Ordinal), $"{shell} -c / {kind}: the script did not finish:\n{output}");
            Assert.False(output.Contains(Sentinel, StringComparison.Ordinal), $"{shell} -c / {kind} ran bash data as PowerShell:\n{output}");
        }
    }

    /// <summary>Text with no command (empty, blanks, comments) transpiles to nothing, never to its own text.
    /// REGRESSION: BashTranspiler returned the input (<c>?? bashCommand</c>) and every door ran it as PowerShell.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   \n\t")]
    [InlineData("# only a comment")]
    [InlineData("#\r" + Payload)]
    [InlineData("  # a\r" + Payload + "\n# b\r" + Payload)]
    public void Transpile_TextWithNoCommand_IsEmpty(string bash)
    {
        Assert.Equal("", BashTranspiler.Transpile(bash));
        Assert.Equal("", BashTranspiler.Transpile(bash, TranspileContext.Eval));
        Assert.Equal("", BashTranspiler.TranspileTrapAction(bash, "EXIT"));
    }
}
