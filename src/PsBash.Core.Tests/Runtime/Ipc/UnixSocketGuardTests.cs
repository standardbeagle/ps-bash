using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests.Runtime.Ipc;

/// <summary>
/// A socket file is removed only after <c>UnixSocketProbe.NothingListens</c> says nobody answers on it. On POSIX,
/// deleting a LIVE socket's file succeeds silently: the host behind it keeps running where no launcher can reach it.
/// The removals that skipped the check were the bind path (a second host took over a live endpoint; on Windows its
/// bind died with an unhandled "address already in use"), the reaper's dead-sidecar path (a replacement binds before
/// it writes its sidecar), and a host's own dispose (after RetireEndpoint a replacement owns the path).
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant; behavior is covered by TransportTests.
/// </summary>
public class UnixSocketGuardTests
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

    // A delete of something named like a socket path: socket, _socketPath, endpoint, sockPath.
    private static readonly Regex SocketDelete = new(
        @"(?:File\.Delete|TryDelete)\(\s*(?:socket|_socketPath|endpoint|sockPath)\s*\)", RegexOptions.Compiled);

    // The one deliberate removal of a possibly live socket: the caller is replacing that host.
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "IpcTransportFactory.cs:RetireEndpoint" };

    [Fact]
    public void ASocketFile_IsRemovedOnlyWhenNothingListensOnIt()
    {
        var offenders = new List<string>();
        foreach (var root in new[] { Path.Combine("src", "PsBash.Core"), Path.Combine("src", "PsBash.Host"), Path.Combine("src", "PsBash.Shell") })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), root), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!SocketDelete.IsMatch(lines[i])) continue;
                // The probe guards it on the same line or in the three lines above.
                bool probed = false;
                for (int k = Math.Max(0, i - 3); k <= i && !probed; k++) probed = lines[k].Contains("NothingListens(");
                if (probed) continue;
                var method = EnclosingMethod(lines, i);
                if (Allowed.Contains($"{Path.GetFileName(file)}:{method}")) continue;
                offenders.Add($"{Path.GetFileName(file)}:{i + 1} ({method}): {lines[i].Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "check UnixSocketProbe.NothingListens before removing a socket file (a live host's file deleted on POSIX "
            + "leaves that host unreachable):\n" + string.Join("\n", offenders));
    }

    private static readonly Regex MethodHeader = new(@"\b(?:static|void|bool|int|Task|ValueTask|string)\b[^=;(]*\b(\w+)\s*\(", RegexOptions.Compiled);

    private static string EnclosingMethod(string[] lines, int i)
    {
        for (int k = i; k >= 0; k--)
        {
            var t = lines[k].TrimStart();
            if (!(t.StartsWith("public ") || t.StartsWith("private ") || t.StartsWith("internal ") || t.StartsWith("protected ")))
                continue;
            var m = MethodHeader.Match(t);
            if (m.Success) return m.Groups[1].Value;
        }
        return "?";
    }
}
