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
    /// Flags the EMITTER force-quotes instead of declaring a cmdlet decoy, because
    /// they are position-critical infix operators a switch decoy would displace.
    /// MUST stay in sync with <c>PsEmitter.FindForceQuoteFlags</c>.
    /// </summary>
    private static readonly Dictionary<string, HashSet<char>> EmitterForceQuoted =
        new(StringComparer.Ordinal) { ["find"] = new() { 'o', 'a' } };

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
            if (!specs.TryGetValue(command, out var flags))
            {
                continue; // command has no documented flag spec — nothing to check
            }

            var guarded = DeclaredSingleLetterNames(type);
            EmitterForceQuoted.TryGetValue(command, out var forceQuoted);

            foreach (var flag in flags)
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
            + "adding it to PsEmitter.FindForceQuoteFlags and this test's EmitterForceQuoted map. "
            + "See docs/solutions/common-parameter-flag-collisions.md.");
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
