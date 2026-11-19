using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A pipeline record becomes lines through ONE splitter, <see cref="BashRuntime.SplitRecord(object?, out bool)"/> /
/// <see cref="BashRuntime.SplitBytes"/> (and <see cref="BashRuntime.RecordLines"/> / <c>LineStreamRecord.SubLines</c> built on it). Every consumer used
/// to spell the split itself as <c>text.TrimEnd('\n').Split('\n')</c>, which strips EVERY trailing newline: a record
/// carries at most one terminator of its own, so the rest were empty lines, and <c>printf 'a\n\n' | grep -c ''</c>
/// counted 1 (bash 2) in grep, sed, cat, nl, cut, rev, tac, sort, uniq, head, tail, shuf, column, rg, fold, expand ….
/// This scan fails on any <c>TrimEnd(…'\n'…)</c> in PsBash.Cmdlets outside the allow-list, which names each remaining
/// trim and why it is not a record → lines split (spec <c>runtime-functions.md</c> §Partial lines).
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent; the behaviour is pinned by
/// RecordTrailingEmptyLinesDifferentialTests.
/// </summary>
public class RecordSplitGuardTests
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

    // A TrimEnd whose argument list names '\n' (as a char, in a char array, or a "\n" string): TrimEnd('\n'),
    // TrimEnd('\r', '\n'), TrimEnd(new[] { '\n' }), TrimEnd("\n".ToCharArray()) ….
    internal static readonly Regex NewlineTrim = new(@"\.TrimEnd\s*\((?:[^;()]|\([^;()]*\))*?(?:'\\n'|""\\n"")", RegexOptions.Compiled);

    /// <summary>"file: text on the line" -> why that trim is not a pipeline record split into lines.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["FormatStyledCommand.cs: return writer.ToString().TrimEnd('\\r', '\\n');"]
            = "styled table text this command just rendered, emitted as one record",
        ["StyledInteractiveSession.cs: return writer.ToString().TrimEnd('\\r', '\\n');"]
            = "styled text this command just rendered for the interactive screen",
        ["InvokeBashIdCommand.cs: var trimmed = run.Stdout.TrimEnd('\\r', '\\n');"]
            = "a child process's one-line answer (id lookup), not pipeline input",
        ["InvokeBashIdCommand.cs: if (run.Stderr.Length > 0) FileSystemHelpers.WriteStderr(this, run.Stderr.TrimEnd('\\r', '\\n'));"]
            = "the system id's diagnostic forwarded to stderr (the writer adds the newline)",
        ["InvokeBashPsCommand.cs: return result.TimedOut ? null : result.Stdout.TrimEnd('\\r', '\\n');"]
            = "a child process's one-line answer, not pipeline input",
        ["InvokeBashJqCommand.cs: if (halt.Text != null) FileSystemHelpers.WriteStderr(this, halt.Text.TrimEnd('\\n'));"]
            = "jq halt_error text written to stderr (the writer adds the newline)",
        ["InvokeBashSedCommand.cs: if (!string.IsNullOrEmpty(res.Stderr)) FileSystemHelpers.WriteStderr(this, res.Stderr.TrimEnd('\\n', '\\r'));"]
            = "a child's stderr text forwarded to stderr",
        ["InvokeBashSedCommand.cs: raw = raw.TrimEnd('\\n');"]
            = "sed -f SCRIPT file text; trailing blank script lines are no-op commands",
        ["InvokeBashTputCommand.cs: if (spawn.Stderr.Length > 0) FileSystemHelpers.WriteStderr(this, spawn.Stderr.TrimEnd('\\n', '\\r'));"]
            = "the system tput's diagnostic forwarded to stderr (the writer adds the newline)",
        ["InvokeBashXanCommand.cs: string body = sb.ToString().TrimEnd('\\r', '\\n');"]
            = "a table this command just rendered, emitted as one record",
    };

    internal static List<string> Scan(string name, string text)
    {
        var hits = new List<string>();
        foreach (Match m in NewlineTrim.Matches(text))
        {
            int lineStart = text.LastIndexOf('\n', m.Index) + 1;
            int lineEnd = text.IndexOf('\n', m.Index);
            var line = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)].Trim();
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal)) continue;
            hits.Add($"{name}: {line}");
        }
        return hits;
    }

    [Fact]
    public void Cmdlets_SplitRecordsIntoLinesOnlyThroughSplitRecord()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        var matchedAllowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            foreach (var hit in Scan(Path.GetFileName(file), File.ReadAllText(file)))
            {
                if (Allowed.ContainsKey(hit)) matchedAllowed.Add(hit);
                else offenders.Add(hit);
            }
        }

        Assert.True(offenders.Count == 0,
            "Split a pipeline record into lines with BashRuntime.SplitRecord / RecordLines (drop ONE own terminator, "
            + "then split) — TrimEnd('\\n') eats trailing empty lines. If this trim is not a record split, add the line "
            + "to the allow-list with the reason:\n" + string.Join("\n", offenders));

        var stale = Allowed.Keys.Where(k => !matchedAllowed.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Allow-list entries that no longer match a trim (remove them):\n" + string.Join("\n", stale));
    }

    [Theory]
    [InlineData("string trimmed = text.TrimEnd('\\n');")]
    [InlineData("var pieces = GetBashText(item).TrimEnd('\\n').Split('\\n');")]
    [InlineData("var t = text.TrimEnd('\\r', '\\n');")]
    [InlineData("var t = text.TrimEnd(new[] { '\\n' });")]
    [InlineData("var t = text.TrimEnd(\"\\n\".ToCharArray());")]
    public void Scan_FlagsTheTrimAllNewlinesSplit(string code)
        => Assert.Single(Scan("X.cs", "class X {\n    void F() {\n        " + code + "\n    }\n}\n"));

    [Theory]
    [InlineData("var lines = BashRuntime.SplitRecord(item, out bool open);")]
    [InlineData("var t = line.TrimEnd('\\r');")]
    [InlineData("// the old text.TrimEnd('\\n') stripped every newline")]
    public void Scan_IgnoresTheSplitterAndNonNewlineTrims(string code)
        => Assert.Empty(Scan("X.cs", "class X {\n    void F() {\n        " + code + "\n    }\n}\n"));
}
