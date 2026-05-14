using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Parity tests for the REFACTOR-2 Phase 2 shared C# helpers
/// (<see cref="BashRuntime"/>), extracted from the psm1 functions every leaf
/// Invoke-Bash* function depends on.
///
/// Oracle: the original psm1 helper implementations. These are pure transforms
/// (arg parsing, escape expansion, BashObject construction, text extraction)
/// with no pipeline / file / signal surface, so the applicable failure-surface
/// axes are empty input, unicode, CRLF-in-text, and quoting (a flag bundle with
/// an unknown char must not be silently swallowed). Large-input / broken-pipe /
/// signal axes do not apply: these helpers operate on fixed in-memory argv and
/// strings.
/// </summary>
public class BashRuntimeTests
{
    // ---- NormalizeBashText ----

    [Theory]
    [InlineData("hello\n", "hello")]
    [InlineData("hello", "hello")]
    [InlineData("", "")]
    [InlineData("\n", "")]
    [InlineData("a\nb\n", "a\nb")]      // only ONE trailing \n stripped
    [InlineData("a\n\n", "a\n")]
    public void NormalizeBashText_StripsSingleTrailingNewline(string input, string expected)
    {
        Assert.Equal(expected, BashRuntime.NormalizeBashText(input));
    }

    // ---- NewBashObject ----

    [Fact]
    public void NewBashObject_TextOutputFastPath_ReturnsBareString()
    {
        var result = BashRuntime.NewBashObject("hello\n");
        Assert.IsType<string>(result);
        Assert.Equal("hello", result);  // trailing \n normalized off
    }

    [Fact]
    public void NewBashObject_TypedPath_ReturnsPSObjectWithTypeNameAndBashText()
    {
        var result = BashRuntime.NewBashObject("data\n", "PsBash.CatLine");
        var pso = Assert.IsType<PSObject>(result);
        Assert.Contains("PsBash.CatLine", pso.TypeNames);
        Assert.Equal("data", pso.Properties["BashText"].Value);
        Assert.Null(pso.Properties["NoTrailingNewline"]);
    }

    [Fact]
    public void NewBashObject_NoTrailingNewline_UsesSlowPathWithMarker()
    {
        var result = BashRuntime.NewBashObject("frag", noTrailingNewline: true);
        var pso = Assert.IsType<PSObject>(result);
        Assert.Equal("frag", pso.Properties["BashText"].Value);
        Assert.Equal(true, pso.Properties["NoTrailingNewline"].Value);
    }

    [Fact]
    public void NewBashObject_CommandProperty_AttachedWhenProvided()
    {
        var result = BashRuntime.NewBashObject("x", "PsBash.WcResult", command: "wc");
        var pso = Assert.IsType<PSObject>(result);
        Assert.Equal("wc", pso.Properties["Command"].Value);
    }

    // ---- EmitBashLines ----

    [Fact]
    public void EmitBashLines_SplitsOnNewline_OneObjectPerLine()
    {
        var lines = BashRuntime.EmitBashLines("line1\nline2\n").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("line1", lines[0]);
        Assert.Equal("line2", lines[1]);
    }

    [Fact]
    public void EmitBashLines_NoTrailingNewline_LastLineMarked()
    {
        // "a\nb" — last line "b" had no trailing newline, so it is the
        // NoTrailingNewline slow-path PSObject; "a" is the fast-path string.
        var lines = BashRuntime.EmitBashLines("a\nb").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("a", lines[0]);
        var last = Assert.IsType<PSObject>(lines[1]);
        Assert.Equal("b", last.Properties["BashText"].Value);
        Assert.Equal(true, last.Properties["NoTrailingNewline"].Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmitBashLines_EmptyOrNull_YieldsNothing(string? input)
    {
        Assert.Empty(BashRuntime.EmitBashLines(input));
    }

    [Fact]
    public void EmitBashLines_UnicodeContent_Preserved()
    {
        var lines = BashRuntime.EmitBashLines("café\n😀\n").ToList();
        Assert.Equal(new object[] { "café", "😀" }, lines);
    }

    // ---- GetBashText ----

    [Fact]
    public void GetBashText_Null_ReturnsEmpty()
    {
        Assert.Equal("", BashRuntime.GetBashText(null));
    }

    [Fact]
    public void GetBashText_String_ReturnsItself()
    {
        Assert.Equal("plain", BashRuntime.GetBashText("plain"));
    }

    [Fact]
    public void GetBashText_ObjectWithBashTextProperty_ReturnsThatValue()
    {
        var obj = BashRuntime.NewBashObject("payload", "PsBash.CatLine");
        Assert.Equal("payload", BashRuntime.GetBashText(obj));
    }

    [Fact]
    public void GetBashText_ArbitraryObject_Stringifies()
    {
        Assert.Equal("42", BashRuntime.GetBashText(42));
    }

    // ---- NewFlagDefs ----

    [Fact]
    public void NewFlagDefs_BuildsOrdinalDictionary()
    {
        var defs = BashRuntime.NewFlagDefs(new[] { "-a", "desc a", "-b", "desc b" });
        Assert.Equal("desc a", defs["-a"]);
        Assert.Equal("desc b", defs["-b"]);
        Assert.False(defs.ContainsKey("-A"));  // case-sensitive (ordinal)
    }

    [Fact]
    public void NewFlagDefs_OddEntryCount_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => BashRuntime.NewFlagDefs(new[] { "-a", "desc a", "-b" }));
    }

    [Fact]
    public void NewFlagDefs_Empty_ReturnsEmptyDictionary()
    {
        Assert.Empty(BashRuntime.NewFlagDefs(Array.Empty<string>()));
    }

    // ---- ConvertFromBashArgs ----

    private static readonly Dictionary<string, string> SampleDefs = new(StringComparer.Ordinal)
    {
        ["-a"] = "flag a",
        ["-b"] = "flag b",
        ["--long"] = "long flag",
    };

    [Fact]
    public void ConvertFromBashArgs_SeparatesFlagsAndOperands()
    {
        var r = BashRuntime.ConvertFromBashArgs(new[] { "-a", "file1", "file2" }, SampleDefs);
        Assert.True(r.Flags["-a"]);
        Assert.False(r.Flags["-b"]);
        Assert.Equal(new[] { "file1", "file2" }, r.Operands);
    }

    [Fact]
    public void ConvertFromBashArgs_BundledShortFlags_AllSet()
    {
        var r = BashRuntime.ConvertFromBashArgs(new[] { "-ab" }, SampleDefs);
        Assert.True(r.Flags["-a"]);
        Assert.True(r.Flags["-b"]);
        Assert.Empty(r.Operands);
    }

    [Fact]
    public void ConvertFromBashArgs_UnknownBundleChar_WholeTokenIsOperand()
    {
        // -aZ : 'a' is known, 'Z' is not — psm1 parity is "whole token becomes
        // an operand and scanning of that token stops".
        var r = BashRuntime.ConvertFromBashArgs(new[] { "-aZ" }, SampleDefs);
        Assert.Contains("-aZ", r.Operands);
    }

    [Fact]
    public void ConvertFromBashArgs_DoubleDash_EndsFlagParsing()
    {
        var r = BashRuntime.ConvertFromBashArgs(new[] { "--", "-a", "-b" }, SampleDefs);
        Assert.False(r.Flags["-a"]);
        Assert.Equal(new[] { "-a", "-b" }, r.Operands);
    }

    [Fact]
    public void ConvertFromBashArgs_LongFlag_Recognized()
    {
        var r = BashRuntime.ConvertFromBashArgs(new[] { "--long", "x" }, SampleDefs);
        Assert.True(r.Flags["--long"]);
        Assert.Equal(new[] { "x" }, r.Operands);
    }

    [Fact]
    public void ConvertFromBashArgs_UnknownLongFlag_IsOperand()
    {
        var r = BashRuntime.ConvertFromBashArgs(new[] { "--nope" }, SampleDefs);
        Assert.Contains("--nope", r.Operands);
    }

    [Fact]
    public void ConvertFromBashArgs_Empty_AllFlagsFalseNoOperands()
    {
        var r = BashRuntime.ConvertFromBashArgs(Array.Empty<string>(), SampleDefs);
        Assert.All(r.Flags.Values, v => Assert.False(v));
        Assert.Empty(r.Operands);
    }

    // ---- ExpandEscapeSequences ----

    [Theory]
    [InlineData(@"a\nb", "a\nb")]
    [InlineData(@"a\tb", "a\tb")]
    [InlineData(@"a\rb", "a\rb")]
    [InlineData("", "")]
    [InlineData("no escapes", "no escapes")]
    public void ExpandEscapeSequences_BasicEscapes(string input, string expected)
    {
        Assert.Equal(expected, BashRuntime.ExpandEscapeSequences(input));
    }

    [Fact]
    public void ExpandEscapeSequences_DoubleBackslash_StaysLiteralBackslashN()
    {
        // \\n must produce a literal backslash + 'n', NOT a newline — this is
        // the whole point of the sentinel two-pass scheme.
        Assert.Equal(@"\n", BashRuntime.ExpandEscapeSequences(@"\\n"));
    }

    [Fact]
    public void ExpandEscapeSequences_AllControlEscapes()
    {
        Assert.Equal("\a\b\f\v", BashRuntime.ExpandEscapeSequences(@"\a\b\f\v"));
    }

    // ---- FormatBashError ----

    [Fact]
    public void FormatBashError_PrefixesCommandName()
    {
        Assert.Equal("grep: no such file", BashRuntime.FormatBashError("grep", "no such file"));
    }
}
