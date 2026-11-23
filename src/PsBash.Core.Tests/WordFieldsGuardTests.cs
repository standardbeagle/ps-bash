using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the word-expansion invariant (emitter-strategy.md "Word expansion: one primitive"): whether an unquoted word is
/// field-split and globbed is decided by ONE predicate, <c>PsEmitter.IsUnquotedSplitWord</c>, and its fields come from ONE
/// emission, <c>EmitUnquotedSplitWordArray</c>, which hands the word's segments to <c>PsBash.Cmdlets.BashWordFields</c>.
/// <para>
/// The bug class: a dozen word-shape predicates (<c>IsUnquotedVarAffixWord</c>, <c>IsUnquotedDefaultAltWord</c>,
/// <c>IsQuotedArrayAffixWord</c>, ...) each split one shape, so every other shape kept one field: with x='a b', <c>$x$x</c>,
/// <c>~/$x</c>, <c>$x*</c>, <c>"$@"$x</c> and <c>{1,2}$x</c> did not split. A new shape predicate, or a split decided from a
/// suffix's raw text, fails here. Behavior against bash: <c>WordFieldsDifferentialTests</c>.
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check.</para>
/// </summary>
public class WordFieldsGuardTests
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

    private static string ParserDir() => Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser");

    /// <summary>The word-shape split predicates and emitters the one primitive replaced.</summary>
    public static readonly string[] RetiredShapes =
    {
        "IsUnquotedVarAffixWord", "IsUnquotedDefaultAltWord", "IsUnquotedElementWord", "IsUnquotedPositionalOpWord",
        "IsUnquotedPositionalAffixWord", "IsUnquotedQuotedAltWord", "IsUnquotedPositionalSliceWord",
        "IsPureUnquotedCommandSubWord", "IsQuotedArrayAffixWord", "UnquotedAffixExpansionIndex", "EmitAffixWordArray",
        "EmitQuotedAltWordArray", "TryQuotedArrayAffix", "EmitQuotedArrayAffixArray", "IsPositionalListDefaultAlt",
        "BashQuotedWords",
    };

    [Fact]
    public void NoRetiredWordShapePredicateIsBack()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ParserDir(), "*.cs"))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                foreach (var name in RetiredShapes)
                    if (lines[i].Contains(name, StringComparison.Ordinal))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {name}");
        }
        Assert.True(offenders.Count == 0,
            "Split a word through IsUnquotedSplitWord + EmitUnquotedSplitWordArray (the segments), not a shape of its own:\n"
            + string.Join('\n', offenders));
    }

    [Theory]
    [InlineData("private static bool IsUnquotedSplitWord(")]
    [InlineData("private static string EmitUnquotedSplitWordArray(")]
    [InlineData("private static void AppendWordSegments(")]
    public void TheOnePredicateAndEmissionLiveInTheFieldsFile(string definition)
    {
        var defined = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ParserDir(), "*.cs"))
            if (File.ReadAllText(file).Contains(definition, StringComparison.Ordinal))
                defined.Add(Path.GetFileName(file));
        Assert.Equal(new[] { "PsEmitter.Fields.cs" }, defined);
    }

    // The segments come from the AST parts: the fields file never scans a word's or a suffix's raw text for quotes.
    [Theory]
    [InlineData("IndexOf('\"')")]
    [InlineData("IndexOf('\\'')")]
    [InlineData("Contains('\"')")]
    [InlineData("Contains(\"\\\"\")")]
    [InlineData(".RawText")]
    public void TheFieldsFileReadsNoRawQuoteText(string scan)
        => Assert.DoesNotContain(scan, File.ReadAllText(Path.Combine(ParserDir(), "PsEmitter.Fields.cs")), StringComparison.Ordinal);

    // Every split word reaches the run time as one ConvertTo-BashWords -Parts call, built by PsBuild.
    [Fact]
    public void OnlyPsBuildSpellsTheSegmentsCall()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ParserDir(), "*.cs"))
        {
            if (Path.GetFileName(file) == "PsBuild.cs") continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains("-Parts @(", StringComparison.Ordinal) && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)
                    && !lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0, "Emit the segments call via PsBuild.WordFieldsArray:\n" + string.Join('\n', offenders));
    }
}
