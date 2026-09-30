using PsBash.Cmdlets.Args;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Pure unit tests for the shared ordered parser (<see cref="ArgParser"/>). Oracle note
/// (qa-rubric Directive 1): the error strings mirror GNU getopt_long as observed in
/// `wsl bash -c 'tee --bogus'` etc.; the parser itself is ps-bash-specific structure with
/// no bash-side equivalent to diff against.
/// </summary>
public class ArgParserTests
{
    private static readonly OptSpecSet Spec = new(
        new[]
        {
            new OptSpec("append", 'a', "append"),
            new OptSpec("recursive", 'r', "recursive"),
            new OptSpec("recursive", 'R', null),
            new OptSpec("verbose", 'v', "verbose"),
            new OptSpec("noclobber", 'n', "no-clobber"),
            new OptSpec("lines", 'l', "lines", OptKind.Value),
            new OptSpec("color", 'c', "color", OptKind.OptionalValue),
        },
        validButUnsupported: new[] { "-i", "--interactive", "-Z", "--context", "--no-target-directory" },
        allowAbbrev: true);

    private static readonly OptSpecSet HeadLike = new(
        new[] { new OptSpec("lines", 'n', "lines", OptKind.Value) },
        numericShorthandId: "lines");

    private static ParsedArgs P(params string[] argv) => ArgParser.Parse(argv, Spec);

    private static string Ids(ParsedArgs p) =>
        string.Join(",", p.Tokens.Where(t => t.Kind == ArgTokKind.Option).Select(t => t.OptId));

    // ── flags, bundles, order ────────────────────────────────────────────────

    [Fact]
    public void Flags_KeepOriginalOrderAcrossOperands()
    {
        var p = P("-a", "x", "--verbose", "y", "-r");
        Assert.Null(p.Error);
        Assert.Equal(new[] { "x", "y" }, p.Operands());
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, p.Tokens.Select(t => t.ArgIndex));
        Assert.Equal("append,verbose,recursive", Ids(p));
    }

    [Fact]
    public void Bundle_YieldsOneTokenPerLetter_SharingArgIndex()
    {
        var p = P("-arv");
        Assert.Null(p.Error);
        Assert.Equal("append,recursive,verbose", Ids(p));
        Assert.All(p.Tokens, t => Assert.Equal(0, t.ArgIndex));
        Assert.All(p.Tokens, t => Assert.Equal("-arv", t.Raw));
    }

    [Fact]
    public void SharedId_UpperAndLowerCaseShortBothMapToIt()
    {
        Assert.True(P("-R").Has("recursive"));
        Assert.True(P("-r").Has("recursive"));
        Assert.Equal(2, P("-rR").All("recursive").Count());
    }

    [Fact]
    public void ShortOptions_AreCaseSensitive()
    {
        var p = P("-A");
        Assert.Equal(ArgErrorKind.Unrecognized, p.Error!.Value.Kind);
        Assert.Equal('A', p.Error.Value.BadChar);
    }

    [Fact]
    public void LoneDash_IsAnOperand()
    {
        var p = P("-", "-a");
        Assert.Null(p.Error);
        Assert.Equal(new[] { "-" }, p.Operands());
        Assert.True(p.Has("append"));
    }

    [Fact]
    public void EmptyArgument_IsAnOperand()
    {
        var p = P("", "x");
        Assert.Equal(new[] { "", "x" }, p.Operands());
    }

    [Fact]
    public void Permutation_OptionsMayFollowOperands()
    {
        var p = P("src", "dst", "-r");
        Assert.True(p.Has("recursive"));
        Assert.Equal(new[] { "src", "dst" }, p.Operands());
    }

    // ── double dash ──────────────────────────────────────────────────────────

    [Fact]
    public void DoubleDash_EndsOptions_EverythingAfterIsOperand()
    {
        var p = P("-a", "--", "-r", "--verbose", "--", "-zz");
        Assert.Null(p.Error);
        Assert.Equal(new[] { "-r", "--verbose", "--", "-zz" }, p.Operands());
        Assert.Equal("append", Ids(p));
        Assert.Contains(p.Tokens, t => t.Kind == ArgTokKind.DoubleDash && t.ArgIndex == 1);
        Assert.All(p.Tokens.Where(t => t.ArgIndex > 1),
            t => { Assert.Equal(ArgTokKind.Operand, t.Kind); Assert.True(t.AfterDoubleDash); });
    }

    [Fact]
    public void DoubleDash_HidesUnsupportedAndUnknownFromTheClassifier()
    {
        var p = P("--", "-i", "--interactive", "--bogus", "-Z");
        Assert.Null(p.Error);
        Assert.Equal(4, p.Operands().Count);
    }

    [Fact]
    public void DoubleDash_ConsumedAsAValueIsNotAMarker()
    {
        // getopt takes the next element as the value even if it is "--".
        var p = P("-l", "--", "-r");
        Assert.Null(p.Error);
        Assert.Equal("--", p.Last("lines")!.Value.Value);
        Assert.True(p.Has("recursive")); // -r after a consumed "--" is still an option
        Assert.DoesNotContain(p.Tokens, t => t.Kind == ArgTokKind.DoubleDash);
    }

    [Fact]
    public void ErrorBeforeDoubleDash_StopsTheScan()
    {
        var p = P("-i", "--", "x");
        Assert.Equal(ArgErrorKind.ValidButUnsupported, p.Error!.Value.Kind);
        Assert.Empty(p.Tokens);
    }

    // ── value options ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("-l5")]
    [InlineData("--lines=5")]
    public void Value_Attached(string arg)
    {
        var p = P(arg);
        Assert.Null(p.Error);
        Assert.Equal("5", p.Last("lines")!.Value.Value);
        Assert.Single(p.Tokens);
    }

    [Theory]
    [InlineData("-l", "5")]
    [InlineData("--lines", "5")]
    public void Value_FromNextElement(string opt, string value)
    {
        var p = P(opt, value, "f");
        Assert.Null(p.Error);
        var tok = p.Last("lines")!.Value;
        Assert.Equal("5", tok.Value);
        Assert.Equal(0, tok.ArgIndex);
        Assert.Equal(new[] { "f" }, p.Operands());
    }

    [Fact]
    public void Value_NextElementIsTakenEvenWhenItLooksLikeAnOption()
    {
        var p = P("-l", "-r");
        Assert.Equal("-r", p.Last("lines")!.Value.Value);
        Assert.False(p.Has("recursive"));
    }

    [Fact]
    public void Value_EndsTheBundle()
    {
        var p = P("-arl7", "f");
        Assert.Equal("append,recursive,lines", Ids(p));
        Assert.Equal("7", p.Last("lines")!.Value.Value);
        Assert.Equal(new[] { "f" }, p.Operands());
    }

    [Fact]
    public void Value_LastBundleLetterTakesNextElement()
    {
        var p = P("-arl", "9");
        Assert.Equal("9", p.Last("lines")!.Value.Value);
    }

    [Fact]
    public void Value_EmptyAttachedLongValueIsAValue()
    {
        var p = P("--lines=");
        Assert.Null(p.Error);
        Assert.Equal("", p.Last("lines")!.Value.Value);
    }

    [Fact]
    public void Value_LastOccurrenceWins()
    {
        var p = P("-l1", "-l2");
        Assert.Equal("2", p.Last("lines")!.Value.Value);
        Assert.Equal(new[] { "1", "2" }, p.All("lines").Select(t => t.Value));
    }

    [Fact]
    public void MissingValue_Short_GnuWording()
    {
        var p = P("-arl");
        Assert.Equal(ArgErrorKind.MissingValue, p.Error!.Value.Kind);
        Assert.Equal("x: option requires an argument -- 'l'", p.Error.Value.Message("x"));
    }

    [Fact]
    public void MissingValue_Long_GnuWording()
    {
        var p = P("--lines");
        Assert.Equal("x: option '--lines' requires an argument", p.Error!.Value.Message("x"));
    }

    [Fact]
    public void OptionalValue_OnlyEverAttached()
    {
        var bare = P("-c", "f");
        Assert.Null(bare.Last("color")!.Value.Value);
        Assert.Equal(new[] { "f" }, bare.Operands());

        Assert.Equal("auto", P("-cauto").Last("color")!.Value.Value);
        Assert.Equal("auto", P("--color=auto").Last("color")!.Value.Value);
        Assert.Null(P("--color").Last("color")!.Value.Value);
    }

    [Fact]
    public void FlagWithAttachedValue_IsAnError()
    {
        var p = P("--append=yes");
        Assert.Equal(ArgErrorKind.UnexpectedValue, p.Error!.Value.Kind);
        Assert.Equal("x: option '--append' doesn't allow an argument", p.Error.Value.Message("x"));
    }

    // ── numeric shorthand ────────────────────────────────────────────────────

    [Theory]
    [InlineData("-5", "5")]
    [InlineData("-25", "25")]
    public void NumericShorthand_IsAValueOption(string arg, string value)
    {
        var p = ArgParser.Parse(new[] { arg, "f" }, HeadLike);
        Assert.Null(p.Error);
        Assert.Equal(value, p.Last("lines")!.Value.Value);
        Assert.Equal(new[] { "f" }, p.Operands());
    }

    [Fact]
    public void NumericShorthand_OffByDefault_DigitIsUnrecognized()
    {
        var p = P("-5");
        Assert.Equal(ArgErrorKind.Unrecognized, p.Error!.Value.Kind);
        Assert.Equal("x: invalid option -- '5'", p.Error.Value.Message("x"));
    }

    [Fact]
    public void NumericShorthand_MixedWithLettersIsNotShorthand()
    {
        var p = ArgParser.Parse(new[] { "-5x" }, HeadLike);
        Assert.Equal(ArgErrorKind.Unrecognized, p.Error!.Value.Kind);
    }

    [Fact]
    public void NumericShorthand_ExplicitFormsStillWork()
    {
        Assert.Equal("5", ArgParser.Parse(new[] { "-n5" }, HeadLike).Last("lines")!.Value.Value);
        Assert.Equal("5", ArgParser.Parse(new[] { "-n", "5" }, HeadLike).Last("lines")!.Value.Value);
    }

    // ── abbreviation ─────────────────────────────────────────────────────────

    [Fact]
    public void Abbrev_UniquePrefixSelectsTheOption()
    {
        var p = P("--app", "--verb", "--rec");
        Assert.Null(p.Error);
        Assert.Equal("append,verbose,recursive", Ids(p));
    }

    [Fact]
    public void Abbrev_ValueOptionThroughPrefix()
    {
        var p = P("--li=3");
        Assert.Equal("3", p.Last("lines")!.Value.Value);
    }

    [Fact]
    public void Abbrev_AmbiguousPrefixIsAnError_ListingCandidates()
    {
        // "no" matches no-clobber and (unsupported) no-target-directory.
        var p = P("--no");
        Assert.Equal(ArgErrorKind.Ambiguous, p.Error!.Value.Kind);
        Assert.Equal(
            "x: option '--no' is ambiguous; possibilities: '--no-clobber' '--no-target-directory'",
            p.Error.Value.Message("x"));
    }

    [Fact]
    public void Abbrev_ExactNameBeatsLongerCandidates()
    {
        var spec = new OptSpecSet(new[]
        {
            new OptSpec("a", '\0', "no"), new OptSpec("b", '\0', "no-clobber"),
        }, allowAbbrev: true);
        var p = ArgParser.Parse(new[] { "--no" }, spec);
        Assert.Null(p.Error);
        Assert.Equal("a", p.Tokens[0].OptId);
    }

    [Fact]
    public void Abbrev_ToAnUnsupportedOptionReportsItsFullName()
    {
        var p = P("--inter");
        Assert.Equal(ArgErrorKind.ValidButUnsupported, p.Error!.Value.Kind);
        Assert.Equal("--interactive", p.Error.Value.Token);
    }

    [Fact]
    public void GnuInfoOptions_TakePartInAbbreviation_LikeRealGnuTools()
    {
        // GNU cp: `--ver` is ambiguous (--verbose/--version); `--vers` is --version; `--he` is --help.
        var spec = new OptSpecSet(
            new[] { new OptSpec("verbose", 'v', "verbose") }, allowAbbrev: true, gnuInfoOptions: true);

        var amb = ArgParser.Parse(new[] { "--ver" }, spec).Error!.Value;
        Assert.Equal(ArgErrorKind.Ambiguous, amb.Kind);
        Assert.Equal("x: option '--ver' is ambiguous; possibilities: '--verbose' '--version'", amb.Message("x"));

        Assert.True(ArgParser.Parse(new[] { "--vers" }, spec).Has(OptSpecSet.VersionId));
        Assert.True(ArgParser.Parse(new[] { "--he" }, spec).Has(OptSpecSet.HelpId));
        Assert.True(ArgParser.Parse(new[] { "--verb" }, spec).Has("verbose"));
    }

    [Fact]
    public void GnuInfoOptions_OffByDefault()
    {
        Assert.Equal(ArgErrorKind.Unrecognized, P("--vers").Error!.Value.Kind);
    }

    [Fact]
    public void Abbrev_OffMeansPrefixesAreUnrecognized()
    {
        var spec = new OptSpecSet(new[] { new OptSpec("append", 'a', "append") });
        Assert.Equal(ArgErrorKind.Unrecognized, ArgParser.Parse(new[] { "--app" }, spec).Error!.Value.Kind);
    }

    [Fact]
    public void Abbrev_NoMatchIsUnrecognized()
    {
        Assert.Equal(ArgErrorKind.Unrecognized, P("--zzz").Error!.Value.Kind);
    }

    // ── classifier ───────────────────────────────────────────────────────────

    [Fact]
    public void ValidButUnsupported_ShortAndLong()
    {
        var s = P("-i").Error!.Value;
        Assert.Equal(ArgErrorKind.ValidButUnsupported, s.Kind);
        Assert.Equal("x: option '-i' is recognized but not supported by ps-bash", s.Message("x"));

        var l = P("--interactive").Error!.Value;
        Assert.Equal("x: option '--interactive' is recognized but not supported by ps-bash", l.Message("x"));
    }

    [Fact]
    public void ValidButUnsupported_LongWithAttachedValue_StripsTheValue()
    {
        var e = P("--context=foo").Error!.Value;
        Assert.Equal(ArgErrorKind.ValidButUnsupported, e.Kind);
        Assert.Equal("--context", e.Token);
    }

    [Fact]
    public void ValidButUnsupported_InsideABundle_NamesTheLetter()
    {
        var e = P("-arZ").Error!.Value;
        Assert.Equal(ArgErrorKind.ValidButUnsupported, e.Kind);
        Assert.Equal("-Z", e.Token);
    }

    [Fact]
    public void Unrecognized_Long_NamesTheWholeToken()
    {
        var e = P("--bogus=1").Error!.Value;
        Assert.Equal("x: unrecognized option '--bogus=1'", e.Message("x"));
    }

    [Fact]
    public void Unrecognized_Short_ReportsTheOffendingLetterNotTheFirst()
    {
        // The pre-parser code reported the FIRST letter ('a') for -az; GNU reports 'z'.
        var e = P("-arz").Error!.Value;
        Assert.Equal("x: invalid option -- 'z'", e.Message("x"));
    }

    [Fact]
    public void FirstErrorWins_AndTokensBeforeItAreKept()
    {
        var p = P("-a", "f", "--bogus", "-i");
        Assert.Equal(ArgErrorKind.Unrecognized, p.Error!.Value.Kind);
        Assert.Equal(2, p.Error.Value.ArgIndex);
        Assert.Equal(2, p.Tokens.Count);
    }

    // Exit status is per spec: GNU file tools exit 1 on a usage error, ls/grep/diff exit 2;
    // ps-bash's own refusal of a valid-but-unsupported option is always 2.
    private static readonly OptSpecSet ExitTwoSpec = new(
        new[] { new OptSpec("append", 'a', "append") },
        validButUnsupported: new[] { "-i" },
        usageExitCode: 2);

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-z")]
    [InlineData("--append=x")]
    [InlineData("--l")]
    public void UsageError_DefaultsToExitOne(string arg) =>
        Assert.Equal(1, P(arg).ErrorExitCode);

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-z")]
    [InlineData("--append=x")]
    public void UsageError_TakesTheSpecExitCode(string arg) =>
        Assert.Equal(2, ArgParser.Parse(new[] { arg }, ExitTwoSpec).ErrorExitCode);

    [Fact]
    public void MissingValue_IsAUsageError() =>
        Assert.Equal(1, P("-l").ErrorExitCode);

    [Fact]
    public void ValidButUnsupported_IsAlwaysExitTwo_RegardlessOfSpec()
    {
        Assert.Equal(2, P("-i").ErrorExitCode);
        Assert.Equal(2, ArgParser.Parse(new[] { "-i" }, ExitTwoSpec).ErrorExitCode);
        var oneSpec = new OptSpecSet(new[] { new OptSpec("a", 'a', null) },
            validButUnsupported: new[] { "-i" }, usageExitCode: 1);
        Assert.Equal(2, ArgParser.Parse(new[] { "-i" }, oneSpec).ErrorExitCode);
    }

    // ── property-style: nothing after `--` is ever an option or an error ─────

    [Fact]
    public void Property_NothingAfterDoubleDashIsEverAnOptionOrError()
    {
        string[] pool =
        {
            "-a", "-r", "-i", "-Z", "-arv", "-l", "-l5", "-c", "-cX", "--append", "--interactive",
            "--lines", "--lines=3", "--no", "--bogus", "--", "-", "x", "y.txt", "-5", "-zz", "--=",
            "--color=auto", "", "-arZ", "--app",
        };
        // Prefix draws from error-free tokens so the marker is usually reached; the suffix draws
        // from EVERYTHING (unsupported, unknown, ambiguous, value options) — that is the property.
        string[] safe = { "-a", "-r", "-arv", "-l5", "-cX", "--append", "--lines=3", "-", "x", "y.txt", "--color=auto", "--app" };
        var rng = new Random(20260929);
        int sawDd = 0;

        for (int iter = 0; iter < 5000; iter++)
        {
            var list = new List<string>();
            for (int k = rng.Next(0, 4); k > 0; k--) list.Add(safe[rng.Next(safe.Length)]);
            list.Add("--");
            for (int k = rng.Next(0, 6); k > 0; k--) list.Add(pool[rng.Next(pool.Length)]);
            var argv = list.ToArray();

            var p = ArgParser.Parse(argv, Spec);

            int ddTok = -1;
            for (int k = 0; k < p.Tokens.Count; k++)
            {
                if (p.Tokens[k].Kind == ArgTokKind.DoubleDash) { ddTok = k; break; }
            }
            if (ddTok < 0) continue;
            sawDd++;

            // A marker was reached => the scan never failed (it would have stopped before it)...
            Assert.Null(p.Error);
            // ...and every later token is a verbatim operand, in order.
            int ddArg = p.Tokens[ddTok].ArgIndex;
            var tail = p.Tokens.Skip(ddTok + 1).ToList();
            Assert.Equal(argv.Length - ddArg - 1, tail.Count);
            for (int k = 0; k < tail.Count; k++)
            {
                Assert.Equal(ArgTokKind.Operand, tail[k].Kind);
                Assert.True(tail[k].AfterDoubleDash);
                Assert.Equal(argv[ddArg + 1 + k], tail[k].Raw);
                Assert.Null(tail[k].OptId);
            }
        }

        Assert.True(sawDd > 500, "generator produced too few `--` cases to mean anything");
    }

    [Fact]
    public void Property_ErrorIndexIsAlwaysBeforeAnyMarker()
    {
        string[] pool = { "-a", "-i", "--bogus", "--", "x", "-l", "-Zr", "--no" };
        var rng = new Random(7);
        for (int iter = 0; iter < 3000; iter++)
        {
            var argv = Enumerable.Range(0, rng.Next(0, 8)).Select(_ => pool[rng.Next(pool.Length)]).ToArray();
            var p = ArgParser.Parse(argv, Spec);
            if (p.Error is not { } err) continue;
            // No DoubleDash token can precede the erroring element.
            Assert.DoesNotContain(p.Tokens, t => t.Kind == ArgTokKind.DoubleDash && t.ArgIndex < err.ArgIndex);
            // ...and no scan error was raised for an element that sits after a marker.
            for (int k = 0; k < err.ArgIndex; k++)
            {
                Assert.True(p.Tokens.All(t => t.Kind != ArgTokKind.DoubleDash));
            }
        }
    }
}
