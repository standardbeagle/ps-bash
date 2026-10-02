using PsBash.Differential.Tests.Oracle;
using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// GNU coreutils 9.4 <c>cp</c> options beyond the basics, compared on stdout, exit status and the
/// RESULTING FILESYSTEM (<see cref="FsStateOracle"/>). Diagnostics are not compared (GNU prints curly
/// quotes in this locale); symbolic-link cases live in the cmdlet tests because creating a symlink on
/// Windows needs a privilege the CI runners may lack. Single-child trees only: GNU lists a directory in
/// readdir order, which no portable fixture can pin.
/// </summary>
public class CpOptionsDifferentialTests
{
    private static string Two() => Tree(("a", "A"), ("b", "B"), ("d", null));

    // ───────────── backups ─────────────

    [SkippableFact] public Task Cp_Backup_Simple() => EqualAsync(Two(), "cp -bv a b");
    [SkippableFact] public Task Cp_Backup_OverwritesAnOldSimpleBackup() => EqualAsync(Tree(("a", "A"), ("b", "B"), ("b~", "OLD")), "cp -bv a b");
    [SkippableFact] public Task Cp_Backup_NoExistingDestination_MakesNone() => EqualAsync(Tree(("a", "A")), "cp -bv a n");
    [SkippableFact] public Task Cp_Backup_Numbered() => EqualAsync(Two(), "cp -v --backup=numbered a b; cp -v --backup=t a b");
    [SkippableFact] public Task Cp_Backup_ExistingPicksNumberedOnlyWhenOneExists() =>
        EqualAsync(Tree(("a", "A"), ("b", "B")), "cp -v --backup=existing a b; cp -v --backup=nil a b; cp -v --backup=numbered a b; cp -v --backup=existing a b");
    [SkippableFact] public Task Cp_Backup_NoneAndOff() => EqualAsync(Two(), "cp -v --backup=none a b; cp -v --backup=off a b");
    [SkippableFact] public Task Cp_Backup_SimpleAndNever() => EqualAsync(Two(), "cp -v --backup=simple a b; cp -v --backup=never a b");
    [SkippableFact] public Task Cp_Backup_BareLongOption() => EqualAsync(Two(), "cp -v --backup a b");
    [SkippableFact] public Task Cp_Backup_ControlWordPrefixes() => EqualAsync(Two(), "cp -v --backup=nu a b; cp -v --backup=sim a b");
    [SkippableFact] public Task Cp_Backup_SuffixOption() => EqualAsync(Two(), "cp -bvS .bak a b");
    [SkippableFact] public Task Cp_Backup_LongSuffixOption() => EqualAsync(Two(), "cp -v --backup=simple --suffix=_x a b");
    [SkippableFact] public Task Cp_Suffix_AloneTurnsBackupsOn() => EqualAsync(Two(), "cp -vS .zz a b");
    [SkippableFact] public Task Cp_Backup_VersionControlEnvironment() => EqualAsync(Two(), "VERSION_CONTROL=numbered cp -bv a b; VERSION_CONTROL=simple cp -bv a b");
    [SkippableFact] public Task Cp_Backup_VersionControlNone() => EqualAsync(Two(), "VERSION_CONTROL=none cp -bv a b");
    [SkippableFact] public Task Cp_Backup_BadVersionControl_IsAnError() => EqualAsync(Two(), "VERSION_CONTROL=bogus cp -b a b");
    [SkippableFact] public Task Cp_Backup_SimpleBackupSuffixEnvironment() => EqualAsync(Two(), "SIMPLE_BACKUP_SUFFIX=.s cp -bv a b");
    [SkippableFact] public Task Cp_Backup_BadControlWord_IsAnError() => EqualAsync(Two(), "cp --backup=bogus a b");
    [SkippableFact] public Task Cp_Backup_AmbiguousControlWord_IsAnError() => EqualAsync(Two(), "cp --backup=n a b");
    [SkippableFact] public Task Cp_Backup_IntoADirectory() => EqualAsync(Tree(("a", "A"), ("d/a", "OLD")), "cp -bv a d");
    [SkippableFact] public Task Cp_Backup_SkippedByNoClobber() => EqualAsync(Two(), "cp -bn a b");
    [SkippableFact] public Task Cp_Backup_InARecursiveCopy() => EqualAsync(Tree(("s/f", "NEW"), ("t/s/f", "OLD")), "cp -rbv s t");

    // ───────────── -t / -T ─────────────

    [SkippableFact] public Task Cp_TargetDirectory() => EqualAsync(Two(), "cp -vt d a b");
    [SkippableFact] public Task Cp_TargetDirectory_LongForm() => EqualAsync(Two(), "cp -v --target-directory=d a b");
    [SkippableFact] public Task Cp_TargetDirectory_Missing() => EqualAsync(Two(), "cp -t nodir a");
    [SkippableFact] public Task Cp_TargetDirectory_NotADirectory() => EqualAsync(Two(), "cp -t b a");
    [SkippableFact] public Task Cp_TargetDirectory_NoSources() => EqualAsync(Two(), "cp -t d");
    [SkippableFact] public Task Cp_TargetDirectory_OptionAfterSources() => EqualAsync(Two(), "cp a b -t d");
    [SkippableFact] public Task Cp_NoTargetDirectory_FileOverFile() => EqualAsync(Two(), "cp -vT a b");
    [SkippableFact] public Task Cp_NoTargetDirectory_OntoADirectory_IsRefused() => EqualAsync(Two(), "cp -T a d");
    [SkippableFact] public Task Cp_NoTargetDirectory_ExtraOperand() => EqualAsync(Two(), "cp -T a b d");
    [SkippableFact] public Task Cp_NoTargetDirectory_MergesATree() => EqualAsync(Tree(("s/f", "F"), ("t/g", "G")), "cp -rvT s t");
    [SkippableFact] public Task Cp_NoTargetDirectory_NewDestinationCreatesIt() => EqualAsync(Tree(("s/f", "F")), "cp -rvT s t");
    [SkippableFact] public Task Cp_TargetAndNoTargetDirectory_Conflict() => EqualAsync(Two(), "cp -t d -T a");
    [SkippableFact] public Task Cp_MissingDestinationOperand() => EqualAsync(Two(), "cp a");
    [SkippableFact] public Task Cp_NoOperands() => EqualAsync(Two(), "cp");

    // ───────────── -u / --update / -n / -i ─────────────

    private static string Aged() => Tree(("a", "A"), ("b", "B")) + "\ntouch -d 2020-01-01 b\ntouch -d 2024-01-01 a";
    private static string Newer() => Tree(("a", "A"), ("b", "B")) + "\ntouch -d 2020-01-01 a\ntouch -d 2024-01-01 b";

    [SkippableFact] public Task Cp_Update_OlderDestination_IsReplaced() => EqualAsync(Aged(), "cp -uv a b");
    [SkippableFact] public Task Cp_Update_NewerDestination_IsKept() => EqualAsync(Newer(), "cp -uv a b");
    [SkippableFact] public Task Cp_UpdateAll_AlwaysCopies() => EqualAsync(Newer(), "cp -v --update=all a b");
    [SkippableFact] public Task Cp_UpdateNone_NeverReplaces() => EqualAsync(Aged(), "cp -v --update=none a b");
    [SkippableFact] public Task Cp_UpdateNone_CopiesWhenMissing() => EqualAsync(Aged(), "cp -v --update=none a n");
    [SkippableFact] public Task Cp_UpdateOlder_Named() => EqualAsync(Newer(), "cp -v --update=older a b; cp -v --update=ol a b");
    [SkippableFact] public Task Cp_UpdateBare_IsOlder() => EqualAsync(Newer(), "cp -v --update a b");
    [SkippableFact] public Task Cp_UpdateUnknownWord_IsAnError() => EqualAsync(Aged(), "cp --update=bogus a b");
    [SkippableFact] public Task Cp_UpdateNoneFail_IsNotAWordIn94() => EqualAsync(Aged(), "cp --update=none-fail a b");
    [SkippableFact] public Task Cp_UpdateEmptyWord_IsAmbiguous() => EqualAsync(Aged(), "cp --update= a b");
    [SkippableFact] public Task Cp_UpdateAll_OverridesAnEarlierNoClobber() => EqualAsync(Aged(), "cp -n --update=all a b");
    [SkippableFact] public Task Cp_UpdateNone_AfterUpdateAll() => EqualAsync(Aged(), "cp --update=all --update=none a b");
    [SkippableFact] public Task Cp_UpdateOlder_DoesNotLiftNoClobber() => EqualAsync(Aged(), "cp -n -u a b");
    [SkippableFact] public Task Cp_Interactive_Yes() => EqualAsync(Two(), "echo y | cp -iv a b");
    [SkippableFact] public Task Cp_Interactive_YesWord() => EqualAsync(Two(), "echo yes | cp -i a b");
    [SkippableFact] public Task Cp_Interactive_No_ExitsOne() => EqualAsync(Two(), "echo n | cp -i a b");
    [SkippableFact] public Task Cp_Interactive_EofIsNo() => EqualAsync(Two(), "cp -i a b < /dev/null");
    [SkippableFact] public Task Cp_Interactive_NewDestinationDoesNotAsk() => EqualAsync(Two(), "echo n | cp -i a n");
    [SkippableFact] public Task Cp_Interactive_LongOption() => EqualAsync(Two(), "echo y | cp --interactive a b");
    [SkippableFact] public Task Cp_Interactive_AfterNoClobber_Asks() => EqualAsync(Two(), "echo y | cp -n -i a b");
    [SkippableFact] public Task Cp_NoClobber_AfterInteractive_Skips() => EqualAsync(Two(), "echo y | cp -i -n a b");
    [SkippableFact] public Task Cp_ForceDoesNotCancelInteractive() => EqualAsync(Two(), "echo y | cp -f -i a b");
    [SkippableFact] public Task Cp_Interactive_InARecursiveCopy() => EqualAsync(Tree(("s/f", "NEW"), ("t/s/f", "OLD")), "echo y | cp -ri s t");
    [SkippableFact] public Task Cp_Interactive_SkippedWhenUpdateKeepsIt() => EqualAsync(Newer(), "echo y | cp -iu a b");

    // ───────────── -l / --remove-destination / --attributes-only / --reflink / --debug ─────────────

    [SkippableFact] public Task Cp_HardLink() => EqualAsync(Tree(("a", "A")), "cp -lv a n; echo more >> n");
    [SkippableFact] public Task Cp_HardLink_OntoAnExistingFile_Fails() => EqualAsync(Two(), "cp -lv a b");
    [SkippableFact] public Task Cp_HardLink_Force_ReplacesIt() => EqualAsync(Two(), "cp -lfv a b");
    [SkippableFact] public Task Cp_HardLink_WithBackup() => EqualAsync(Two(), "cp -lbv a b");
    [SkippableFact] public Task Cp_HardLink_RecursiveLinksTheFiles() => EqualAsync(Tree(("s/x/f", "F")), "cp -rlv s t; echo more >> t/x/f");
    [SkippableFact] public Task Cp_HardLink_LongOption() => EqualAsync(Tree(("a", "A")), "cp --link a n");
    [SkippableFact] public Task Cp_HardAndSymbolic_AreExclusive() => EqualAsync(Two(), "cp -l -s a n");
    [SkippableFact] public Task Cp_RemoveDestination_BreaksAnExistingHardLink() => EqualAsync(Tree(("a", "A"), ("b", "B")), "ln b b2; cp --remove-destination a b; echo more >> b2");
    [SkippableFact] public Task Cp_RemoveDestination_WithNoDestination() => EqualAsync(Tree(("a", "A")), "cp --remove-destination a n");
    [SkippableFact] public Task Cp_AttributesOnly_CreatesAnEmptyFile() => EqualAsync(Tree(("a", "A")), "cp -v --attributes-only a n");
    [SkippableFact] public Task Cp_AttributesOnly_KeepsAnExistingFilesContent() => EqualAsync(Two(), "cp --attributes-only a b");
    [SkippableFact] public Task Cp_AttributesOnly_Recursive() => EqualAsync(Tree(("s/x/f", "F")), "cp -r --attributes-only s t");
    [SkippableFact] public Task Cp_Reflink_Always_FailsWhereNoFilesystemClones() => EqualAsync(Two(), "cp -v --reflink=always a n");
    [SkippableFact] public Task Cp_Reflink_BareMeansAlways() => EqualAsync(Two(), "cp --reflink a n");
    [SkippableFact] public Task Cp_Reflink_AutoAndNever() => EqualAsync(Two(), "cp -v --reflink=auto a n1; cp -v --reflink=never a n2");
    [SkippableFact] public Task Cp_Reflink_AbbreviatedWord() => EqualAsync(Two(), "cp --reflink=al a n");
    [SkippableFact] public Task Cp_Reflink_BadWord() => EqualAsync(Two(), "cp --reflink=bogus a n");
    [SkippableFact] public Task Cp_Sparse_WordsAreValidatedAndHarmless() => EqualAsync(Two(), "cp --sparse=auto a n1; cp --sparse=always a n2; cp --sparse=never a n3; cp --sparse=al a n4");
    [SkippableFact] public Task Cp_Sparse_BadWord() => EqualAsync(Two(), "cp --sparse=bogus a n");
    [SkippableFact] public Task Cp_Sparse_NeedsAnArgument() => EqualAsync(Two(), "cp --sparse a n");
    [SkippableFact] public Task Cp_Debug_ImpliesVerbose() => EqualAsync(Two(), "cp --debug a n");
    [SkippableFact] public Task Cp_Debug_ReflinkNever() => EqualAsync(Two(), "cp --debug --reflink=never a n");
    [SkippableFact] public Task Cp_Debug_SkippedFile() => EqualAsync(Two(), "cp --debug -n a b");
    [SkippableFact] public Task Cp_Debug_Recursive() => EqualAsync(Tree(("s/f", "F")), "cp -r --debug s t");

    // ───────────── --parents / --strip-trailing-slashes / dereference / -x ─────────────

    [SkippableFact] public Task Cp_Parents() => EqualAsync(Tree(("p/q/r", "R"), ("out", null)), "cp -v --parents p/q/r out");
    [SkippableFact] public Task Cp_Parents_SingleComponent() => EqualAsync(Tree(("a", "A"), ("out", null)), "cp -v --parents a out");
    [SkippableFact] public Task Cp_Parents_ReusesExistingDirectories() => EqualAsync(Tree(("p/q/r", "R"), ("out/p", null)), "cp -v --parents p/q/r out");
    [SkippableFact] public Task Cp_Parents_DestinationMustBeADirectory() => EqualAsync(Tree(("p/q/r", "R"), ("b", "B")), "cp --parents p/q/r b");
    [SkippableFact] public Task Cp_Parents_MissingDestination() => EqualAsync(Tree(("p/q/r", "R")), "cp --parents p/q/r nodir");
    [SkippableFact] public Task Cp_Parents_RecursiveDirectory() => EqualAsync(Tree(("p/q/r", "R"), ("out", null)), "cp -rv --parents p/q out");
    [SkippableFact] public Task Cp_StripTrailingSlashes_FileIntoDirectory() => EqualAsync(Two(), "cp -v --strip-trailing-slashes a d");
    [SkippableFact] public Task Cp_TrailingSlashOnAFile_WithoutTheFlag() => EqualAsync(Two(), "cp a/ d");
    [SkippableFact] public Task Cp_StripTrailingSlashes_FileWithSlashIntoDirectory() => EqualAsync(Two(), "cp -v --strip-trailing-slashes a/ d");
    [SkippableFact] public Task Cp_StripTrailingSlashes_PairFormKeepsTheSlash() => EqualAsync(Two(), "cp --strip-trailing-slashes a/ n");
    [SkippableFact] public Task Cp_Dereference_OptionsAreAcceptedWithoutLinks() => EqualAsync(Tree(("a", "A")), "cp -L a n1; cp -P a n2; cp -H a n3; cp -d a n4; cp --dereference a n5; cp --no-dereference a n6");
    [SkippableFact] public Task Cp_Dereference_Recursive() => EqualAsync(Tree(("s/f", "F")), "cp -rL s t1; cp -rP s t2; cp -rH s t3; cp -rd s t4");
    [SkippableFact] public Task Cp_OneFileSystem_OrdinaryTree() => EqualAsync(Tree(("s/x/f", "F")), "cp -rxv s t");
    [SkippableFact] public Task Cp_OneFileSystem_LongOption() => EqualAsync(Tree(("s/f", "F")), "cp -r --one-file-system s t");

    // ───────────── ordering of the verbose lines of a tree (directory-first) ─────────────

    [SkippableFact] public Task Cp_RecursiveVerbose_NamesEveryEntry() => EqualAsync(Tree(("s/x/f", "F")), "cp -rv s t");
    [SkippableFact] public Task Cp_RecursiveVerbose_IntoAnExistingDirectory() => EqualAsync(Tree(("s/f", "F"), ("t", null)), "cp -rv s t");
    [SkippableFact] public Task Cp_RecursiveVerbose_MergeIntoAnExistingTree() => EqualAsync(Tree(("s/f", "NEW"), ("t/s/f", "OLD")), "cp -rv s t");
}
