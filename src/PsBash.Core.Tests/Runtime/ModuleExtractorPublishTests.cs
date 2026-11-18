using System.Text.RegularExpressions;
using PsBash.Core.Runtime;
using Xunit;

namespace PsBash.Core.Tests.Runtime;

/// <summary>
/// Re-extracting the module must never rewrite a published file in place: another ps-bash-host may have the cmdlets
/// DLL memory-mapped from the same directory, and a file truncated under a mapping is a SIGBUS on macOS / Linux (a
/// sharing violation on Windows). The extractor stages each file beside the destination and renames it over
/// (<see cref="ModuleExtractor.PublishFile"/>), so a process holding the old file keeps reading the old bytes.
/// <para>
/// The holder here is an open <see cref="FileStream"/>, not a mapping: truncating a mapped file would kill the test
/// process on POSIX instead of failing the assert. Oracle note (qa-rubric Directive 1): extraction is ps-bash runtime
/// plumbing with no bash equivalent.
/// </para>
/// </summary>
public class ModuleExtractorPublishTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ps-bash", "extract-test-" + Guid.NewGuid().ToString("N"));

    public ModuleExtractorPublishTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] ReadAll(FileStream s)
    {
        s.Position = 0;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static FileStream Hold(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    [Fact]
    public void ReExtract_OverAFileAnotherProcessHolds_LeavesTheHoldersBytesAlone()
    {
        var asm = typeof(ModuleExtractor).Assembly;
        ModuleExtractor.ExtractTo(_dir, asm);
        var dll = Path.Combine(_dir, ModuleExtractor.CmdletsDllFileName);
        var fresh = File.ReadAllBytes(dll);

        // A stale copy (an interrupted extraction) that a running host has open, and no marker: re-extract.
        var stale = "stale cmdlets dll held by a running host"u8.ToArray();
        File.WriteAllBytes(dll, stale);
        File.Delete(Path.Combine(_dir, ".extracted"));
        using var holder = Hold(dll);

        ModuleExtractor.ExtractTo(_dir, asm);

        Assert.Equal(stale, ReadAll(holder)); // the old file was not truncated or rewritten under its holder
        Assert.Equal(fresh, File.ReadAllBytes(dll)); // the directory now has the embedded bytes
        Assert.True(File.Exists(Path.Combine(_dir, ".extracted")), "the marker is published after the files");
    }

    [Fact]
    public void PublishFile_SameBytes_LeavesTheFileUntouched()
    {
        var path = Path.Combine(_dir, "same.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        var written = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, written.AddHours(-1));

        using (var content = new MemoryStream([1, 2, 3]))
            ModuleExtractor.PublishFile(path, content);

        Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(path));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_dir)); // no staging file left behind
    }

    [Fact]
    public void PublishFile_NewBytes_ReplacesTheFileAndLeavesNoStagingFile()
    {
        var path = Path.Combine(_dir, "new.bin");
        File.WriteAllBytes(path, [9]);
        using (var holder = Hold(path))
        {
            using (var content = new MemoryStream([4, 5]))
                ModuleExtractor.PublishFile(path, content);
            Assert.Equal(new byte[] { 9 }, ReadAll(holder));
        }

        Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(path));
        // On Windows the held file was moved aside; once its holder is gone a later publish's cleanup may remain, but
        // no staging (.tmp) file ever does.
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }

    /// <summary>
    /// Guard: the extractor writes a file only through <see cref="ModuleExtractor.PublishFile"/>. A truncating open
    /// (<c>FileMode.Create</c>, <c>FileMode.Truncate</c>, <c>File.WriteAll*</c>, <c>File.Create</c>) of a published
    /// module file is the SIGBUS this class exists for. The staging file is opened <c>CreateNew</c> (never an existing file).
    /// </summary>
    [Fact]
    public void Extractor_WritesOnlyThroughPublishFile()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Core", "Runtime", "ModuleExtractor.cs"));
        var truncating = new Regex(@"FileMode\.(Create|Truncate|Append)\b|File\.(WriteAll\w*|AppendAll\w*|Create|OpenWrite|Copy)\s*\(");
        var hits = text.Split('\n')
            .Select((line, i) => (line, i))
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && truncating.IsMatch(x.line))
            .Select(x => $"ModuleExtractor.cs:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.True(hits.Count == 0,
            "ModuleExtractor rewrites a file in place; a running host may have it mapped (SIGBUS on POSIX). Write "
            + "through ModuleExtractor.PublishFile (stage beside it, rename over):\n  " + string.Join("\n  ", hits));
    }
}
