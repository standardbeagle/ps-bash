using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests;

/// <summary>
/// Guards the one-evaluator invariant: every bash arithmetic context (a slice offset or length, an indexed subscript read,
/// written or unset, an indexed array literal's <c>[N]=</c>, <c>declare -i</c>, <c>$(( ))</c>, <c>(( ))</c>, <c>let</c>)
/// reaches <c>Invoke-BashArith</c> / <c>BashArith.Evaluate</c>, and the only shortcut is
/// <c>BashArithmeticParser.TryPlainDecimal</c> (its PowerShell twin <c>PlainDecimal</c>, via <c>PsBuild.ArithPlainDecimal</c>).
/// <para>
/// The bug class: each context decided for itself with <c>int.TryParse</c> / <c>[int]</c> / <c>[long]</c> /
/// <c>-match '^-?[0-9]+$'</c>, so <c>${s:010}</c>, <c>${a[$k]}</c> (k=010), <c>a[010]=x</c>, <c>a=([010]=x)</c> and
/// <c>unset 'a[-010]'</c> read 010 as 10 (bash: octal 8), <c>08</c> as 8 (bash: an error), and <c>${a[@]:$j}</c> with
/// j='1+1' as a PowerShell cast that threw. Behavior against bash: <c>Differential.Tests/ArithOneEvaluatorDifferentialTests</c>.
/// </para>
/// <para>
/// Each scan matches every spelling of an integer read (C#: <c>int</c>/<c>long</c>/<c>Int32</c>/<c>Int64</c>/<c>double</c>…
/// <c>.Parse</c>/<c>.TryParse</c>, also split across lines, <c>Convert.ToInt*</c>, <c>LanguagePrimitives.ConvertTo</c>;
/// PowerShell: <c>[int]</c>/<c>[long]</c>/<c>[int32]</c>/<c>[int64]</c> casts and <c>-as</c>, <c>[T]::TryParse</c>,
/// <c>-match '^[0-9]+$'</c>/<c>'^\d+$'</c>/<c>'^-?\d+$'</c>). What is NOT an arithmetic operand (an fd, an exit status, a
/// job number) is inventoried by file and line context with the reason, so a new read is a decision, not a default.
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

    /// <summary>An allowed integer read: the file, a piece of the line it is on, and why it is not an arithmetic operand.</summary>
    public readonly record struct Site(string File, string Context, string Why);

    private const string IntTypes = @"int|long|int16|int32|int64|uint16|uint32|uint64";

    // ---- C#: a number parsed from text -------------------------------------------------------------------------------

    private static readonly Regex s_csParse = new(
        @"\b(?:int|long|short|uint|ulong|Int16|Int32|Int64|UInt16|UInt32|UInt64|double|decimal|BigInteger)\s*\.\s*(?:Try)?Parse\s*\("
        + @"|\bConvert\s*\.\s*To(?:Int16|Int32|Int64|UInt16|UInt32|UInt64|Double|Decimal)\s*\("
        + @"|\bLanguagePrimitives\s*\.\s*ConvertTo\b",
        RegexOptions.CultureInvariant);

    private static readonly Site[] s_csInventory =
    [
        new("PsEmitter.cs", "int.TryParse(t, out int n) && n >= 3", "a redirection's fd number"),
        new("PsEmitter.cs", "int.TryParse(target, out var n) && n >= 3", "a duplicated fd number (>&N)"),
        new("PsEmitter.cs", "int.TryParse(t.AsSpan(0, t.Length - 1)", "a moved fd number (>&M-), digits only"),
        new("PsEmitter.cs", "int.TryParse(countText,","break / continue N (a loop count, not arithmetic in bash)"),
        new("PsEmitter.cs", "long.TryParse(retLiteral,", "return N (a status)"),
        new("PsEmitter.cs", "long.TryParse(exitLiteral,", "exit N (a status)"),
        new("PsEmitter.cs", "long.TryParse(items[0]", "a brace range {1..5}: decimal by bash's rules"),
        new("PsEmitter.cs", "long.TryParse(items[^1]", "a brace range {1..5}: decimal by bash's rules"),
        new("PsEmitter.cs", "long.TryParse(items[i]", "a brace range {1..5}: decimal by bash's rules"),
        new("InvokeBashTestCommand.cs", "int.TryParse(operand, out int fd)", "test -t FD (an fd number)"),
        new("BashSparseArray.cs", "long.TryParse(Convert.ToString(subscriptValuePairs[i]",
            "a=([N]=v) keys: the emitter passes a plain decimal or the evaluator's value (TryEmitIndexedLiteral)"),
        new("BashSparseArray.cs", "string s when long.TryParse(s, out var p)",
            "PowerShell's indexer key: emitted subscripts are evaluated (or plain decimals) before they index"),
    ];

    // ---- PowerShell: an integer cast of text ------------------------------------------------------------------------

    // Not after a word character (`HashSet[int]` is a type argument) and not a static member (`[int]::MaxValue`).
    private static readonly Regex s_psCast = new(@"(?<![\w.])\[(?:" + IntTypes + @")\](?!::)(?=(.{0,40}))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // The evaluator's own value: PsBuild.ArithValue (`[long]$(" + invocation`) and a subscript's `[long]$(Invoke-BashArith`.
    private static readonly string[] s_evaluatorValue = ["$(\" + invocation", "$(Invoke-BashArith"];

    private static readonly Site[] s_emittedCastInventory =
    [
        new("PsEmitter.cs", "$global:PIPESTATUS = @([int]$global:LASTEXITCODE)", "PIPESTATUS from a status"),
        new("PsEmitter.cs", "Write-BashFd ([int]$__psbFd)", "a redirection's fd number"),
        new("PsEmitter.cs", "Read-BashFd ([int]$__psbFd)", "a redirection's fd number"),
        new("PsEmitter.cs", "([long]$__psb_r % 256)", "return's status, after its decimal test"),
        new("PsEmitter.cs", "[int]$__ind", "${!n} with n a positional number (decimal, not arithmetic)"),
        new("PsBuild.cs", "BashSubshellExitException]::new([int]$__psbash_xc)", "a subshell's exit status"),
        new("PsBuild.cs", "BashSubshellExitException]::new([int]$global:LASTEXITCODE)", "a subshell's exit status"),
        new("PsBuild.cs", "[long]$__psbash_al", "ArithCompare's left operand: an Invoke-BashArith value"),
        new("PsBuild.cs", "[long]$__psbash_ar", "ArithCompare's right operand: an Invoke-BashArith value"),
        new("PsBuild.cs", "[int](-not (", "a boolean as a status"),
        new("PsBuild.cs", "[int]((([decimal]$__psb_es", "an exit status wrapped to 0-255"),
        new("PsBuild.cs", "$__psbash_eval_depth = [int](Get-Variable", "the eval depth counter"),
        new("PsBuild.cs", "[long][System.Security.Principal.WindowsIdentity]", "UID / EUID"),
    ];

    private static readonly Site[] s_moduleCastInventory =
    [
        new("PsBash.psm1", "[int]$n -eq 0", "${!n} with n a positional number (decimal, not arithmetic)"),
        new("PsBash.psm1", "[int]$n -le $Pos.Count", "${!n} with n a positional number (decimal, not arithmetic)"),
        new("PsBash.psm1", "$i = [long]$iv", "the indirect element's subscript: an Invoke-BashArith value"),
        new("PsBash.psm1", "'EPOCHREALTIME'", "the clock"),
        new("PsBash.psm1", "WindowsIdentity]::GetCurrent().User.Value", "UID / EUID"),
        new("PsBash.psm1", "$code = [int]$c", "a character's code point"),
        new("PsBash.psm1", "(([long]$st % 256) + 256) % 256", "a builtin's return status, after its decimal test"),
        new("PsBash.psm1", "[int]$__psbashIdx", "Set-BashElement's index: PlainDecimal or an Invoke-BashArith value"),
        new("PsBash.psm1", "$mode = [int]$Item.UnixFileMode", "a file mode"),
        new("PsBash.psm1", "[long]$Item.Length", "a file size"),
        new("PsBash.psm1", "$idx = [int]$inner", "a jq .[N] path index (Resolve-JqDotPath)"),
        new("PsBash.psm1", "$longVal = [long]0", "the yaml scalar reader"),
        new("PsBash.psm1", "([int]$selfSignal)", "a job's signal number"),
        new("PsBash.psm1", "[int]$Job.Status.Code", "a job's exit status"),
        new("PsBash.psm1", "return [int]$Job.JobNumber", "a job number"),
        new("PsBash.psm1", "$want = [int]$num", "a %N job spec"),
        new("PsBash.psm1", "$wantId = [int]$Spec", "a job id"),
        new("PsBash.psm1", "BashSignalDescriptions[[int]$sig]", "a signal number"),
        new("PsBash.psm1", "WaitOne([int]$left)", "a timeout in ms"),
        new("PsBash.psm1", "[int]$done.ExitCode", "a job's exit status"),
        new("PsBash.psm1", "[int]$job.ExitCode", "a job's exit status"),
        new("PsBash.psm1", "[int]$target.ExitCode", "a job's exit status"),
    ];

    // ---- PowerShell: "is it a number?" -------------------------------------------------------------------------------

    private static readonly Regex s_psIntTest = new(
        @"\[(?:" + IntTypes + @"|double|decimal)\]::TryParse\("
        + @"|-(?:c|i)?(?:not)?match\s+(['""])\^(?:\\?-\?)?(?:\[0-9\]|\\{1,2}d)\+\$\1",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Site[] s_psIntTestInventory =
    [
        new("PsEmitter.cs", "$__psbFd -match '^[0-9]+$'", "a redirection's fd number"),
        new("PsEmitter.cs", "$__psb_r -match '^-?[0-9]+$'", "return's status"),
        new("PsEmitter.cs", "$__ind -match '^[0-9]+$'", "${!n} with n a positional number (decimal, not arithmetic)"),
        new("PsBash.psm1", "$n -match '^[0-9]+$'", "${!n} with n a positional number (decimal, not arithmetic)"),
        new("PsBash.psm1", "$st -notmatch '^-?[0-9]+$'", "a builtin's return status"),
        new("PsBash.psm1", "[int]::TryParse($lp", "Compare-Version"),
        new("PsBash.psm1", "[int]::TryParse($rp", "Compare-Version"),
        new("PsBash.psm1", "[double]::TryParse($trimmed", "ConvertFrom-HumanNumeric (sort -h)"),
        new("PsBash.psm1", "$s -match '^\\-?\\d+$'", "the yaml scalar reader"),
        new("PsBash.psm1", "[long]::TryParse($s,", "the yaml scalar reader"),
        new("PsBash.psm1", "[double]::TryParse($s,", "the yaml scalar reader"),
        new("PsBash.psm1", "$num -match '^\\d+$'", "a %N job spec"),
        new("PsBash.psm1", "$Spec -match '^\\d+$'", "a job id"),
        new("PsBash.psm1", "$pidArg -notmatch '^\\d+$'", "kill's pid operand"),
        new("PsBash.psm1", "$spec -match '^\\d+$'", "fg / bg's job number"),
    ];

    // A parameter declaration is a type constraint, not a cast of data: `param(... [int]$Line = 0)`, a function's inline
    // parameter list, or one declaration on its own line (`[Parameter(Mandatory)][int]$Index,`) with a literal default.
    private static readonly Regex s_ownLineParam = new(
        @"^\s*(?:\[[A-Za-z]+(?:\([^)]*\))?\]\s*)*\[(?:" + IntTypes + @")\]\$\w+\s*(?:=\s*(?:-?\d+|\$true|\$false|\$null|'[^']*'))?\s*,?\s*\)?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex s_functionHeader = new(@"^\s*function\s+[\w-]+\s*\(", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static bool IsModuleTypeConstraint(string line, int column)
    {
        string before = line[..column];
        return before.EndsWith("-is ", StringComparison.OrdinalIgnoreCase)
            || before.EndsWith("-isnot ", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[OutputType(", StringComparison.OrdinalIgnoreCase)
            || line.Contains("param(", StringComparison.OrdinalIgnoreCase)
            || s_functionHeader.IsMatch(line)
            || s_ownLineParam.IsMatch(line);
    }

    // ---- files -------------------------------------------------------------------------------------------------------

    private static IEnumerable<string> EmitterFiles(string root)
    {
        string parser = Path.Combine(root, "src", "PsBash.Transpiler", "Parser");
        foreach (var f in Directory.EnumerateFiles(parser, "PsEmitter*.cs")) yield return f;
        yield return Path.Combine(parser, "PsBuild.cs");
    }

    private static string ModuleFile(string root) => Path.Combine(root, "src", "PsBash.Module", "PsBash.psm1");

    // The cmdlets that evaluate arithmetic or hold its operands: the evaluator, whatever evaluates a subscript or element
    // operand, and let / declare / the sparse array.
    private static IEnumerable<string> ArithmeticCmdletFiles(string root)
    {
        string cmdlets = Path.Combine(root, "src", "PsBash.Cmdlets");
        string[] always = ["BashArith.cs", "InvokeBashLetCommand.cs", "InvokeBashDeclareCommand.cs", "BashSparseArray.cs"];
        foreach (var f in Directory.EnumerateFiles(cmdlets, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(cmdlets, f);
            if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            string code = File.ReadAllText(f);
            if (Array.IndexOf(always, Path.GetFileName(f)) >= 0 || code.Contains("BashArith.Evaluate(", StringComparison.Ordinal)
                || code.Contains("BashArrayElement.Index(", StringComparison.Ordinal)
                || code.Contains("BashArrayElement.ReadOnly(", StringComparison.Ordinal))
                yield return f;
        }
    }

    // ---- the scanner -------------------------------------------------------------------------------------------------

    /// <summary>The file's lines with comment lines blanked (C# <c>//</c>, PowerShell <c>#</c> and <c>&lt;# … #&gt;</c>).</summary>
    private static string[] CodeLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        bool inBlock = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].TrimStart();
            if (inBlock)
            {
                if (t.Contains("#>", StringComparison.Ordinal)) inBlock = false;
                lines[i] = "";
            }
            else if (t.StartsWith("<#", StringComparison.Ordinal))
            {
                inBlock = !t.Contains("#>", StringComparison.Ordinal);
                lines[i] = "";
            }
            else if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith('#'))
                lines[i] = "";
        }
        return lines;
    }

    /// <summary>
    /// Every match of <paramref name="pattern"/> in <paramref name="text"/> (matched across line breaks), minus those
    /// <paramref name="skip"/> drops and those an inventory <see cref="Site"/> of the same file covers (recorded in
    /// <paramref name="used"/>): the offenders, as <c>file:line: text</c>.
    /// </summary>
    public static List<string> Scan(string fileName, string text, Regex pattern, IReadOnlyList<Site> inventory, ISet<Site> used,
        Func<string, int, Match, bool>? skip = null)
    {
        var offenders = new List<string>();
        string[] lines = CodeLines(text);
        var starts = new int[lines.Length];
        for (int i = 1; i < lines.Length; i++) starts[i] = starts[i - 1] + lines[i - 1].Length + 1;
        string joined = string.Join('\n', lines);
        foreach (Match m in pattern.Matches(joined))
        {
            int lineNo = Array.BinarySearch(starts, m.Index);
            if (lineNo < 0) lineNo = ~lineNo - 1;
            string line = lines[lineNo];
            if (skip?.Invoke(line, m.Index - starts[lineNo], m) == true) continue;
            int hit = -1;
            for (int s = 0; s < inventory.Count && hit < 0; s++)
                if (inventory[s].File == fileName && line.Contains(inventory[s].Context, StringComparison.Ordinal)) hit = s;
            if (hit >= 0) { used.Add(inventory[hit]); continue; }
            offenders.Add($"{fileName}:{lineNo + 1}: {line.Trim()}");
        }
        return offenders;
    }

    private static void AssertClean(List<string> offenders, IReadOnlyList<Site> inventory, ISet<Site> used, string advice)
    {
        Assert.True(offenders.Count == 0, advice + "\n" + string.Join('\n', offenders));
        var stale = inventory.Where(s => !used.Contains(s)).Select(s => $"{s.File}: {s.Context}").ToList();
        Assert.True(stale.Count == 0, "Inventory entries that match nothing any more (remove them):\n" + string.Join('\n', stale));
    }

    // ---- the guards --------------------------------------------------------------------------------------------------

    [Fact]
    public void NoCSharpIntegerParseOfAnArithmeticOperand()
    {
        string root = RepoRoot();
        var used = new HashSet<Site>();
        var offenders = new List<string>();
        foreach (var file in EmitterFiles(root).Concat(ArithmeticCmdletFiles(root)))
            offenders.AddRange(Scan(Path.GetFileName(file), File.ReadAllText(file), s_csParse, s_csInventory, used));
        AssertClean(offenders, s_csInventory, used,
            "An arithmetic operand goes to the one evaluator (Invoke-BashArith / BashArith.Evaluate); its only shortcut is "
            + "BashArithmeticParser.TryPlainDecimal. A parse of something else: add a Site (file, line context, why) to s_csInventory.");
    }

    [Fact]
    public void NoEmittedIntegerCastOfAnArithmeticOperand()
    {
        string root = RepoRoot();
        var used = new HashSet<Site>();
        var offenders = new List<string>();
        foreach (var file in EmitterFiles(root))
            offenders.AddRange(Scan(Path.GetFileName(file), File.ReadAllText(file), s_psCast, s_emittedCastInventory, used,
                (_, _, m) => Array.Exists(s_evaluatorValue, a => m.Groups[1].Value.StartsWith(a, StringComparison.Ordinal))));
        AssertClean(offenders, s_emittedCastInventory, used,
            "Emit an arithmetic operand's value as PsBuild.ArithValue / ArithPlainOrValue (or a literal that "
            + "BashArithmeticParser.TryPlainDecimal accepted), never a PowerShell integer cast of its text. Something else: a Site.");
    }

    [Fact]
    public void NoModuleIntegerCastOfAnArithmeticOperand()
    {
        string root = RepoRoot();
        var used = new HashSet<Site>();
        var offenders = Scan("PsBash.psm1", File.ReadAllText(ModuleFile(root)), s_psCast, s_moduleCastInventory, used,
            (line, column, m) => IsModuleTypeConstraint(line, column)
                                 || Array.Exists(s_evaluatorValue, a => m.Groups[1].Value.StartsWith(a, StringComparison.Ordinal)));
        AssertClean(offenders, s_moduleCastInventory, used,
            "An arithmetic operand in the module goes through [BashArithmeticParser]::PlainDecimal, else Invoke-BashArith; "
            + "never an [int]/[long]/[int64] cast or -as of its text. Something else: a Site in s_moduleCastInventory.");
    }

    [Fact]
    public void NoEmittedOrModuleIntegerTestOfAnArithmeticOperand()
    {
        string root = RepoRoot();
        var used = new HashSet<Site>();
        var offenders = new List<string>();
        foreach (var file in EmitterFiles(root).Append(ModuleFile(root)))
            offenders.AddRange(Scan(Path.GetFileName(file), File.ReadAllText(file), s_psIntTest, s_psIntTestInventory, used));
        AssertClean(offenders, s_psIntTestInventory, used,
            "A run-time 'is it a number?' test of an arithmetic operand is PsBuild.ArithPlainDecimal "
            + "([PsBash.Core.Parser.BashArithmeticParser]::PlainDecimal), else Invoke-BashArith. Something else: a Site.");
    }

    /// <summary>The scanner itself: each spelling of an integer read is caught, a site covers only its own file and line.</summary>
    [Theory]
    [InlineData("long\n            .TryParse(x, out var n)", "C#", true)]
    [InlineData("Int64.Parse(x)", "C#", true)]
    [InlineData("var n = Convert.ToInt32(text);", "C#", true)]
    [InlineData("LanguagePrimitives.ConvertTo<long>(v)", "C#", true)]
    [InlineData("double.TryParse(s, out var d)", "C#", true)]
    [InlineData("// int.Parse(x) in a comment", "C#", false)]
    [InlineData("$i = [int64]$v", "PS-cast", true)]
    [InlineData("$i = $v -as [long]", "PS-cast", true)]
    [InlineData("$i = [Int32]$v", "PS-cast", true)]
    [InlineData("if ($v -is [int]) { }", "PS-cast", false)]
    [InlineData("$h = [System.Collections.Generic.HashSet[int]]::new()", "PS-cast", false)]
    [InlineData("[int]$Index = 0,", "PS-cast", false)]
    [InlineData("[int]$Index = $v", "PS-cast", true)]
    [InlineData("if ($v -match '^\\d+$') { }", "PS-test", true)]
    [InlineData("if ($v -match '^-?\\d+$') { }", "PS-test", true)]
    [InlineData("if ($v -notmatch '^[0-9]+$') { }", "PS-test", true)]
    [InlineData("if ([int64]::TryParse($v, [ref]$n)) { }", "PS-test", true)]
    public void Scanner_CatchesEverySpelling(string code, string kind, bool caught)
    {
        Regex pattern = kind switch { "C#" => s_csParse, "PS-cast" => s_psCast, _ => s_psIntTest };
        Func<string, int, Match, bool>? skip = kind == "PS-cast" ? (line, column, _) => IsModuleTypeConstraint(line, column) : null;
        var offenders = Scan("X.cs", code, pattern, Array.Empty<Site>(), new HashSet<Site>(), skip);
        Assert.Equal(caught, offenders.Count > 0);
    }

    [Fact]
    public void Scanner_SiteCoversOnlyItsOwnFileAndLine()
    {
        var site = new Site("A.cs", "int.TryParse(fd,", "an fd");
        var used = new HashSet<Site>();
        Assert.Empty(Scan("A.cs", "x = int.TryParse(fd, out var n);", s_csParse, [site], used));
        Assert.Single(Scan("B.cs", "x = int.TryParse(fd, out var n);", s_csParse, [site], used));
        Assert.Single(Scan("A.cs", "x = int.TryParse(index, out var n);", s_csParse, [site], used));
    }

    [Fact]
    public void TheOneHelperIsUsedByEveryContext()
    {
        string root = RepoRoot();
        string emitter = File.ReadAllText(Path.Combine(root, "src", "PsBash.Transpiler", "Parser", "PsEmitter.cs"));
        string module = File.ReadAllText(ModuleFile(root));
        Assert.Contains("BashArithmeticParser.TryPlainDecimal(", emitter, StringComparison.Ordinal);
        Assert.Contains("PsBuild.ArithPlainOrValue(", emitter, StringComparison.Ordinal);
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
