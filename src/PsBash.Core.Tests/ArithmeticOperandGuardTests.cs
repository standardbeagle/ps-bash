using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the one-evaluator invariant: every bash arithmetic context (a slice offset or length, an indexed subscript read,
/// written or unset, <c>declare -i</c>, <c>$(( ))</c>, <c>(( ))</c>, <c>let</c>) reaches <c>Invoke-BashArith</c> /
/// <c>BashArith.Evaluate</c>, and the only shortcut is <c>BashArithmeticParser.TryPlainDecimal</c> (its PowerShell twin
/// <c>PlainDecimal</c>, via <c>PsBuild.ArithPlainDecimal</c>).
/// <para>
/// The bug class: each context decided for itself with <c>int.TryParse</c> / <c>[int]</c> / <c>[long]</c> /
/// <c>-match '^-?[0-9]+$'</c>, so <c>${s:010}</c>, <c>${a[$k]}</c> (k=010), <c>a[010]=x</c> and <c>unset 'a[-010]'</c>
/// read 010 as 10 (bash: octal 8), <c>08</c> as 8 (bash: an error), and <c>${a[@]:$j}</c> with j='1+1' as a PowerShell
/// cast that threw. Behavior against bash: <c>Differential.Tests/ArithOneEvaluatorDifferentialTests</c>.
/// </para>
/// <para>Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent, so a source check.</para>
/// </summary>
public class ArithmeticOperandGuardTests
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

    private static readonly Regex s_csParse = new(@"\b(?:int|long)\.(?:Try)?Parse\(\s*([^,)]+)", RegexOptions.CultureInvariant);

    // The C# integer parses in the emitter and the arithmetic cmdlets that read something OTHER than an arithmetic
    // operand, by the text they parse: a redirection fd (319, 6438), break/continue N, return N, exit N, the brace range
    // `{1..5}` items, and test -t's fd. A new parse is either one of these or an operand that belongs to the evaluator.
    private static readonly HashSet<string> s_csInventory = new(StringComparer.Ordinal)
    {
        "t", "countText", "target", "retLiteral", "items[0]", "items[^1]", "items[i]", "exitLiteral", "operand",
    };

    private static readonly Regex s_psCast = new(@"\[(?:int|long)\](?!::)(?=(.{0,40}))", RegexOptions.CultureInvariant);

    // An emitted [int]/[long] cast may only take the evaluator's own value (PsBuild.ArithValue, a subscript's
    // `[long]$(Invoke-BashArith`, ArithCompare's `$__psbash_al` / `$__psbash_ar`) or a number that is not arithmetic:
    // $? for PIPESTATUS and subshell exit, a redirection fd, return's / exit's status, the indirect positional index,
    // the eval depth counter, a boolean status, and the uid.
    private static readonly string[] s_psCastAllowed =
    [
        "$(\" + invocation", "$(Invoke-BashArith", "$__psbash_al", "$__psbash_ar",
        "$global:LASTEXITCODE", "$__psbash_xc", "$__psbFd", "$__psb_r", "((([decimal]$__psb_es", "$__ind",
        "(Get-Variable -Name '__BashEvalDepth'", "(-not (", "[System.Security.Principal", "(id -u)",
    ];

    private static readonly Regex s_psIntTest = new(@"\[(?:int|long)\]::TryParse\(\s*([^,)]+)|(\$\w+) -(?:not)?match '\^-\?\[0-9\]\+\$'", RegexOptions.CultureInvariant);

    // PowerShell integer tests that are not arithmetic: return's status, `$?` restore, the version compare of
    // Compare-BashVersion, and the yaml scalar reader.
    private static readonly HashSet<string> s_psIntTestInventory = new(StringComparer.Ordinal)
    {
        "$__psb_r", "$st", "$lp", "$rp", "$s",
    };

    private static IEnumerable<string> EmitterFiles(string root)
    {
        string parser = Path.Combine(root, "src", "PsBash.Transpiler", "Parser");
        foreach (var f in Directory.EnumerateFiles(parser, "PsEmitter*.cs")) yield return f;
        yield return Path.Combine(parser, "PsBuild.cs");
    }

    // The cmdlets that evaluate arithmetic themselves (the evaluator, a subscript, an element operand).
    private static IEnumerable<string> ArithmeticCmdletFiles(string root)
    {
        string cmdlets = Path.Combine(root, "src", "PsBash.Cmdlets");
        foreach (var f in Directory.EnumerateFiles(cmdlets, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(cmdlets, f);
            if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            string code = File.ReadAllText(f);
            if (Path.GetFileName(f) == "BashArith.cs" || code.Contains("BashArith.Evaluate(", StringComparison.Ordinal)
                || code.Contains("BashArrayElement.Index(", StringComparison.Ordinal)
                || code.Contains("BashArrayElement.ReadOnly(", StringComparison.Ordinal))
                yield return f;
        }
    }

    private static bool IsComment(string line)
    {
        string t = line.TrimStart();
        return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith('#') || t.StartsWith("///", StringComparison.Ordinal);
    }

    [Fact]
    public void NoCSharpIntegerParseOfAnArithmeticOperand()
    {
        string root = RepoRoot();
        var offenders = new List<string>();
        foreach (var file in EmitterFiles(root))
            Scan(file);
        foreach (var file in ArithmeticCmdletFiles(root))
            Scan(file);
        Assert.True(offenders.Count == 0,
            "An arithmetic operand goes to the one evaluator (Invoke-BashArith / BashArith.Evaluate); its only shortcut is "
            + "BashArithmeticParser.TryPlainDecimal. A parse of something else: add its operand to s_csInventory.\n"
            + string.Join('\n', offenders));

        void Scan(string file)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i])) continue;
                foreach (Match m in s_csParse.Matches(lines[i]))
                    if (!s_csInventory.Contains(m.Groups[1].Value.Trim()))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
    }

    [Fact]
    public void NoEmittedIntegerCastOfAnArithmeticOperand()
    {
        string root = RepoRoot();
        var offenders = new List<string>();
        foreach (var file in EmitterFiles(root))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i])) continue;
                foreach (Match m in s_psCast.Matches(lines[i]))
                {
                    string after = m.Groups[1].Value;
                    if (Array.Exists(s_psCastAllowed, a => after.StartsWith(a, StringComparison.Ordinal))) continue;
                    // A C# cast in the emitter's own code (`(int)n`) is not emitted text; `[int]` / `[long]` only is.
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "Emit an arithmetic operand's value as PsBuild.ArithValue(Invoke-BashArith ...) (or a literal that "
            + "BashArithmeticParser.TryPlainDecimal accepted), never a PowerShell [int]/[long] cast of its text:\n"
            + string.Join('\n', offenders));
    }

    [Fact]
    public void NoEmittedOrModuleIntegerTestOfAnArithmeticOperand()
    {
        string root = RepoRoot();
        var files = new List<string>(EmitterFiles(root)) { Path.Combine(root, "src", "PsBash.Module", "PsBash.psm1") };
        var offenders = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i])) continue;
                foreach (Match m in s_psIntTest.Matches(lines[i]))
                {
                    string operand = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                    if (!s_psIntTestInventory.Contains(operand))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "A run-time 'is it a number?' test of an arithmetic operand is PsBuild.ArithPlainDecimal "
            + "([PsBash.Core.Parser.BashArithmeticParser]::PlainDecimal), else Invoke-BashArith:\n"
            + string.Join('\n', offenders));
    }

    [Fact]
    public void TheOneHelperIsUsedByEveryContext()
    {
        string root = RepoRoot();
        string emitter = File.ReadAllText(Path.Combine(root, "src", "PsBash.Transpiler", "Parser", "PsEmitter.cs"));
        string module = File.ReadAllText(Path.Combine(root, "src", "PsBash.Module", "PsBash.psm1"));
        Assert.Contains("BashArithmeticParser.TryPlainDecimal(", emitter, StringComparison.Ordinal);
        Assert.Contains("PsBuild.ArithPlainDecimal(", emitter, StringComparison.Ordinal);
        Assert.Contains("BashArithmeticParser, PsBash.Transpiler]::PlainDecimal(", module, StringComparison.Ordinal);
        foreach (var file in ArithmeticCmdletFiles(root))
        {
            string name = Path.GetFileName(file);
            if (name is "BashArrayElement.cs" or "InvokeBashArithmeticCommand.cs" or "InvokeBashUnsetCommand.cs")
                Assert.Contains("BashArithmeticParser.TryPlainDecimal(", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    /// <summary>The fast path is exactly the canonical decimals; everything else is the evaluator's (bash 5.2 values).</summary>
    [Theory]
    [InlineData("0", true, 0L)]
    [InlineData("7", true, 7L)]
    [InlineData("-12", true, -12L)]
    [InlineData("9223372036854775807", true, long.MaxValue)]
    [InlineData("-9223372036854775808", true, long.MinValue)]
    [InlineData("9223372036854775808", false, 0L)]    // wraps in bash: the evaluator's
    [InlineData("010", false, 0L)]                    // octal 8
    [InlineData("08", false, 0L)]                     // value too great for base
    [InlineData("-010", false, 0L)]                   // -8
    [InlineData("-0", false, 0L)]
    [InlineData("+5", false, 0L)]
    [InlineData(" 5", false, 0L)]
    [InlineData("5 ", false, 0L)]
    [InlineData("", false, 0L)]
    [InlineData("-", false, 0L)]
    [InlineData("1+1", false, 0L)]
    [InlineData("0x10", false, 0L)]
    [InlineData("i", false, 0L)]
    [InlineData("١", false, 0L)]                 // an Arabic-Indic digit is not a bash digit
    public void TryPlainDecimal_AcceptsOnlyCanonicalDecimals(string text, bool plain, long value)
    {
        Assert.Equal(plain, BashArithmeticParser.TryPlainDecimal(text, out long v));
        Assert.Equal(value, v);
        Assert.Equal(plain ? (long?)value : null, BashArithmeticParser.PlainDecimal(text));
    }
}
