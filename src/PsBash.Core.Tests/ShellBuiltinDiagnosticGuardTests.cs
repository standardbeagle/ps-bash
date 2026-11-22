using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// A builtin's own error (<c>s.sh: line 3: cd: /x: No such file or directory</c>, <c>((: 1/0 : division by 0</c>,
/// <c>wait: %5: no such job</c>) carries bash's source prefix built in ONE place, <c>PsBash.Cmdlets.ShellDiagnostic.Prefix</c>
/// (emitted text: <c>PsBuild.DiagnosticPrefix</c> / <c>PsBuild.ShellDiagnosticLine</c>). The sites this replaced each
/// hard-coded <c>bash: </c> or <c>ps-bash: </c> (type, declare, alias, command -V, builtin, help, [, wait, local) or wrote
/// no prefix at all (cd, unalias, <c>[: a: integer expression expected</c>, arithmetic), so none showed the script name
/// or line (docs/specs/error-handling.md "Diagnostic prefix"). A product line that spells such a message names the
/// primitive on the same line. The usage line after <c>invalid option</c> (<c>declare: usage: ...</c>) has no prefix in
/// bash and is not matched. Oracle note (qa-rubric Directive 1): a repo-structure invariant; the behaviour is
/// Differential <c>BuiltinDiagnosticDifferentialTests</c> / <c>ArithDiagnosticDifferentialTests</c>.
/// </summary>
public class ShellBuiltinDiagnosticGuardTests
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

    private const string Builtins = @"(?:cd|type|declare|typeset|local|alias|unalias|wait|command|builtin|help|let|source|test|break|continue|return|exit|shift|history|\[\[?|\(\()";

    /// <summary>A string literal that starts a builtin's error with a hard-coded shell name: <c>"bash: type: </c>.</summary>
    private static readonly Regex HardCoded = new(@"[""'](?:ps-)?bash: " + Builtins + ": ", RegexOptions.Compiled);

    /// <summary>A string literal that starts a builtin's error with no prefix: <c>"cd: x: No such file or directory"</c>.</summary>
    private static readonly Regex Bare = new(
        @"[""'`]" + Builtins + @": [^""'\r\n]*(?:not found|invalid option|too many arguments|not set|No such file|not a |no such job"
        + @"|integer expression|missing|readonly variable|division by|syntax error|numeric argument required|only meaningful in a)",
        RegexOptions.Compiled);

    /// <summary>The same with the operand spliced in: <c>'return: ' + $r + ': numeric argument required'</c>.</summary>
    private static readonly Regex Spliced = new(
        @"[""'`]" + Builtins + @": [""'`][^\r\n]*: (?:numeric argument required|too many arguments)", RegexOptions.Compiled);

    /// <summary>A line that builds the prefix through the primitive.</summary>
    private static readonly Regex Primitive = new(
        @"ShellDiagnostic\b|ShellDiagnostic\.Prefix|PsBuild\.DiagnosticPrefix|ShellDiagnosticLine|PsBuild\.UsageDiscard\(|PsBuild\.AssignmentError\(|\$\{prefix\}",
        RegexOptions.Compiled);

    /// <summary>file name -> why it spells such a message without the prefix.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["AliasExpander.cs"] = "the interactive host's own alias table, outside any runspace (no ShellDiagnostic there)",
        ["HistoryBuiltin.cs"] = "the interactive host's own history store, outside any runspace (no ShellDiagnostic there)",
    };

    internal static bool IsOffender(string code)
        => (HardCoded.IsMatch(code) || Bare.IsMatch(code) || Spliced.IsMatch(code)) && !Primitive.IsMatch(code);

    [Fact]
    public void ProductCode_SpellsABuiltinErrorOnlyAfterTheSharedPrefix()
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
                    if (IsOffender(ShellDiagnosticPrefixGuardTests.CodeOf(lines[i], ext != ".cs")))
                        offenders.Add($"{project}/{name}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A builtin's error starts with bash's source prefix: build it with " +
            "[PsBash.Cmdlets.ShellDiagnostic]::Prefix (C#: ShellDiagnostic.Prefix(SessionState); emitted text: " +
            "PsBuild.ShellDiagnosticLine / PsBuild.DiagnosticPrefix), never a hard-coded 'bash: ' / 'ps-bash: ' or none. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheAllowList_OnlyNamesFilesThatStillExist()
    {
        var root = Path.Combine(RepoRoot(), "src");
        foreach (var name in Allowed.Keys)
            Assert.True(Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).Any(),
                $"Allow-listed file {name} no longer exists; drop it from ShellBuiltinDiagnosticGuardTests.Allowed.");
    }

    [Theory]
    // Spellings this replaced (InvokeBashTypeCommand, PsEmitter cd / builtin / local, psm1 wait, InvokeBashTestIntCommand).
    [InlineData("FileSystemHelpers.WriteBashError(this, $\"bash: type: {name}: not found\");", false, true)]
    [InlineData("return $\"Write-Error 'cd: too many arguments' -ErrorAction Continue; {PsBuild.SetStatus(1)}\";", false, true)]
    [InlineData("return \"Write-Error -Message 'cd: OLDPWD not set' -ErrorAction Continue\";", false, true)]
    [InlineData("PsBuild.StderrLine(\"bash: builtin: \" + named + \": not a shell builtin\")", false, true)]
    [InlineData("parts.Add(PsBuild.StderrLine(\"bash: local: \" + n + \": not found\"));", false, true)]
    [InlineData("Write-BashHostStderr \"bash: wait: ${pidArg}: no such job\"", true, true)]
    [InlineData("FileSystemHelpers.WriteBashError(this, $\"[: {text}: integer expression expected\");", false, true)]
    [InlineData("FileSystemHelpers.WriteBashError(this, $\"unalias: {name}: not found\");", false, true)]
    // The numeric-argument builtins (InvokeBashShiftCommand, PsEmitter break / return / exit, psm1 return, PsBuild).
    [InlineData("FileSystemHelpers.WriteBashError(this, $\"shift: {args[0]}: numeric argument required\");", false, true)]
    [InlineData("FileSystemHelpers.WriteBashError(this, \"shift: too many arguments\");", false, true)]
    [InlineData("Write-Error ('return: ' + $st + ': numeric argument required') -ErrorAction Continue", true, true)]
    [InlineData("\"Write-Error \" + SingleQuote(\"break: only meaningful in a `for' loop\")", false, true)]
    [InlineData("specialResult = $\"Write-Error 'exit: too many arguments' -ErrorAction Continue\";", false, true)]
    [InlineData("FileSystemHelpers.WriteBashError(this, ShellDiagnostic.Prefix(SessionState) + \"shift: too many arguments\");", false, false)]
    // Through the primitive, a usage line, or only quoted in a comment: not a site.
    [InlineData("FileSystemHelpers.WriteBashError(this, ShellDiagnostic.Prefix(SessionState) + $\"type: {name}: not found\");", false, false)]
    [InlineData("return PsBuild.ShellDiagnosticLine(\"'cd: too many arguments'\") + \"; \" + PsBuild.SetStatus(1);", false, false)]
    [InlineData("Write-BashHostStderr ([PsBash.Cmdlets.ShellDiagnostic]::Prefix($global:BASH_SOURCE, $global:BashPositional0) + \"wait: ${n}: no such job\")", true, false)]
    [InlineData("specialResult = PsBuild.UsageDiscard(\"exit: too many arguments\");", false, false)]
    [InlineData("sb.Append(PsBuild.AssignmentError($\"local: {n}: readonly variable\"));", false, false)]
    [InlineData("FileSystemHelpers.WriteBashError(this, \"unalias: usage: unalias [-a] name [name ...]\");", false, false)]
    [InlineData("// bash says \"cd: x: No such file or directory\"", false, false)]
    [InlineData("string report = \"bash: command substitution: line \" + line;", false, false)]
    public void TheGuard_CatchesAHardCodedOrMissingPrefix(string line, bool powerShell, bool offends)
        => Assert.Equal(offends, IsOffender(ShellDiagnosticPrefixGuardTests.CodeOf(line, powerShell)));
}
