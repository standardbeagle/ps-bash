using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Every C# source and cassette under <c>src/</c> is LF, as <c>.gitattributes</c> declares (<c>*.cs text eol=lf</c>,
/// <c>*.cassette text eol=lf</c>).
/// <para>
/// The bug class: 33 .cs files were committed with CRLF blobs (some mixed) despite <c>eol=lf</c>. Git cannot hold such a
/// file clean: once a checkout rewrites it, the working copy no longer matches the blob and it shows as modified for
/// good. Fleet snapshots (built through a temporary index, which normalizes) carried LF blobs and real commits carried
/// CRLF, so switching the beagle-ab checkout between the two left 34 phantom changes and the next gate aborted on
/// "tracked changes" (s23k). On a checkout of the commit itself the CRLF lands in the working tree, which this scans.
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check.</para>
/// </summary>
public class SourceLineEndingGuardTests
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
    public void SourceAndCassettes_HaveNoCarriageReturn()
    {
        string src = Path.Combine(RepoRoot(), "src");
        var sep = Path.DirectorySeparatorChar;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            if (!(file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".cassette", StringComparison.Ordinal))) continue;
            if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}")) continue;
            if (Array.IndexOf(File.ReadAllBytes(file), (byte)'\r') >= 0)
                offenders.Add(Path.GetRelativePath(src, file));
        }
        Assert.True(offenders.Count == 0,
            "These files contain a carriage return; .gitattributes makes them LF (write \\r in a literal as an escape):\n"
            + string.Join("\n", offenders));
    }
}
