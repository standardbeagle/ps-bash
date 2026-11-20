using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>Every test file that registers or fires a hook in the process-global registry runs in
/// <see cref="HookRegistryCollection"/>, so no two of them fire each other's hooks.</summary>
public class HookRegistryCollectionGuardTests
{
    // A use of the registry, from C# or from the PowerShell a test runs.
    private static readonly Regex RegistryUse = new(
        @"\bHookRegistry\b|\bRegister-Bash(?:Chpwd|Prompt)Hook\b|\bEnable-BashHookPrompt\b", RegexOptions.Compiled);

    /// <summary>REGRESSION (Windows fleet s23e): <c>ChpwdHook_DoesNotFire_WhenPathUnchanged</c> failed when a parallel
    /// class's prompt tick, with a directory change, ran its chpwd hook.</summary>
    [Fact]
    public void EveryTestFileThatUsesTheHookRegistry_IsInTheCollection()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PsBash.Cmdlets.Tests.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir!.FullName, "*.cs", SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}")) continue;
            if (Path.GetFileName(file) is nameof(HookRegistryCollection) + ".cs" or nameof(HookRegistryCollectionGuardTests) + ".cs") continue;
            var text = File.ReadAllText(file);
            if (RegistryUse.IsMatch(text) && !text.Contains("[Collection(HookRegistryCollection.Name)]", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }
        Assert.True(offenders.Count == 0,
            "A test that registers or fires a hook runs in [Collection(HookRegistryCollection.Name)]:\n" + string.Join("\n", offenders));
    }
}
