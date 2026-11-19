using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A command's identity is its cmdlet, never the alias name it was called by. REGRESSION: <c>printenv</c> was
/// <c>Invoke-BashEnv</c> told apart by <c>MyInvocation.InvocationName</c>, and <c>gunzip</c> / <c>zcat</c> were
/// <c>Invoke-BashGzip</c> the same way. Under an indirect call (<c>env X=1 printenv X</c>, <c>xargs gunzip</c>,
/// <c>timeout 5 zcat f</c>, <c>command printenv</c>) the cmdlet runs from <c>&amp; $c</c>, where InvocationName is
/// <c>&amp;</c>: printenv printed <c>NAME=VALUE</c> and gunzip compressed. Each identity is now its own cmdlet
/// (<c>Invoke-BashPrintenv</c>, <c>Invoke-BashGunzip</c>, <c>Invoke-BashZcat</c>). This scan fails on a cmdlet that
/// reads InvocationName again. Allowed: <c>[</c> (test needs its closing <c>]</c>, a syntax fact the transpiler also
/// passes), and BashRuntime's raw-line flag recovery (it locates the command in the line, it does not pick a mode).
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check.
/// </summary>
public class InvocationIdentityGuardTests
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

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "InvokeBashTestCommand.cs", "BashRuntime.cs",
    };

    [Fact]
    public void Cmdlets_NeverPickBehaviourFromInvocationName()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var name = Path.GetFileName(file);
            if (Allowed.Contains(name)) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                if (code.Contains("InvocationName", StringComparison.Ordinal)) offenders.Add($"{name}:{i + 1}: {code}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Give the identity its own cmdlet (and alias) instead of reading InvocationName:\n" + string.Join("\n", offenders));
    }

    // The psm1 alias for each split identity names its own cmdlet (a revert to the shared cmdlet would pass the scan).
    [Theory]
    [InlineData("printenv", "Invoke-BashPrintenv")]
    [InlineData("gunzip", "Invoke-BashGunzip")]
    [InlineData("zcat", "Invoke-BashZcat")]
    [InlineData("true", "Invoke-BashTrue")]
    [InlineData("false", "Invoke-BashFalse")]
    public void Psm1_AliasNamesTheIdentityCmdlet(string alias, string cmdlet)
    {
        var psm1 = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PsBash.Module", "PsBash.psm1"));
        Assert.Matches($@"Set-Alias -Name '{alias}'\s+-Value '{cmdlet}'", psm1);
    }
}
