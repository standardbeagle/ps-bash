using PsBash.Differential.Tests.Oracle;
using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// GNU tar 1.35 member selection (<c>tar t|x ARCHIVE NAME...</c>, <c>--wildcards</c>), file lists (<c>-T</c>,
/// <c>-X</c>), <c>--exclude</c> on list/extract and the default archive (stdin/stdout), compared on stdout, exit status
/// and the resulting filesystem. The archive is built by the shell under test and removed before the snapshot (its bytes
/// differ between tars); listings go through <c>sort</c> because GNU stores a directory in readdir order. Diagnostics
/// are not compared (the ps-bash wording is pinned in the cmdlet tests).
/// </summary>
public class TarMembersDifferentialTests
{
    private static string Fixture() => Tree(("d/a.txt", "1"), ("d/sub/b.txt", "2"), ("d/other/c.log", "3"), ("e/f.txt", "4"), ("t.txt", "top"));

    private static string WithArchive(string commands) => "tar cf a.tar d e t.txt; " + commands + "; rm -f a.tar";

    // ───────────── member names ─────────────

    [SkippableFact] public Task List_DirectoryMember_SelectsItAndEverythingBelow() => EqualAsync(Fixture(), WithArchive("tar tf a.tar d/sub | sort"));
    [SkippableFact] public Task List_FileMembers() => EqualAsync(Fixture(), WithArchive("tar tf a.tar t.txt e/f.txt | sort"));
    [SkippableFact] public Task List_WholeDirectory_TrailingSlash() => EqualAsync(Fixture(), WithArchive("tar tf a.tar d/ | sort"));
    [SkippableFact] public Task List_MissingMember_Exit2() => EqualAsync(Fixture(), WithArchive("tar tf a.tar nosuch"));
    [SkippableFact] public Task List_MissingAndPresent_ListsThePresentOne() => EqualAsync(Fixture(), WithArchive("tar tf a.tar t.txt nosuch"));
    [SkippableFact] public Task List_PartialNameSelectsNothing() => EqualAsync(Fixture(), WithArchive("tar tf a.tar d/su"));
    [SkippableFact] public Task List_DuplicateName_SecondIsNotFound() => EqualAsync(Fixture(), WithArchive("tar tf a.tar t.txt t.txt"));
    [SkippableFact] public Task List_DotSlashPrefixIsNotNormalised() => EqualAsync(Fixture(), WithArchive("tar tf a.tar ./t.txt"));
    [SkippableFact] public Task List_PatternWithoutWildcardsFlag_IsLiteral() => EqualAsync(Fixture(), WithArchive("tar tf a.tar 'd/*.txt'"));
    [SkippableFact] public Task List_NoWildcards_IsLiteral() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --no-wildcards 'd/*.txt'"));

    // ───────────── --wildcards ─────────────

    [SkippableFact] public Task Wildcards_Star() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --wildcards '*.txt' | sort"));
    [SkippableFact] public Task Wildcards_StarCrossesSlash() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --wildcards '*/b.txt'"));
    [SkippableFact] public Task Wildcards_Question() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --wildcards 'd/?.txt'"));
    [SkippableFact] public Task Wildcards_Bracket() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --wildcards 'd/[a-b].txt'"));
    [SkippableFact] public Task Wildcards_MatchesLeadingDirectory() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --wildcards 'd/su?' | sort"));
    [SkippableFact] public Task Wildcards_DirectoryContents() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --wildcards 'd/*' | sort"));
    [SkippableFact] public Task Wildcards_NoMatch_Exit2() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --wildcards 'z*'"));
    [SkippableFact] public Task Wildcards_ThenExclude() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --wildcards '*.txt' --exclude='d/*' | sort"));
    [SkippableFact] public Task Wildcards_Extract() => EqualAsync(Fixture(), WithArchive("mkdir x; tar xf a.tar -C x --wildcards '*.log'"));

    // ───────────── extract ─────────────

    [SkippableFact] public Task Extract_DirectoryMember() => EqualAsync(Fixture(), WithArchive("mkdir x; tar xf a.tar -C x d/sub"));
    [SkippableFact] public Task Extract_FileMembers_Verbose() => EqualAsync(Fixture(), WithArchive("mkdir x; tar xvf a.tar -C x t.txt e/f.txt | sort"));
    [SkippableFact] public Task Extract_MissingMember_StillExtractsTheRest() => EqualAsync(Fixture(), WithArchive("mkdir x; tar xf a.tar -C x nosuch t.txt"));
    [SkippableFact] public Task Extract_MemberToStdout() => EqualAsync(Fixture(), WithArchive("tar xOf a.tar t.txt"));
    [SkippableFact] public Task Extract_Exclude() => EqualAsync(Fixture(), WithArchive("mkdir x; tar xf a.tar -C x --exclude='*.txt'"));

    // ───────────── --exclude on list ─────────────

    [SkippableFact] public Task Exclude_ByExtension() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --exclude='*.txt' | sort"));
    [SkippableFact] public Task Exclude_UnanchoredComponent() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --exclude='sub' | sort"));
    [SkippableFact] public Task Exclude_UnanchoredFileName() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --exclude='b.txt' | sort"));
    [SkippableFact] public Task Exclude_TopLevelDirectory() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --exclude='d' | sort"));
    [SkippableFact] public Task Exclude_PathPrefix_ExcludesTheSubtree() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --exclude='d/sub' | sort"));
    [SkippableFact] public Task Exclude_StarSpansSlash() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --exclude='*b*' | sort"));
    [SkippableFact] public Task Exclude_SingleCharacterPattern() => EqualAsync(Fixture(), WithArchive("tar tf a.tar --exclude='?' | sort"));

    // ───────────── -T / -X ─────────────

    [SkippableFact] public Task FilesFrom_List() => EqualAsync(Fixture(), WithArchive("printf 't.txt\\nd/sub\\n' > list; tar tf a.tar -T list | sort; rm list"));
    [SkippableFact] public Task FilesFrom_ListPlusOperand() => EqualAsync(Fixture(), WithArchive("printf 't.txt\\nd/sub\\n' > list; tar tf a.tar -T list e/f.txt | sort; rm list"));
    [SkippableFact] public Task FilesFrom_EmptyLineIsSkipped_TrailingBlankIsKept() => EqualAsync(Fixture(), WithArchive("printf 't.txt\\n\\ne/f.txt  \\n' > list; tar tf a.tar -T list; rm list"));
    [SkippableFact] public Task FilesFrom_NoTrailingNewline() => EqualAsync(Fixture(), WithArchive("printf 't.txt' > list; tar tf a.tar -T list; rm list"));
    [SkippableFact] public Task FilesFrom_Stdin() => EqualAsync(Fixture(), WithArchive("printf 't.txt\\n' | tar tf a.tar -T -"));
    [SkippableFact] public Task FilesFrom_WildcardLineWithoutFlag_IsLiteral() => EqualAsync(Fixture(), WithArchive("printf 'd/*.txt\\n' > list; tar tf a.tar -T list; rm list"));
    [SkippableFact] public Task FilesFrom_WildcardLineWithFlag() => EqualAsync(Fixture(), WithArchive("printf 'd/*.txt\\n' > list; tar tf a.tar --wildcards -T list | sort; rm list"));
    [SkippableFact] public Task FilesFrom_MissingFile_Exit2() => EqualAsync(Fixture(), WithArchive("tar tf a.tar -T nolist"));
    [SkippableFact] public Task FilesFrom_Extract() => EqualAsync(Fixture(), WithArchive("mkdir x; printf 't.txt\\ne/f.txt\\n' > list; tar xf a.tar -C x -T list; rm list"));
    [SkippableFact] public Task FilesFrom_Create() => EqualAsync(Fixture(), "printf 'd/a.txt\\nt.txt\\n' > list; tar cf b.tar -T list; tar tf b.tar; rm list b.tar");
    [SkippableFact] public Task FilesFrom_Create_KeepsOperandOrder() => EqualAsync(Fixture(), "printf 'd/a.txt\\n' > list; tar cf b.tar t.txt -T list e/f.txt; tar tf b.tar; rm list b.tar");
    [SkippableFact] public Task FilesFrom_Create_Directory() => EqualAsync(Fixture(), "printf 'e\\n' > list; tar cf b.tar -T list; tar tf b.tar; rm list b.tar");
    [SkippableFact] public Task FilesFrom_Create_MissingEntry_KeepsTheRest() => EqualAsync(Fixture(), "printf 'nosuch\\nt.txt\\n' > list; tar cf b.tar -T list; tar tf b.tar; rm list b.tar");

    [SkippableFact] public Task ExcludeFrom_List() => EqualAsync(Fixture(), WithArchive("printf '*.log\\nsub\\n' > ex; tar tf a.tar -X ex | sort; rm ex"));
    [SkippableFact] public Task ExcludeFrom_EmptyLineIgnored() => EqualAsync(Fixture(), WithArchive("printf '\\n*.log\\n' > ex; tar tf a.tar -X ex | sort; rm ex"));
    [SkippableFact] public Task ExcludeFrom_PlusExclude() => EqualAsync(Fixture(), WithArchive("printf '*.log\\nsub\\n' > ex; tar tf a.tar --exclude='e*' -X ex | sort; rm ex"));
    [SkippableFact] public Task ExcludeFrom_Create() => EqualAsync(Fixture(), "printf '*.log\\nsub\\n' > ex; tar cf c.tar -X ex d; tar tf c.tar | sort; rm ex c.tar");
    [SkippableFact] public Task ExcludeFrom_MissingFile_Exit2() => EqualAsync(Fixture(), WithArchive("tar tf a.tar -X noex"));

    // ───────────── default archive (stdin / stdout) ─────────────

    [SkippableFact] public Task Stdout_NoF_PipedToList() => EqualAsync(Fixture(), "tar c d t.txt | tar tf - | sort");
    [SkippableFact] public Task Stdout_DashF_PipedToExtract() => EqualAsync(Fixture(), "mkdir x; tar cf - d | tar xf - -C x");
    [SkippableFact] public Task Stdin_NoF_List() => EqualAsync(Fixture(), WithArchive("cat a.tar | tar t | sort"));
    [SkippableFact] public Task Stdin_Redirected_List() => EqualAsync(Fixture(), WithArchive("tar t < a.tar | sort"));
    [SkippableFact] public Task Stdin_NoF_Extract() => EqualAsync(Fixture(), WithArchive("mkdir x; cat a.tar | tar x -C x"));
    [SkippableFact] public Task Stdin_MemberFilter() => EqualAsync(Fixture(), WithArchive("cat a.tar | tar tf - t.txt"));
    [SkippableFact] public Task Stdin_MemberToStdout() => EqualAsync(Fixture(), WithArchive("cat a.tar | tar xOf - t.txt"));
    [SkippableFact] public Task Stdin_Extract_Verbose() => EqualAsync(Fixture(), WithArchive("mkdir x; tar xvf - -C x < a.tar | sort"));
    [SkippableFact] public Task Gzip_Stdout_PipedToList() => EqualAsync(Fixture(), "tar czf - d | tar tzf - | sort");
    [SkippableFact] public Task Gzip_StdinWithoutZ_IsAnError() => EqualAsync(Fixture(), "tar czf z.tgz d; cat z.tgz | tar tf -; rm z.tgz");
    [SkippableFact] public Task Gzip_StdinWithZ() => EqualAsync(Fixture(), "tar czf z.tgz d; cat z.tgz | tar tzf - | sort; rm z.tgz");
    [SkippableFact] public Task Gzip_FileIsDetectedWithoutZ() => EqualAsync(Fixture(), "tar czf z.tgz d; tar tf z.tgz | sort; rm z.tgz");
    [SkippableFact] public Task Stdin_Empty_IsNotATarArchive() => EqualAsync(Fixture(), "tar t </dev/null");
    [SkippableFact] public Task Stdin_Garbage_IsNotATarArchive() => EqualAsync(Fixture(), "echo garbage | tar t");
    [SkippableFact] public Task Create_MissingSource_ExitsTwoAndKeepsTheRest() => EqualAsync(Fixture(), "tar cf b.tar nosuch t.txt; tar tf b.tar; rm b.tar");
    [SkippableFact] public Task Create_Exclude_TopLevelDirectory() => EqualAsync(Fixture(), "tar cf b.tar --exclude=d d t.txt; tar tf b.tar; rm b.tar");
}
