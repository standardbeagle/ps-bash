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
    [InlineData("IndexOfAny(")]
    public void TheFieldsFileReadsNoRawQuoteText(string scan)
        => Assert.DoesNotContain(scan, File.ReadAllText(Path.Combine(ParserDir(), "PsEmitter.Fields.cs")), StringComparison.Ordinal);

    // Every split word reaches the run time through PsBuild.WordFieldsArray (the static BashWordFields.Words call, then
    // the ConvertTo-BashWords cmdlet when a field globs): no other file spells either call. A hand-written
    // `@(ConvertTo-BashWords $s)` (a run-time command word) once skipped the IFS of an `IFS=: cmd` binding.
    [Theory]
    [InlineData("-Parts @(")]
    [InlineData("ConvertTo-BashWords")]
    [InlineData("BashWordFields]::")]
    public void OnlyPsBuildSpellsTheWordFieldsCalls(string spelling)
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ParserDir(), "*.cs"))
        {
            if (Path.GetFileName(file) == "PsBuild.cs") continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains(spelling, StringComparison.Ordinal) && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0, "Emit the word fields via PsBuild (WordFieldsArray, WordSplitArray, ListFieldsArray, NumberFieldsArray):\n"
            + string.Join('\n', offenders));
    }

    /// <summary>
    /// Every <c>Is…Word(</c> predicate of the transpiler, pinned. A new word-shape predicate is how the bug class came back
    /// (a review found <c>IsPlainDefaultWord</c> taking a nested <c>${w:-'a b'}</c> as plain text and splitting the
    /// quoted space): one that decides fields belongs in PsEmitter.Fields.cs beside <c>IsUnquotedSplitWord</c>, and any
    /// other is added here with the reason it does not decide splitting.
    /// </summary>
    public static readonly string[] KnownWordPredicates =
    {
        // Fields: the one predicate, the quoted-list operand form, the lone-default shortcut.
        "PsEmitter.Fields.cs:IsUnquotedSplitWord", "PsEmitter.Fields.cs:IsQuotedListWord", "PsEmitter.Fields.cs:IsPlainDefaultWord",
        // Lexing and parsing, not expansion.
        "BashEreClasses.cs:IsWord", "BashLexer.cs:IsReservedWord", "BashParser.Simple.cs:IsInertWord",
        // shopt/set scans of a static word.
        "PsEmitter.ShellOptions.cs:IsQuotedWholeWord", "PsEmitter.ShellOptions.cs:IsLoopWord",
        // Command words and binding forms (how a word is passed, never how many fields it has).
        "PsEmitter.cs:IsPositionalAllWord", "PsEmitter.cs:IsArrayAllWord", "PsEmitter.cs:IsPlainVarRefWord", "PsEmitter.cs:IsEmptyQuotedWord",
        "PsEmitter.cs:IsPureUnquotedVarWord", "PsEmitter.cs:IsUnquotedPositionalListWord", "PsEmitter.cs:IsArrayValuedWord",
        "PsEmitter.cs:IsQuotedArrayAllWord", "PsEmitter.cs:IsPlainBraceWord", "PsEmitter.cs:IsUnquotedArrayAllWord",
        "PsEmitter.cs:IsGlobWord", "PsEmitter.cs:IsBraceGlobWord", "PsEmitter.cs:IsQuotedCommandWord", "PsEmitter.cs:IsVariableCommandWord",
        "PsEmitter.cs:IsBareVariableCommandWord", "PsEmitter.cs:IsExpandedCommandWord", "PsEmitter.cs:IsPureProcessSubWord",
    };

    [Fact]
    public void NoNewWordShapePredicate()
    {
        var found = new List<string>();
        var definition = new System.Text.RegularExpressions.Regex(@"\bbool\s+(Is\w*Word)\s*\(");
        foreach (var file in Directory.EnumerateFiles(ParserDir(), "*.cs"))
            foreach (var line in File.ReadAllLines(file))
                foreach (System.Text.RegularExpressions.Match m in definition.Matches(line))
                    found.Add(Path.GetFileName(file) + ":" + m.Groups[1].Value);
        var added = new List<string>(found);
        added.RemoveAll(n => Array.IndexOf(KnownWordPredicates, n) >= 0);
        Assert.True(added.Count == 0,
            "A new Is…Word predicate: decide fields in PsEmitter.Fields.cs (IsUnquotedSplitWord + the segments), or pin it here with why it does not:\n"
            + string.Join('\n', added));
    }
}
