using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A pipeline record's bytes come from ONE rule, <see cref="PsBash.Core.StdoutRecord.Bytes"/> — through
/// <see cref="BashRuntime.RecordBytes"/> / <see cref="BashRuntime.IsExactBytes"/> on the cmdlet side, and directly in the
/// host renderer (<c>SdkWorker.GetOutputText</c>, which has no cmdlets reference): a plain record is its text plus the
/// boundary newline, ALWAYS; an exact-bytes (<c>NoTrailingNewline</c>) record is its text. Regression: the renderer used
/// that rule but every reader re-derived its own (<c>text.EndsWith('\n') || IsUnterminated(r) ? text : text + "\n"</c>),
/// so a plain record ending in a newline (<c>jq -r '"a\n"'</c>, <c>basename $'a\n'</c>, <c>echo "$x" &gt;&amp;2</c>) was two
/// lines on the screen and one in <c>| cat</c>, <c>| od</c>, <c>&gt; f</c>, <c>$(…)</c> and on stderr (bash: two
/// everywhere). This scan fails on (1) a read of the <c>NoTrailingNewline</c> property outside the primitive, and (2) a
/// line that rebuilds the reader rule from <c>EndsWith('\n')</c> and <c>IsUnterminated(</c>, in PsBash.Cmdlets,
/// PsBash.Host and the psm1 (spec <c>runtime-functions.md</c> §Partial lines).
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant; the behaviour is pinned by RecordBytesDifferentialTests.
/// </summary>
public class RecordBytesGuardTests
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

    // C#: Properties["NoTrailingNewline"] / Properties[StdoutRecord.ExactBytesProperty]; psm1: Properties['NoTrailingNewline'],
    // $x.NoTrailingNewline.
    internal static readonly Regex FlagRead = new(
        @"Properties\s*\[\s*(?:""NoTrailingNewline""|'NoTrailingNewline'|[\w.]*ExactBytesProperty)\s*\]|[\w)\]]\.NoTrailingNewline\b",
        RegexOptions.Compiled);

    internal static readonly Regex HandRule = new(@"EndsWith\s*\(\s*(?:'\\n'|""\\n"")[^;]*IsUnterminated\s*\(|IsUnterminated\s*\([^;]*EndsWith\s*\(\s*(?:'\\n'|""\\n"")",
        RegexOptions.Compiled);

    /// <summary>"file: text on the line" -> why it may read the flag / is not yet routed.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["BashRuntime.cs: => item is PSObject pso && pso.Properties[PsBash.Core.StdoutRecord.ExactBytesProperty]?.Value is true;"]
            = "THE primitive",
        ["BashRuntime.cs: public static bool IsUnterminated(object? item)"]
            = "the primitive's own definition (exact bytes not ending in a newline); the scan spans into its body",
        ["SdkWorker.cs: bool exact = item.Properties[PsBash.Core.StdoutRecord.ExactBytesProperty]?.Value is true;"]
            = "the host has no cmdlets reference; it routes through StdoutRecord.Bytes on the next line",
        ["InvokeBashWcCommand.cs: if (!text.EndsWith('\\n') && !BashRuntime.IsUnterminated(InputObject)) text += \"\\n\";"]
            = "PENDING: wc is being edited concurrently by another owner; route through BashRuntime.RecordBytes",
    };

    internal static List<string> Scan(string name, string text)
    {
        var hits = new List<string>();
        foreach (var rx in new[] { FlagRead, HandRule })
        {
            foreach (Match m in rx.Matches(text))
            {
                int lineStart = text.LastIndexOf('\n', m.Index) + 1;
                int lineEnd = text.IndexOf('\n', m.Index);
                var line = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal)
                    || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var hit = $"{name}: {line}";
                if (!hits.Contains(hit)) hits.Add(hit);
            }
        }
        return hits;
    }

    private static IEnumerable<string> Sources(string root)
    {
        foreach (var project in new[] { "PsBash.Cmdlets", "PsBash.Host" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                yield return file;
            }
        }
        yield return Path.Combine(root, "src", "PsBash.Module", "PsBash.psm1");
    }

    [Fact]
    public void RecordBytes_ComeOnlyFromTheOnePrimitive()
    {
        var offenders = new List<string>();
        var matchedAllowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Sources(RepoRoot()))
        {
            foreach (var hit in Scan(Path.GetFileName(file), File.ReadAllText(file)))
            {
                if (Allowed.ContainsKey(hit)) matchedAllowed.Add(hit);
                else offenders.Add(hit);
            }
        }

        Assert.True(offenders.Count == 0,
            "A record's bytes / exactness come from BashRuntime.RecordBytes / IsExactBytes / SplitRecord (one rule: a plain "
            + "record is its text plus the boundary newline even when the text ends in one). Route these through it:\n"
            + string.Join("\n", offenders));

        var stale = Allowed.Keys.Where(k => !matchedAllowed.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Allow-list entries that no longer match (remove them):\n" + string.Join("\n", stale));
    }

    [Theory]
    [InlineData("bool exact = o.Properties[\"NoTrailingNewline\"]?.Value is true;")]
    [InlineData("$nnl = $InputObject.PSObject.Properties['NoTrailingNewline']")]
    [InlineData("if ($r.NoTrailingNewline) { }")]
    [InlineData("var e = pso.Properties[StdoutRecord.ExactBytesProperty];")]
    [InlineData("if (!text.EndsWith('\\n') && !BashRuntime.IsUnterminated(item)) text += \"\\n\";")]
    [InlineData("string chunk = text.EndsWith('\\n') || BashRuntime.IsUnterminated(InputObject) ? text : text + \"\\n\";")]
    public void Scan_FlagsAHandBuiltRecordBytesRule(string code)
        => Assert.Single(Scan("X.cs", "class X {\n    void F() {\n        " + code + "\n    }\n}\n"));

    [Theory]
    [InlineData("obj.Properties.Add(new PSNoteProperty(\"NoTrailingNewline\", true));")]   // producing the flag is fine
    [InlineData("string bytes = BashRuntime.RecordBytes(item);")]
    [InlineData("[switch]$NoTrailingNewline,")]
    [InlineData("// the old text.EndsWith('\\n') || IsUnterminated(r) rule")]
    public void Scan_IgnoresProducersThePrimitiveAndComments(string code)
        => Assert.Empty(Scan("X.cs", "class X {\n    void F() {\n        " + code + "\n    }\n}\n"));
}
