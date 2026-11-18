using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A usage error from the shared argv parser is written by ONE sink, <see cref="FileSystemHelpers.TryWriteParseError"/>:
/// the GNU first line plus the tool's hint (<c>Try 'CMD --help' for more information.</c>, or the tool's own wording,
/// <see cref="FileSystemHelpers.UsageHint"/>). REGRESSION: the sink wrote only the first line, so all 42 commands on
/// the parser printed <c>cat: invalid option -- 'x'</c> without the hint GNU prints. This scan fails on a cmdlet that
/// formats a parse error itself (<c>.Message(cmd)</c> on a parser error) and so would skip the hint again.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent; the wording per tool is
/// the oracle's (UsageHint_MatchesTheOracleWording).
/// </summary>
public class ParseErrorSinkGuardTests
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

    // `parsed.Error.Value.Message(`, `err.Message(`, `parsed.Error!.Value.Message(` … : a parser error formatted by hand.
    private static readonly Regex HandFormatted = new(@"\b(?:Error(?:\!|\?)?\.Value|err|error)\.Message\(", RegexOptions.Compiled);

    [Fact]
    public void Cmdlets_WriteParseErrorsOnlyThroughTheSink()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var name = Path.GetFileName(file);
            if (name == "FileSystemHelpers.cs") continue;   // the sink
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (HandFormatted.IsMatch(lines[i])) offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0,
            "A parse error must go through FileSystemHelpers.TryWriteParseError (it adds the tool's usage hint):\n"
            + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("cat", "Try 'cat --help' for more information.")]
    [InlineData("readlink", "Try 'readlink --help' for more information.")]
    [InlineData("diff", "diff: Try 'diff --help' for more information.")]
    [InlineData("gzip", "Try `gzip --help' for more information.")]
    [InlineData("tar", "Try 'tar --help' or 'tar --usage' for more information.")]
    [InlineData("grep", "Usage: grep [OPTION]... PATTERNS [FILE]...\nTry 'grep --help' for more information.")]
    public void UsageHint_MatchesTheOracleWording(string cmd, string expected)
        => Assert.Equal(expected, FileSystemHelpers.UsageHint(cmd));
}
