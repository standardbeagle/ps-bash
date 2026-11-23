using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// sed reads delimited text (address regexes, <c>s</c>, <c>y</c>) through ONE scanner, <see cref="SedDelimited"/>.
/// The splitter (<c>SplitSedCommands</c> / <c>SkipSedAddress</c>) and the parser (<c>ParseExpressionCore</c>) each had
/// their own loop: the address parser took the first <c>/</c> (<c>IndexOf('/')</c>) while the splitter honoured
/// <c>\/</c>, the <c>y</c> parser split on every delimiter, and none knew bracket expressions, so where a field ended
/// depended on who asked. This scan fails when a sed source file hand-rolls a delimiter search again, and when one of
/// the three readers stops calling the shared scanner.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent; the behaviour is pinned by
/// <see cref="SedDelimitedTests"/> and <c>SedDifferentialTests</c>.
/// </summary>
public class SedDelimitedGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }

    private static string Source(string file) => File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets", file));

    // A hand-rolled delimiter search: comparing a char with the delimiter, splitting on it, or looking for '/'.
    private static readonly Regex DelimiterScan = new(
        @"==\s*delim\b|\bdelim\s*==|\.Split\(\s*delim\b|IndexOf\(\s*'/'|\bSkipRegex\b|\bScanDelimited\b",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("InvokeBashSedCommand.cs")]
    [InlineData("SedEngine.cs")]
    [InlineData("SedRuntime.cs")]
    [InlineData("LineStreamStages.cs")]
    public void SedSources_HaveNoDelimiterScanOfTheirOwn(string file)
    {
        var hits = Source(file).Split('\n')
            .Select((line, i) => (line, i))
            .Where(t => DelimiterScan.IsMatch(t.line))
            .Select(t => $"{file}:{t.i + 1}: {t.line.Trim()}")
            .ToList();
        Assert.True(hits.Count == 0, "scan delimited sed text with SedDelimited, not by hand:\n" + string.Join("\n", hits));
    }

    /// <summary>The body of the method whose declaration contains <paramref name="signature"/> (up to the next member).</summary>
    private static string Body(string text, string signature)
    {
        int at = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' not found: update this guard if it was renamed");
        var next = Regex.Match(text.Substring(at + signature.Length), @"\n    (?:private|internal|public) ");
        return next.Success ? text.Substring(at, signature.Length + next.Index) : text.Substring(at);
    }

    [Theory]
    [InlineData("internal static List<string> SplitSedCommands(string expression, out List<int> ends)", "SedDelimited.ScanCommandFields")]
    [InlineData("private static int SkipSedAddress(string s, int pos)", "SedDelimited.ScanRegexAddress")]
    [InlineData("private static SedCommand? ParseExpressionCore(", "SedDelimited.ScanRegexAddress")]
    [InlineData("private static SedCommand? ParseExpressionCore(", "SedDelimited.ScanCommandFields")]
    public void SplitterAndParser_ReadFieldsWithTheSharedScanner(string signature, string call)
    {
        Assert.Contains(call, Body(Source("InvokeBashSedCommand.cs"), signature));
    }

    [Fact]
    public void AddressAndSubstRegexes_CompileThroughOneHelper()
    {
        // Both kinds compile at parse time (a bad one is a script error, not a run-time "can't read FILE").
        string parse = Body(Source("InvokeBashSedCommand.cs"), "private static SedCommand? ParseExpressionCore(");
        Assert.Equal(2, Regex.Matches(parse, @"SedRegex\.TryCompile\(").Count);
        Assert.DoesNotContain("new Regex(", parse);
        Assert.DoesNotContain("Regex.IsMatch(", Source("SedEngine.cs"));
    }
}
