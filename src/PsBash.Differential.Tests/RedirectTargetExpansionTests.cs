using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>A file redirect target is expanded like a word: one match is the file; several matches, or a variable that splits, is "ambiguous redirect" (status 1).</summary>
public class RedirectTargetExpansionTests
{
    [SkippableFact]
    public Task SingleGlobMatch_IsTheFile() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\"; touch one-bar; echo hi > one-*; cat one-bar");

    [SkippableFact]
    public Task GlobWithSeveralMatches_IsAmbiguous() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\"; touch foo-bar foo-spam; { echo hi > foo-*; } 2>/dev/null; echo status=$?; head foo-bar foo-spam");

    [SkippableFact]
    public Task ExtendedGlob_PicksTheFile() => AssertOracle.EqualAsync(
        "shopt -s extglob\ncd \"$(mktemp -d)\"; touch foo-bar; echo hi > @(*-bar|other); echo status=$?; cat foo-bar");

    [SkippableFact]
    public Task VariableWithSpaces_IsAmbiguous() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\"; g='a b'; { echo w > $g; } 2>/dev/null; echo status=$?; ls | wc -l; f=out.txt; echo v > $f; cat out.txt");

    // `"$@"` is one word per parameter: two of them is ambiguous for `>&` and `>` alike (ps-bash joined them into a file
    // named `2 3 c d`); exactly one is the file.
    [SkippableFact]
    public Task QuotedAtWithSeveralParameters_IsAmbiguous() => AssertOracle.EqualAsync(
        "cd \"$(mktemp -d)\"; set -- '2 3' 'c d'; { echo hi 1>& \"$@\"; } 2>/dev/null; echo status=$?; { echo hi > \"$@\"; } 2>/dev/null; echo status=$?; ls | wc -l\n"
        + "set -- 'o n e'; echo one >& \"$@\"; echo status=$?; cat 'o n e'; a=(x); echo two > \"${a[@]}\"; cat x");
}
