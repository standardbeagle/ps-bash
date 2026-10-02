using Xunit;
using static PsBash.Differential.Tests.Oracle.FsStateOracle;

namespace PsBash.Differential.Tests;

/// <summary>
/// <c>ls</c> sort keys (<c>-U -X --sort=extension|none</c>), the time shown / sorted by (<c>-u --time=WORD</c>), the date
/// forms (<c>--time-style</c>, <c>--full-time</c>) and the owner columns (<c>-g -o -G -n</c>) against GNU coreutils 9.4.
/// The files carry FIXED modification and access times (no "now": a recorded date would go stale), owner names and numbers
/// differ between the two sides so those columns are cut away with awk, and the UTC offset of full-iso is stripped.
/// </summary>
public class LsSortTimeDifferentialTests
{
    // modified: a oldest .. d newest (b/c in between); accessed in the REVERSE order, plus one recent-looking and one future file
    private const string Setup =
        "for f in aaaa.c bbbb.h cccc dddd.a.c ee.tar.gz ff; do printf 'x' > $f; done; " +
        "touch -m -d '2024-01-01 10:00:00' aaaa.c; touch -m -d '2024-01-02 10:00:00' bbbb.h; touch -m -d '2024-01-03 10:00:00' cccc; " +
        "touch -m -d '2023-06-01 10:00:00' dddd.a.c; touch -m -d '2099-01-02 03:04:05' ee.tar.gz; touch -m -d '2000-02-03 04:05:06' ff; " +
        "touch -a -d '2022-01-01 10:00:00' aaaa.c; touch -a -d '2021-01-01 10:00:00' bbbb.h; touch -a -d '2020-01-01 10:00:00' cccc; " +
        "touch -a -d '2023-06-01 10:00:00' dddd.a.c; touch -a -d '2019-01-01 10:00:00' ee.tar.gz; touch -a -d '2024-05-05 05:05:05' ff";

    // "date time name" of a long listing whose owner and group are present (perms links owner group size DATE TIME NAME)
    private const string DateName = "awk 'NF >= 8 { print $6, $7, $8 }'";

    // ---- sort keys ----
    [SkippableFact] public Task X_ExtensionThenName() => EqualAsync(Setup, "ls -X");
    [SkippableFact] public Task X_Reversed() => EqualAsync(Setup, "ls -Xr");
    [SkippableFact] public Task SortExtension_LongForm() => EqualAsync(Setup, "ls --sort=extension; ls --sort=ext");
    [SkippableFact] public Task X_WithAll_DotfilesSortByTheirWholeName() => EqualAsync(Setup + "; printf '' > .cfg; printf '' > .bashrc", "ls -Xa");
    [SkippableFact] public Task X_ThenT_LastWins() => EqualAsync(Setup, "ls -Xt; ls -tX");
    [SkippableFact] public Task U_ListsEverything() => EqualAsync(Setup, "ls -U | sort");
    [SkippableFact] public Task SortNone_ListsEverything() => EqualAsync(Setup, "ls --sort=none | sort; ls --sort=n | sort");
    [SkippableFact] public Task U_ThenT_Sorts() => EqualAsync(Setup, "ls -Ut");
    [SkippableFact] public Task SortWord_Bogus() => EqualAsync(Setup, "ls --sort=bogus; echo rc=$?");

    // ---- time selection ----
    [SkippableFact] public Task T_ByModification() => EqualAsync(Setup, "ls -t");
    [SkippableFact] public Task U_AloneSortsByAccessTime() => EqualAsync(Setup, "ls -u");
    [SkippableFact] public Task Ut_SortsByAccessTime() => EqualAsync(Setup, "ls -ut; ls -tu");
    [SkippableFact] public Task Utr_Reversed() => EqualAsync(Setup, "ls -utr");
    [SkippableFact] public Task Us_SizeSortKeepsNameOrderForEqualSizes() => EqualAsync(Setup, "ls -uS");
    [SkippableFact] public Task Lu_NameOrderWithAccessDates() =>
        EqualAsync(Setup, "ls -lu --time-style=long-iso | " + DateName);
    [SkippableFact] public Task Lut_AccessOrderAndDates() =>
        EqualAsync(Setup, "ls -lut --time-style=long-iso | " + DateName);
    [SkippableFact] public Task Time_Atime_Access_Use() =>
        EqualAsync(Setup, "ls --time=atime; ls --time=access; ls --time=use; ls -t --time=atime");
    [SkippableFact] public Task Time_Mtime_BeatsEarlierU() => EqualAsync(Setup, "ls -u --time=mtime; ls -u --time=modification");
    [SkippableFact] public Task LastOfUAndTimeWins() =>
        EqualAsync(Setup, "ls -l --time=ctime -u --time-style=long-iso | " + DateName + "; ls -l -u --time=mtime --time-style=long-iso | " + DateName);
    [SkippableFact] public Task Time_AbbreviationsAndErrors() =>
        EqualAsync(Setup, "ls --time=at; ls --time=a; echo rc=$?; ls --time=bogus; echo rc=$?");
    [SkippableFact] public Task C_AndCtime_ListEveryEntry() => EqualAsync(Setup, "ls -c | sort; ls -lc | wc -l; ls --time=ctime | sort; ls --time=status | sort");

    // ---- time styles ----
    [SkippableFact] public Task LongIso() => EqualAsync(Setup, "ls -l --time-style=long-iso | " + DateName);
    [SkippableFact] public Task Iso_OldRecentAndFuture() => EqualAsync(Setup, "ls -l --time-style=iso | cat -A | awk '{ $1=$2=$3=$4=$5=\"\"; print }'");
    [SkippableFact] public Task Locale_IsTheDefault() => EqualAsync(Setup, "ls -l --time-style=locale | " + DateName + "; ls -l | " + DateName + " ");
    [SkippableFact] public Task PosixPrefixed_AreTheLocaleForm() => EqualAsync(Setup, "ls -l --time-style=posix-long-iso | " + DateName);
    [SkippableFact] public Task FullIso_WithoutTheOffset() =>
        EqualAsync(Setup, "ls -l --time-style=full-iso | sed 's/ [+-][0-9][0-9][0-9][0-9] / /' | " + DateName);
    [SkippableFact] public Task FullTime() =>
        EqualAsync(Setup, "ls --full-time | sed 's/ [+-][0-9][0-9][0-9][0-9] / /' | " + DateName);
    [SkippableFact] public Task FullTime_Last_OfFullTimeAndStyle() =>
        EqualAsync(Setup, "ls -l --full-time --time-style=long-iso | " + DateName + "; ls -l --time-style=long-iso --full-time | sed 's/ [+-][0-9][0-9][0-9][0-9] / /' | " + DateName);
    [SkippableFact] public Task FullTime_WithU() =>
        EqualAsync(Setup, "ls --full-time -u | sed 's/ [+-][0-9][0-9][0-9][0-9] / /' | " + DateName);
    [SkippableFact] public Task FullTime_ThenColumnsAreNotLong() => EqualAsync(Setup, "ls --full-time -C -w 40; ls --full-time -1 | wc -l");
    [SkippableFact] public Task TimeStyle_Abbreviations() =>
        EqualAsync(Setup, "ls -l --time-style=long-i | " + DateName + "; ls -l --time-style=f | wc -l; ls -l --time-style=i | wc -l");
    [SkippableFact] public Task TimeStyle_Environment() => EqualAsync(Setup, "TIME_STYLE=long-iso ls -l | " + DateName);
    [SkippableFact] public Task TimeStyle_OptionBeatsEnvironment() => EqualAsync(Setup, "TIME_STYLE=long-iso ls -l --time-style=iso | cat -A | awk '{ $1=$2=$3=$4=$5=\"\"; print }'");
    [SkippableFact] public Task TimeStyle_BogusWithoutLongIsNoError() => EqualAsync(Setup, "ls --time-style=bogus | wc -l; echo rc=$?");
    [SkippableFact] public Task TimeStyle_BogusIsAnErrorForLong() => EqualAsync(Setup, "ls -l --time-style=bogus; echo rc=$?");
    [SkippableFact] public Task TimeStyle_AmbiguousAbbreviation() => EqualAsync(Setup, "ls -l --time-style=l; echo rc=$?");

    // ---- owner columns ----
    [SkippableFact] public Task G_NoOwnerColumn() => EqualAsync(Setup, "ls -g --time-style=long-iso | awk '{ $3=\"\"; print }'");
    [SkippableFact] public Task O_NoGroupColumn() => EqualAsync(Setup, "ls -o --time-style=long-iso | awk '{ $3=\"\"; print }'");
    [SkippableFact] public Task GO_NeitherColumn() => EqualAsync(Setup, "ls -go --time-style=long-iso; ls -og --time-style=long-iso; ls -lgo --time-style=long-iso");
    [SkippableFact] public Task G_NoGroup_Long() => EqualAsync(Setup, "ls -lG --time-style=long-iso | awk '{ $3=\"\"; print }'; ls -l --no-group --time-style=long-iso | awk '{ $3=\"\"; print }'");
    [SkippableFact] public Task G_Alone_IsNotLong() => EqualAsync(Setup, "ls -G");
    [SkippableFact] public Task N_NumericIdsImplyLong() => EqualAsync(Setup, "ls -n --time-style=long-iso | awk '{ print NF, $1, $2, $5, $6, $7, $8 }'; ls --numeric-uid-gid | wc -l");
    [SkippableFact] public Task N_ColumnsAreNumbers() =>
        EqualAsync(Setup, "ls -n | awk 'NF >= 8 { print ($3 ~ /^[0-9]+$/) ? \"num\" : \"name\", ($4 ~ /^[0-9]+$/) ? \"num\" : \"name\" }' | sort -u");
    [SkippableFact] public Task GO_WithHumanAndBlocks() => EqualAsync(Setup, "ls -gohs --time-style=long-iso");
}
