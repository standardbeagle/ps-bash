using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Emitter detection for the fused-pipeline lane (PERF task
/// 01KXQ0KMG5C26BWXNVPZXBVA6H, phase 2). An all-mapped, terminal-bound, plain-`|`
/// pipeline is wrapped in <c>Invoke-BashFusedPipeline { … }</c>; every fallback
/// trigger (unmapped stage, per-stage redirect / env-prefix, <c>|&amp;</c>, kill
/// switch, capture context, non-pipeline) keeps today's PowerShell-pipeline path.
///
/// The kill switch is exercised through <see cref="PsEmitter.FusionEnabledOverride"/>
/// (a <c>[ThreadStatic]</c> seam) and the pure
/// <see cref="PsEmitter.IsFusionDisabledByEnvValue"/> parse, so no test mutates the
/// process-global <c>PSBASH_FUSED</c> env var — that would race parallel transpiles
/// of the fusable pipelines other test classes assert.
/// </summary>
public class PsEmitterFusedPipelineTests
{
    private const string Wrap = "Invoke-BashFusedPipeline { ";

    [Fact]
    public void Fused_AllMappedTwoStage_WrapsWholePipeline()
    {
        var result = PsEmitter.Transpile("seq 1 100 | wc -l");
        Assert.Equal("Invoke-BashFusedPipeline { Invoke-BashSeq 1 100 | Invoke-BashWc -l }", result);
    }

    [Fact]
    public void Fused_SedChain_WrapsWholePipeline()
    {
        var result = PsEmitter.Transpile("cat f | sed 's/a/b/' | wc -l");
        Assert.StartsWith(Wrap, result);
        Assert.Contains("Invoke-BashCat f", result);
        Assert.Contains("Invoke-BashSed", result);
        Assert.Contains("Invoke-BashWc -l", result);
        Assert.EndsWith(" }", result);
    }

    [Fact]
    public void Fused_GrepChain_WrapsWholePipeline()
    {
        var result = PsEmitter.Transpile("cat f | grep foo | head -n 3");
        Assert.StartsWith(Wrap, result);
        Assert.Contains("Invoke-BashGrep foo", result);
    }

    [Fact]
    public void Fallback_UnmappedFirstStage_NotFused()
    {
        // ls is a typed-object producer, deliberately NOT in the allowlist.
        var result = PsEmitter.Transpile("ls | grep foo");
        Assert.DoesNotContain("Invoke-BashFusedPipeline", result);
    }

    [Fact]
    public void Fallback_AwkStage_NotFused()
    {
        var result = PsEmitter.Transpile("seq 1 5 | awk '{print}'");
        Assert.DoesNotContain("Invoke-BashFusedPipeline", result);
    }

    [Fact]
    public void Fallback_PerStageRedirect_NotFused()
    {
        var result = PsEmitter.Transpile("seq 1 5 | wc -l > out.txt");
        Assert.DoesNotContain("Invoke-BashFusedPipeline", result);
    }

    [Fact]
    public void Fallback_EnvPrefixStage_NotFused()
    {
        var result = PsEmitter.Transpile("seq 1 5 | FOO=bar wc -l");
        Assert.DoesNotContain("Invoke-BashFusedPipeline", result);
    }

    [Fact]
    public void Fallback_StderrMergePipe_NotFused()
    {
        // |& (stderr-merge) is out of scope this slice → fall back.
        var result = PsEmitter.Transpile("seq 1 5 |& grep 3");
        Assert.DoesNotContain("Invoke-BashFusedPipeline", result);
    }

    [Fact]
    public void Fallback_NegatedPipeline_NotFused()
    {
        var result = PsEmitter.Transpile("! grep x f | wc -l");
        Assert.DoesNotContain("Invoke-BashFusedPipeline", result);
    }

    [Fact]
    public void Fallback_SingleMappedCommand_NotFused()
    {
        // A lone command is a Simple, not a Pipeline — nothing to fuse.
        var result = PsEmitter.Transpile("seq 1 100");
        Assert.DoesNotContain("Invoke-BashFusedPipeline", result);
    }

    [Fact]
    public void Fallback_NestedInCommandSub_NotFused()
    {
        // Captured output ($()) never crosses the IPC return path — no batching win,
        // and fusing would change the captured object shape.
        var result = PsEmitter.Transpile("echo $(seq 1 5 | wc -l)");
        Assert.DoesNotContain("Invoke-BashFusedPipeline", result);
    }

    [Fact]
    public void Fallback_NestedInProcessSub_NotFused()
    {
        var result = PsEmitter.Transpile("diff <(sort a | uniq) b");
        Assert.DoesNotContain("Invoke-BashFusedPipeline", result);
    }

    [Fact]
    public void KillSwitch_OverrideOff_NotFused()
    {
        PsEmitter.FusionEnabledOverride = false;
        try
        {
            var result = PsEmitter.Transpile("seq 1 100 | wc -l");
            Assert.DoesNotContain("Invoke-BashFusedPipeline", result);
            Assert.Equal("Invoke-BashSeq 1 100 | Invoke-BashWc -l", result);
        }
        finally
        {
            PsEmitter.FusionEnabledOverride = null;
        }
    }

    [Fact]
    public void KillSwitch_OverrideOn_Fused()
    {
        PsEmitter.FusionEnabledOverride = true;
        try
        {
            var result = PsEmitter.Transpile("seq 1 100 | wc -l");
            Assert.StartsWith(Wrap, result);
        }
        finally
        {
            PsEmitter.FusionEnabledOverride = null;
        }
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("false", true)]
    [InlineData("FALSE", true)]
    [InlineData("no", true)]
    [InlineData("off", true)]
    [InlineData(" off ", true)]
    [InlineData("1", false)]
    [InlineData("true", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsFusionDisabledByEnvValue_ParsesFalsyTokens(string? value, bool expectedDisabled)
    {
        Assert.Equal(expectedDisabled, PsEmitter.IsFusionDisabledByEnvValue(value));
    }
}
