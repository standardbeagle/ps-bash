using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the run-time array element invariant: a cmdlet that holds a <c>NAME[subscript]</c> from an operand or a value
/// (<c>printf -v 'm[$k]'</c>, <c>unset 'm[$k]'</c>, <c>x='a[i]'; $(( x = 1 ))</c>) resolves and writes it through
/// <c>PsBash.Cmdlets.BashArrayElement</c>, never with its own copy of the rules.
/// <para>
/// The bug class: three cmdlets re-derived them, and <c>printf -v</c>'s copy never expanded <c>$k</c>, so
/// <c>printf -v 'm[$k]'</c> added a key named <c>$k</c> (oils <c>array-assoc</c> spec, Windows fleet s22a). The marker
/// of a copy is the ad-hoc <c>$k</c> / <c>${k}</c> replacement regex. Behavior against bash:
/// <c>ArraySubscriptWordTests.RunTimeElementOperand_ResolvesLikeBash</c>.
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check.</para>
/// </summary>
public class ArrayElementGuardTests
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

    private const string KeyExpansionRegex = @"\$\{?(\w+)\}?";

    [Fact]
    public void OnlyBashArrayElementExpandsARunTimeSubscriptsVariables()
    {
        string root = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file);
            if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.GetFileName(file) == "BashArrayElement.cs") continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains(KeyExpansionRegex, StringComparison.Ordinal)
                    && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                    offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0,
            "Resolve a run-time NAME[subscript] through BashArrayElement (AssocKey / Index / Write), not a local copy:\n"
            + string.Join('\n', offenders));
        Assert.Contains(KeyExpansionRegex, File.ReadAllText(Path.Combine(root, "BashArrayElement.cs")), StringComparison.Ordinal);
    }

    /// <summary>
    /// A builtin that evaluates an element operand's subscript itself (<c>BashArrayElement.Index</c> / <c>Write</c>:
    /// <c>unset 'a[1/0]'</c>, <c>test -v 'a[1/0]'</c>) hands an arithmetic error to <c>BashArrayElement.FailSubscript</c>,
    /// bash's expansion error: the line is dropped (a <c>bash -c</c> shell ends). The bug class: each builtin caught it
    /// its own way, as a plain failed command (status 1, the script went on) or not at all (a PowerShell error).
    /// Behavior against bash: <c>ShellLanguageGapDifferentialTests</c> element-operand theories. The arithmetic command
    /// is exempt: it marks the error <c>InSubscript</c> and rethrows to its own handler, the same expansion error.
    /// </summary>
    [Fact]
    public void ABuiltinEvaluatingASubscript_FailsItThroughFailSubscript()
    {
        string root = Path.Combine(RepoRoot(), "src", "PsBash.Cmdlets");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file);
            if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            if (EvaluatesWithoutFailSubscript(Path.GetFileName(file), File.ReadAllText(file))) offenders.Add(rel);
        }
        Assert.True(offenders.Count == 0,
            "Catch BashArith.BashArithException from BashArrayElement.Index / Write and call BashArrayElement.FailSubscript:\n"
            + string.Join('\n', offenders));
    }

    private static readonly System.Text.RegularExpressions.Regex s_evaluates =
        new(@"BashArrayElement\.(Index|Write)\(", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool EvaluatesWithoutFailSubscript(string fileName, string code)
        => fileName is not ("BashArrayElement.cs" or "InvokeBashArithmeticCommand.cs")
           && s_evaluates.IsMatch(code)
           && !code.Contains("BashArrayElement.FailSubscript(", StringComparison.Ordinal);

    [Theory]
    [InlineData("InvokeBashFoo.cs", "try { i = BashArrayElement.Index(s, n, v); } catch (BashArith.BashArithException) { return false; }", true)]
    [InlineData("InvokeBashFoo.cs", "BashArrayElement.Write(SessionState, n, s, e, v, vars);", true)]
    [InlineData("InvokeBashFoo.cs", "try { i = BashArrayElement.Index(s, n, v); } catch (BashArith.BashArithException ex) { BashArrayElement.FailSubscript(this, s, ex); }", false)]
    [InlineData("InvokeBashFoo.cs", "BashArrayElement.Assign(this, target, value);", false)]
    [InlineData("InvokeBashArithmeticCommand.cs", "try { BashArrayElement.Write(SessionState, n, s, e, v, vars); } catch (BashArith.BashArithException ex) { ex.InSubscript = true; throw; }", false)]
    public void EvaluatesWithoutFailSubscript_FlagsAnUnroutedSubscriptError(string file, string code, bool flagged)
        => Assert.Equal(flagged, EvaluatesWithoutFailSubscript(file, code));
}
