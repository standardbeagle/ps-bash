using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The jq engine's pure seams, with expectations read from jq 1.7 (<c>wsl jq</c>): number printing and literal preservation, the JSON
/// reader's error wording and positions, the option parser, compile-time name checking and a sample of evaluation order rules.
/// Behaviour against the real binary (stdout, stderr, exit status) is <c>JqDifferentialTests</c> / <c>JqFilesDifferentialTests</c>.
/// </summary>
public class JqEngineTests
{
    // ───────────── helpers ─────────────

    private static string[] Run(string program, string? inputJson = "null", params (string Name, object? Value)[] vars)
    {
        var node = JqParser.Parse(program);
        var globals = new Dictionary<string, object?>();
        foreach (var (name, value) in vars) globals[name] = value;
        Assert.Empty(JqChecker.Check(node, globals.Keys));
        var interp = new JqInterp(() => (false, null), globals);
        var input = JqJsonReader.ParseSingle(inputJson ?? "null");
        return interp.Run(node, new JEnv(), input).Select(JqValue.ToCompactJson).ToArray();
    }

    private static string Compile(string program)
    {
        try
        {
            var node = JqParser.Parse(program);
            var errors = JqChecker.Check(node, Array.Empty<string>());
            return errors.Count == 0 ? "ok" : errors[0].Message;
        }
        catch (JqCompileException ex) { return ex.Message; }
    }

    private static string ReadError(string text)
    {
        try
        {
            var reader = new JqJsonReader(text);
            while (reader.TryRead(out _)) { }
            return "ok";
        }
        catch (JqJsonException ex) { return ex.Message; }
    }

    // ───────────── numbers ─────────────

    [Theory]
    [InlineData("1.0", "1.0")]
    [InlineData("1.50", "1.50")]
    [InlineData("1e2", "1E+2")]
    [InlineData("1E-2", "0.01")]
    [InlineData("0.1e1", "1")]
    [InlineData("5e0", "5")]
    [InlineData("-0", "-0")]
    [InlineData("0.00001", "0.00001")]
    [InlineData("0.0000001", "1E-7")]
    [InlineData("100000000000000000000", "100000000000000000000")]
    [InlineData("1.", "1")]
    [InlineData(".5", "0.5")]
    [InlineData("01", "1")]
    [InlineData("+1", "1")]
    [InlineData("1E400", "1E+400")]
    [InlineData("12345678901234567890", "12345678901234567890")]
    [InlineData("0.0", "0.0")]
    [InlineData("1e-1000", "1E-1000")]
    public void CanonicalLiteral_IsDecNumbersScientificString(string literal, string expected)
    {
        Assert.Equal(expected, JqValue.CanonicalLiteral(literal));
    }

    [Theory]
    [InlineData(1.0, "1")]
    [InlineData(1.5, "1.5")]
    [InlineData(100.0, "100")]
    [InlineData(1e15, "1000000000000000")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e17, "1e+17")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(1e-7, "1e-07")]
    [InlineData(1.5e-10, "1.5e-10")]
    [InlineData(1.5e300, "1.5e+300")]
    [InlineData(0.1 + 0.2, "0.30000000000000004")]
    [InlineData(1.0 / 3, "0.3333333333333333")]
    [InlineData(9007199254740993.0, "9007199254740992")]
    [InlineData(123456789012345680.0, "123456789012345680")]
    [InlineData(-0.0, "-0")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(double.PositiveInfinity, "1.7976931348623157e+308")]
    [InlineData(double.NegativeInfinity, "-1.7976931348623157e+308")]
    [InlineData(double.NaN, "null")]
    public void FormatDouble_IsJq17sShortestRoundTrip(double d, string expected)
    {
        Assert.Equal(expected, JqValue.FormatDouble(d));
    }

    [Fact]
    public void Literal_KeepsItsText_UntilArithmetic()
    {
        Assert.Equal(new[] { "[1.0,1.50,1E+2]" }, Run(".", "[1.0, 1.50, 1e2]"));
        Assert.Equal(new[] { "[1,1.5,100]" }, Run("map(. + 0)", "[1.0, 1.50, 1e2]"));
        Assert.Equal(new[] { "\"1.0\"" }, Run("tostring", "1.0"));
        Assert.Equal(new[] { "[1.000,5,0.10]" }, Run(".", "[1.000, 5e0, 0.10]"));
        Assert.Equal(new[] { "6.1" }, Run("add", "[1.000, 5e0, 0.10]"));
    }

    [Fact]
    public void Numbers_CompareByValue_NotText()
    {
        Assert.Equal(new[] { "true" }, Run("1.0 == 1", "null"));
        Assert.Equal(new[] { "[0.10,1.000,5]" }, Run("sort", "[1.000, 5e0, 0.10]"));
    }

    // ───────────── JSON reader ─────────────

    [Theory]
    [InlineData("{\"a\":", "Unfinished JSON term at EOF at line 1, column 5")]
    [InlineData("1 2 x 3", "Invalid numeric literal at line 1, column 6")]
    [InlineData("[1,2\n3]", "Expected separator between values at line 2, column 2")]
    [InlineData("}", "Unmatched '}' at line 1, column 1")]
    [InlineData("]", "Unmatched ']' at line 1, column 1")]
    [InlineData("{'a':1}", "Invalid numeric literal at line 1, column 5")]
    [InlineData("tru", "Invalid literal at EOF at line 1, column 3")]
    [InlineData("nul", "Invalid literal at EOF at line 1, column 3")]
    [InlineData("{\"a\":1,}", "Expected another key-value pair at line 1, column 8")]
    [InlineData("[1,]", "Expected another array element at line 1, column 4")]
    [InlineData("\"abc", "Unfinished string at EOF at line 1, column 4")]
    [InlineData("{\"a\" 1}", "Expected separator between values at line 1, column 7")]
    [InlineData("{\"a\"::1}", "Expected string key before ':' at line 1, column 6")]
    [InlineData("{1:2}", "Object keys must be strings at line 1, column 3")]
    [InlineData("[,1]", "Expected value before ',' at line 1, column 2")]
    [InlineData("\"a\tb\"", "Invalid string: control characters from U+0000 through U+001F must be escaped at line 1, column 5")]
    [InlineData("\"\\q\"", "Invalid escape at line 1, column 4")]
    [InlineData("\"\\u12\"", "Invalid \\uXXXX escape at line 1, column 6")]
    [InlineData("\"\\ud800\"", "Invalid \\uXXXX\\uXXXX surrogate pair escape at line 1, column 8")]
    [InlineData("[1e]", "Invalid numeric literal at line 1, column 4")]
    [InlineData("[-]", "Invalid numeric literal at line 1, column 3")]
    [InlineData("# c\n1", "Invalid numeric literal at line 1, column 2")]
    [InlineData("1 [", "Unfinished JSON term at EOF at line 1, column 3")]
    public void Reader_ErrorsAreJqsWords_AtJqsPositions(string text, string expected)
    {
        Assert.Equal(expected, ReadError(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("1 2\n[3]\n\"x\"")]
    [InlineData("[0, -0, 0.0, 1., .5, 01, +1]")]
    [InlineData("[NaN,Infinity,-Infinity,nan]")]
    [InlineData("1\r\n2\r\n")]
    [InlineData("\"\\u00e9\\ud83d\\ude00\\udc00\"")]
    public void Reader_AcceptsWhatJqAccepts(string text)
    {
        Assert.Equal("ok", ReadError(text));
    }

    [Fact]
    public void Reader_LoneLowSurrogateEscapeBecomesReplacementCharacter()
    {
        Assert.Equal("\uFFFD", JqJsonReader.ParseSingle("\"\\udc00\""));
    }

    [Fact]
    public void Reader_DuplicateKeysLastWins_FirstPositionKept()
    {
        Assert.Equal("{\"a\":3,\"b\":2}", JqValue.ToCompactJson(JqJsonReader.ParseSingle("{\"a\":1,\"b\":2,\"a\":3}")));
    }

    [Theory]
    [InlineData("1", 0)]
    [InlineData("1\n", 1)]
    [InlineData("1\n\n\n\"x\"\n3\n", 5)]
    [InlineData("{\n\"a\":\n\"x\"\n}\n", 4)]
    public void Reader_LineNumberIsLinesFedWhenTheValueCompleted(string text, int expectedLastLine)
    {
        var reader = new JqJsonReader(text);
        int line = -1;
        while (reader.TryRead(out _)) line = reader.LinesConsumedThroughLine;
        // the LAST value of each text is the one these expectations describe
        Assert.Equal(expectedLastLine, line);
    }

    [Fact]
    public void ParseSingle_ErrorsCarryTheWhileParsingSuffix()
    {
        var ex = Assert.Throws<JqJsonException>(() => JqJsonReader.ParseSingle("{a"));
        Assert.Equal("Invalid numeric literal at EOF at line 1, column 2 (while parsing '{a')", ex.Message);
        Assert.Equal("Unexpected extra JSON values (while parsing '1 2')", Assert.Throws<JqJsonException>(() => JqJsonReader.ParseSingle("1 2")).Message);
        Assert.Equal("Expected JSON value (while parsing '')", Assert.Throws<JqJsonException>(() => JqJsonReader.ParseSingle("")).Message);
    }

    // ───────────── options ─────────────

    private static JqOptions Opts(params string[] args)
    {
        Assert.True(JqOptions.TryParse(args, f => throw new IOException(f), out var o, out var err), err?.Message);
        return o;
    }

    private static string OptError(params string[] args)
    {
        Assert.False(JqOptions.TryParse(args, f => throw new IOException(f), out _, out var err));
        return err!.Message.Split('\n')[0] + " / " + err.ExitCode;
    }

    [Fact]
    public void Options_BundlesAndOrder()
    {
        var o = Opts("-nrc", ".a", "x.json", "-e", "--tab", "y.json");
        Assert.True(o.NullInput && o.RawOutput && o.Compact && o.ExitStatus && o.Tab);
        Assert.Equal(".a", o.Program);
        Assert.Equal(new[] { "x.json", "y.json" }, o.Files);
    }

    [Fact]
    public void Options_NoProgramMeansIdentity()
    {
        Assert.Equal(".", Opts("-n").Program);
        Assert.Equal(".", Opts("--arg", "a", "1").Program);
    }

    [Fact]
    public void Options_ArgsAndJsonargs_AfterTheProgram()
    {
        var o = Opts("-n", "$ARGS", "--args", "a", "b");
        Assert.Equal(new object?[] { "a", "b" }, o.Positional);
        Assert.Empty(o.Files);
        var j = Opts("-n", "$ARGS", "--jsonargs", "1", "\"x\"", "null");
        Assert.Equal(3, j.Positional.Count);
        Assert.Equal("jq: invalid JSON text passed to --jsonargs / 2", OptError("-n", ".", "--jsonargs", "{"));
    }

    [Fact]
    public void Options_Named_FirstWinsOnRepeat()
    {
        var o = Opts("-n", ".", "--arg", "a", "1", "--arg", "a", "2", "--argjson", "a", "3", "--argjson", "b", "[1]");
        Assert.Equal("1", o.Named["a"]);
        Assert.Equal("[1]", JqValue.ToCompactJson(o.Named["b"]));
    }

    [Fact]
    public void Options_FilesRead_ThroughTheReader()
    {
        JqOptions.TryParse(new[] { "-n", "--slurpfile", "s", "docs", "--rawfile", "r", "txt", "." },
            f => f == "docs" ? "1 2" : "a\nb", out var o, out _);
        Assert.Equal("[1,2]", JqValue.ToCompactJson(o.Named["s"]));
        Assert.Equal("a\nb", o.Named["r"]);
        Assert.Equal("jq: Bad JSON in --slurpfile a nosuch: Could not open nosuch: No such file or directory / 2", OptError("-n", "--slurpfile", "a", "nosuch", "."));
    }

    [Theory]
    [InlineData("--arg a / 2", "--arg", "a")]
    [InlineData("--argjson a / 2", "--argjson", "a")]
    [InlineData("--bogus / 2", "--bogus", ".")]
    [InlineData("-z / 2", "-z", ".")]
    [InlineData("--indent 8 / 2", "--indent", "8", ".")]
    public void Options_UsageErrors(string unused, params string[] args)
    {
        _ = unused;
        var message = OptError(args);
        Assert.EndsWith("/ 2", message);
    }

    [Fact]
    public void Options_Indent()
    {
        Assert.Equal(0, Opts("--indent", "0", ".").Indent);
        Assert.Equal(7, Opts("--indent", "7", ".").Indent);
        Assert.Equal(0, Opts("--indent", "x", ".").Indent);       // C atoi
        Assert.Equal("jq: --indent takes a number between -1 and 7 / 2", OptError("--indent", "9", "."));
        Assert.Equal("jq: --indent takes one parameter / 2", OptError("--indent"));
    }

    [Fact]
    public void Options_WriteOptions()
    {
        Assert.True(Opts("--tab", ".").WriteOptions.Tab);
        Assert.False(Opts("--tab", "-c", ".").WriteOptions.Tab);       // -c wins over --tab
        Assert.True(Opts("--indent", "0", ".").WriteOptions.IsCompact);
        Assert.Equal(3, Opts("--indent", "3", ".").WriteOptions.Indent);
    }

    // ───────────── output layout ─────────────

    [Fact]
    public void Layout_PrettyTabCompactAndSorted()
    {
        var v = JqJsonReader.ParseSingle("{\"b\":[1,{\"c\":null}],\"a\":{},\"d\":[]}");
        Assert.Equal("{\n  \"b\": [\n    1,\n    {\n      \"c\": null\n    }\n  ],\n  \"a\": {},\n  \"d\": []\n}", JqValue.ToJson(v, new JqValue.WriteOptions(2, false, false, false)));
        Assert.StartsWith("{\n\t\"b\": [\n\t\t1", JqValue.ToJson(v, new JqValue.WriteOptions(2, true, false, false)));
        Assert.Equal("{\"a\":{},\"b\":[1,{\"c\":null}],\"d\":[]}", JqValue.ToJson(v, new JqValue.WriteOptions(0, false, true, false)));
    }

    [Fact]
    public void Layout_StringEscapes()
    {
        Assert.Equal("\"é\\u0000\\u001f\\u007f😀\\\"\\\\\\n\\t\\r\\b\\f\"", JqValue.ToCompactJson("é\0\u001f\u007f😀\"\\\n\t\r\b\f"));
        Assert.Equal("\"\\u00e9\\ud83d\\ude00\"", JqValue.ToJson("é😀", new JqValue.WriteOptions(0, false, false, true)));
    }

    // ───────────── compile-time checks ─────────────

    [Theory]
    [InlineData("foo", "foo/0 is not defined")]
    [InlineData("foo(1)", "foo/1 is not defined")]
    [InlineData("$x", "$x is not defined")]
    [InlineData(".a as $x | $y", "$y is not defined")]
    [InlineData("break $x", "$*label-x is not defined")]
    [InlineData("map(.)", "ok")]
    [InlineData("def f(g): g; f(.)", "ok")]
    [InlineData("def f($a): $a + a; f(1)", "ok")]
    [InlineData("def f: g; def g: 1; f", "g/0 is not defined")]
    [InlineData("def f: f; f", "ok")]
    [InlineData("label $l | 1, break $l", "ok")]
    [InlineData("reduce .[] as $x (0; . + $x)", "ok")]
    [InlineData(". as [$a, {b: $c}] | [$a, $c]", "ok")]
    [InlineData("leaf_paths", "leaf_paths/0 is not defined")]
    [InlineData("recurse_down", "recurse_down/0 is not defined")]
    [InlineData("todate", "todate/0 is not defined")]
    [InlineData("splits(\"a\")", "ok")]
    [InlineData("$ENV", "ok")]
    [InlineData("$__prog_args", "$__prog_args is not defined")]
    public void Checker_ResolvesNames(string program, string expected)
    {
        // $ENV is a global only when the host provides it; the helper passes none
        Assert.Equal(expected, Compile(program));
    }

    [Theory]
    [InlineData("{", "syntax error, unexpected end of file")]
    [InlineData("1 +", "syntax error, unexpected end of file")]
    [InlineData(". |", "syntax error, unexpected end of file")]
    [InlineData("[1,", "syntax error, unexpected end of file")]
    [InlineData("def f: 1;", "Top-level program not given (try \".\")")]
    [InlineData("{(1): 2}", "Cannot use number (1) as object key at <top-level>, line 1:")]
    [InlineData("{(null): 2}", "Cannot use null (null) as object key at <top-level>, line 1:")]
    public void Parser_RejectsWhatJqRejects(string program, string expectedStart)
    {
        Assert.StartsWith(expectedStart, Compile(program));
    }

    // ───────────── evaluation order and semantics ─────────────

    [Fact]
    public void Order_BinaryOperatorsRunTheRightOperandOuter()
    {
        Assert.Equal(new[] { "[11,12,21,22]" }, Run("[(1,2) + (10,20)]"));
    }

    [Fact]
    public void Order_InterpolationAndObjectConstruction()
    {
        Assert.Equal(new[] { "\"1-3\"", "\"2-3\"", "\"1-4\"", "\"2-4\"" }, Run("\"\\(1,2)-\\(3,4)\""));
        Assert.Equal(new[] { "{\"a\":1,\"b\":3}", "{\"a\":1,\"b\":4}", "{\"a\":2,\"b\":3}", "{\"a\":2,\"b\":4}" }, Run("{a:(1,2),b:(3,4)}"));
        Assert.Equal(new[] { "{\"a\":1}", "{\"a\":2}", "{\"b\":1}", "{\"b\":2}" }, Run("{(\"a\",\"b\"):(1,2)}"));
    }

    [Fact]
    public void Paths_AssignmentAndDeletion()
    {
        Assert.Equal(new[] { "{\"a\":[1,9,3]}" }, Run(".a[1] = 9", "{\"a\":[1,2,3]}"));
        Assert.Equal(new[] { "[1,3]" }, Run(".[1] |= empty", "[1,2,3]"));
        Assert.Equal(new[] { "[1,2]" }, Run("(.[] | select(. > 2)) |= empty", "[1,2,3,4]"));
        Assert.Equal(new[] { "{\"a\":[3]}" }, Run("del(.a[0,1])", "{\"a\":[1,2,3]}"));
        Assert.Equal(new[] { "[[\"a\"],[\"a\",\"b\"]]" }, Run("[paths]", "{\"a\":{\"b\":1}}"));
        Assert.Equal(new[] { "{\"a\":{\"b\":{\"c\":1}}}" }, Run(".a.b.c = 1", "null"));
        Assert.Equal(new[] { "[null,null,null,1]" }, Run(".[3] = 1", "null"));
        Assert.Equal(new[] { "\"Invalid path expression with result 1\"" }, Run("try path(1) catch .", "null"));
    }

    [Fact]
    public void Destructuring_AlternativeOperator()
    {
        Assert.Equal(new[] { "1", "3" }, Run("[[1,2],{\"a\":3}][] as [$a] ?// {a:$a} | $a"));
    }

    [Fact]
    public void Try_CatchesErrorsRaisedWhileTheBuiltinRuns()
    {
        Assert.Equal(new[] { "[1,\"bad\"]" }, Run("map(try tonumber catch \"bad\")", "[1,\"a\"]"));
        Assert.Equal(new[] { "null" }, Run("try error(null) catch .", "null"));
        Assert.Equal(new[] { "\"x\"" }, Run("try error(\"x\") catch .", "null"));
    }

    [Fact]
    public void Regex_SubGsubMultipleOutputs()
    {
        Assert.Equal(new[] { "[\"a1b1c\",\"a2b2c\"]" }, Run("[gsub(\"X\"; \"1\", \"2\")]", "\"aXbXc\""));
        Assert.Equal(new[] { "[\"axbpc\",\"aybqc\",\"azc\"]" }, Run("[gsub(\"(?<d>[0-9])\"; if .d == \"1\" then \"x\", \"y\", \"z\" else \"p\", \"q\" end)]", "\"a1b2c\""));
        Assert.Equal(new[] { "\"-a-b-c-\"" }, Run("gsub(\"\"; \"-\")", "\"abc\""));
        Assert.Equal(new[] { "[0,1]" }, Run("[match(\".\"; \"g\") | .offset]", "\"é😀\""));
    }

    [Fact]
    public void Builtins_Quirks_Of_17()
    {
        Assert.Equal(new[] { "[2,2,2]" }, Run("[limit(3; repeat(. * 2))]", "1"));            // jq 1.7 as shipped
        Assert.Equal(new[] { "[]" }, Run("[nth(5; 1, 2)]"));
        Assert.Equal(new[] { "\"nth doesn't support negative indices\"" }, Run("try nth(-1; 1, 2) catch .", "null"));
        Assert.Equal(new[] { "{\"a\":null}" }, Run("from_entries", "[{\"key\":\"a\"}]"));
        Assert.Equal(new[] { "\"Cannot use number (1) as object key\"" }, Run("try from_entries catch .", "[{\"key\":1}]"));
        Assert.Equal(new[] { "\"1--a-true\"" }, Run("join(\"-\")", "[1,null,\"a\",true]"));
        Assert.Equal(new[] { "\"string (\\\"\\\") and array ([1]) cannot be added\"" }, Run("try join(\",\") catch .", "[[1]]"));
        Assert.Equal(new[] { "[\"ababab\",\"\",\"\",null]" }, Run("[\"ab\"*3, \"ab\"*0, \"ab\"*0.5, \"ab\"*-1]"));
        Assert.Equal(new[] { "[2,-2,2]" }, Run("[5%3, -5%3, 5%-3]"));
    }

    [Fact]
    public void Format_Strings()
    {
        Assert.Equal(new[] { "\"1,\\\"a b\\\",,true\"" }, Run("@csv", "[1,\"a b\",null,true]"));
        Assert.Equal(new[] { "\"1 'a b' null true\"" }, Run("@sh", "[1,\"a b\",null,true]"));
        Assert.Equal(new[] { "\"a%20b%2F\"" }, Run("@uri", "\"a b/\""));
        Assert.Equal(new[] { "\"WzEsImEgYiJd\"" }, Run("@base64", "[1,\"a b\"]"));
        Assert.Equal(new[] { "\"x=aGk=\"" }, Run("@base64 \"x=\\(\"hi\")\""));
        Assert.Equal(new[] { "\"base32 is not a valid format\"" }, Run("try @base32 catch .", "\"a\""));
    }
}
