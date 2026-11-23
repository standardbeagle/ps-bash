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
/// GLOBIGNORE, <c>compgen -X</c>, <c>ls -I</c>, <c>help PATTERN</c>. Three matchers used to disagree with each other
/// and with bash (the emitter's GlobToRegex, BashPattern's converter, LsGlob's hand matcher): a reversed range threw in
/// one, <c>&amp;</c> and <c>$</c> in a replacement reached .NET's substitution syntax in another, nocaseglob
/// lower-cased classes in the third. This guard fails when emitter or runtime code builds a glob regex, or matches a
/// shell pattern, anywhere else. The allow-list names each remaining use by FILE AND MEMBER, and why it is not a shell
/// pattern context.
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
    /// the compiler's converter, the glob-to-regex idioms (<c>*</c> → <c>.*</c> by Append, a lambda, a
    /// <c>Replace("\\*", ".*")</c> or a <c>".*" +</c> concatenation), PowerShell's wildcard engine and .NET's file-name
    /// matcher.</summary>
    internal static readonly Regex ForbiddenSource = new(
        @"GlobToRegex|ToAnchoredRegex|BashPattern\.ToRegex|BashLocale\.Replace\(|new\s+BashPatternProgram\(|new\s+(BashPattern\.)?Converter\("
        + @"|Append\(""\.\*\??""\)|=>\s*""\.\*\??""|""\.\*\??""\s*\+|\+\s*""\.\*\??""|Replace\([^;]*,\s*@?""\.(\*\??)?""\s*\)"
        + @"|WildcardPattern\.Get\(|new\s+WildcardPattern\(|\bMatchAt\(|FileSystemName\.Matches(Simple|Win32)Expression",
        RegexOptions.Compiled);

    /// <summary>Emitted PowerShell that matches a pattern itself instead of calling the runtime facade
    /// (<c>BashPatternMatch</c>): <c>-creplace</c> / <c>-like</c> / <c>switch -Wildcard</c> / a <c>[regex]'…'</c> literal.</summary>
    private static readonly Regex ForbiddenEmitted = new(
        @"-creplace\b|-c?like\b|switch\s+-Wildcard|\[regex\]'|BashLocale\]::Replace|BashPattern\]::ToRegex",
        RegexOptions.Compiled);

    /// <summary>A member declaration line: modifiers, then the member's name before <c>(</c>, <c>=&gt;</c>, <c>=</c>,
    /// <c>{</c> or the end of the line. A line belongs to the last member declared above it.</summary>
    private static readonly Regex MemberDeclaration = new(
        @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|internal|protected|static|readonly|override|virtual|sealed|async|unsafe|new|partial|extern|const)\s+)+"
        + @"[^=;(]*?\b(?<name>[A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*(?:\(|=>|=|\{|;|$)",
        RegexOptions.Compiled);

    /// <summary>"File.cs:Member" -> why its remaining match is not a second shell pattern compiler.</summary>
    private static readonly Dictionary<string, string> AllowedSource = new(StringComparer.Ordinal)
    {
        ["BashPattern.cs:Compile"] = "THE compiler: the cache's miss path",
        ["BashPattern.cs:BashPatternProgram"] = "THE compiler: a program compiles its pattern",
        ["BashPattern.cs:PrefixShort"] = "THE compiler: the lazy (shortest-prefix) reading of the same pattern",
        ["RgTypes.cs:GlobRegex"] = "rg --type definitions: ripgrep's own glob dialect for file types, not a shell pattern",
        ["InvokeBashDuCommand.cs:EndProcessing"] = "du --exclude: the tool's own option (GNU fnmatch); not a shell pattern context (follow-up)",
        ["InvokeBashFindCommand.cs:GlobMatch"] = "find -name: the tool's own option; not a shell pattern context (follow-up)",
        ["InvokeBashFindCommand.cs:PathGlobMatch"] = "find -path: the tool's own option; not a shell pattern context (follow-up)",
        ["InvokeBashGrepCommand.cs:BuildRecursiveFileFilter"] = "grep --include / --exclude: the tool's own option; not a shell pattern context (follow-up)",
        ["InvokeBashRgCommand.cs:BuildAccept"] = "rg -g: ripgrep's gitignore-style globs, not a shell pattern",
        ["InvokeBashTreeCommand.cs:WildcardMatch"] = "tree -P / -I: the tool's own option; not a shell pattern context (follow-up)",
    };

    /// <summary>An emitted line holding one of these is a known non-pattern use.</summary>
    private static readonly string[] AllowedEmittedLines =
    [
        // ${!prefix*}: variable NAMES that start with an identifier (no glob character can occur in it).
        "Where-Object {{ $_ -clike '{prefix}*' }}",
        // declare -n: the runtime's own diagnostic text, a fixed literal prefix (no bash data).
        "($__psbNr -like 'warning:*' ? 0 : 1)",
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

    /// <summary>
    /// The CODE of each line (a <c>//</c> line comment and the text of a <c>/* … */</c> block comment removed; a line
    /// that starts with <c>*</c> is code unless it is inside a block comment), with its 1-based number and the member it
    /// belongs to.
    /// </summary>
    internal static IEnumerable<(int Line, string Code, string Member)> CodeLines(IReadOnlyList<string> lines)
    {
        bool inBlock = false;
        string member = "-";
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            var code = new System.Text.StringBuilder(line.Length);
            for (int j = 0; j < line.Length; j++)
            {
                if (inBlock)
                {
                    if (line[j] == '*' && j + 1 < line.Length && line[j + 1] == '/') { inBlock = false; j++; }
                    continue;
                }
                if (line[j] == '/' && j + 1 < line.Length && line[j + 1] == '*') { inBlock = true; j++; continue; }
                if (line[j] == '/' && j + 1 < line.Length && line[j + 1] == '/') break;
                if (line[j] == '\'') // a char literal ('"', '\\', '/'): copied whole
                {
                    int k = j + 1 < line.Length && line[j + 1] == '\\' ? j + 3 : j + 2;
                    if (k < line.Length && line[k] == '\'')
                    {
                        code.Append(line, j, k + 1 - j);
                        j = k;
                        continue;
                    }
                }
                if (line[j] == '"') // a string literal: copied whole, so a `//` or `/*` in it is not a comment
                {
                    bool verbatim = j > 0 && line[j - 1] == '@' || j > 1 && line[j - 1] == '$' && line[j - 2] == '@';
                    int k = j + 1;
                    while (k < line.Length)
                    {
                        if (verbatim && line[k] == '"' && k + 1 < line.Length && line[k + 1] == '"') { k += 2; continue; }
                        if (line[k] == '"') break;
                        k += !verbatim && line[k] == '\\' ? 2 : 1;
                    }
                    code.Append(line, j, Math.Min(k + 1, line.Length) - j);
                    j = k;
                    continue;
                }
                code.Append(line[j]);
            }
            string text = code.ToString();
            if (MemberDeclaration.Match(text) is { Success: true } m) member = m.Groups["name"].Value;
            if (text.Trim().Length > 0) yield return (i + 1, text, member);
        }
    }

    private static List<(string Key, string Text)> SourceHits(string file)
    {
        var name = Path.GetFileName(file);
        var hits = new List<(string, string)>();
        foreach (var (line, code, member) in CodeLines(File.ReadAllLines(file, System.Text.Encoding.Latin1)))
            if (ForbiddenSource.IsMatch(code)) hits.Add(($"{name}:{member}", $"{name}:{line} [{member}]: {code.Trim()}"));
        return hits;
    }

    [Fact]
    public void RuntimeAndEmitter_BuildNoGlobRegexOutsideTheCompiler()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles("PsBash.Transpiler", "PsBash.Cmdlets", "PsBash.Host", "PsBash.Core"))
            foreach (var (key, text) in SourceHits(file))
                if (!AllowedSource.ContainsKey(key)) offenders.Add(text);
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
            foreach (var (line, code, _) in CodeLines(File.ReadAllLines(file, System.Text.Encoding.Latin1)))
            {
                if (!ForbiddenEmitted.IsMatch(code)) continue;
                if (AllowedEmittedLines.Any(a => code.Contains(a, StringComparison.Ordinal))) continue;
                offenders.Add($"{name}:{line}: {code.Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Emitted pattern matching goes through PsBuild.PatternTest / PatternRemove / PatternSubstitute / "
            + "PatternCaseModify ([PsBash.Cmdlets.BashPatternMatch]), never a PowerShell regex or wildcard. Offenders:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheAllowList_OnlyNamesMembersThatStillMatch()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles("PsBash.Transpiler", "PsBash.Cmdlets", "PsBash.Host", "PsBash.Core"))
            foreach (var (key, _) in SourceHits(file))
                keys.Add(key);
        Assert.All(AllowedSource.Keys, k => Assert.True(keys.Contains(k), $"{k} is allow-listed but no longer matches: remove it from the list."));

        var emitted = string.Join("\n", SourceFiles("PsBash.Transpiler")
            .Where(f => Path.GetFileName(f) is var n && (n.StartsWith("PsEmitter", StringComparison.Ordinal) || n == "PsBuild.cs"))
            .Select(f => File.ReadAllText(f, System.Text.Encoding.Latin1)));
        Assert.All(AllowedEmittedLines, a => Assert.True(emitted.Contains(a, StringComparison.Ordinal),
            $"'{a}' is allow-listed but no longer emitted: remove it from the list."));
    }

    // The scanner itself: each idiom is caught, comments are not code, a `*` line is code outside a block comment, and
    // the allow-list key is the member the line sits in.
    [Theory]
    [InlineData("        var rx = glob.Replace(\"\\\\*\", \".*\");")]
    [InlineData("        var rx = glob.Replace(\"?\", \".\");")]
    [InlineData("        string rx = \"^\" + \".*\" + tail;")]
    [InlineData("        string rx = head + \".*\";")]
    [InlineData("        return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, n);")]
    [InlineData("        sb.Append(\".*?\");")]
    public void ForbiddenSource_CatchesEachTranslatorIdiom(string line) =>
        Assert.Matches(ForbiddenSource, line);

    [Fact]
    public void CodeLines_SkipCommentsButNotAStarLineOutsideABlockComment()
    {
        string[] src =
        [
            "class C",
            "{",
            "    // sb.Append(\".*\");",
            "    /* sb.Append(\".*\");",
            "     * sb.Append(\".*\");",
            "     */",
            "    private static string Rx(string g)",
            "        => \"^\"",
            "         + \".*\";",
            "}",
        ];
        var hits = CodeLines(src).Where(c => ForbiddenSource.IsMatch(c.Code)).ToList();
        var hit = Assert.Single(hits);
        Assert.Equal(9, hit.Line);
        Assert.Equal("Rx", hit.Member);
    }
}
