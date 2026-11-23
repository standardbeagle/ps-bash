using System;
using System.Collections.Generic;
using System.IO;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// <c>[[ s =~ re ]]</c> runs ONLY in the POSIX ERE primitive (<see cref="BashEre"/>, docs/specs/regex-ere.md).
/// It used to be PowerShell's <c>-cmatch</c>, i.e. .NET regex: leftmost-first alternation (<c>(a|ab)</c> on ab gave
/// a), lazy <c>*?</c>, Perl groups <c>(?:)</c>/<c>(?=)</c> accepted, <c>\d</c>/<c>\w</c>/<c>\s</c> as Perl classes,
/// <c>$</c> before a final newline, <c>.</c> not matching a newline. Any route back to .NET regex brings that class
/// back, so this fails when the <c>=~</c> emit, its runtime entry points or the engine reach System.Text.RegularExpressions.
/// Oracle note (qa-rubric Directive 1): a source invariant; the behaviour is oracle-checked in BashEreTests and
/// RegexEreDifferentialTests.
/// </summary>
public class EreNoDotNetRegexGuardTests
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

    // PowerShell's regex operators and type accelerator; the .NET type is matched as a whole word (WordToRegex is not it).
    private static readonly string[] PsRegexTokens = ["System.Text.RegularExpressions", "-match", "-cmatch", "-imatch", "-notmatch", "[regex]"];

    private static bool NamesRegexType(string line)
    {
        for (int at = line.IndexOf("Regex", StringComparison.Ordinal); at >= 0; at = line.IndexOf("Regex", at + 1, StringComparison.Ordinal))
        {
            bool wordStart = at == 0 || !(char.IsLetterOrDigit(line[at - 1]) || line[at - 1] == '_');
            if (wordStart) return true; // Regex, Regex.IsMatch, RegexOptions, new Regex(…)
        }
        return false;
    }

    /// <summary>Lines of <paramref name="code"/> (comments dropped) that name .NET regex.</summary>
    internal static List<string> Offenders(string where, string code)
    {
        var offenders = new List<string>();
        var lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)) continue;
            var cut = t.IndexOf(" //", StringComparison.Ordinal);
            if (cut >= 0) t = t[..cut];
            bool bad = NamesRegexType(t);
            foreach (var token in PsRegexTokens)
                bad |= t.Contains(token, StringComparison.OrdinalIgnoreCase);
            if (bad) offenders.Add($"{where}:{i + 1}: {t}");
        }
        return offenders;
    }

    /// <summary>
    /// The member that starts at <paramref name="signature"/>: an expression body up to its <c>;</c>, a block body up to
    /// its closing brace at 4 spaces, or up to <paramref name="endMarker"/> when one is given.
    /// </summary>
    private static string Member(string source, string signature, string? endMarker = null)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found: update the guard with the member's new name.");
        int end;
        if (endMarker is not null)
            end = source.IndexOf(endMarker, start, StringComparison.Ordinal) + endMarker.Length;
        else
        {
            int arrow = source.IndexOf("=>", start, StringComparison.Ordinal), brace = source.IndexOf('{', start);
            end = arrow >= 0 && (brace < 0 || arrow < brace)
                ? source.IndexOf(";\n", arrow, StringComparison.Ordinal)
                : source.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        }
        Assert.True(end > start, $"end of '{signature}' not found");
        return source[start..end];
    }

    [Fact]
    public void EreEngineSources_DoNotUseDotNetRegex()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser");
        var files = Directory.GetFiles(dir, "BashEre*.cs");
        Assert.True(files.Length >= 4, "BashEre engine files not found");
        var offenders = new List<string>();
        foreach (var f in files)
            offenders.AddRange(Offenders(Path.GetFileName(f), File.ReadAllText(f)));
        Assert.True(offenders.Count == 0, "the ERE engine reaches .NET regex:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EreEmitAndRuntimeEntryPoints_DoNotUseDotNetRegex()
    {
        var root = RepoRoot();
        var emitter = File.ReadAllText(Path.Combine(root, "src", "PsBash.Transpiler", "Parser", "PsEmitter.cs"));
        var build = File.ReadAllText(Path.Combine(root, "src", "PsBash.Transpiler", "Parser", "PsBuild.cs"));
        var match = File.ReadAllText(Path.Combine(root, "src", "PsBash.Cmdlets", "BashPatternMatch.cs"));

        var offenders = new List<string>();
        offenders.AddRange(Offenders("PsEmitter =~ branch", Member(emitter, "if (op == \"=~\")", "return PsBuild.EreTest(")));
        offenders.AddRange(Offenders("PsEmitter.BuildEreWord", Member(emitter, "private static string BuildEreWord(")));
        foreach (var sig in new[] { "public static string EreTest(", "public static string EreWord(", "public static string EreLiteral(",
                     "public static string EreQuoted(", "public static string EreUnquoted(" })
            offenders.AddRange(Offenders("PsBuild." + sig, Member(build, sig)));
        foreach (var sig in new[] { "public static PsBash.Core.Parser.BashEreResult EreTest(", "public static string EreQuote(", "public static string EreRaw(" })
            offenders.AddRange(Offenders("BashPatternMatch." + sig, Member(match, sig)));
        Assert.True(offenders.Count == 0, "=~ reaches .NET regex:\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("[[ $a =~ ^(a|ab)$ ]]")]
    [InlineData("[[ $a =~ $re ]]")]
    [InlineData("[[ $a =~ \"$q\"x$re ]]")]
    [InlineData("[[ $a =~ ^a\\.b$ ]] && echo y")]
    [InlineData("if [[ ! $a =~ [[:alpha:]] ]]; then :; fi")]
    [InlineData("shopt -s nocasematch; [[ AB =~ ab ]]")]
    public void Transpile_RegexMatch_EmitsNoDotNetRegex(string bash)
    {
        var ps = PsEmitter.Transpile(bash)!;
        Assert.Contains("[PsBash.Cmdlets.BashPatternMatch]::EreTest(", ps);
        Assert.Empty(Offenders("emitted", ps));
    }

    [Fact]
    public void Guard_CatchesADotNetRegexRoute()
    {
        // The guard's own detector must see the shapes the old emit used.
        Assert.NotEmpty(Offenders("x", "$( $global:__psbash_m = $env:a -cmatch '^a$' )"));
        Assert.NotEmpty(Offenders("x", "return System.Text.RegularExpressions.Regex.IsMatch(s, p);"));
        Assert.NotEmpty(Offenders("x", "[regex]::Escape($v)"));
        Assert.Empty(Offenders("x", "// .NET Regex is not used here"));
    }
}
