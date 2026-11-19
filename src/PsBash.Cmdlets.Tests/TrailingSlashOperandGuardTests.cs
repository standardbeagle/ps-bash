using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A name ending in <c>/</c> must resolve to a DIRECTORY (POSIX path resolution). Windows and .NET drop the slash, so a
/// command that created an entry from such an operand made a plain one: <c>ln a eight/</c> made a file <c>eight</c>,
/// <c>install a thirteen/</c> a directory, <c>touch five/</c> / <c>tee ten/</c> / <c>&gt; twenty/</c> failed with the
/// wrong errno. The one answer is <see cref="FileSystemHelpers.SlashedNameErrno"/> (per system call) and
/// <see cref="FileSystemHelpers.ThrowIfNotWritableName"/> (open for writing, used by RedirectFileWriter.OpenFile).
/// <para>
/// The scan fails on a source file in PsBash.Cmdlets that creates an entry by name (opens for writing, writes a whole
/// file, makes a link) without consulting either, unless the allow-list says why its names never come from an operand
/// that can end in <c>/</c>. Oracle: <c>RuntimeCommandParityDifferentialTests.TrailingSlashOperand_*</c>.
/// </para>
/// </summary>
public class TrailingSlashOperandGuardTests : IDisposable
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

    // Creates an entry by name: a write-open (StreamingFileWriterGuardTests' shape), a whole-file write, a link.
    private static readonly Regex CreatesByName = new(
        @"new\s+StreamWriter\s*\(|File\.(?:Create|OpenWrite|AppendText|CreateText|WriteAllBytes|WriteAllText|WriteAllLines|CreateSymbolicLink)\s*\("
        + @"|File\.(?:Copy|Move)\s*\(|Directory\.CreateSymbolicLink\s*\(|CreateHardLink|-ItemType\s+HardLink"
        + @"|(?:new\s+FileStream|File\.Open)\s*\([^;]*?(?:FileAccess\.(?:Write|ReadWrite)|FileMode\.(?:Create|CreateNew|Append|OpenOrCreate|Truncate))",
        RegexOptions.Compiled);

    private static readonly Regex Consults = new(@"\b(?:SlashedNameErrno|ThrowIfNotWritableName)\s*\(", RegexOptions.Compiled);

    /// <summary>file name -> why the names it creates never come from an operand that can end in '/'.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["AwkOutput.cs"] = "`print | \"cmd\"`: a writer over a child's stdin pipe; `print > f` goes through RedirectFileWriter.OpenText",
        ["ChildStdio.cs"] = "a merged child's stdin pipe, not a file",
        ["ProcessSubFileWriter.cs"] = "the `>( )`/`<( )` backing file: a generated temp name",
        ["InvokeBashSplitCommand.cs"] = "output names are PREFIX + suffix letters, never ending in '/'",
        ["InvokeBashSedCommand.cs"] = "sed -i: a temp file beside the input; `w` files use RedirectFileWriter.OpenText",
        ["InvokeBashGzipCommand.cs"] = "a staging file named after the input plus the suffix",
        ["InvokeBashLessCommand.cs"] = "the pager's temp file",
        ["InvokeBashSourceCommand.cs"] = "the placeholder for a missing Claude shell snapshot (a generated name)",
        ["FileLinks.cs"] = "the link primitives: ln and cp consult SlashedNameErrno on the name first",
        ["FileTransfer.cs"] = "the copy/move primitives under cp, mv and install, which consult SlashedNameErrno on the destination first",
        ["BackupControl.cs"] = "renames an existing destination to its backup name (NAME~, NAME.~N~): never ends in '/'",
    };

    [Fact]
    public void Cmdlets_ThatCreateByName_ConsultTheTrailingSlash()
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
            foreach (Match m in CreatesByName.Matches(text))
            {
                int lineStart = text.LastIndexOf('\n', m.Index) + 1;
                var line = text[lineStart..m.Index].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal)) continue;
                if (Allowed.ContainsKey(name)) { matchedAllowed.Add(name); continue; }
                if (Consults.IsMatch(text)) continue;
                int lineNo = 1;
                for (int i = 0; i < m.Index; i++) if (text[i] == '\n') lineNo++;
                offenders.Add($"{name}:{lineNo}: {m.Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A name ending in '/' must be a directory: consult FileSystemHelpers.SlashedNameErrno (or ThrowIfNotWritableName / "
            + "RedirectFileWriter.OpenFile) before creating it, or allow-list the file with why its names never end in '/':\n"
            + string.Join("\n", offenders));

        var stale = Allowed.Keys.Where(k => !matchedAllowed.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Allow-list entries that no longer create anything by name (remove them):\n" + string.Join("\n", stale));
    }

    private readonly string _root = Directory.CreateTempSubdirectory("psbash-slash-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Slashed(string name) => Path.Combine(_root, name) + "/";

    // The errno each system call gives (oracle: the TrailingSlashOperand differential tests, Linux 6.x).
    [Theory]
    [InlineData("Stat", "No such file or directory", "Not a directory")]
    [InlineData("OpenForWrite", "Is a directory", "Is a directory")]
    [InlineData("Link", "No such file or directory", "File exists")]
    public void SlashedNameErrno_IsTheSystemCallsAnswer(string call, string missing, string existingFile)
    {
        var use = Enum.Parse<FileSystemHelpers.NameUse>(call);
        File.WriteAllText(Path.Combine(_root, "file"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "dir"));

        Assert.Equal(missing, FileSystemHelpers.SlashedNameErrno(Slashed("none"), use));
        Assert.Equal(existingFile, FileSystemHelpers.SlashedNameErrno(Slashed("file"), use));
        Assert.Equal(existingFile, FileSystemHelpers.SlashedNameErrno(Slashed("file") + "/", use));
        Assert.Null(FileSystemHelpers.SlashedNameErrno(Slashed("dir"), use));
        Assert.Null(FileSystemHelpers.SlashedNameErrno(Path.Combine(_root, "none"), use));
        Assert.Null(FileSystemHelpers.SlashedNameErrno(Path.GetPathRoot(_root)!, use));
    }
}
