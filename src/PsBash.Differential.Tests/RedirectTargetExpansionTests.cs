using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A file redirect target is expanded like a word: one match is the file; several matches, or a variable that splits, is "ambiguous redirect" (status 1).</summary>
public class RedirectTargetExpansionTests
{
    [Fact]
    public Task SingleGlobMatch_IsTheFile() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\"; touch one-bar; echo hi > one-*; cat one-bar");

    [Fact]
    public Task GlobWithSeveralMatches_IsAmbiguous() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\"; touch foo-bar foo-spam; { echo hi > foo-*; } 2>/dev/null; echo status=$?; head foo-bar foo-spam");

    [Fact]
    public Task ExtendedGlob_PicksTheFile() => AssertOracle.EqualAsync(
        "shopt -s extglob\ncd \"$(mktemp -d)\"; touch foo-bar; echo hi > @(*-bar|other); echo status=$?; cat foo-bar");

    [Fact]
    public Task VariableWithSpaces_IsAmbiguous() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\"; g='a b'; { echo w > $g; } 2>/dev/null; echo status=$?; ls | wc -l; f=out.txt; echo v > $f; cat out.txt");
}
