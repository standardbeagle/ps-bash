using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// A test must not assert a PLATFORM fact unconditionally. The case this guards: <c>\</c> becomes <c>/</c>
/// only on Windows (<c>BashPath.ToShell</c>, docs/specs/paths.md). On Linux and macOS <c>\</c> is a filename
/// character, so <c>dirname 'C:\Users\me\file.txt'</c> is <c>.</c> there, as in bash. A test that feeds a
/// backslash drive path to the runtime and expects the <c>C:/</c> form passes on Windows and fails on every
/// other host.
/// <para>
/// The scan flags any test method (from one <c>[Fact]</c>/<c>[Theory]</c> to the next) that drives the
/// runtime (<c>Invoke-Bash*</c>, <c>BashPath.ToShell</c>, <c>ToShellPath</c>), contains a backslash drive
/// literal, and expects a <c>X:/</c> form, unless the method states an OS condition. Pure functions named for
/// Windows (<c>WindowsPath.*</c>, <c>ToWindowsPathList</c>) behave the same on every OS and are not flagged.
/// Rule: .claude/rules/testing.md ("Platform facts"). Oracle note (qa-rubric Directive 1): a repo-structure
/// invariant with no bash equivalent.
/// </para>
/// </summary>
public class WindowsPathExpectationGuardTests
{
    private static readonly Regex TestStart = new(@"^\s*\[(Skippable)?(Fact|Theory)\b", RegexOptions.Compiled);

    // A backslash drive path inside a verbatim string (C:\x), a regular string (C:\\x), or a PS single-quoted word.
    private static readonly Regex BackslashDrivePath = new(
        @"@""[^""]*\b[A-Za-z]:\\[A-Za-z]|""[^""@]*\b[A-Za-z]:\\\\[A-Za-z]|'[A-Za-z]:\\[A-Za-z]", RegexOptions.Compiled);

    private static readonly Regex SlashDrivePath = new(@"\b[A-Za-z]:/[A-Za-z]", RegexOptions.Compiled);

    private static readonly Regex DrivesRuntime = new(@"Invoke-Bash|BashPath\.ToShell|ToShellPath", RegexOptions.Compiled);

    private static readonly Regex OsCondition = new(
        @"OperatingSystem\.Is|IsWindows|Skip\.If|DirectorySeparatorChar|bool windows|\bwindows:\s*(true|false)\b|Trait\(""Platform""", RegexOptions.Compiled);

    /// <summary>True when <paramref name="methodText"/> asserts Windows-only <c>\</c>→<c>/</c> normalisation without an OS condition.</summary>
    internal static bool AssertsWindowsSeparatorUnconditionally(string methodText) =>
        DrivesRuntime.IsMatch(methodText)
        && BackslashDrivePath.IsMatch(methodText)
        && SlashDrivePath.IsMatch(methodText)
        && !OsCondition.IsMatch(methodText);

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

    [Fact]
    public void TestSources_NoUnconditionalWindowsSeparatorExpectation()
    {
        var offenders = new List<string>();
        var sep = Path.DirectorySeparatorChar;
        foreach (var project in Directory.EnumerateDirectories(Path.Combine(RepoRoot(), "src"), "PsBash.*.Tests"))
        foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
            if (Path.GetFileName(file) == nameof(WindowsPathExpectationGuardTests) + ".cs") continue;

            var lines = File.ReadAllLines(file);
            var starts = new List<int>();
            for (int i = 0; i < lines.Length; i++)
                if (TestStart.IsMatch(lines[i])) starts.Add(i);

            for (int k = 0; k < starts.Count; k++)
            {
                int end = k + 1 < starts.Count ? starts[k + 1] : lines.Length;
                var body = string.Join('\n', lines[starts[k]..end].Where(l =>
                {
                    var t = l.TrimStart();
                    return !t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith('*');
                }));
                if (AssertsWindowsSeparatorUnconditionally(body))
                    offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{starts[k] + 1}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These tests feed a backslash drive path to the runtime and expect the C:/ form with no OS condition. "
            + "`\\` -> `/` happens only on Windows (docs/specs/paths.md); assert the bash result elsewhere, e.g. "
            + "OperatingSystem.IsWindows() ? \"C:/x\" : <bash result>:\n  " + string.Join("\n  ", offenders));
    }

    [Theory]
    // The two Linux failures this guard was written for, as they read before the fix.
    [InlineData("[Fact]\npublic void D() {\n  var lines = RunLines(@\"Invoke-BashDirname 'C:\\Users\\me\\file.txt'\");\n  Assert.Equal(new[] { \"C:/Users/me\" }, lines);\n}", true)]
    [InlineData("[Fact]\npublic void P() {\n  var lines = RunBashText(@\"$global:__PsBashCwd = 'C:\\Users\\me'; Invoke-BashPwd\");\n  Assert.Equal(new[] { \"C:/Users/me\" }, lines);\n}", true)]
    // ... and after it.
    [InlineData("[Fact]\npublic void D() {\n  var lines = RunLines(@\"Invoke-BashDirname 'C:\\Users\\me\\file.txt'\");\n  Assert.Equal(new[] { OperatingSystem.IsWindows() ? \"C:/Users/me\" : \".\" }, lines);\n}", false)]
    // A pure Windows-named function: same result on every OS.
    [InlineData("[Theory]\n[InlineData(@\"C:\\Users\\me/bin:C:/tools\", @\"C:\\Users\\me/bin;C:/tools\")]\npublic void L(string v, string e) => Assert.Equal(e, ExecutableLookup.ToWindowsPathList(v));", false)]
    // The platform passed explicitly (BashPathTests): the same answer on every host.
    [InlineData("[Theory]\n[InlineData(@\"C:\\Users\\x\", \"C:/Users/x\")]\npublic void W(string n, string e) => Assert.Equal(e, BashPath.ToShell(n, windows: true));", false)]
    // Backslash kept verbatim: no separator claim.
    [InlineData("[Fact]\npublic void T() => Run(\"Invoke-BashTest -f 'Z:\\\\nothere.txt'\");", false)]
    public void Guard_FlagsOnlyUnconditionalSeparatorExpectations(string methodText, bool flagged)
        => Assert.Equal(flagged, AssertsWindowsSeparatorUnconditionally(methodText));
}
