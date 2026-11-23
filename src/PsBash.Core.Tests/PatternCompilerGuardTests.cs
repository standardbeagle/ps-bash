using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// ONE bash pattern compiler (<c>Transpiler/Parser/BashPattern.cs</c>, bash §3.5.8.1) serves every pattern context:
/// pathname components, <c>case</c>, <c>[[ == != ]]</c>, <c># ## % %%</c>, <c>/ // /# /%</c>, <c>^ ^^ , ,,</c>,
/// GLOBIGNORE, <c>compgen -X</c>, <c>ls -I</c>. Three matchers used to disagree with each other and with bash (the
/// emitter's GlobToRegex, BashPattern's converter, LsGlob's hand matcher): a reversed range threw in one, <c>&amp;</c>
/// and <c>$</c> in a replacement reached .NET's substitution syntax in another, nocaseglob lower-cased classes in the
/// third. This guard fails when emitter or runtime code builds a glob regex, or matches a shell pattern, anywhere
/// else. The allow-list names each remaining use and why it is not a shell pattern context.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent; the behaviour is covered by
/// PatternContextDifferentialTests.
/// </summary>
public class PatternCompilerGuardTests
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

    /// <summary>C# that turns a glob into a regex or matches one by hand: the removed translators, a second entry into
    /// the compiler's converter, the glob-to-regex idioms (<c>*</c> → <c>.*</c>), and PowerShell's wildcard engine.</summary>
    private static readonly Regex ForbiddenSource = new(
        @"GlobToRegex|ToAnchoredRegex|BashPattern\.ToRegex|BashLocale\.Replace\(|new\s+BashPatternProgram\(|new\s+(BashPattern\.)?Converter\("
        + @"|Append\(""\.\*\??""\)|=>\s*""\.\*\??""|WildcardPattern\.Get\(|new\s+WildcardPattern\(|\bMatchAt\(",
        RegexOptions.Compiled);

    /// <summary>Emitted PowerShell that matches a pattern itself instead of calling the runtime facade
    /// (<c>BashPatternMatch</c>): <c>-creplace</c> / <c>-like</c> / <c>switch -Wildcard</c> / a <c>[regex]'…'</c> literal.</summary>
    private static readonly Regex ForbiddenEmitted = new(
        @"-creplace\b|-c?like\b|switch\s+-Wildcard|\[regex\]'|BashLocale\]::Replace|BashPattern\]::ToRegex",
        RegexOptions.Compiled);

    /// <summary>file name -> why its remaining match is not a shell pattern context.</summary>
    private static readonly Dictionary<string, string> AllowedSource = new(StringComparer.Ordinal)
    {
        ["BashPattern.cs"] = "THE compiler",
        ["RgTypes.cs"] = "rg --type definitions: ripgrep's own glob dialect for file types, not a shell pattern",
        ["InvokeBashDuCommand.cs"] = "du --exclude: the tool's own option (GNU fnmatch); not a shell pattern context (follow-up)",
        ["InvokeBashFindCommand.cs"] = "find -name / -path: the tool's own option; not a shell pattern context (follow-up)",
        ["InvokeBashGrepCommand.cs"] = "grep --include / --exclude: the tool's own option; not a shell pattern context (follow-up)",
        ["InvokeBashRgCommand.cs"] = "rg -g: ripgrep's gitignore-style globs, not a shell pattern",
        ["InvokeBashTreeCommand.cs"] = "tree -P / -I: the tool's own option; not a shell pattern context (follow-up)",
    };

    /// <summary>An emitted line holding one of these is a known non-pattern use.</summary>
    private static readonly string[] AllowedEmittedLines =
    [
        // ${!prefix*}: variable NAMES that start with an identifier (no glob character can occur in it).
        "Get-ChildItem env: | Where-Object {{ $_.Name -clike '{bvs.Name}*' }}",
    ];

    private static IEnumerable<string> SourceFiles(params string[] projects)
    {
        foreach (var project in projects)
        {
            var dir = Path.Combine(RepoRoot(), "src", project);
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                yield return file;
            }
        }
    }

    private static bool IsComment(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeAndEmitter_BuildNoGlobRegexOutsideTheCompiler()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles("PsBash.Transpiler", "PsBash.Cmdlets", "PsBash.Host", "PsBash.Core"))
        {
            var name = Path.GetFileName(file);
            if (AllowedSource.ContainsKey(name)) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (!IsComment(lines[i]) && ForbiddenSource.IsMatch(lines[i]))
                    offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0,
            "A bash pattern is compiled only by BashPattern.Compile (canonical text: quoted characters backslash-escaped, "
            + "BashPattern.EscapeLiteral) and matched through BashPatternProgram / BashPatternMatch. Offenders:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void EmittedPowerShell_MatchesPatternsOnlyThroughTheRuntimeFacade()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles("PsBash.Transpiler"))
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith("PsEmitter", StringComparison.Ordinal) && name != "PsBuild.cs") continue;
            var lines = File.ReadAllLines(file, System.Text.Encoding.Latin1);
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]) || !ForbiddenEmitted.IsMatch(lines[i])) continue;
                if (AllowedEmittedLines.Any(a => lines[i].Contains(a, StringComparison.Ordinal))) continue;
                offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Emitted pattern matching goes through PsBuild.PatternTest / PatternRemove / PatternSubstitute / "
            + "PatternCaseModify ([PsBash.Cmdlets.BashPatternMatch]), never a PowerShell regex or wildcard. Offenders:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheAllowList_OnlyNamesFilesThatStillMatch()
    {
        foreach (var file in SourceFiles("PsBash.Transpiler", "PsBash.Cmdlets", "PsBash.Host", "PsBash.Core"))
        {
            var name = Path.GetFileName(file);
            if (!AllowedSource.ContainsKey(name)) continue;
            Assert.True(File.ReadAllLines(file).Any(l => !IsComment(l) && ForbiddenSource.IsMatch(l)),
                $"{name} is allow-listed but no longer matches: remove it from the list.");
        }
        var names = SourceFiles("PsBash.Transpiler", "PsBash.Cmdlets").Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Assert.All(AllowedSource.Keys, k => Assert.Contains(k, names));
    }
}
