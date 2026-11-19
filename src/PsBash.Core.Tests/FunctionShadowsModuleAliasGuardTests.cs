using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// A script function wins over every command ps-bash aliases (bash: functions precede builtins and PATH; only a POSIX
/// special builtin in posix mode outranks one). PowerShell resolves an alias BEFORE a function, so a call of a function
/// whose name the module aliases (<c>Set-Alias -Name 'true'</c> in PsBash.psm1) must name the function explicitly
/// (<c>&amp; ((Get-Command -Name 'true' -CommandType Function …) ?? 'true')</c>), or the alias runs instead.
/// <para>
/// The bug class: the emitter knew which names were aliased only through its own mapping tables, so a command given its own
/// alias later (true / false became Invoke-BashTrue / Invoke-BashFalse) silently outranked a same-named function:
/// <c>true() { echo 'true func'; }; true hi</c> printed nothing (oils builtin-special spec). The primitive is
/// <c>PsEmitter.IsAliasedCommandName</c>; this guard walks the module's own alias list so a new alias cannot reopen it.
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): the run-time behavior is pinned by the oils builtin-special spec and
/// <c>FunctionShadowsBuiltinDifferentialTests</c>; this is the repo-structure half (every alias, not one example).</para>
/// </summary>
public class FunctionShadowsModuleAliasGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }

    /// <summary>The names PsBash.psm1 aliases for the whole session (<c>Set-Alias -Name 'X' … -Scope Global</c>).</summary>
    private static IReadOnlyList<string> ModuleAliasNames()
    {
        var psm1 = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Module", "PsBash.psm1"));
        return Regex.Matches(psm1, @"(?m)^\s*Set-Alias\s+-Name\s+'([^']+)'")
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void ModuleAliasList_IsFound()
    {
        // The walk below proves nothing if the alias lines moved or changed shape.
        var names = ModuleAliasNames();
        Assert.Contains("true", names);
        Assert.Contains("echo", names);
        Assert.True(names.Count > 50, $"only {names.Count} Set-Alias lines found in PsBash.psm1");
    }

    [Fact]
    public void ModuleAliases_IsThePsm1sAliasList()
    {
        var psm1 = ModuleAliasNames().ToHashSet(StringComparer.Ordinal);
        var missing = psm1.Except(ModuleAliases.Names).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var stale = ModuleAliases.Names.Except(psm1).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0 && stale.Count == 0,
            "ModuleAliases.Names must list exactly PsBash.psm1's Set-Alias names. missing: " + string.Join(" ", missing)
            + "; stale: " + string.Join(" ", stale));
    }

    // The emitter's constant-status shortcuts for true / : / false (`if true` -> `$true`, `| true` -> Out-Null) stand in
    // for the builtin only; a function of that name runs (bash: `true() { return 1; }; if true` takes the else branch).
    [Theory]
    [InlineData("true() { return 1; }\nif true; then echo y; fi", "true")]
    [InlineData("true() { return 1; }\nwhile true; do break; done", "true")]
    [InlineData("false() { return 0; }\nif false && false; then echo y; fi", "false")]
    [InlineData("true() { return 1; }\necho x | true", "true")]
    public void ConstantStatusShortcut_YieldsToAFunction(string bash, string name)
    {
        var ps = PsEmitter.Transpile(bash)!;
        Assert.Contains("Get-Command -Name " + PsBuild.SingleQuote(name) + " -CommandType Function", ps);
    }

    [Fact]
    public void EveryModuleAlias_LosesToASameNamedFunction()
    {
        var wrong = new List<string>();
        foreach (var name in ModuleAliasNames())
        {
            // A reserved word (`time`) is not callable as a function; `[` is not a valid function name in a script.
            if (BashBuiltins.Keywords.Contains(name) || name == "[")
                continue;
            var ps = PsEmitter.Transpile(name + "() { echo my; }\n" + name + " arg")!;
            if (!ps.Contains("Get-Command -Name " + PsBuild.SingleQuote(name) + " -CommandType Function", StringComparison.Ordinal))
                wrong.Add(name + " => " + ps[Math.Max(0, ps.Length - 160)..].ReplaceLineEndings(" "));
        }
        Assert.True(wrong.Count == 0,
            "a call of a function named like a module alias must name the function (the alias outranks it in PowerShell):\n"
            + string.Join("\n", wrong));
    }
}
