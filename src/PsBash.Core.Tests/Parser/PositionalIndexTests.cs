using System.Text.RegularExpressions;
using PsBash.Core.Parser;
using Xunit;

namespace PsBash.Core.Tests.Parser;

/// <summary>
/// A positional's digits are script text and may exceed any integer: <see cref="PositionalIndex"/> saturates them (an
/// index no parameter list reaches, so the positional is unset, as in bash). A bare <c>int.Parse</c> threw
/// <see cref="OverflowException"/> out of the emitter (<c>local -n z=$1</c> called as <c>h "${99999999999999999999}"</c>).
/// The behavior against bash is in Differential.Tests/HugePositionalDifferentialTests.
/// </summary>
public class PositionalIndexTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    [InlineData("0", 0)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("2147483648", int.MaxValue)]
    [InlineData("99999999999999999999", int.MaxValue)]
    public void TryParse_DigitString_SaturatesAtIntMax(string name, int expected)
    {
        Assert.True(PositionalIndex.TryParse(name, out int index));
        Assert.Equal(expected, index);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("@")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1a")]
    [InlineData("١")] // Arabic-Indic digit one: not an ASCII digit
    public void TryParse_NotAllAsciiDigits_IsNoPositional(string? name)
        => Assert.False(PositionalIndex.TryParse(name, out _));

    // The function's nameref target is a forwarded positional past int range: was OverflowException.
    [Fact]
    public void Transpile_NamerefTargetForwardedAsHugeBracedPositional_DoesNotThrow()
    {
        var ps = PsEmitter.Transpile("h() { local -n z=$1; echo \"c[$z]\"; }; g() { h \"${99999999999999999999}\"; }; g a");
        Assert.NotNull(ps);
    }

    [Fact]
    public void Transpile_NamerefFromHugeBracedPositional_DoesNotThrow()
    {
        var ps = PsEmitter.Transpile("f() { local -n q=${99999999999999999999}; echo \"b[$q]\"; }; f x");
        Assert.NotNull(ps);
    }

    [Theory]
    [InlineData("echo \"[${99999999999999999999}]\"")]
    [InlineData("echo $(( ${99999999999999999999} + 1 ))")]
    [InlineData("f() { echo \"${2147483648}\"; }; f a")]
    public void Transpile_HugeBracedPositional_DoesNotThrow(string bash)
        => Assert.NotNull(PsEmitter.Transpile(bash));
}

/// <summary>
/// Guard: the transpiler's parser and emitter read script text only, so no throwing integer parse may appear there
/// (<c>int.Parse</c>, <c>long.Parse</c>, <c>Convert.ToInt32</c>, ...). A positional goes through
/// <see cref="PositionalIndex"/>; any other number uses <c>TryParse</c> with a defined result for an out-of-range value.
/// Oracle note (qa-rubric Directive 1): a repo-structure invariant with no bash equivalent.
/// </summary>
public class ScriptDigitParseGuardTests
{
    private static readonly Regex ThrowingParse = new(
        @"\b(?:int|long|short|uint|ulong|ushort|byte|Int32|Int64)\.Parse\(|\bConvert\.To(?:U?Int(?:16|32|64)|Byte)\(",
        RegexOptions.Compiled);

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

    [Fact]
    public void TranspilerParser_HasNoThrowingIntegerParse()
    {
        var dir = Path.Combine(RepoRoot(), "src", "PsBash.Transpiler", "Parser");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal)) continue;
                if (ThrowingParse.IsMatch(lines[i])) offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {code}");
            }
        }
        Assert.True(offenders.Count == 0,
            "A throwing integer parse on script text (OverflowException ends the transpile); use PositionalIndex.TryParse or a TryParse with a defined out-of-range result:\n"
            + string.Join("\n", offenders));
    }
}
