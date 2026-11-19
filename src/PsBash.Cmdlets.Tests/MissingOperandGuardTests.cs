using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// GNU's "missing operand" usage error has ONE spelling, written by <see cref="FileSystemHelpers.WriteMissingOperand"/>
/// / <see cref="FileSystemHelpers.MissingOperandMessage"/>: <c>CMD: missing operand[ after ‘X’]</c>, the tool's usage
/// hint, status 1 (<c>expr</c> 2, <c>nohup</c> 125). REGRESSION: each command spelled it itself, so dirname and
/// basename printed nothing at status 0, mkdir/rm/rmdir/sleep left out the hint and chmod/comm/join quoted the operand
/// with ASCII quotes. This scan fails on a cmdlet that writes the literal again. Oracle note (qa-rubric Directive 1):
/// a repo-structure invariant; the wording is the oracle's (RuntimeCommandParityDifferentialTests.MissingOperand_*).
/// </summary>
public class MissingOperandGuardTests
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

    // A string literal that carries the message (not a comment mentioning it), in either spelling: "missing operand"
    // and the file tools' "missing file operand" / "missing destination file operand" (WriteMissingFileOperand).
    // REGRESSION: touch and install spelled the file form themselves and left out the usage hint.
    private static readonly Regex Literal = new("\"[^\"]*missing [a-z ]*operand", RegexOptions.Compiled);

    // diff is GNU diffutils: its hint is `diff: Try ...` and it names the program, not an operand, after `after`.
    // gzip: its "missing file operand" is not a GNU message at all (GNU gzip reads stdin); a known leftover.
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "FileSystemHelpers.cs", "DiffPlan.cs", "InvokeBashGzipCommand.cs" };

    [Fact]
    public void Cmdlets_WriteMissingOperandOnlyThroughThePrimitive()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var name = Path.GetFileName(file);
            if (Allowed.Contains(name)) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                if (Literal.IsMatch(code)) offenders.Add($"{name}:{i + 1}: {code}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Write a missing operand through FileSystemHelpers.WriteMissingOperand / MissingOperandMessage:\n"
            + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("dirname", null, "dirname: missing operand\nTry 'dirname --help' for more information.")]
    [InlineData("chmod", "755", "chmod: missing operand after ‘755’\nTry 'chmod --help' for more information.")]
    [InlineData("gzip", null, "gzip: missing operand\nTry `gzip --help' for more information.")]
    public void Message_IsGnuWording(string cmd, string? after, string expected)
        => Assert.Equal(expected, FileSystemHelpers.MissingOperandMessage(cmd, after));
}
