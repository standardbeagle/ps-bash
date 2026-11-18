using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PsBash.Core;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A background job reads the same stdin cursor as the shell (<see cref="BackgroundJobStdin"/>), so every take from it is
/// ONE atomic step (<see cref="SharedStdin.TryTake"/> / <see cref="StdinCursor.TryDequeue(out object?)"/>).
/// REGRESSION: readers checked <c>Count</c> and then called <c>Dequeue</c>; another reader could take the record in
/// between, and <c>Dequeue</c> threw "Queue empty" at end of input (or a reader took past the record it checked).
/// Oracle note (qa-rubric Directive 1): a thread-safety invariant of the cursor, with no single-threaded bash equivalent.
/// </summary>
public class SharedStdinTakeTests
{
    private const int Readers = 4;

    private static void Drain(int rounds, int records, Func<StdinCursor, (bool Ok, string? Item)> take)
    {
        for (int round = 0; round < rounds; round++)
        {
            var expected = Enumerable.Range(0, records).Select(i => "r" + i).ToArray();
            var cursor = new StdinCursor(expected.Cast<object>());
            var got = new ConcurrentBag<string>();
            var errors = new ConcurrentBag<Exception>();
            using var start = new Barrier(Readers);
            var threads = Enumerable.Range(0, Readers).Select(_ => new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    while (take(cursor) is (true, var item)) got.Add(item!);
                }
                catch (Exception ex) { errors.Add(ex); }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.True(errors.IsEmpty, $"round {round}: a reader threw: {errors.FirstOrDefault()}");
            // Each record exactly once: none lost, none taken twice.
            Assert.Equal(expected.Order(StringComparer.Ordinal), got.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void TryTake_ManyReadersOnOneCursor_TakeEachRecordExactlyOnce()
        => Drain(300, 40, c => SharedStdin.TryTake(c, out var r) ? (true, (string)r!) : (false, null));

    [Fact]
    public void TryDequeueLine_ManyReadersOnOneCursor_TakeEachLineExactlyOnce()
        => Drain(300, 40, c => SharedStdin.TryDequeueLine(c, out var line, out _) ? (true, line) : (false, null));

    [Fact]
    public void TryTake_AtEndOfInput_IsFalse_NotAnException()
    {
        var cursor = new StdinCursor(new object[] { "only" });
        Assert.True(SharedStdin.TryTake(cursor, out var first));
        Assert.Equal("only", first);
        Assert.False(SharedStdin.TryTake(cursor, out var none));
        Assert.Null(none);
        Assert.False(SharedStdin.TryTake(new Queue<object>(), out _));
        Assert.False(SharedStdin.TryTake(null, out _));
    }
}

/// <summary>
/// Nothing takes from a shared stdin by <c>Count</c> then <c>Dequeue</c>: the emitted feed (<see cref="PsBuild.StdinFeed"/>),
/// the psm1 and the cmdlets take with <c>TryDequeue</c>. The scan fails on a line that names <c>$global:__BashStdIn</c>
/// and checks <c>.Count</c> or calls <c>.Dequeue()</c>, and on a <c>Dequeue()</c> in SharedStdin.cs on anything but a
/// plain single-threaded <c>Queue</c> local. Oracle note (qa-rubric Directive 1): a repo-structure invariant.
/// </summary>
public class SharedStdinTakeGuardTests
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

    [Fact]
    public void EmittedFeed_TakesWithTryDequeue()
    {
        Assert.Contains("TryDequeue([ref]", PsBuild.StdinFeed, StringComparison.Ordinal);
        Assert.DoesNotContain(".Count", PsBuild.StdinFeed, StringComparison.Ordinal);
        Assert.DoesNotContain(".Dequeue()", PsBuild.StdinFeed, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSource_ChecksCountThenDequeues_OnTheSharedStdin()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        foreach (var project in new[] { "PsBash.Cmdlets", "PsBash.Transpiler", "PsBash.Module", "PsBash.Host", "PsBash.Core" })
        {
            var dir = Path.Combine(root, "src", project);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                if (!(file.EndsWith(".cs") || file.EndsWith(".psm1") || file.EndsWith(".ps1"))) continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.TrimStart().StartsWith("//") || line.TrimStart().StartsWith("#")) continue;
                    if (line.Contains("__BashStdIn") && (line.Contains("__BashStdIn.Count") || line.Contains("__BashStdIn.Dequeue()")))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
                }
            }
        }
        Assert.True(offenders.Count == 0, "count-then-take on the shared stdin (take with TryDequeue):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void SharedStdin_DequeuesOnlyFromAPlainQueueLocal()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets", "SharedStdin.cs"));
        var bad = Regex.Matches(text, @"(\w+)\.Dequeue\(\)")
            .Where(m => m.Groups[1].Value is not ("q" or "queue"))
            .Select(m => m.Value)
            .ToList();
        Assert.True(bad.Count == 0, "SharedStdin.cs takes from a cursor with Dequeue (use TryTake): " + string.Join(", ", bad));
    }
}
