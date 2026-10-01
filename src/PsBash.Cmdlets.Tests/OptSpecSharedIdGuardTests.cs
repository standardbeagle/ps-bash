using System.Reflection;
using PsBash.Cmdlets.Args;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// ArgParser (like glibc getopt_long) treats long names that share one OptSpec Id as ONE option when
/// resolving an abbreviation, so a prefix matching only such names is accepted silently. That is right
/// for true aliases (<c>--quiet</c>/<c>--silent</c>) and WRONG for a catch-all id grouping unrelated
/// options (gzip's old OptNoOp: <c>gzip --n</c> resolved instead of being ambiguous). This guard walks
/// EVERY <see cref="OptSpecSet"/> (all <c>static</c> fields of that type in the Cmdlets assembly), and
/// fails for any id with 2+ long names that is not listed below with its evidence. Adding a member to
/// an allowlisted group, or a new multi-name group, fails the build until it is oracle-checked
/// (<c>wsl.exe -d Ubuntu-24.04 -- bash -c 'CMD --PREFIX ...'</c>) and recorded here.
/// </summary>
public class OptSpecSharedIdGuardTests
{
    private static readonly string[] Aliases = new[]
    {
        // Same getopt val in GNU: abbreviation of either name is legal and never ambiguous between them.
        "InvokeBashColumnCommand.ColumnSpec keep-empty-lines: keep-empty-lines table-empty-lines",  // util-linux 'L'
        "InvokeBashGrepCommand.GrepSpec F: fixed-strings fixed-regexp",           // oracle: grep --fix a -> accepted
        "InvokeBashGrepCommand.GrepSpec color: color colour",                      // oracle: grep --colo a -> accepted
        "InvokeBashGrepCommand.GrepSpec quiet: quiet silent",
        "InvokeBashGzipCommand.GzipSpec stdout: to-stdout stdout",
        "InvokeBashGzipCommand.GzipSpec decompress: decompress uncompress",
        "InvokeBashGzipCommand.GzipSpec quiet: quiet silent",
        "InvokeBashHeadCommand.HeadSpec quiet: quiet silent",
        "InvokeBashSedCommand.SedSpec quiet: quiet silent",
        "InvokeBashSedCommand.SedSpec null-data: null-data zero-terminated",
        "InvokeBashTailCommand.TailSpec quiet: quiet silent",
        "InvokeBashTarCommand.TarSpec extract: extract get",                       // oracle: tar --gu -> gunzip, --get listed once
        "InvokeBashTarCommand.TarSpec gzip: gzip gunzip ungzip",                   // oracle: tar --gu / --ung accepted
        "InvokeBashTarCommand.TarSpec noop:preserve-permissions: preserve-permissions same-permissions", // -p alias pair
    };

    private static readonly string[] DistinctWithoutSharedAbbreviation = new[]
    {
        // Different options in GNU, but no abbreviation matches ONLY these names (the test recomputes
        // that, so a new member that creates one fails here). The other candidates of any shared
        // prefix carry different ids, so the parser reports it ambiguous like GNU does.
        "InvokeBashColumnCommand.ColumnSpec noop: table-header-repeat table-noheadings", // oracle: --table-h / --table-n ambiguous
        "InvokeBashDuCommand.DuSpec noop: apparent-size count-links one-file-system",
        "InvokeBashExpandCommand.ExpandSpec initial: initial first-only",           // --first-only is a ps-bash extension (GNU expand: unrecognized)
        "InvokeBashGrepCommand.GrepSpec recursive: recursive dereference-recursive",
        "InvokeBashGzipCommand.GzipSpec version: license version",
        "InvokeBashSedCommand.SedSpec noop: unbuffered binary posix sandbox follow-symlinks", // oracle: sed --s ambiguous (silent/sandbox/separate: other ids)
    };

    private static readonly string[] NoAbbreviationTools = new[]
    {
        // ripgrep has NO long-option abbreviation (oracle: rg --no-m / --no-ign are "unrecognized flag"),
        // and RgSpec is built with allowAbbrev:false, so shared ids can never resolve a prefix.
        "InvokeBashRgCommand.RgSpec no-ignore: no-ignore no-ignore-vcs",
        "InvokeBashRgCommand.RgSpec noop: no-messages no-config mmap no-mmap",
    };

    internal static IEnumerable<(string Owner, OptSpecSet Set)> AllSets()
    {
        foreach (var t in typeof(OptSpecSet).Assembly.GetTypes())
        {
            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.FieldType == typeof(OptSpecSet) && f.GetValue(null) is OptSpecSet s)
                    yield return ($"{t.Name}.{f.Name}", s);
            }
        }
    }

    private static IEnumerable<(string Owner, OptSpecSet Set, string Id, string[] Names)> SharedGroups() =>
        AllSets().OrderBy(x => x.Owner, StringComparer.Ordinal).SelectMany(x =>
            x.Set.Specs.Where(s => s.Long is not null).GroupBy(s => s.Id)
                .Select(g => (x.Owner, x.Set, g.Key, Names: g.Select(s => s.Long!).Distinct().ToArray()))
                .Where(g => g.Names.Length > 1));

    private static string Key(string owner, string id, string[] names) => $"{owner} {id}: {string.Join(" ", names)}";

    /// <summary>Abbreviations that select ONLY members of the group (what the parser would accept silently).</summary>
    private static List<string> ExclusiveSharedPrefixes(OptSpecSet set, string[] names)
    {
        var shared = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var n in names)
        {
            for (var len = 1; len < n.Length; len++)
            {
                var p = n[..len];
                var hits = set.LongNamesWithPrefix(p);
                if (hits.All(names.Contains) && hits.Count >= 2) shared.Add(p);
            }
        }
        return shared.ToList();
    }

    [Fact]
    public void RegistryFindsTheMigratedCommands()
    {
        // Reflection seam sanity: a rename of the field type or a move would otherwise make the guard vacuous.
        var owners = AllSets().Select(s => s.Owner).ToList();
        foreach (var must in new[] { "TarSpec", "GrepSpec", "RmSpec", "GzipSpec", "SedSpec", "ColumnSpec", "DuSpec" })
            Assert.Contains(owners, o => o.EndsWith("." + must, StringComparison.Ordinal));
        Assert.True(owners.Count >= 30, $"expected every migrated command's OptSpecSet, found {owners.Count}");
    }

    [Fact]
    public void EveryMultiNameId_IsAVerifiedAliasOrHasNoSharedAbbreviation()
    {
        var known = new HashSet<string>(Aliases.Concat(DistinctWithoutSharedAbbreviation).Concat(NoAbbreviationTools));
        var unverified = SharedGroups().Select(g => Key(g.Owner, g.Id, g.Names)).Where(k => !known.Contains(k)).ToList();
        Assert.True(unverified.Count == 0,
            "OptSpec ids with 2+ long names must be verified aliases (same getopt val in the oracle) or split into "
            + "one id per option. Unverified groups:\n" + string.Join("\n", unverified));
    }

    [Fact]
    public void AllowlistHasNoStaleEntries()
    {
        var actual = new HashSet<string>(SharedGroups().Select(g => Key(g.Owner, g.Id, g.Names)));
        var stale = Aliases.Concat(DistinctWithoutSharedAbbreviation).Concat(NoAbbreviationTools)
            .Where(k => !actual.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "allowlist entries that no longer match a spec group:\n" + string.Join("\n", stale));
    }

    [Fact]
    public void DistinctGroups_HaveNoAbbreviationThatSelectsOnlyTheirMembers()
    {
        var distinct = new HashSet<string>(DistinctWithoutSharedAbbreviation);
        var bad = new List<string>();
        foreach (var g in SharedGroups().Where(g => distinct.Contains(Key(g.Owner, g.Id, g.Names))))
        {
            var p = ExclusiveSharedPrefixes(g.Set, g.Names);
            if (p.Count > 0) bad.Add($"{Key(g.Owner, g.Id, g.Names)} => {string.Join(" ", p)}");
        }
        Assert.True(bad.Count == 0, "GNU treats these as different options, so these prefixes must be ambiguous; split the id:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void NoAbbreviationTools_AreBuiltWithoutAbbreviation()
    {
        var names = new HashSet<string>(NoAbbreviationTools);
        foreach (var g in SharedGroups().Where(g => names.Contains(Key(g.Owner, g.Id, g.Names))))
            Assert.False(g.Set.AllowAbbrev, g.Owner);
    }

    [Theory]
    [InlineData("--no-same")]
    [InlineData("--no-sa")]
    [InlineData("--no-same-")]
    public void Tar_NoSameAbbreviations_AreAmbiguousLikeGnu(string arg)
    {
        // oracle: tar --no-same -> option '--no-same' is ambiguous; possibilities: '--no-same-owner' '--no-same-permissions'
        var parsed = InvokeBashTarCommand.ScanArgs(new[] { arg, "-cf", "a.tar", "x" });
        Assert.Equal(ArgErrorKind.Ambiguous, parsed.Error?.Kind);
    }
}
