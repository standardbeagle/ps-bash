using PsBash.Core.Runtime.Compaction;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Core.Tests.Compaction;

/// <summary>
/// P0 — pure FilterEngine + FilterStage. The parity tests are the regression guard
/// (Directive 1/2): with no matching filter, FilterEngine.Apply must be byte-equal to
/// the generic OutputCompactor it wraps, so nothing regresses for unmatched commands.
/// </summary>
public class FilterEngineTests
{
    [Fact]
    public void SelectOverride_MatchingFilter_ReturnsConfiguredArgv()
    {
        var filter = new FilterSpec
        {
            Name = "git/status",
            Match = new FilterMatch { Command = "git", Args = ["status"] },
            Override = ["git", "status", "--porcelain=v1", "-b"]
        };

        var result = FilterEngine.SelectOverride("git status --short", [filter]);

        Assert.Equal(filter.Override, result);
    }

    [Fact]
    public void SelectOverride_NoMatchingFilter_ReturnsNull()
    {
        Assert.Null(FilterEngine.SelectOverride("git push", []));
    }

    [Theory]
    [InlineData("git diff -p")]
    [InlineData("git diff --name-only")]
    [InlineData("git diff HEAD~1")]
    [InlineData("git log feature-branch")]
    [InlineData("git status -- src/file.cs")]
    public void SelectLaunchOverride_ExplicitSemantics_ReturnsNull(string command)
    {
        var subcommand = command.Split(' ')[1];
        var filter = new FilterSpec
        {
            Name = $"git/{subcommand}",
            Match = new FilterMatch { Command = "git", Args = [subcommand] },
            Override = ["git", subcommand, "--compact-shape"]
        };

        Assert.Null(FilterEngine.SelectLaunchOverride(command, [filter]));
    }
    private static OutputFrame Out(string text) => new(StreamTag.Stdout, text);
    private static OutputFrame Err(string text) => new(StreamTag.Stderr, text);

    // ---- Fallback parity (no filters → identical to OutputCompactor) ----

    public static IEnumerable<object[]> ParityCases()
    {
        yield return ["dotnet build", 0, false, new[] { Out("line a\nline b\n") }];
        yield return ["dotnet test", 1, false, new[] { Out("ok\n"), Err("src/App.cs:42: error CS1002: ; expected\n") }];
        yield return ["sleep 99", 124, true, Array.Empty<OutputFrame>()];
        yield return ["noise", 0, false, Enumerable.Repeat(Out("same\n"), 30).ToArray()];
    }

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void Apply_NoFilter_IsByteEqualToOutputCompactor(
        string command, int exitCode, bool timedOut, OutputFrame[] frames)
    {
        var viaEngine = FilterEngine.Apply(command, exitCode, timedOut, frames);
        var viaCompactor = OutputCompactor.CompactCommandOutput(command, exitCode, timedOut, frames);

        Assert.Equal(viaCompactor, viaEngine);
    }

    [Fact]
    public void Apply_NonMatchingFilter_FallsBackToCompactor()
    {
        var filters = new[] { GitStatusFilter() };
        var frames = new[] { Out("hello\n") };

        var viaEngine = FilterEngine.Apply("ls -la", 0, false, frames, filters);
        var viaCompactor = OutputCompactor.CompactCommandOutput("ls -la", 0, false, frames);

        Assert.Equal(viaCompactor, viaEngine);
    }

    [Fact]
    public void Apply_EmptyFilterList_FallsBack()
    {
        var frames = new[] { Out("x\n") };
        Assert.Equal(
            OutputCompactor.CompactCommandOutput("git status", 0, false, frames),
            FilterEngine.Apply("git status", 0, false, frames, []));
    }

    // ---- Selector ----

    [Fact]
    public void Apply_MatchingCommandAndArgsPrefix_UsesFilter()
    {
        var filters = new[] { GitStatusFilter() };
        var result = FilterEngine.Apply("git status", 0, false, new[] { Out("On branch main\nclean\n") }, filters);

        Assert.DoesNotContain("On branch main", result); // skip rule dropped it
    }

    [Fact]
    public void Apply_CommandMatchesOnLeafPath()
    {
        var filters = new[] { GitStatusFilter() };
        var result = FilterEngine.Apply("/usr/bin/git status", 0, false, new[] { Out("On branch main\nkeep\n") }, filters);

        Assert.DoesNotContain("On branch main", result);
        Assert.Contains("keep", result);
    }

    [Fact]
    public void Apply_WrongSubcommand_DoesNotMatch()
    {
        var filters = new[] { GitStatusFilter() };
        var frames = new[] { Out("On branch main\n") };

        // git log (not status) must not hit the status filter — falls back, keeps the line.
        var result = FilterEngine.Apply("git log", 0, false, frames, filters);
        Assert.Contains("On branch main", result);
    }

    // ---- Stages ----

    [Fact]
    public void Run_Skip_DropsMatchingLines()
    {
        var filter = new FilterSpec
        {
            Name = "t", Match = new FilterMatch { Command = "tool" },
            Skip = ["^drop "],
        };
        var result = FilterEngine.Apply("tool", 0, false,
            new[] { Out("drop me\nkeep me\n") }, new[] { filter });

        Assert.DoesNotContain("drop me", result);
        Assert.Contains("keep me", result);
    }

    [Fact]
    public void Run_Keep_AllowlistsOnlyMatching()
    {
        var filter = new FilterSpec
        {
            Name = "t", Match = new FilterMatch { Command = "tool" },
            Keep = ["error"],
        };
        var result = FilterEngine.Apply("tool", 1, false,
            new[] { Out("info noise\nan error here\nmore noise\n") }, new[] { filter });

        Assert.Contains("an error here", result);
        Assert.DoesNotContain("info noise", result);
        Assert.DoesNotContain("more noise", result);
    }

    [Fact]
    public void Run_Replace_AppliesRegexInOrder()
    {
        var filter = new FilterSpec
        {
            Name = "t", Match = new FilterMatch { Command = "tool" },
            Replace = [new ReplaceRule { Pattern = @"\d+", With = "N" }],
        };
        var result = FilterEngine.Apply("tool", 0, false,
            new[] { Out("count 1234 done\n") }, new[] { filter });

        Assert.Contains("count N done", result);
        Assert.DoesNotContain("1234", result);
    }

    [Fact]
    public void Run_Dedup_CollapsesConsecutiveDuplicates()
    {
        var filter = new FilterSpec
        {
            Name = "t", Match = new FilterMatch { Command = "tool" },
            Dedup = true,
        };
        var result = FilterEngine.Apply("tool", 0, false,
            Enumerable.Repeat(Out("same\n"), 5).ToArray(), new[] { filter });

        Assert.Contains("repeated 4 more times", result);
    }

    [Fact]
    public void Run_StripAnsi_RemovesEscapes()
    {
        var filter = new FilterSpec
        {
            Name = "t", Match = new FilterMatch { Command = "tool" },
            StripAnsi = true,
        };
        var result = FilterEngine.Apply("tool", 0, false,
            new[] { Out("\x1b[31mred\x1b[0m\n") }, new[] { filter });

        Assert.Contains("red", result);
        Assert.DoesNotContain("\x1b[31m", result);
    }

    [Fact]
    public void Run_TrimLines_TrimsWhitespace()
    {
        var filter = new FilterSpec
        {
            Name = "t", Match = new FilterMatch { Command = "tool" },
            TrimLines = true, Skip = ["^indented$"],
        };
        var result = FilterEngine.Apply("tool", 0, false,
            new[] { Out("   indented   \n") }, new[] { filter });

        // After trim the line becomes "indented" and the skip rule drops it.
        Assert.DoesNotContain("indented", result);
    }

    [Fact]
    public void Run_MatchOutput_ShortCircuitsWithTemplate()
    {
        var filter = new FilterSpec
        {
            Name = "t", Match = new FilterMatch { Command = "git", Args = ["status"] },
            MatchOutput = [new MatchOutputRule { Contains = "nothing to commit", Emit = "clean" }],
            Skip = ["."], // would drop everything — proves the short-circuit ran first
        };
        var result = FilterEngine.Apply("git status", 0, false,
            new[] { Out("nothing to commit, working tree clean\n") }, new[] { filter });

        Assert.Contains("clean", result);
    }

    [Fact]
    public void Run_OnSuccessTemplate_UsedWhenExitZero()
    {
        var filter = new FilterSpec
        {
            Name = "t", Match = new FilterMatch { Command = "git", Args = ["push"] },
            OnSuccess = "ok pushed", OnFailure = "push failed",
        };
        var result = FilterEngine.Apply("git push", 0, false, new[] { Out("noise\n") }, new[] { filter });

        Assert.Contains("ok pushed", result);
        Assert.DoesNotContain("push failed", result);
    }

    [Fact]
    public void Run_OnFailureTemplate_UsedWhenExitNonZero()
    {
        var filter = new FilterSpec
        {
            Name = "t", Match = new FilterMatch { Command = "git", Args = ["push"] },
            OnSuccess = "ok", OnFailure = "FAILED:\n{{body}}",
        };
        var result = FilterEngine.Apply("git push", 1, false, new[] { Err("rejected\n") }, new[] { filter });

        Assert.Contains("FAILED:", result);
        Assert.Contains("rejected", result);
    }

    [Fact]
    public void Run_HeaderReflectsExitCodeAndCounts()
    {
        var filter = new FilterSpec { Name = "t", Match = new FilterMatch { Command = "tool" } };
        var result = FilterEngine.Apply("tool", 7, false,
            new[] { Out("a\n"), Err("b\n") }, new[] { filter });

        Assert.Contains("exit=7", result);
        Assert.Contains("stdout_lines=1", result);
        Assert.Contains("stderr_lines=1", result);
    }

    // ---- Security (Directive 12) ----

    [Fact]
    public void Run_TemplateBodyContainingPlaceholder_NotReExpanded()
    {
        var filter = new FilterSpec
        {
            Name = "t", Match = new FilterMatch { Command = "tool" },
            OnSuccess = "{{body}}",
        };
        // Output literally contains the placeholder text; it must survive verbatim,
        // not trigger a second substitution.
        var result = FilterEngine.Apply("tool", 0, false,
            new[] { Out("evil {{body}} payload\n") }, new[] { filter });

        Assert.Contains("evil {{body}} payload", result);
    }

    [Fact]
    public void Run_PathologicalRegex_DoesNotThrowOrHang()
    {
        var filter = new FilterSpec
        {
            Name = "t", Match = new FilterMatch { Command = "tool" },
            Skip = ["(a+)+$"], // classic catastrophic-backtracking pattern
        };
        var evil = new string('a', 5000) + "!";

        // RegexBudget bounds it; a timeout is swallowed (treated as no-match), never thrown.
        var result = FilterEngine.Apply("tool", 0, false, new[] { Out(evil + "\n") }, new[] { filter });
        Assert.Contains("exit=0", result);
    }

    [Fact]
    public void Run_InvalidReplacePattern_LeavesLineUnchanged()
    {
        var filter = new FilterSpec
        {
            Name = "t", Match = new FilterMatch { Command = "tool" },
            Replace = [new ReplaceRule { Pattern = "(", With = "x" }], // invalid regex
        };
        var result = FilterEngine.Apply("tool", 0, false, new[] { Out("keep this\n") }, new[] { filter });

        Assert.Contains("keep this", result);
    }

    // ---- Failure surface (Directive 3) ----

    [Fact]
    public void Run_EmptyFrames_EmitsHeaderOnly()
    {
        var filter = new FilterSpec { Name = "t", Match = new FilterMatch { Command = "tool" } };
        var result = FilterEngine.Apply("tool", 0, false, Array.Empty<OutputFrame>(), new[] { filter });

        Assert.Contains("stdout_lines=0", result);
    }

    [Fact]
    public void Run_CrlfInput_NormalizedToLines()
    {
        var filter = new FilterSpec { Name = "t", Match = new FilterMatch { Command = "tool" } };
        var result = FilterEngine.Apply("tool", 0, false, new[] { Out("a\r\nb\r\n") }, new[] { filter });

        Assert.Contains("[out] a", result);
        Assert.Contains("[out] b", result);
    }

    [Fact]
    public void Run_UnicodeInput_Preserved()
    {
        var filter = new FilterSpec { Name = "t", Match = new FilterMatch { Command = "tool" } };
        var result = FilterEngine.Apply("tool", 0, false, new[] { Out("café 日本語 🚀\n") }, new[] { filter });

        Assert.Contains("café 日本語 🚀", result);
    }

    [Fact]
    public void Apply_NullCommand_Throws()
        => Assert.Throws<ArgumentNullException>(() => FilterEngine.Apply(null!, 0, false, []));

    // ---- Branch-coverage closers (P0 QA review) ----

    [Fact]
    public void Run_Replace_SecondRuleTransformsFirstRuleOutput()
    {
        var filter = new FilterSpec
        {
            Name = "t", Match = new FilterMatch { Command = "tool" },
            Replace =
            [
                new ReplaceRule { Pattern = "alpha", With = "beta" },  // alpha -> beta
                new ReplaceRule { Pattern = "beta",  With = "gamma" }, // then beta -> gamma
            ],
        };
        // Ordered application: alpha -> beta -> gamma. A wrong order (or single pass) leaves "beta".
        var result = FilterEngine.Apply("tool", 0, false, new[] { Out("alpha\n") }, new[] { filter });

        Assert.Contains("gamma", result);
        Assert.DoesNotContain("alpha", result);
        Assert.DoesNotContain("[out] beta", result);
    }

    [Fact]
    public void Run_MatchOutputMisses_PipelineContinues()
    {
        var filter = new FilterSpec
        {
            Name = "t", Match = new FilterMatch { Command = "tool" },
            MatchOutput = [new MatchOutputRule { Contains = "never-present", Emit = "shortcut" }],
            Skip = ["^drop "],
        };
        // matchOutput does NOT hit, so the replace/skip pipeline must still run.
        var result = FilterEngine.Apply("tool", 0, false,
            new[] { Out("drop me\nkeep me\n") }, new[] { filter });

        Assert.DoesNotContain("shortcut", result);
        Assert.DoesNotContain("drop me", result);
        Assert.Contains("keep me", result);
    }

    [Fact]
    public void Run_HardCap_TruncatesToMaxLines()
    {
        var filter = new FilterSpec { Name = "t", Match = new FilterMatch { Command = "tool" } };
        var frames = Enumerable.Range(1, 5).Select(i => Out($"line{i}\n")).ToArray();

        var result = FilterEngine.Apply("tool", 0, false, frames, new[] { filter }, maxLines: 3);

        Assert.Contains("line1", result);
        Assert.Contains("line3", result);
        Assert.DoesNotContain("line5", result);
    }

    [Fact]
    public void IsMatch_ArgvShorterThanRequiredArgs_DoesNotMatch()
    {
        var filters = new[] { GitStatusFilter() }; // requires Args ["status"]
        var frames = new[] { Out("On branch main\n") };

        // bare "git" has no subcommand -> args-too-short branch -> no match -> fallback keeps the line.
        var result = FilterEngine.Apply("git", 0, false, frames, filters);
        Assert.Contains("On branch main", result);
    }

    [Fact]
    public void Apply_EmptyCommandString_FallsBackWithoutThrow()
    {
        var filters = new[] { GitStatusFilter() };
        var frames = new[] { Out("x\n") };

        var result = FilterEngine.Apply("", 0, false, frames, filters);
        Assert.Equal(OutputCompactor.CompactCommandOutput("", 0, false, frames), result);
    }

    [Fact]
    public void Run_TimedOut_HeaderShowsTimeoutThroughMatchedFilter()
    {
        var filter = new FilterSpec { Name = "t", Match = new FilterMatch { Command = "tool" } };

        var result = FilterEngine.Apply("tool", 124, true, new[] { Out("partial\n") }, new[] { filter });

        Assert.Contains("timeout=true", result);
        Assert.Contains("exit=124", result);
    }

    private static FilterSpec GitStatusFilter() => new()
    {
        Name = "git/status",
        Match = new FilterMatch { Command = "git", Args = ["status"] },
        Skip = ["^On branch "],
    };
}
