using PsBash.Differential.Tests.Oracle;
using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// GNU coreutils 9.4 <c>mv</c> options (<c>-i -t -T -b -S -u --update</c>) compared on stdout, exit status
/// and the RESULTING FILESYSTEM (<see cref="FsStateOracle"/>). Diagnostics are not compared (GNU prints
/// curly quotes in this locale). Single-child trees only (readdir order is not portable).
/// </summary>
public class MvOptionsDifferentialTests
{
    private static string Two() => Tree(("a", "A"), ("b", "B"), ("d", null));

    [SkippableFact] public Task Mv_Verbose() => EqualAsync(Two(), "mv -v a n");
    [SkippableFact] public Task Mv_Verbose_IntoDirectory() => EqualAsync(Two(), "mv -v a d");

    [SkippableFact] public Task Mv_Interactive_Yes() => EqualAsync(Two(), "echo y | mv -vi a b");
    [SkippableFact] public Task Mv_Interactive_No() => EqualAsync(Two(), "echo n | mv -vi a b");
    [SkippableFact] public Task Mv_Interactive_EmptyStdin() => EqualAsync(Two(), "mv -vi a b </dev/null");
    [SkippableFact] public Task Mv_Interactive_NewDestination_NoPrompt() => EqualAsync(Two(), "mv -vi a n </dev/null");
    [SkippableFact] public Task Mv_Interactive_LastOfINWins() => EqualAsync(Two(), "mv -i -n a b </dev/null; mv -n -i a b </dev/null; mv -i -f a b </dev/null");

    [SkippableFact] public Task Mv_TargetDirectory() => EqualAsync(Two(), "mv -vt d a b");
    [SkippableFact] public Task Mv_TargetDirectory_Long() => EqualAsync(Two(), "mv -v --target-directory=d a");
    [SkippableFact] public Task Mv_TargetDirectory_Missing() => EqualAsync(Two(), "mv -t nodir a");
    [SkippableFact] public Task Mv_TargetDirectory_NotADirectory() => EqualAsync(Two(), "mv -t b a");
    [SkippableFact] public Task Mv_NoTargetDirectory_PlainRename() => EqualAsync(Two(), "mv -vT a z");
    [SkippableFact] public Task Mv_NoTargetDirectory_OntoDirectory() => EqualAsync(Two(), "mv -T a d");
    [SkippableFact] public Task Mv_NoTargetDirectory_ExtraOperand() => EqualAsync(Two(), "mv -T a b d");
    [SkippableFact] public Task Mv_TargetAndNoTarget_Conflict() => EqualAsync(Two(), "mv -t d -T a");

    [SkippableFact] public Task Mv_Backup_Simple() => EqualAsync(Two(), "mv -bv a b");
    [SkippableFact] public Task Mv_Backup_OverwritesAnOldSimpleBackup() => EqualAsync(Tree(("a", "A"), ("b", "B"), ("b~", "OLD")), "mv -bv a b");
    [SkippableFact] public Task Mv_Backup_NoExistingDestination_MakesNone() => EqualAsync(Tree(("a", "A")), "mv -bv a n");
    [SkippableFact] public Task Mv_Backup_Numbered() => EqualAsync(Tree(("a", "A"), ("b", "B")), "mv -v --backup=numbered a b; echo C > a; mv -v --backup=t a b");
    [SkippableFact] public Task Mv_Backup_ExistingPicksNumberedOnlyWhenOneExists() =>
        EqualAsync(Tree(("a", "A"), ("b", "B")), "mv -v --backup=existing a b; echo C > a; mv -v --backup=numbered a b; echo D > a; mv -v --backup=existing a b");
    [SkippableFact] public Task Mv_Backup_NoneAndOff() => EqualAsync(Tree(("a", "A"), ("b", "B")), "mv -v --backup=none a b; echo C > a; mv -v --backup=off a b");
    [SkippableFact] public Task Mv_Backup_BareLongOption() => EqualAsync(Two(), "mv -v --backup a b");
    [SkippableFact] public Task Mv_Backup_SuffixOption() => EqualAsync(Two(), "mv -bvS .bak a b");
    [SkippableFact] public Task Mv_Backup_LongSuffixOption() => EqualAsync(Two(), "mv -v --backup=simple --suffix=_x a b");
    [SkippableFact] public Task Mv_Suffix_AloneTurnsBackupsOn() => EqualAsync(Two(), "mv -vS .zz a b");
    [SkippableFact] public Task Mv_Backup_VersionControlEnvironment() => EqualAsync(Two(), "VERSION_CONTROL=numbered mv -bv a b");
    [SkippableFact] public Task Mv_Backup_SimpleBackupSuffixEnvironment() => EqualAsync(Two(), "SIMPLE_BACKUP_SUFFIX=.s mv -bv a b");
    [SkippableFact] public Task Mv_Backup_BadControlWord_IsAnError() => EqualAsync(Two(), "mv --backup=bogus a b");
    [SkippableFact] public Task Mv_Backup_IntoADirectory() => EqualAsync(Tree(("a", "A"), ("d/a", "OLD")), "mv -bv a d");

    [SkippableFact] public Task Mv_Update_NewerSourceReplaces() => EqualAsync(Two(), "touch -d '2020-01-01' b; mv -uv a b");
    [SkippableFact] public Task Mv_Update_OlderSourceKept() => EqualAsync(Two(), "touch -d '2020-01-01' a; mv -uv a b");
    [SkippableFact] public Task Mv_Update_EqualTimesKept() => EqualAsync(Two(), "touch -d '2020-01-01' a b; mv -uv a b");
    [SkippableFact] public Task Mv_Update_None() => EqualAsync(Two(), "mv -v --update=none a b");
    [SkippableFact] public Task Mv_Update_All() => EqualAsync(Two(), "touch -d '2020-01-01' a; mv -v --update=all a b");
    [SkippableFact] public Task Mv_Update_Older() => EqualAsync(Two(), "touch -d '2020-01-01' b; mv -v --update=older a b");
    [SkippableFact] public Task Mv_Update_BadWord() => EqualAsync(Two(), "mv --update=sometimes a b");
}
