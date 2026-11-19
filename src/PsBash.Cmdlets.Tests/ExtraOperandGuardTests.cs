using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// GNU's "extra operand" usage error has ONE spelling, written by <see cref="FileSystemHelpers.ExtraOperandMessage"/>
/// / <see cref="FileSystemHelpers.WriteExtraOperand"/>: <c>CMD: extra operand ‘X’</c> (the tools that call
/// <c>quote()</c>) or <c>'X'</c> (cp, mv, ln, install, join, sort, wc, diff: <c>quoteaf</c>), then the tool's usage hint.
/// REGRESSION: each command spelled it itself, so comm, base64, uniq and split used ASCII quotes and left out the hint,
/// date used ASCII quotes, and basename named no operand. This scan fails on a cmdlet that writes the literal again.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant; the wording is the oracle's
/// (RuntimeCommandParityDifferentialTests.ExtraOperand_OneSpellingPerTool).
/// </summary>
public class ExtraOperandGuardTests
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

    // A string literal that carries the message (not a comment mentioning it).
    private static readonly Regex Literal = new("\"[^\"]*extra operand", RegexOptions.Compiled);

    [Fact]
    public void Cmdlets_WriteExtraOperandOnlyThroughThePrimitive()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var name = Path.GetFileName(file);
            if (name == "FileSystemHelpers.cs") continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                if (Literal.IsMatch(code)) offenders.Add($"{name}:{i + 1}: {code}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Write an extra operand through FileSystemHelpers.WriteExtraOperand / ExtraOperandMessage:\n"
            + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("comm", "c", null, "comm: extra operand ‘c’\nTry 'comm --help' for more information.")]
    [InlineData("basename", "c", null, "basename: extra operand ‘c’\nTry 'basename --help' for more information.")]
    [InlineData("cp", "c", null, "cp: extra operand 'c'\nTry 'cp --help' for more information.")]
    [InlineData("diff", "c", null, "diff: extra operand 'c'\ndiff: Try 'diff --help' for more information.")]
    [InlineData("wc", "a", "file operands cannot be combined with --files0-from",
        "wc: extra operand 'a'\nfile operands cannot be combined with --files0-from\nTry 'wc --help' for more information.")]
    public void Message_IsGnuWording(string cmd, string operand, string? why, string expected)
        => Assert.Equal(expected, FileSystemHelpers.ExtraOperandMessage(cmd, operand, why));

    [Fact]
    public void Message_SortCheck_NamesTheOptionOnTheSameLineWithoutAHint()
        => Assert.Equal("sort: extra operand 'f2' not allowed with -c",
            FileSystemHelpers.ExtraOperandMessage("sort", "f2", sameLine: " not allowed with -c", hint: false));
}
