using PsBash.Host.Shell;
using Xunit;

namespace PsBash.Host.Tests.Shell;

/// <summary>
/// Tests for bash programmable completion (P5): the <see cref="BashCompletionRegistry"/> parser
/// (`complete -W`/`-r`) and its integration into <see cref="CompletionEngine"/> (Tier-1 word lists
/// surfaced when completing an argument of a registered command).
///
/// Oracle note (qa-rubric Directive 1): `complete`/tab is a ps-bash-specific interactive surface
/// with no byte-comparable bash oracle, so hand-written asserts are justified per the exception
/// list. The registry is process-global state (like the alias table); every test calls
/// <see cref="BashCompletionRegistry.Clear"/> first so the cases are order-independent. All cases
/// live in one class so they never race the static registry against each other (no sleeps —
/// Directive 6). The engine cases use a null worker on purpose: the Tier-1 spec path is local and
/// runs before any runspace round-trip.
/// </summary>
public class BashCompletionTests
{
    private static CompletionEngine NullWorkerEngine() => new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        cwd: () => Environment.CurrentDirectory,
        lastCommand: () => null,
        history: null,
        worker: null);

    // ---- registry parser ------------------------------------------------------------------

    [Fact]
    public void Register_DashW_QuotedWordList_SplitsIntoCandidates()
    {
        BashCompletionRegistry.Clear();
        var handled = BashCompletionRegistry.TryApplyCompleteCommand("complete -W 'start stop restart' svc");

        Assert.True(handled);
        Assert.True(BashCompletionRegistry.HasSpec("svc"));
        Assert.Equal(["start", "stop", "restart"], BashCompletionRegistry.GetCandidates("svc", ""));
        Assert.Equal(["start", "stop"], BashCompletionRegistry.GetCandidates("svc", "st"));
        Assert.Equal(["restart"], BashCompletionRegistry.GetCandidates("svc", "re"));
        Assert.Empty(BashCompletionRegistry.GetCandidates("svc", "zzz"));
    }

    [Fact]
    public void GetCandidates_IsCaseSensitive_LikeBash()
    {
        BashCompletionRegistry.Clear();
        BashCompletionRegistry.TryApplyCompleteCommand("complete -W 'Start STOP stop' svc");

        // bash compgen -W is case-sensitive: "st" matches only the lowercase entry.
        Assert.Equal(["stop"], BashCompletionRegistry.GetCandidates("svc", "st"));
    }

    [Fact]
    public void Register_MultipleNames_ShareTheWordList()
    {
        BashCompletionRegistry.Clear();
        BashCompletionRegistry.TryApplyCompleteCommand("complete -W 'a b' foo bar");

        Assert.Equal(["a", "b"], BashCompletionRegistry.GetCandidates("foo", ""));
        Assert.Equal(["a", "b"], BashCompletionRegistry.GetCandidates("bar", ""));
    }

    [Fact]
    public void Remove_DashR_DropsNamedSpec()
    {
        BashCompletionRegistry.Clear();
        BashCompletionRegistry.TryApplyCompleteCommand("complete -W 'x y' svc");
        var handled = BashCompletionRegistry.TryApplyCompleteCommand("complete -r svc");

        Assert.True(handled);
        Assert.False(BashCompletionRegistry.HasSpec("svc"));
    }

    [Fact]
    public void Remove_DashR_NoName_ClearsAll()
    {
        BashCompletionRegistry.Clear();
        BashCompletionRegistry.TryApplyCompleteCommand("complete -W 'x' a");
        BashCompletionRegistry.TryApplyCompleteCommand("complete -W 'y' b");
        BashCompletionRegistry.TryApplyCompleteCommand("complete -r");

        Assert.False(BashCompletionRegistry.HasSpec("a"));
        Assert.False(BashCompletionRegistry.HasSpec("b"));
    }

    [Fact]
    public void NonCompleteInput_ReturnsFalse_AndIsLeftToTranspile()
    {
        BashCompletionRegistry.Clear();
        Assert.False(BashCompletionRegistry.TryApplyCompleteCommand("ls -la"));
        Assert.False(BashCompletionRegistry.TryApplyCompleteCommand("completely unrelated"));
    }

    [Fact]
    public void DashF_RegistersSpecButHasNoTier1Candidates()
    {
        // Tier 2 (function-based completion) is out of scope: -F is accepted (the line is consumed,
        // not transpiled to a missing `complete`), but it contributes no static word list.
        BashCompletionRegistry.Clear();
        var handled = BashCompletionRegistry.TryApplyCompleteCommand("complete -F _git_complete git");

        Assert.True(handled);
        Assert.True(BashCompletionRegistry.HasSpec("git"));
        Assert.Empty(BashCompletionRegistry.GetCandidates("git", ""));
    }

    // ---- engine integration ---------------------------------------------------------------

    [Fact]
    public async Task CompleteSpec_ArgumentPosition_OffersWordListFilteredByPrefix()
    {
        BashCompletionRegistry.Clear();
        BashCompletionRegistry.TryApplyCompleteCommand("complete -W 'start stop restart' svc");

        const string line = "svc st";
        var result = (await NullWorkerEngine().CompleteAsync(line, line.Length, default)).Texts();

        Assert.Contains("start", result);
        Assert.Contains("stop", result);
        Assert.DoesNotContain("restart", result); // does not match the typed "st"
    }

    [Fact]
    public async Task CompleteSpec_AppliesEvenWithoutAWorker()
    {
        // Proves the Tier-1 path is local: it runs before (and independent of) any runspace query.
        BashCompletionRegistry.Clear();
        BashCompletionRegistry.TryApplyCompleteCommand("complete -W 'alpha beta' deploy");

        const string line = "deploy a";
        var result = (await NullWorkerEngine().CompleteAsync(line, line.Length, default)).Texts();

        Assert.Contains("alpha", result);
        Assert.DoesNotContain("beta", result);
    }

    [Fact]
    public async Task CompleteSpec_CommandPosition_IsNotApplied()
    {
        // At the command word, completion is command-name completion — a registered word list for
        // that same name must not leak in as a candidate.
        BashCompletionRegistry.Clear();
        BashCompletionRegistry.TryApplyCompleteCommand("complete -W 'start stop' svc");

        const string line = "sv";
        var result = (await NullWorkerEngine().CompleteAsync(line, line.Length, default)).Texts();

        Assert.DoesNotContain("start", result);
        Assert.DoesNotContain("stop", result);
    }

    [Fact]
    public async Task UnregisteredCommand_ArgumentPosition_NoSpecCandidates()
    {
        BashCompletionRegistry.Clear();

        const string line = "svc st";
        var result = (await NullWorkerEngine().CompleteAsync(line, line.Length, default)).Texts();

        Assert.DoesNotContain("start", result);
        Assert.DoesNotContain("stop", result);
    }
}
