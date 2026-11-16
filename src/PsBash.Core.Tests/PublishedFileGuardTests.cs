using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PsBash.Testing;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// A test that reads a file another process writes while the test runs (a pid file) reads it through
/// <see cref="PublishedFile"/>. <c>File.Exists</c> then <c>File.ReadAllText</c> is a race: the name can
/// be on disk while the writer, or on Windows whatever inspects a freshly renamed file, still holds
/// it, and the read throws "being used by another process" (fleet g43,
/// ProcessTreeStopTests.SpawnedChild_DisposedWhileRunning_EndsTheTree).
/// Oracle note (qa-rubric Directive 1): a test-harness invariant with no bash equivalent.
/// </summary>
public class PublishedFileGuardTests
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

    // A direct read of something named like a pid file: File.ReadAllText(pidFile), File.ReadAllLines(s.PidFile) ...
    private static readonly Regex DirectPidFileRead = new(
        @"\bFile\.(ReadAllText|ReadAllLines|ReadLines|OpenRead|OpenText)\s*\([^)]*pid", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static List<string> Offenders(string src)
    {
        var offenders = new List<string>();
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
            var project = Path.GetRelativePath(src, file).Split(sep)[0];
            if (!project.EndsWith(".Tests", StringComparison.Ordinal) && project != "PsBash.Testing") continue;
            if (Path.GetFileName(file) is "PublishedFileGuardTests.cs") continue;

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                if (DirectPidFileRead.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: {t}");
            }
        }
        return offenders;
    }

    [Fact]
    public void Tests_ReadAnotherProcessPidFile_OnlyThroughPublishedFile()
    {
        var offenders = Offenders(Path.Combine(RepoRoot(), "src"));

        Assert.True(offenders.Count == 0,
            "Read a file another process publishes with PsBash.Testing.PublishedFile (TryRead / ReadWhenReady): " +
            "a direct read races the writer and fails with a sharing violation. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void DirectPidFileRead_MatchesTheRacyShape_AndNotTheHelper()
    {
        Assert.Matches(DirectPidFileRead, "return int.Parse(File.ReadAllText(PidFile).Trim());");
        Assert.Matches(DirectPidFileRead, "if (int.TryParse(File.ReadAllText(pidFile).Trim(), out var p))");
        Assert.DoesNotMatch(DirectPidFileRead, "var text = PublishedFile.ReadWhenReady(PidFile, limit);");
        Assert.DoesNotMatch(DirectPidFileRead, "var text = File.ReadAllText(scriptPath);");
    }

    [Fact]
    public void TryRead_IsNull_WhileTheWriterHoldsTheFile_AndTheContentAfter()
    {
        var path = Path.Combine(Path.GetTempPath(), "ps-bash-published-" + Guid.NewGuid().ToString("N") + ".pid");
        try
        {
            Assert.Null(PublishedFile.TryRead(path));
            using (var held = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                held.Write("4242\n"u8);
                held.Flush();
                // The writer still holds it exclusively: the exact state a bare File.ReadAllText throws on (Windows).
                if (OperatingSystem.IsWindows()) Assert.Null(PublishedFile.TryRead(path));
            }
            Assert.Equal("4242", PublishedFile.TryRead(path)!.Trim());
            Assert.Equal("4242", PublishedFile.ReadWhenReady(path, TimeSpan.FromSeconds(5))!.Trim());
        }
        finally { File.Delete(path); }
    }
}
