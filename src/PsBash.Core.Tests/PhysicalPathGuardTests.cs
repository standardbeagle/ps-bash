using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PsBash.Testing;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// A test of a command whose answer is a canonical path (readlink, realpath) builds its expectation from
/// <see cref="PhysicalPath.Of"/>, not from the temp dir as typed. On macOS <c>/var</c> is a link to
/// <c>/private/var</c>, so the typed spelling is never the answer (publish v0.15.0: eight such tests failed on macOS
/// only, while the commands were right).
/// Oracle note (qa-rubric Directive 1): a test-harness invariant with no bash equivalent.
/// </summary>
public class PhysicalPathGuardTests
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

    // Cmdlets.Tests runs the cmdlets and compares their output; transpiler tests only mention the names.
    private static readonly Regex CanonicalPathCommand = new(@"Invoke-Bash(Readlink|Realpath)\b", RegexOptions.Compiled);

    internal static List<string> Offenders(string src)
    {
        var offenders = new List<string>();
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(src, "PsBash.Cmdlets.Tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;

            var text = File.ReadAllText(file);
            if (CanonicalPathCommand.IsMatch(text) && text.Contains("Path.GetTempPath()", StringComparison.Ordinal)
                && !text.Contains("PhysicalPath.Of(", StringComparison.Ordinal))
                offenders.Add(Path.GetRelativePath(src, file));
        }
        return offenders;
    }

    [Fact]
    public void CanonicalPathTests_BuildTheirExpectationFromPhysicalPath()
    {
        var offenders = Offenders(Path.Combine(RepoRoot(), "src"));
        Assert.True(offenders.Count == 0,
            "a readlink/realpath test under the temp dir must resolve it with PsBash.Testing.PhysicalPath.Of "
            + "(macOS: /var -> /private/var):\n  " + string.Join("\n  ", offenders));
    }

    [SkippableFact]
    public void Of_ResolvesALinkedComponent_AndKeepsAMissingTail()
    {
        var root = Path.Combine(Path.GetTempPath(), "ps-bash", "physpath-" + Guid.NewGuid().ToString("N")[..8]);
        var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
        try
        {
            var link = Path.Combine(root, "link");
            var linked = true;
            try { Directory.CreateSymbolicLink(link, real); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { linked = false; }
            Skip.IfNot(linked, "symlink creation not permitted on this host");

            Assert.Equal(Path.Combine(PhysicalPath.Of(real), "missing", "x"),
                PhysicalPath.Of(Path.Combine(link, "missing", "x")));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
