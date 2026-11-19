using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Keeps path mapping in ONE place per direction (docs/specs/paths.md): a filesystem path becomes the
/// bash-visible spelling (<c>C:/x</c>) only through <see cref="BashPath.ToShell(string?)"/>
/// (<c>BashRuntime.ToShellPath</c> / <c>PsBuild.RuntimeToShellPath</c> from PowerShell), and a script's
/// path becomes a native one only through <see cref="WindowsPath"/> / <see cref="RuntimePath"/>. An ad-hoc
/// <c>Replace('\\', '/')</c> re-derives the rule in one cmdlet and drifts (Linux-unsafe, no UNC / long-path
/// handling, missed by the next change). The allow-list names each remaining use and why it is not a
/// filesystem-path mapping.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent.
/// </summary>
public class PathMappingGuardTests
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

    // C#: .Replace('\\', '/'), .Replace("\\", "/"), .Replace('/', '\\'), .Replace("/", "\\") — also inside
    // emitted PowerShell text (".Replace('\\','/')"). PowerShell: -replace '\\', '/' and .Replace('\', '/').
    private static readonly Regex CSharpForbidden = new(
        @"\.Replace\(\s*(?:'\\\\'\s*,\s*'/'|""\\\\""\s*,\s*""/""|'/'\s*,\s*'\\\\'|""/""\s*,\s*""\\\\"")\s*\)",
        RegexOptions.Compiled);
    private static readonly Regex PowerShellForbidden = new(
        @"-replace\s+'\\\\'\s*,\s*'/'|\.Replace\(\s*'\\'\s*,\s*'/'\s*\)|-replace\s+'/'\s*,\s*'\\\\?'",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>file name -> why its remaining use is not a filesystem-path mapping.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["BashPath.cs"] = "the outbound helper itself",
        ["WindowsPath.cs"] = "the inbound mapper itself (and its /tmp text inverse)",
        ["TarMemberFilter.cs"] = "archive member names: a tar written on Windows may carry '\\' separators on any OS",
        ["InvokeBashTarCommand.cs"] = "archive member names read from an archive (same reason)",
    };

    private static readonly string[] ProductDirs =
        ["PsBash.Cmdlets", "PsBash.Transpiler", "PsBash.Host", "PsBash.Core", "PsBash.Shell", "PsBash.Module"];

    [Fact]
    public void ProductCode_MapsPathsOnlyThroughTheTwoHelpers()
    {
        var offenders = new List<string>();
        foreach (var project in ProductDirs)
        {
            var dir = Path.Combine(RepoRoot(), "src", project);
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                var ext = Path.GetExtension(file);
                Regex? forbidden = ext switch
                {
                    ".cs" => CSharpForbidden,
                    ".psm1" or ".ps1" => PowerShellForbidden,
                    _ => null,
                };
                if (forbidden is null) continue;
                var name = Path.GetFileName(file);
                if (Allowed.ContainsKey(name)) continue;

                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (forbidden.IsMatch(lines[i]))
                        offenders.Add($"{project}/{name}:{i + 1}: {t}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Map a filesystem path to its bash spelling with BashPath.ToShell (PowerShell: [PsBash.Cmdlets.BashRuntime]::ToShellPath, " +
            "emitted text: PsBuild.RuntimeToShellPath) and a script path to a native one with RuntimePath/WindowsPath " +
            "(FileSystemHelpers.NormalizeOperandPath, BashRuntime.MapPath) — not an ad-hoc separator Replace. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }

    // `dir + Path.DirectorySeparatorChar + name`: a hand-joined path. A native join is Path.Combine/Path.Join; a path
    // that is PRINTED (a walker's display path) is BashPath.Child, which joins with '/'. rg built `src\a.txt` this way.
    private static readonly Regex HandJoinedPath = new(
        @"\+\s*(?:System\.IO\.)?Path\.(?:Alt)?DirectorySeparatorChar\s*\+", RegexOptions.Compiled);

    [Fact]
    public void ProductCode_NeverHandJoinsAPathWithTheNativeSeparator()
    {
        var offenders = new List<string>();
        foreach (var project in ProductDirs)
        {
            var dir = Path.Combine(RepoRoot(), "src", project);
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("///", StringComparison.Ordinal)) continue;
                    if (HandJoinedPath.IsMatch(lines[i]))
                        offenders.Add($"{project}/{Path.GetFileName(file)}:{i + 1}: {t}");
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "Join a printed (display) path with BashPath.Child and a native one with Path.Combine — not " +
            "`a + Path.DirectorySeparatorChar + b`. Offenders:\n  " + string.Join("\n  ", offenders));
        Assert.Matches(HandJoinedPath, "return display + Path.DirectorySeparatorChar + name;");
        Assert.DoesNotMatch(HandJoinedPath, "cwd.StartsWith(full + Path.DirectorySeparatorChar)");
    }

    [Theory]
    [InlineData("src", "a.txt", "src/a.txt")]
    [InlineData("src/", "a.txt", "src/a.txt")]
    [InlineData("./src", "a.txt", "./src/a.txt")]
    [InlineData("", "a.txt", "a.txt")]
    [InlineData("C:/x", "y", "C:/x/y")]
    public void BashPath_Child_JoinsWithForwardSlash(string dir, string name, string expected)
        => Assert.Equal(expected, BashPath.Child(dir, name));

    [Fact]
    public void TheAllowList_OnlyNamesFilesThatStillExist()
    {
        var root = Path.Combine(RepoRoot(), "src");
        foreach (var name in Allowed.Keys)
            Assert.True(Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).Any(),
                $"Allow-listed file {name} no longer exists; drop it from PathMappingGuardTests.Allowed.");
    }

    [Fact]
    public void TheGuard_CatchesEachForbiddenSpelling()
    {
        Assert.Matches(CSharpForbidden, "x = path.Replace('\\\\', '/');");
        Assert.Matches(CSharpForbidden, "x = path.Replace(\"\\\\\", \"/\");");
        Assert.Matches(CSharpForbidden, "x = path.Replace('/', '\\\\');");
        Assert.Matches(CSharpForbidden, "Dynamic(expr + \".Replace('\\\\','/')\");");
        Assert.Matches(PowerShellForbidden, "$n = $Path -replace '\\\\', '/'");
        Assert.DoesNotMatch(CSharpForbidden, "x = s.Replace(\"'\", \"''\");");
    }
}
