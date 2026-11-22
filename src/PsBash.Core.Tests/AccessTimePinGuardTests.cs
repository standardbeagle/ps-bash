using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PsBash.Testing;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// A test sets an access time only through <see cref="AccessTimePin"/>. With last-access updates on (the Windows
/// default), whatever reads a freshly written file (Defender, the indexer) moves its access time to now between the
/// test's set and its check: <c>ls -u</c> ordered wrong and <c>find -anewer</c> matched an old file on beagle-ab
/// (fleet s24m, LsTimeSortTests / FindPredicateTests). The pin re-applies the times and reruns a disturbed check.
/// Oracle note (qa-rubric Directive 1): a test-harness invariant with no bash equivalent.
/// </summary>
public class AccessTimePinGuardTests
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

    // File.SetLastAccessTime(..), Directory.SetLastAccessTimeUtc(..), info.LastAccessTime = ..
    internal static readonly Regex DirectAccessTimeSet = new(
        @"\.SetLastAccessTime(Utc)?\s*\(|\.LastAccessTime(Utc)?\s*=(?!=)", RegexOptions.Compiled);

    internal static List<string> Offenders(string src)
    {
        var offenders = new List<string>();
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
            var project = Path.GetRelativePath(src, file).Split(sep)[0];
            if (!project.EndsWith(".Tests", StringComparison.Ordinal) && project != "PsBash.Testing") continue;
            if (Path.GetFileName(file) is "AccessTimePin.cs" or "AccessTimePinGuardTests.cs") continue;

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                if (DirectAccessTimeSet.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: {t}");
            }
        }
        return offenders;
    }

    [Fact]
    public void Tests_SetAccessTimes_OnlyThroughAccessTimePin()
    {
        var offenders = Offenders(Path.Combine(RepoRoot(), "src"));

        Assert.True(offenders.Count == 0,
            "Set an access time in a test with PsBash.Testing.AccessTimePin (Set, and Run around the check that " +
            "reads it): another process reading a new file moves a directly set access time. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void DirectAccessTimeSet_MatchesTheRacyShapes_AndNotTheHelper()
    {
        Assert.Matches(DirectAccessTimeSet, "File.SetLastAccessTime(p, atime);");
        Assert.Matches(DirectAccessTimeSet, "Directory.SetLastAccessTimeUtc(dir, t);");
        Assert.Matches(DirectAccessTimeSet, "new FileInfo(p).LastAccessTime = old;");
        Assert.DoesNotMatch(DirectAccessTimeSet, "_atimes.Set(p, atime);");
        Assert.DoesNotMatch(DirectAccessTimeSet, "Assert.True(File.GetLastAccessTimeUtc(f) > old);");
        Assert.DoesNotMatch(DirectAccessTimeSet, "if (info.LastAccessTime == old) return;");
    }

    [Fact]
    public void Run_AppliesThePin_AndRerunsWhileAnotherReaderMovesIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "ps-bash-atime-pin-" + Guid.NewGuid().ToString("N"));
        var old = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        try
        {
            File.WriteAllText(path, "x");
            var pin = new AccessTimePin();
            pin.Set(path, old);
            Assert.Equal(old, File.GetLastAccessTimeUtc(path));

            // The first two checks are "disturbed" (the time moves while they run); the third sees the pin hold.
            int runs = 0;
            var seen = pin.Run(() =>
            {
                var at = File.GetLastAccessTimeUtc(path);
                if (++runs < 3) File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                return at;
            });
            Assert.Equal(3, runs);
            Assert.Equal(old, seen);

            // A check that keeps moving the time (a product bug) runs Tries times and its result still shows it.
            runs = 0;
            pin.Run(() => { runs++; File.SetLastAccessTimeUtc(path, DateTime.UtcNow); });
            Assert.Equal(AccessTimePin.Tries, runs);
            Assert.NotEqual(old, File.GetLastAccessTimeUtc(path));

            // A check that sets the time on purpose names it in `after` and runs once.
            runs = 0;
            var stamp = new DateTime(2024, 1, 2, 15, 30, 0, DateTimeKind.Utc);
            pin.Run(() => { runs++; File.SetLastAccessTimeUtc(path, stamp); }, (path, stamp));
            Assert.Equal(1, runs);
        }
        finally { File.Delete(path); }
    }
}
