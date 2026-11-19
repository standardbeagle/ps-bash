using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A command that writes a file WHILE it runs (a redirect, <c>tee</c>, awk's <c>print &gt; f</c>, find's
/// <c>-fprint</c>, sed's <c>w</c>, uniq's OUTPUT, <c>exec 3&gt; f</c>) opens it through ONE writer,
/// <see cref="RedirectFileWriter"/> (<c>OpenFile</c> / <c>OpenText</c>), so every such file follows one flush policy.
/// Each ad-hoc <c>new StreamWriter(new FileStream(...), 65536)</c> picked its own buffer, and a slow producer's output
/// sat in it until the command ended: a <c>tail -f</c>, a reader in another job or a later stage saw nothing.
/// This scan fails on a new write-opening <see cref="FileStream"/> / <see cref="StreamWriter"/> / <c>File.Create</c> /
/// <c>File.Open(..., Write)</c> in PsBash.Cmdlets outside the allow-list, which names each remaining opener and why
/// it is not a streaming output file (a whole-content write, a lock, a temp file renamed into place, a child's pipe).
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent.
/// </summary>
public class StreamingFileWriterGuardTests
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

    // A statement that opens a file for writing: any StreamWriter, File.Create/OpenWrite/AppendText/CreateText, and a
    // FileStream / File.Open whose arguments (up to the statement's ';') ask for write access or a creating mode.
    private static readonly Regex WriteOpen = new(
        @"new\s+StreamWriter\s*\(|File\.(?:Create|OpenWrite|AppendText|CreateText)\s*\("
        + @"|(?:new\s+FileStream|File\.Open)\s*\([^;]*?(?:FileAccess\.(?:Write|ReadWrite)|FileMode\.(?:Create|CreateNew|Append|OpenOrCreate|Truncate))",
        RegexOptions.Compiled);

    /// <summary>file name -> why its write-opens are not a streaming output file.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["RedirectFileWriter.cs"] = "the shared writer itself (OpenFile)",
        ["InvokeBashRedirectCommand.cs"] = "opens a `>`/`>>` target and hands the stream straight to RedirectFileWriter",
        ["AwkOutput.cs"] = "`print | \"cmd\"`: a text writer over a child process's stdin pipe, not a file",
        ["ChildStdio.cs"] = "a merged child's stdin: a text writer over the pipe to the child process, not a file",
        ["ProcessSubFileWriter.cs"] = "the `>( )`/`<( )` backing file: its own spill writer, read back by the consumer",
        ["BashRuntime.cs"] = "WriteRawText: a whole-content append in one call, closed at once",
        ["InvokeBashHistoryCommand.cs"] = "history -w/-a: the whole list written in one call, closed at once",
        ["InvokeBashFlockCommand.cs"] = "the lock file: opened to hold a lock, never written",
        ["InvokeBashTouchCommand.cs"] = "creates an empty file",
        ["InvokeBashSplitCommand.cs"] = "each piece written whole in one call; the last piece reopened only to truncate",
        ["InvokeBashSedCommand.cs"] = "sed -i: a temp file renamed over the input once complete (`w` files use OpenText)",
        ["InvokeBashGzipCommand.cs"] = "a staging file renamed into place once complete",
        ["InvokeBashTarCommand.cs"] = "the archive and extracted members: final products, each written in one pass",
        ["BashFdTable.cs"] = "`exec N<>file`: reader and writer share ONE FileStream position, as one descriptor does, flushed per write; `>`/`>>` descriptors use OpenText",
    };

    [Fact]
    public void Cmdlets_OpenStreamingOutputFilesOnlyThroughRedirectFileWriter()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        var matchedAllowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);
            foreach (Match m in WriteOpen.Matches(text))
            {
                int lineStart = text.LastIndexOf('\n', m.Index) + 1;
                var line = text[lineStart..m.Index].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal)) continue;
                if (Allowed.ContainsKey(name)) { matchedAllowed.Add(name); continue; }
                int lineNo = 1;
                for (int i = 0; i < m.Index; i++) if (text[i] == '\n') lineNo++;
                offenders.Add($"{name}:{lineNo}: {m.Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Open a file a command streams output into with RedirectFileWriter.OpenFile/OpenText (one flush policy), "
            + "or add the file to the allow-list with the reason it is not a streaming output file:\n"
            + string.Join("\n", offenders));

        var stale = Allowed.Keys.Where(k => !matchedAllowed.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Allow-list entries that no longer open a file for writing (remove them):\n" + string.Join("\n", stale));
    }

    [Fact]
    public void Scan_FlagsAnAdHocBufferedWriter()
    {
        // The shape every streaming writer used before: a private 64 KB buffer over a FileStream.
        const string sample = "_w = new StreamWriter(\n    new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), enc, 65536);";
        Assert.Matches(WriteOpen, sample);
        Assert.Matches(WriteOpen, "var fs = new FileStream(\n p,\n append ? FileMode.Append : FileMode.Create,\n FileAccess.Write);");
        Assert.DoesNotMatch(WriteOpen, "using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);");
    }
}
