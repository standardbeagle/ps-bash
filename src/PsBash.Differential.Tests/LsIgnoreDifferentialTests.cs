using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>ls -I PATTERN</c> / <c>--ignore</c> / <c>--hide</c> / <c>-B</c> against GNU coreutils 9.4: fnmatch with FNM_PERIOD
/// (a leading period only matches a literal period), <c>-I</c> beating <c>-a</c>, <c>--hide</c> yielding to <c>-a</c> /
/// <c>-A</c>, operands never filtered, recursion skipping ignored directories. Lowercase names (C-locale sort order).
/// </summary>
public class LsIgnoreDifferentialTests
{
    private const string Setup =
        "for f in alpha.c beta.h gamma delta.tar.gz epsilon.txt zeta.c theta .hid 'bk~' x.o noext 'a b' z.a m.zz.b .cfg '.old~'; do printf '' > \"$f\"; done; mkdir dd; printf '' > dd/g.c";

    [SkippableFact] public Task Ignore_Suffix() => EqualAsync(Setup, "ls -I '*.c' | cat -A");
    [SkippableFact] public Task Ignore_Repeated() => EqualAsync(Setup, "ls -I '*.c' -I '*.h' | cat -A");
    [SkippableFact] public Task Ignore_LongForm() => EqualAsync(Setup, "ls --ignore='*.c' | cat -A; ls --ignore '*.h' | cat -A");
    [SkippableFact] public Task Ignore_Abbreviation_IsAmbiguousWithIgnoreBackups() => EqualAsync(Setup, "ls --ign='*.c'; echo rc=$?");
    [SkippableFact] public Task Ignore_AppliesWithDashA() => EqualAsync(Setup, "ls -a -I '*.c' | cat -A");
    [SkippableFact] public Task Ignore_DotPatternRemovesHiddenAndDots() => EqualAsync(Setup, "ls -a -I '.*' | cat -A; ls -A -I '.*' | cat -A");
    [SkippableFact] public Task Ignore_ExactDot_RemovesOnlyDot() => EqualAsync(Setup, "ls -a -I '.' | cat -A");
    [SkippableFact] public Task Ignore_PatternForms() =>
        EqualAsync(Setup, "ls -I 'a*' | cat -A; ls -I '[a-c]*' | cat -A; ls -I '?eta*' | cat -A; ls -I 'noext' | cat -A; ls -I 'a b' | cat -A; ls -I '[!a-m]*' | cat -A");
    [SkippableFact] public Task Ignore_Everything() => EqualAsync(Setup, "ls -I '*'; echo rc=$?");
    [SkippableFact] public Task Ignore_NotAppliedToOperands() => EqualAsync(Setup, "ls -I '*.c' alpha.c beta.h | cat -A");
    [SkippableFact] public Task Ignore_NotAppliedToDirOperandItself() => EqualAsync(Setup, "ls -I 'dd' dd | cat -A");
    [SkippableFact] public Task Ignore_WithDashD() => EqualAsync(Setup, "ls -d -I '*.c' alpha.c beta.h | cat -A");
    [SkippableFact] public Task Ignore_Recursive_SkipsIgnoredDirectories() =>
        EqualAsync(Setup + "; mkdir -p dd/x; printf '' > dd/x/f.c; printf '' > dd/x/f.h", "ls -R -I 'x' dd | cat -A; ls -R -I '*.c' dd | cat -A");
    [SkippableFact] public Task Ignore_WithLong() => EqualAsync(Setup, "ls -l -I '*.c' | wc -l");
    [SkippableFact] public Task Ignore_WithColumns() => EqualAsync(Setup, "ls -C -w 40 -I '*.c' | cat -A");

    [SkippableFact] public Task Hide_Suffix() => EqualAsync(Setup, "ls --hide='*.c' | cat -A");
    [SkippableFact] public Task Hide_OverriddenByDashA() => EqualAsync(Setup, "ls -a --hide='*.c' | cat -A");
    [SkippableFact] public Task Hide_OverriddenByDashAUpper() => EqualAsync(Setup, "ls -A --hide='*.c' | cat -A");
    [SkippableFact] public Task Hide_AndIgnoreTogether() => EqualAsync(Setup, "ls --hide='*.c' -I '*.h' | cat -A");
    [SkippableFact] public Task Hide_Abbreviation_IsAmbiguousWithHideControlChars() => EqualAsync(Setup, "ls --hi='*.c'; echo rc=$?");

    [SkippableFact] public Task IgnoreBackups() => EqualAsync(Setup, "ls -B | cat -A; ls -aB | cat -A; ls --ignore-backups | cat -A");

    [SkippableFact] public Task Error_IgnoreWithoutArgument() => EqualAsync(Setup, "ls -I; echo rc=$?");
    [SkippableFact] public Task Error_HideWithoutArgument() => EqualAsync(Setup, "ls --hide; echo rc=$?");
}
