using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// Guards the per-unit mode invariant (<c>docs/specs/emitter-strategy.md</c> §set): a mode that decides what the emitter
/// writes is decided on the PARSED commands, never on a search of the source text. A text search is wrong both ways: it
/// misses a word known only at run time (<c>set -$o</c> turned errexit on with no checks emitted) and it fires on data
/// (<c>grep -c pipefail f</c>). The status reset after an assignment was gated on the text holding <c>$?</c>, so
/// <c>f() { [ -n "" ] || y=def; }; if f</c> was false.
/// <para>The modes still decided on the text are listed below with why. A new one fails this test: decide it on the AST
/// (<c>PsEmitter.ShellOptions.cs</c>, <c>CanSetShellOption</c>) instead. Errexit, ERR traps, errtrace, inherit_errexit
/// and the status of an assignment must never be in the list.</para>
/// <para>Oracle note (qa-rubric Directive 1): a source-structure invariant with no bash equivalent, so a source check.</para>
/// </summary>
public class TranspileModeTextScanGuardTests
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

    /// <summary>The text-scanned modes that remain (each costs only a slower or larger emission when it misfires).</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "_scriptLastpipe",       // "lastpipe": the last pipe stage runs in this shell
        "_scriptPosix",          // "set -o posix": a prefix assignment outlives a special builtin
        "_scriptReadsLastArg",   // `$_` / `${_…}`: every command records its last argument
        "_lineAbandonMode",      // "failglob": a failed glob abandons the rest of the line
        "_runsDynamicCode",      // eval / source / `.`: run-time text may read anything
        "_declarePrintMode",     // declare -p / export: attributes are recorded for printing
        "_trapMode",             // `trap`
        "_bashLinenoMode",       // BASH_LINENO
        "_discardInteractive",   // "shopt -s psbash_interactive"
    };

    private static readonly Regex TextScan = new(@"\bbash\.Contains\(|IsMatch\(bash\b", RegexOptions.CultureInvariant);
    private static readonly Regex Assigned = new(@"(_\w+)\s*=(?!=)", RegexOptions.CultureInvariant);

    [Fact]
    public void EveryModeDecidedOnTheSourceText_IsAllowListed()
    {
        string dir = Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "PsEmitter*.cs"))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in TextScan.Matches(text))
            {
                // The statement holding the scan: its line, and the lines before it that it continues (`|| ...`). Not the
                // last `;` / `{` / `}`: a pattern holds those (`[|;&)}]`).
                int start = text.LastIndexOf('\n', m.Index) + 1;
                while (start > 1 && Continues(text.Substring(start, Math.Min(8, text.Length - start)).TrimStart()))
                    start = text.LastIndexOf('\n', start - 2) + 1;
                string statement = text[start..m.Index];
                var field = Assigned.Match(statement);
                string name = field.Success ? field.Groups[1].Value : "";
                // A predicate that only decides HOW (not WHETHER) a scan is used is out of scope: the DEBUG-trap and reader
                // probes are their own files with their own guards.
                if (Path.GetFileName(file) is "PsEmitter.DebugTrapMode.cs" or "PsEmitter.Reader.cs") continue;
                if (!Allowed.Contains(name))
                    offenders.Add(Path.GetFileName(file) + ": " + (name.Length > 0 ? name : statement.Trim()));
            }
        }
        Assert.True(offenders.Count == 0,
            "a mode decided on the source text (decide it on the parsed commands, see this class):\n" + string.Join("\n", offenders));
    }

    private static bool Continues(string lineStart) =>
        lineStart.StartsWith("||", StringComparison.Ordinal) || lineStart.StartsWith("&&", StringComparison.Ordinal)
        || lineStart.StartsWith("? ", StringComparison.Ordinal) || lineStart.StartsWith(": ", StringComparison.Ordinal);

    [Fact]
    public void ErrexitAndStatusModes_AreNeverAllowListed()
        => Assert.DoesNotContain(Allowed, n => n.Contains("rrexit", StringComparison.Ordinal) || n.Contains("rrtrace", StringComparison.Ordinal)
                                               || n.Contains("Status", StringComparison.Ordinal));
}
