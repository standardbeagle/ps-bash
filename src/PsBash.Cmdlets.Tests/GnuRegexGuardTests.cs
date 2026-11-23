using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// ONE translator from a GNU regex to .NET (<see cref="GnuRegex"/>) for sed, grep, expr and find. Before it each tool
/// had its own pass (<c>BashRuntime.TranslateBre</c> / <c>SedRegexToNet</c> / <c>LiteralBracketBackslashes</c>,
/// find's dialect translator) and they drifted: grep took <c>a$b</c>'s <c>$</c> as an anchor, sed's <c>.</c> missed
/// the newline in a multi-line pattern space, <c>a\{1</c> was .NET's "Invalid pattern" instead of
/// <c>Unmatched \{</c>, and <c>\d</c> was a digit class. This scan fails when a removed per-tool translator comes back,
/// when a tool stops calling the shared one, and when a GNU regcomp message is spelled outside the translator.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent; the behaviour is pinned by
/// <see cref="GnuRegexTests"/> and <c>GnuRegexDifferentialTests</c>.
/// </summary>
public class GnuRegexGuardTests
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

    private static string CmdletsDir => Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");

    private static string Source(string file) => File.ReadAllText(Path.Combine(CmdletsDir, file));

    [Theory]
    [InlineData("TranslateBre")]
    [InlineData("SedRegexToNet")]
    [InlineData("LiteralBracketBackslashes")]
    public void RemovedPerToolTranslators_StayRemoved(string name)
    {
        var found = typeof(BashRuntime).GetMember(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(found.Length == 0, $"BashRuntime.{name} is back: translate a GNU regex with GnuRegex.TryTranslate");
    }

    [Theory]
    [InlineData("SedRegex.cs")]
    [InlineData("ExprEvaluator.cs")]
    [InlineData("InvokeBashGrepCommand.cs")]
    [InlineData("FindRegexDialect.cs")]
    public void EveryGnuRegexTool_TranslatesThroughGnuRegex(string file)
        => Assert.Contains("GnuRegex.TryTranslate(", Source(file));

    [Theory]
    [InlineData("SedRegex.cs")]
    [InlineData("SedEngine.cs")]
    [InlineData("InvokeBashSedCommand.cs")]
    [InlineData("ExprEvaluator.cs")]
    [InlineData("FindRegexDialect.cs")]
    public void GnuRegexTools_DoNotPreRewriteClasses(string file)
    {
        // GnuRegex validates and translates [[:name:]] itself; a pre-pass hides "Invalid character class name".
        Assert.DoesNotContain("TranslatePosixClasses(", Source(file));
    }

    [Fact]
    public void Grep_PreRewritesClassesOnlyForPerl()
    {
        var lines = Source("InvokeBashGrepCommand.cs").Split('\n')
            .Where(l => l.Contains("TranslatePosixClasses(", StringComparison.Ordinal))
            .ToList();
        Assert.All(lines, l => Assert.Contains("GrepPcre.", l));
    }

    // GNU regcomp's messages: whoever spells one is translating a regex of their own.
    private static readonly Regex RegcompMessage = new(
        @"Invalid preceding regular expression|Invalid character class name|Invalid content of|Invalid back reference|Invalid collation character|Invalid range end|Trailing backslash|Unmatched \[, \[\^",
        RegexOptions.Compiled);

    [Fact]
    public void RegcompMessages_AreSpelledOnlyByTheTranslator()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GnuRegex.cs", "SedRegex.cs" };
        var hits = Directory.EnumerateFiles(CmdletsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => !allowed.Contains(Path.GetFileName(f)))
            .Where(f => RegcompMessage.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(hits.Count == 0, "GNU regex messages outside GnuRegex/SedRegex: " + string.Join(", ", hits));
    }
}
