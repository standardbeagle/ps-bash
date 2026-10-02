using PsBash.Differential.Tests.Oracle;
using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// GNU coreutils 9.4 <c>ln</c> <c>-b -S -i</c> and the <c>-r</c> error, compared on stdout, exit status and the
/// RESULTING FILESYSTEM (<see cref="FsStateOracle"/>). HARD links only: creating a symbolic link on Windows needs
/// a privilege the CI runners may lack, so the relative-symlink cases live in the cmdlet tests (and skip there
/// when the OS refuses). Diagnostics are not compared (GNU prints curly quotes in this locale).
/// </summary>
public class LnOptionsDifferentialTests
{
    private static string Two() => Tree(("a", "A"), ("b", "B"), ("c", "C"), ("d", null));

    [SkippableFact] public Task Ln_Backup_Simple() => EqualAsync(Two(), "ln -bv a b");
    [SkippableFact] public Task Ln_Backup_NoExistingName_MakesNone() => EqualAsync(Tree(("a", "A")), "ln -bv a n");
    [SkippableFact] public Task Ln_Backup_OverwritesAnOldSimpleBackup() => EqualAsync(Tree(("a", "A"), ("b", "B"), ("b~", "OLD")), "ln -bv a b");
    [SkippableFact] public Task Ln_Backup_Numbered() => EqualAsync(Two(), "ln -v --backup=numbered a b; ln -vf --backup=t c b");
    [SkippableFact] public Task Ln_Backup_ExistingPicksNumberedOnlyWhenOneExists() =>
        EqualAsync(Two(), "ln -v --backup=existing a b; ln -v --backup=numbered c b; ln -v --backup=existing a b");
    [SkippableFact] public Task Ln_Backup_NoneAndOff() => EqualAsync(Two(), "ln -v --backup=none -f a b; ln -vf --backup=off c b");
    [SkippableFact] public Task Ln_Backup_BareLongOption() => EqualAsync(Two(), "ln -v --backup a b");
    [SkippableFact] public Task Ln_Backup_SuffixOption() => EqualAsync(Two(), "ln -bvS .bak a b");
    [SkippableFact] public Task Ln_Suffix_AloneTurnsBackupsOn() => EqualAsync(Two(), "ln -vS .zz a b");
    [SkippableFact] public Task Ln_Backup_VersionControlEnvironment() => EqualAsync(Two(), "VERSION_CONTROL=numbered ln -bv a b");
    [SkippableFact] public Task Ln_Backup_BadControlWord_IsAnError() => EqualAsync(Two(), "ln --backup=bogus a b");
    [SkippableFact] public Task Ln_Backup_IntoADirectory() => EqualAsync(Tree(("a", "A"), ("d/a", "OLD")), "ln -bv a d");
    [SkippableFact] public Task Ln_Backup_WithForce() => EqualAsync(Two(), "ln -bfv a b");

    [SkippableFact] public Task Ln_Interactive_Yes() => EqualAsync(Two(), "echo y | ln -iv a b");
    [SkippableFact] public Task Ln_Interactive_No() => EqualAsync(Two(), "echo n | ln -iv a b");
    [SkippableFact] public Task Ln_Interactive_EmptyStdin() => EqualAsync(Two(), "ln -iv a b </dev/null");
    [SkippableFact] public Task Ln_Interactive_NewName_NoPrompt() => EqualAsync(Two(), "ln -iv a n </dev/null");
    [SkippableFact] public Task Ln_Interactive_LastOfIFWins() => EqualAsync(Two(), "echo n | ln -if a b; echo n | ln -fi c b");
    [SkippableFact] public Task Ln_Interactive_WithBackup() => EqualAsync(Two(), "echo y | ln -ibv a b");
    [SkippableFact] public Task Ln_Interactive_IntoADirectory() => EqualAsync(Tree(("a", "A"), ("d/a", "OLD")), "echo y | ln -iv a d");

    [SkippableFact] public Task Ln_Relative_WithoutSymbolic_IsAnError() => EqualAsync(Two(), "ln -r a n");
    [SkippableFact] public Task Ln_Relative_LongWithoutSymbolic_IsAnError() => EqualAsync(Two(), "ln --relative a n; ln -v --rel a n");
}
