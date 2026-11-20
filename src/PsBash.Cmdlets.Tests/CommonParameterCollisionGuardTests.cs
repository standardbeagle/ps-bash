using System.Management.Automation;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Build-gate guard for PowerShell common-parameter flag collisions
/// (docs/solutions/common-parameter-flag-collisions.md). Every
/// <c>Invoke-Bash*</c> binary cmdlet is an advanced <see cref="PSCmdlet"/>, so it
/// inherits PowerShell's common parameters. A bare bash short flag whose letter
/// prefixes a common parameter — <c>-c -d -e -i -o -p -v -w</c> — or the cmdlet's
/// own <c>-Arguments</c> (<c>-a</c>) is consumed by the binder BEFORE it reaches
/// <c>[ValueFromRemainingArguments] Arguments</c>: a hard crash for the ambiguous
/// ones (<c>-e/-i/-o/-p/-w</c>) or a silent drop for the unique ones
/// (<c>-c/-d/-v</c>). This test cross-references every command's documented flags
/// (<c>BashFlagSpecs.json</c>) against the cmdlet's declared
/// <c>[Parameter]</c>/<c>[Alias]</c> single-letter names plus the emitter's
/// force-quote allowlist, and fails if a colliding short flag is left unguarded —
/// turning a whole class of dogfood-only bugs into a compile gate.
///
/// Oracle note (qa-rubric Directive 1): ps-bash-specific structural invariant, no
/// bash oracle.
/// </summary>
public class CommonParameterCollisionGuardTests
{
    /// <summary>
    /// Single-letter flags whose <c>-x</c> prefix-collides with a PowerShell common
    /// parameter (the binder is case-insensitive, so <c>-C</c> == <c>-c</c>), plus
    /// <c>a</c> which collides with the cmdlet's own <c>-Arguments</c> parameter.
    /// </summary>
    private static readonly HashSet<char> CollidingLetters =
        new() { 'a', 'c', 'd', 'e', 'i', 'o', 'p', 'v', 'w' };

    /// <summary>
    /// Flags the EMITTER single-quotes instead of the cmdlet declaring a decoy. MUST stay in sync with
    /// <c>PsEmitter.OrderedArgCommands</c> (and, for echo, its force-quote set).
    /// </summary>
    private static readonly Dictionary<string, HashSet<char>> EmitterForceQuoted =
        new(StringComparer.Ordinal)
        {
            // find: on OrderedArgCommands — every dash word of the expression (-o/-a operators, the argv of
            // -exec) is single-quoted and reaches Arguments verbatim and in order.
            ["find"] = new(CollidingLetters),
            // xargs: single-quoted like the ordered set below, so the INNER command's flags
            // (`xargs basename -a`) reach Arguments verbatim too. The manual scan stops at the
            // first operand; I/P/D stay declared for direct calls.
            ["xargs"] = new(CollidingLetters),
            // PsEmitter.OrderedArgCommands: the emitter single-quotes EVERY dash-leading literal
            // for these (shared ordered parser), so all colliding letters reach Arguments. Their
            // cmdlets still keep decoys for DIRECT calls (Pester: `Invoke-BashTee -a f`).
            // getopts: its argv is the script's own (`getopts ab: o -a`), so every dash word is single-quoted.
            ["getopts"] = new(CollidingLetters),
            // flock: -n/-x/-s/-w/-E/-c are the script's own flags; every dash word is single-quoted.
            ["flock"] = new(CollidingLetters),
            // timeout: -s/-k/-v are its own flags and the inner command's flags follow; every dash word is single-quoted.
            ["timeout"] = new(CollidingLetters),
            // times/hash/umask: argv is the script's own; every dash word is single-quoted.
            ["times"] = new(CollidingLetters),
            // history: -c/-d/-p/-w are its own; every dash word is single-quoted. c/d/p/w decoys for direct calls.
            ["history"] = new(CollidingLetters),
            ["hash"] = new(CollidingLetters),
            // help: -d is its own flag (bare, it bound -Debug: help -d umask printed the long form); every dash word is single-quoted.
            ["help"] = new(CollidingLetters),
            ["umask"] = new(CollidingLetters),
            ["ulimit"] = new(CollidingLetters),
            ["compgen"] = new(CollidingLetters),
            // mapfile / seq: every dash word single-quoted, so an option the builtin rejects keeps its spelling and place
            // (`seq 2 -w 4` is an operand -w, `mapfile -Q` reports -Q); decoys stay for direct calls.
            ["mapfile"] = new(CollidingLetters),
            ["seq"] = new(CollidingLetters),
            // hostname: -a/-A/-d/-i/-I are Debian hostname's display options; bare, -i was ambiguous to the binder.
            ["hostname"] = new(CollidingLetters),
            ["time"] = new(CollidingLetters),
            ["env"] = new(CollidingLetters),
            // command: emitter single-quotes every dash literal (its own -v/-V/-p AND the inner
            // command's flags); the cmdlet scans options only up to the first operand.
            ["command"] = new(CollidingLetters),
            // kill: every dash literal single-quoted (`-PIPE` prefix-matched -PipelineVariable); PIPE decoy for direct calls.
            ["kill"] = new(CollidingLetters),
            // declare (print forms): every dash literal single-quoted; P stays declared for direct calls.
            ["declare"] = new(CollidingLetters),
            // type: -p vs -P differ only in case, which the binder folds; quoted they keep it. A/P decoys for direct calls.
            ["type"] = new(CollidingLetters),
            // bash: the script's own args / args after `-c CMD NAME` are positional; C stays declared for direct calls.
            ["bash"] = new(CollidingLetters),
            // awk: -v/-F/-f parsed from Arguments (repeated -v crashed the declared string[] V); V stays for one direct -v.
            ["awk"] = new(CollidingLetters),
            ["tee"] = new(CollidingLetters),
            // diff: the emitter single-quotes every dash literal; I/W/C stay declared for direct calls.
            ["diff"] = new(CollidingLetters),
            // jq: every dash literal (-c -e -a -r -n --arg ...) reaches Arguments verbatim; C/E/A stay declared for direct calls.
            ["jq"] = new(CollidingLetters),
            ["cp"] = new(CollidingLetters),
            ["mv"] = new(CollidingLetters),
            ["rm"] = new(CollidingLetters),
            ["mkdir"] = new(CollidingLetters),
            ["rmdir"] = new(CollidingLetters),
            ["ln"] = new(CollidingLetters),
            ["touch"] = new(CollidingLetters),
            ["head"] = new(CollidingLetters),
            ["tail"] = new(CollidingLetters),
            ["wc"] = new(CollidingLetters),
            ["cat"] = new(CollidingLetters),
            ["tac"] = new(CollidingLetters),
            ["nl"] = new(CollidingLetters),
            ["uniq"] = new(CollidingLetters),
            ["fold"] = new(CollidingLetters),
            ["expand"] = new(CollidingLetters),
            ["unexpand"] = new(CollidingLetters),
            ["paste"] = new(CollidingLetters),
            ["join"] = new(CollidingLetters),
            ["comm"] = new(CollidingLetters),
            ["split"] = new(CollidingLetters),
            ["strings"] = new(CollidingLetters),
            ["base64"] = new(CollidingLetters),
            ["stat"] = new(CollidingLetters),
            ["file"] = new(CollidingLetters),
            ["cut"] = new(CollidingLetters),
            ["sort"] = new(CollidingLetters),
            // grep: GNU getopt via the shared parser; direct calls go through the psm1 literal-args proxy.
            ["grep"] = new(CollidingLetters),
            // sed: GNU getopt via the shared parser; direct calls go through the psm1 literal-args proxy.
            ["sed"] = new(CollidingLetters),
["shopt"] = new(CollidingLetters),
            // rg: ripgrep-flavoured ordered parser; the native passthrough receives the argv verbatim.
            ["rg"] = new(CollidingLetters),
            ["echo"] = new(CollidingLetters),
            ["printf"] = new(CollidingLetters),
            ["test"] = new(CollidingLetters),
            ["ls"] = new(CollidingLetters),
            ["du"] = new(CollidingLetters),
            ["tree"] = new(CollidingLetters),
            ["column"] = new(CollidingLetters),
            ["gzip"] = new(CollidingLetters),
            ["tar"] = new(CollidingLetters),
            ["md5sum"] = new(CollidingLetters),
            ["od"] = new(CollidingLetters),
            ["chmod"] = new(CollidingLetters),
            ["sha1sum"] = new(CollidingLetters),
            ["sha256sum"] = new(CollidingLetters),
            ["readlink"] = new(CollidingLetters),
            ["realpath"] = new(CollidingLetters),
            // ping -W / uname -o -p -i -v / shuf -o / traceroute -w / id: ambiguous binder prefixes before they joined the
            // ordered set (REGRESSION: "parameter name 'W' is ambiguous"); decoys W / o p i v a / e i o / W / a for direct calls.
            ["ping"] = new(CollidingLetters),
            ["uname"] = new(CollidingLetters),
            ["shuf"] = new(CollidingLetters),
            ["traceroute"] = new(CollidingLetters),
            ["id"] = new(CollidingLetters),
        };

    [Fact]
    public void NoBinaryCmdlet_HasUnguardedCommonParameterCollidingShortFlag()
    {
        var specs = LoadFlagSpecs();
        var assembly = typeof(InvokeBashFindCommand).Assembly;
        var violations = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            var cmdletAttr = type.GetCustomAttribute<CmdletAttribute>();
            if (cmdletAttr is null
                || !cmdletAttr.NounName.StartsWith("Bash", StringComparison.Ordinal))
            {
                continue;
            }

            var command = cmdletAttr.NounName.Substring(4).ToLowerInvariant();
            // The documented flags (BashFlagSpecs.json) and, below, the flags the cmdlet's own parser declares. A command
            // with no spec entry is still checked through its OptSpecSet. REGRESSION: ping -W, uname -o/-p/-i and shuf -o
            // were not in the spec file, so this guard never saw them while each crashed the binder.
            specs.TryGetValue(command, out var flags);

            var guarded = DeclaredSingleLetterNames(type);
            EmitterForceQuoted.TryGetValue(command, out var forceQuoted);

            // A short flag can also reach the binder from the cmdlet's own
            // "valid-but-unsupported" classifier sets (e.g. cp/mv/rm -i, column -o,
            // tee -p) — those are NOT in BashFlagSpecs.json, but the token still has
            // to survive binding to reach the classifier and emit its exit-2 message.
            // Fold every single-letter -x found in a static flag-set field into the
            // checked set so the guard covers the whole binder-collision class.
            var allFlags = new List<string>(flags ?? new List<string>());
            // Classifier sets (unsupported / passthrough flag lists) only for documented commands. git has no flag universe
            // (its argv is git's): it declares a switch decoy for every colliding letter and rebuilds the argv in order
            // (BashRuntime.TypedArguments; InvokeBashGitCommandTests.Direct_*).
            if (flags is not null) allFlags.AddRange(ClassifierShortFlags(type));
            allFlags.AddRange(OptSpecShortFlags(type));
            if (allFlags.Count == 0) continue; // nothing documented or declared to check

            foreach (var flag in allFlags)
            {
                // Only bare single-letter short flags (-x) can prefix-collide.
                if (flag.Length != 2 || flag[0] != '-' || !char.IsLetter(flag[1]))
                {
                    continue;
                }

                char letter = char.ToLowerInvariant(flag[1]);
                if (!CollidingLetters.Contains(letter))
                {
                    continue;
                }

                bool ok = guarded.Contains(letter)
                          || (forceQuoted?.Contains(letter) ?? false);
                if (!ok)
                {
                    violations.Add(
                        $"{command} {flag} ({type.Name}): colliding short flag is neither a declared " +
                        "[Parameter]/[Alias] nor emitter-force-quoted");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "PowerShell common-parameter flag collisions (the binder will crash on -e/-i/-o/-p/-w "
            + "or silently drop -c/-d/-v before the cmdlet sees them):\n  "
            + string.Join("\n  ", violations.OrderBy(v => v, StringComparer.Ordinal))
            + "\n\nFix each by declaring a single-letter [Parameter] decoy on the cmdlet (and reading it "
            + "like the existing I/V/C/W/O switches), or — for a position-critical infix operator — by "
            + "putting the command on PsEmitter.OrderedArgCommands and this test's EmitterForceQuoted map. "
            + "See docs/solutions/common-parameter-flag-collisions.md.");
    }

    /// <summary>
    /// A one-letter decoy is case-insensitive: <c>W</c> takes a direct <c>-w</c> and <c>-W</c> alike. When the command
    /// has BOTH flags (ping <c>-w</c> deadline / <c>-W</c> timeout), the cmdlet must read back which one was typed —
    /// <c>BashRuntime.TypedArguments</c> / <c>TypedCase</c> (the command's syntax tree), or the older raw-line recovery
    /// on <c>BashRuntime.CurrentPipelineSegment</c>. REGRESSION: a direct <c>Invoke-BashPing -w 3 host</c> ran as
    /// <c>-W 3</c>. Oracle note (qa-rubric Directive 1): a ps-bash-specific structural invariant, no bash oracle.
    /// </summary>
    /// <summary>Commands whose decoys may stay case-blind, with the reason.</summary>
    private static readonly Dictionary<string, string> CaseBlindDecoyAllowed = new(StringComparer.Ordinal)
    {
        // GNU tr: `-c, -C, --complement` — one option, two spellings.
        ["tr"] = "-c and -C are the same option",
        // Direct calls reach the psm1 literal-args proxy (Invoke-BashSed), which never binds a decoy.
        ["sed"] = "direct calls go through the literal-args proxy",
    };

    [Fact]
    public void CaseCollidingDecoy_RecoversTheTypedFlag()
    {
        var specs = LoadFlagSpecs();
        var sources = Directory.EnumerateFiles(Path.GetDirectoryName(FindRepoFile(Path.Combine("src", "PsBash.Cmdlets", "BashRuntime.cs")))!, "*.cs")
            .Select(File.ReadAllText).ToList();
        var violations = new List<string>();
        foreach (var type in typeof(InvokeBashFindCommand).Assembly.GetTypes())
        {
            var cmdletAttr = type.GetCustomAttribute<CmdletAttribute>();
            if (cmdletAttr is null || !cmdletAttr.NounName.StartsWith("Bash", StringComparison.Ordinal)) continue;
            var command = cmdletAttr.NounName.Substring(4).ToLowerInvariant();
            if (CaseBlindDecoyAllowed.ContainsKey(command)) continue;
            var flags = new HashSet<string>(StringComparer.Ordinal);
            if (specs.TryGetValue(command, out var documented)) flags.UnionWith(documented);
            flags.UnionWith(ClassifierShortFlags(type));
            flags.UnionWith(OptSpecShortFlags(type));
            foreach (var letter in DeclaredSingleLetterNames(type))
            {
                if (!flags.Contains("-" + letter) || !flags.Contains("-" + char.ToUpperInvariant(letter))) continue;
                var source = sources.FirstOrDefault(s => s.Contains("class " + type.Name + " ", StringComparison.Ordinal)) ?? "";
                if (source.Contains("TypedArguments(", StringComparison.Ordinal) || source.Contains("TypedCase(", StringComparison.Ordinal)
                    || source.Contains("CurrentPipelineSegment(", StringComparison.Ordinal))
                    continue;
                violations.Add($"{command} ({type.Name}): decoy '{letter}' takes both -{letter} and -{char.ToUpperInvariant(letter)}");
            }
        }
        Assert.True(violations.Count == 0,
            "A case-insensitive decoy hides which flag a direct call typed; rebuild the argv with BashRuntime.TypedArguments:\n  "
            + string.Join("\n  ", violations.OrderBy(v => v, StringComparer.Ordinal)));
    }

    /// <summary>
    /// Every single-letter short flag (<c>-x</c>) mentioned in a cmdlet's static
    /// string-set fields — the "valid-but-unsupported" classifier tables. These flags
    /// are not in <c>BashFlagSpecs.json</c> but must still survive the binder to reach
    /// the classifier, so they belong in the collision check.
    /// </summary>
    private static IEnumerable<string> ClassifierShortFlags(Type type)
    {
        foreach (var field in type.GetFields(
                     BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (field.GetValue(null) is not IEnumerable<string> set) continue;
            foreach (var entry in set)
            {
                if (entry is { Length: 2 } && entry[0] == '-' && char.IsLetter(entry[1]))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>
    /// Every short option (<c>-x</c>) of a cmdlet's static <see cref="PsBash.Cmdlets.Args.OptSpecSet"/> fields: the
    /// options its shared ordered parser accepts, whether or not BashFlagSpecs.json lists them.
    /// </summary>
    private static IEnumerable<string> OptSpecShortFlags(Type type)
    {
        foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (field.GetValue(null) is not PsBash.Cmdlets.Args.OptSpecSet set) continue;
            foreach (var spec in set.Specs)
            {
                if (spec.Short != '\0') yield return "-" + spec.Short;
            }
        }
    }

    private static HashSet<char> DeclaredSingleLetterNames(Type type)
    {
        var set = new HashSet<char>();
        foreach (var prop in type.GetProperties())
        {
            if (prop.GetCustomAttribute<ParameterAttribute>() is null)
            {
                continue;
            }

            if (prop.Name.Length == 1)
            {
                set.Add(char.ToLowerInvariant(prop.Name[0]));
            }

            // A long-named parameter can register a single-letter short name via
            // [Alias("d")] — e.g. paste/fold/sed. Those bind the bare flag too.
            var alias = prop.GetCustomAttribute<AliasAttribute>();
            if (alias is not null)
            {
                foreach (var a in alias.AliasNames)
                {
                    if (a.Length == 1)
                    {
                        set.Add(char.ToLowerInvariant(a[0]));
                    }
                }
            }
        }

        return set;
    }

    /// <summary>
    /// The completion / collision-guard spec must list the options the migrated cmdlets implement: a
    /// flag missing here gets no completion and — worse — HIDES a binder collision from the guard
    /// above. Pins the options added with the file-mutator and batch-3 migrations.
    /// </summary>
    [Theory]
    [InlineData("rm", "-d,-i,-I,--interactive")]
    [InlineData("mkdir", "-m,--mode")]
    [InlineData("touch", "-t,-r,-a,-m,-c,-h,--time")]
    [InlineData("cp", "-p,-a,--preserve,--no-preserve")]
    [InlineData("ln", "-n,-t,-T")]
    [InlineData("comm", "--total,--output-delimiter")]
    [InlineData("jq", "-R,-j")]
    [InlineData("split", "-b,--numeric-suffixes,--additional-suffix")]
    [InlineData("head", "-c,-q")]
    [InlineData("join", "-j,-a,-v,-i")]
    [InlineData("uniq", "-u,-D,-i,-f,-s,-w")]
    [InlineData("wc", "-m,-L")]
    public void FlagSpecs_ListTheImplementedOptions(string command, string flagsCsv)
    {
        var specs = LoadFlagSpecs();
        Assert.True(specs.TryGetValue(command, out var have), $"{command} has no spec");
        var missing = flagsCsv.Split(',').Where(f => !have!.Contains(f)).ToList();
        Assert.True(missing.Count == 0, $"{command}: missing {string.Join(' ', missing)}");
    }

    private static Dictionary<string, List<string>> LoadFlagSpecs()
    {
        var path = FindRepoFile(Path.Combine("src", "PsBash.Module", "BashFlagSpecs.json"));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var command in doc.RootElement.EnumerateObject())
        {
            var flags = new List<string>();
            foreach (var entry in command.Value.EnumerateArray())
            {
                if (entry.TryGetProperty("flag", out var f) && f.GetString() is { } fs)
                {
                    flags.Add(fs);
                }
            }

            result[command.Name] = flags;
        }

        return result;
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {relative} walking up from {AppContext.BaseDirectory}");
    }
}
