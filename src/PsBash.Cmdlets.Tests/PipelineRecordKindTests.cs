using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The pipeline record-kind rule (docs/specs/runtime-functions.md "Pipeline record kinds"):
/// <list type="bullet">
/// <item><b>Filters</b> — a command whose output line IS one of its input lines (grep plain,
/// head, tail, sort, uniq plain, tac, shuf, cat with no flags, tee, less, more) — pass the
/// ORIGINAL upstream object through, so <c>ls | grep .txt</c> still yields
/// <c>PsBash.LsEntry</c>.</item>
/// <item><b>Transformers</b> — a command or mode that changes the text of a line (sed, tr, cut,
/// awk, rev, nl, grep -o/-n/-H, uniq -c, cat -n, head -c / tail -c) — emit FRESH text objects:
/// the upstream typed object no longer describes the line.</item>
/// </list>
/// Either way the RENDERED BYTES must match GNU. Every expected-bytes value below was captured
/// from GNU coreutils/sed/grep in WSL Ubuntu 24.04 with <c>printf 'b\na' | cmd | od -c</c>
/// (an UNTERMINATED last line); the terminator rules differ per command — head/tail/cat/tee/rev/tr
/// copy the missing newline through, grep/sort/uniq/tac/cut/nl/awk always terminate — which is
/// exactly what a stale <c>NoTrailingNewline</c> flag on a passed-through object gets wrong.
/// </summary>
public class PipelineRecordKindTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _dir;

    public PipelineRecordKindTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "psbash-reckind-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x\n");
        File.WriteAllText(Path.Combine(_dir, "b.log"), "y\n");
        File.WriteAllText(Path.Combine(_dir, "c.txt"), "z\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private System.Collections.ObjectModel.Collection<PSObject> Run(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result;
    }

    /// <summary>The bytes the host serializer writes for these records.</summary>
    private static string Render(IEnumerable<PSObject> records)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var o in records)
        {
            string text = BashRuntime.GetBashText(o);
            bool noNl = o.Properties["NoTrailingNewline"]?.Value is true;
            sb.Append(text);
            if (!noNl && !text.EndsWith('\n')) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Q(string path) => "'" + path.Replace("'", "''") + "'";

    // ---- byte fidelity: unterminated last line, GNU oracle -----------------------------

    [Theory]
    // copies the missing final newline through (byte-stream tools)
    [InlineData("Invoke-BashHead '-n5'", "b\na")]
    [InlineData("Invoke-BashTail '-n5'", "b\na")]
    [InlineData("Invoke-BashCat", "b\na")]
    [InlineData("Invoke-BashRev", "b\na")]
    [InlineData("Invoke-BashTr a-z A-Z", "B\nA")]
    [InlineData("Invoke-BashHead '-c3'", "b\na")]
    [InlineData("Invoke-BashTail '-c3'", "b\na")]
    [InlineData("Invoke-BashCat '-n'", "     1\tb\n     2\ta")]
    [InlineData("Invoke-BashLess", "b\na")]
    [InlineData("Invoke-BashMore", "b\na")]
    [InlineData("Invoke-BashSed p", "b\nb\na\na")]
    // always terminate the last record
    [InlineData("Invoke-BashGrep b", "b\n")]
    [InlineData("Invoke-BashGrep '-v' z", "b\na\n")]
    [InlineData("Invoke-BashSort", "a\nb\n")]
    [InlineData("Invoke-BashSort '-r'", "b\na\n")]
    [InlineData("Invoke-BashUniq", "b\na\n")]
    [InlineData("Invoke-BashTac", "ab\n")]
    [InlineData("Invoke-BashCut '-c1'", "b\na\n")]
    [InlineData("Invoke-BashNl", "     1\tb\n     2\ta\n")]
    [InlineData("Invoke-BashAwk 1", "b\na\n")]
    public void UnterminatedLastLine_RendersGnuBytes(string command, string expected)
    {
        var records = Run($"Invoke-BashPrintf 'b\\na' | {command}");
        Assert.Equal(expected, Render(records));
    }

    [Theory]
    // GNU: printf 'abcdef\n' | head -c3 -> "abc" (a byte slice adds NO record boundary)
    [InlineData("Invoke-BashPrintf 'abcdef\\n' | Invoke-BashHead '-c3'", "abc")]
    // GNU: printf 'abcdef\n' | tail -c3 -> "ef\n" (the slice ends on the source's own newline)
    [InlineData("Invoke-BashPrintf 'abcdef\\n' | Invoke-BashTail '-c3'", "ef\n")]
    public void ByteSlice_RendersExactlyTheSlice(string script, string expected)
    {
        Assert.Equal(expected, Render(Run(script)));
    }

    [Fact]
    public void Tee_UnterminatedLastLine_RendersGnuBytes()
    {
        var file = Path.Combine(_dir, "tee.out");
        var records = Run($"Invoke-BashPrintf 'b\\na' | Invoke-BashTee {Q(file)}");
        Assert.Equal("b\na", Render(records));
        Assert.Equal("b\na", File.ReadAllText(file));
    }

    [Fact]
    public void Shuf_UnterminatedSingleLine_IsTerminated()
    {
        // GNU: printf a | shuf -> "a\n"
        Assert.Equal("a\n", Render(Run("Invoke-BashPrintf 'a' | Invoke-BashShuf")));
    }

    [Fact]
    public void Sort_MovesUnterminatedRecordOffTheEnd_TerminatesIt()
    {
        // printf 'b\na' | sort: the flagged 'a' record sorts FIRST — a passed-through
        // NoTrailingNewline object there glued the lines (ab\n).
        Assert.Equal("a\nb\n", Render(Run("Invoke-BashPrintf 'b\\na' | Invoke-BashSort")));
    }

    // ---- filters keep the upstream object ----------------------------------------------

    private string LsIn(string command) =>
        $"Invoke-BashLs {Q(_dir)} | {command}";

    [Theory]
    [InlineData("Invoke-BashGrep txt")]
    [InlineData("Invoke-BashGrep '-v' zzz")]
    [InlineData("Invoke-BashHead '-n2'")]
    [InlineData("Invoke-BashTail '-n2'")]
    [InlineData("Invoke-BashSort")]
    [InlineData("Invoke-BashUniq")]
    [InlineData("Invoke-BashTac")]
    [InlineData("Invoke-BashCat")]
    [InlineData("Invoke-BashLess")]
    public void Filter_KeepsTheUpstreamLsEntry(string command)
    {
        var records = Run(LsIn(command));
        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.Contains("PsBash.LsEntry", r.TypeNames));
    }

    [Fact]
    public void Tee_KeepsTheUpstreamLsEntry()
    {
        var records = Run(LsIn($"Invoke-BashTee {Q(Path.Combine(_dir, "t.out"))}"));
        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.Contains("PsBash.LsEntry", r.TypeNames));
    }

    [Fact]
    public void Shuf_KeepsTheUpstreamLsEntry()
    {
        var records = Run(LsIn("Invoke-BashShuf"));
        Assert.NotEmpty(records);
        Assert.All(records, r => Assert.Contains("PsBash.LsEntry", r.TypeNames));
    }

    // ---- transformers emit text --------------------------------------------------------

    [Theory]
    [InlineData("Invoke-BashSed 's/a/A/'")]
    [InlineData("Invoke-BashTr a-z A-Z")]
    [InlineData("Invoke-BashCut '-c1-3'")]
    [InlineData("Invoke-BashAwk 1")]
    [InlineData("Invoke-BashRev")]
    [InlineData("Invoke-BashNl")]
    [InlineData("Invoke-BashGrep '-o' txt")]
    [InlineData("Invoke-BashGrep '-n' txt")]
    [InlineData("Invoke-BashUniq '-c'")]
    [InlineData("Invoke-BashCat '-n'")]
    [InlineData("Invoke-BashHead '-c5'")]
    [InlineData("Invoke-BashTail '-c5'")]
    public void Transformer_EmitsTextNotTheUpstreamLsEntry(string command)
    {
        var records = Run(LsIn(command));
        Assert.NotEmpty(records);
        Assert.All(records, r =>
        {
            Assert.DoesNotContain("PsBash.LsEntry", r.TypeNames);
            Assert.False(string.IsNullOrEmpty(BashRuntime.GetBashText(r)));
        });
    }

    [Fact]
    public void Transformer_TextIsTheTransformedLine()
    {
        var text = Render(Run(LsIn("Invoke-BashCut '-c1-2'")));
        Assert.Equal("a.\nb.\nc.\n", text);
    }
}
