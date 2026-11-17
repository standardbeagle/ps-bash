using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using PsBash.Core;
using PsBash.Host.Runtime;
using Xunit;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// The shell's runspace keeps no PowerShell alias except the runtime's own (<see cref="PowerShellAliases"/>): a bash
/// command word the emitter does not map must reach a function, a program or "command not found", never a cmdlet
/// (<c>write -- $x</c> printed <c>--</c>, Windows fleet s22e). Bash-visible behavior: Differential.Tests/PowerShellAliasWordTests.
/// Oracle note (Directive 1): host-internal session state, no bash equivalent.
/// </summary>
public class ForeignAliasTests
{
    [Fact]
    public void BaseSessionState_HoldsNoPowerShellAlias()
    {
        var kept = SdkRunspace.CreateBaseSessionState().Commands
            .OfType<SessionStateAliasEntry>()
            .Where(a => !PowerShellAliases.IsBashRuntimeAlias(a.Definition))
            .Select(a => $"{a.Name}={a.Definition}")
            .ToList();
        Assert.True(kept.Count == 0, "PowerShell aliases left in the bash runspace: " + string.Join(", ", kept));
    }

    // Dropping the aliases is safe only while ps-bash's own PowerShell names every command in full.
    [Fact]
    public void PsBashPowerShell_UsesNoBuiltinAlias()
    {
        var aliases = InitialSessionState.CreateDefault2().Commands.OfType<SessionStateAliasEntry>()
            .Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("write", aliases);
        string root = RepoRoot();
        var scripts = new[] { Path.Combine(root, "src", "PsBash.Module"), Path.Combine(root, "src", "PsBash.Host", "Resources") }
            .SelectMany(d => Directory.EnumerateFiles(d, "*.ps*1", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(scripts);
        var offenders = new List<string>();
        foreach (var file in scripts)
        {
            var ast = Parser.ParseFile(file, out _, out _);
            foreach (CommandAst cmd in ast.FindAll(n => n is CommandAst, searchNestedScriptBlocks: true))
                if (cmd.GetCommandName() is { } name && aliases.Contains(name))
                    offenders.Add($"{Path.GetFileName(file)}:{cmd.Extent.StartLineNumber}: {name}");
        }
        Assert.True(offenders.Count == 0, "Name the command in full (the bash runspace has no PowerShell aliases):\n" + string.Join('\n', offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }
}
