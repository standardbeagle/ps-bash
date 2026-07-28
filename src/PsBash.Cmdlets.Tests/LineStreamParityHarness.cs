using System.Management.Automation;
using System.Text;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Shared harness for the fused-lane streaming-core parity suites
/// (<see cref="LineStreamSortParityTests"/>, <see cref="LineStreamUniqParityTests"/>).
///
/// <para><b>The real cmdlet is the oracle</b> (Directive 1 — no hand-written
/// expectations where a comparison is possible): a case feeds the SAME input lines to
/// (a) the streaming core via <see cref="LineStreamRegistry.TryCreate"/> and (b) the
/// real <c>Invoke-Bash*</c> cmdlet in a live runspace, then diffs. Deliberately NOT a
/// bash oracle: the cmdlets carry known pre-existing divergences from GNU (notably
/// <c>sort -k</c> / <c>-V</c>), and a core matching bash instead of the cmdlet would
/// make the fused and unfused lanes disagree — strictly worse than a documented
/// divergence.</para>
/// </summary>
public abstract class LineStreamParityHarness : IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;

    protected LineStreamParityHarness(SharedPwshFixture fixture) => _fixture = fixture;

    /// <summary>Run the real cmdlet with <paramref name="lines"/> as pipeline input and
    /// return each output object's BashText. Lines are passed as a PS array literal, so
    /// the cmdlet sees exactly one record per line — the shape a fused stage feeds.
    /// Every arg is single-quoted (the emitter's own passthrough-quoting rule for
    /// comma/brace-bearing flags, applied uniformly here) so an argv token can never be
    /// re-read by the PowerShell tokenizer as an array literal or a foreign parameter
    /// name; quoted args land in the cmdlet's <c>Arguments</c> and take its documented
    /// manual-scan path.</summary>
    private List<string> RunCmdlet(string cmdlet, string[] argv, IEnumerable<string> lines)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.Commands.Clear();
        var arr = string.Join(",", lines.Select(l => "'" + l.Replace("'", "''") + "'"));
        var argsLiteral = string.Join(" ", argv.Select(a => "'" + a.Replace("'", "''") + "'"));
        var result = pwsh.AddScript($"@({arr}) | {cmdlet} {argsLiteral}").Invoke();
        pwsh.Commands.Clear();
        var outLines = new List<string>();
        foreach (var o in result)
        {
            if (o is null) continue;
            var bt = o.Properties["BashText"]?.Value;
            outLines.Add(bt?.ToString() ?? o.BaseObject as string ?? o.ToString());
        }
        return outLines;
    }

    /// <summary>Build the streaming core for <paramref name="argv"/> and run it. Fails
    /// loudly if the core DECLINES an argv the suite calls certified.</summary>
    protected static List<string> RunCore(string name, string[] argv, IEnumerable<string> lines)
    {
        Assert.True(LineStreamRegistry.TryCreate(name, argv, out var stage),
            $"streaming core declined a certified argv: {name} {string.Join(' ', argv)}");
        return stage.Run(lines).ToList();
    }

    /// <summary>Core output must equal cmdlet output, line for line.</summary>
    protected void AssertCoreMatchesCmdlet(string name, string cmdlet, string[] argv, IEnumerable<string> lines)
    {
        var input = lines.ToList();
        var expected = RunCmdlet(cmdlet, argv, input);
        var actual = RunCore(name, argv, input);
        Assert.Equal(string.Join("\n", expected), string.Join("\n", actual));
    }

    /// <summary>Mirrors <c>SdkWorker.GetOutputText</c> (see
    /// <c>InvokeBashFusedPipelineCommandTests.Render</c>) so two renders diff at the
    /// byte level the launcher would see.</summary>
    private static string Render(IEnumerable<PSObject> objs)
    {
        var sb = new StringBuilder();
        foreach (var o in objs)
        {
            if (o is null) continue;
            var bt = o.Properties["BashText"]?.Value;
            if (bt is not null)
            {
                sb.Append(bt.ToString());
                if (o.Properties["NoTrailingNewline"]?.Value is not true) sb.Append(Environment.NewLine);
                continue;
            }
            if (o.BaseObject is string s) { sb.Append(s).Append(Environment.NewLine); continue; }
            sb.Append(o.ToString()).Append(Environment.NewLine);
        }
        return sb.ToString();
    }

    protected string RenderScript(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.Commands.Clear();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return Render(result);
    }

    /// <summary>End-to-end through the real fused cmdlet. The <c>-Fallback</c> THROWS,
    /// so a green assertion also proves the STREAMING lane (not the scriptblock lane)
    /// produced the bytes.</summary>
    protected void AssertStreamedMatchesUnfused(string unfusedInner, string stagesLiteral)
        => Assert.Equal(
            RenderScript(unfusedInner),
            RenderScript($"Invoke-BashFusedPipeline -Stages {stagesLiteral} -Fallback {{ throw 'fell back to scriptblock lane' }}"));

    /// <summary>Split a space-separated flag string into argv (empty → no args).</summary>
    protected static string[] Split(string flags)
        => string.IsNullOrWhiteSpace(flags)
            ? Array.Empty<string>()
            : flags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
