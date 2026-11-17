using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// A "command not found" report carries bash's source prefix (<c>s.sh: line 3: x: command not found</c>, under
/// <c>bash -c</c> <c>bash: line 1: ...</c>) built in ONE place, <c>PsBash.Cmdlets.ShellDiagnostic.Prefix</c>
/// (emitted text: <c>PsBuild.DiagnosticPrefix</c> / <c>PsBuild.ShellDiagnosticLine</c>). The four sites that wrote it
/// each hard-coded <c>bash: </c> or <c>ps-bash: </c>, so none showed the script name or line
/// (docs/specs/error-handling.md "Diagnostic prefix"). A product line that spells the message must name the
/// primitive; <c>CommandNotFoundReport.cs</c> builds the reason after the prefix.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant; the behaviour is Differential
/// <c>ShellDiagnosticLineDifferentialTests</c>.
/// </summary>
public class ShellDiagnosticPrefixGuardTests
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

    private static readonly string[] ProductDirs =
        ["PsBash.Cmdlets", "PsBash.Transpiler", "PsBash.Host", "PsBash.Core", "PsBash.Shell", "PsBash.Module"];

    /// <summary>The message inside a string literal (a comment that quotes it is not a site).</summary>
    private static readonly Regex Message = new(@"[""'][^""'\r\n]*command not found", RegexOptions.Compiled);

    /// <summary>A line that builds the prefix through the primitive (PowerShell: the handler's <c>$prefix</c> from it).</summary>
    private static readonly Regex Primitive = new(
        @"ShellDiagnostic\b|ShellDiagnostic\.Prefix|PsBuild\.DiagnosticPrefix|ShellDiagnosticLine|\$\{prefix\}",
        RegexOptions.Compiled);

    /// <summary>file name -> why it spells the message without the prefix.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["CommandNotFoundReport.cs"] = "the reason after the prefix (NAME: command not found / No such file ...)",
    };

    /// <summary>The code part of a line: a trailing <c>// ...</c> or <c> # ...</c> comment dropped, a comment line empty.</summary>
    internal static string CodeOf(string line, bool powerShell)
    {
        var t = line.TrimStart();
        if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("#", StringComparison.Ordinal)
            || t.StartsWith("*", StringComparison.Ordinal) || t.StartsWith("<#", StringComparison.Ordinal)) return "";
        var trailing = powerShell ? Regex.Match(line, @"\s#\s") : Regex.Match(line, @"\s//\s");
        return trailing.Success ? line[..trailing.Index] : line;
    }

    internal static bool IsOffender(string code) => Message.IsMatch(code) && !Primitive.IsMatch(code);

    [Fact]
    public void ProductCode_SpellsCommandNotFoundOnlyAfterTheSharedPrefix()
    {
        var offenders = new List<string>();
        foreach (var project in ProductDirs)
        {
            var dir = Path.Combine(RepoRoot(), "src", project);
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                var ext = Path.GetExtension(file);
                if (ext is not (".cs" or ".ps1" or ".psm1")) continue;
                var name = Path.GetFileName(file);
                if (Allowed.ContainsKey(name)) continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    if (IsOffender(CodeOf(lines[i], ext != ".cs")))
                        offenders.Add($"{project}/{name}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A command-not-found report starts with bash's source prefix: build it with " +
            "[PsBash.Cmdlets.ShellDiagnostic]::Prefix (C#: ShellDiagnostic.Prefix(SessionState); emitted text: " +
            "PsBuild.ShellDiagnosticLine / PsBuild.DiagnosticPrefix), never a hard-coded 'bash: ' or 'ps-bash: '. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheAllowList_OnlyNamesFilesThatStillExist()
    {
        var root = Path.Combine(RepoRoot(), "src");
        foreach (var name in Allowed.Keys)
            Assert.True(Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).Any(),
                $"Allow-listed file {name} no longer exists; drop it from ShellDiagnosticPrefixGuardTests.Allowed.");
    }

    [Theory]
    // The four spellings this replaced (psm1 Resolve-BashCommand x2, InvokeBashCommandCommand, PsEmitter's empty word).
    [InlineData("Write-Error -Message \"bash: ${Name}: command not found\" -Category ObjectNotFound", true, true)]
    [InlineData("FileSystemHelpers.WriteBashError(this, $\"bash: {name}: command not found\");", false, true)]
    [InlineData("return \"Write-Error -Message 'ps-bash: : command not found' -ErrorAction Continue\";", false, true)]
    [InlineData("    $msg = \"bash: ${name}: command not found\"", true, true)]
    // Through the primitive, or only quoted in a comment: not a site.
    [InlineData("FileSystemHelpers.WriteBashError(this, ShellDiagnostic.Prefix(SessionState) + $\"{name}: command not found\");", false, false)]
    [InlineData("return PsBuild.ShellDiagnosticLine(\"': command not found'\");", false, false)]
    [InlineData("    $msg = \"${prefix}${name}: command not found\"", true, false)]
    [InlineData("} catch { }   # keep \"command not found\"", true, false)]
    [InlineData("x = 1; // bash says \"x: command not found\"", false, false)]
    [InlineData("/// bash's \"x: command not found\"", false, false)]
    public void TheGuard_CatchesAHardCodedPrefix(string line, bool powerShell, bool offends)
        => Assert.Equal(offends, IsOffender(CodeOf(line, powerShell)));
}
