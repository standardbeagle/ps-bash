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

    // ---- group 1: transformers (fold/expand/unexpand/paste/strings/base64), GNU oracle ----
    // Bytes captured from GNU in WSL Ubuntu 24.04: `printf 'b\na' | CMD | od -c`.

    [Theory]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashFold '-w1'", "b\na")]
    [InlineData("Invoke-BashPrintf 'abc\\nd' | Invoke-BashFold '-w2'", "ab\nc\nd")]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashExpand", "b\na")]
    [InlineData("Invoke-BashPrintf 'b\\n\\ta' | Invoke-BashExpand", "b\n        a")]
    [InlineData("Invoke-BashPrintf 'b\\n        a' | Invoke-BashUnexpand", "b\n\ta")]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashPaste '-s'", "b\ta\n")]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashPaste '-sd,'", "b,a\n")]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashPaste - -", "b\ta\n")]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashPaste -", "b\na\n")]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashStrings '-n1'", "b\na\n")]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashBase64", "Ygph\n")]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashBase64 '-w0'", "Ygph")]
    [InlineData("Invoke-BashPrintf 'Ygph' | Invoke-BashBase64 '-d'", "b\na")]
    public void Group1Transformer_StdinRendersGnuBytes(string script, string expected)
    {
        Assert.Equal(expected, Render(Run(script)));
    }

    [Fact]
    public void Group1_FileOperandsRenderGnuBytes()
    {
        var u1 = Path.Combine(_dir, "u1"); var u2 = Path.Combine(_dir, "u2");
        var j1 = Path.Combine(_dir, "j1"); var j2 = Path.Combine(_dir, "j2");
        File.WriteAllText(u1, "a\nb"); File.WriteAllText(u2, "a\nc");
        File.WriteAllText(j1, "a 1\nb 2"); File.WriteAllText(j2, "a x\nb y");
        Assert.Equal("a\nb", Render(Run($"Invoke-BashFold '-w1' {Q(u1)}")));
        Assert.Equal("a\nb", Render(Run($"Invoke-BashExpand {Q(u1)}")));
        Assert.Equal("a\tb\n", Render(Run($"Invoke-BashPaste '-s' {Q(u1)}")));
        Assert.Equal("a\ta\nb\tc\n", Render(Run($"Invoke-BashPaste {Q(u1)} {Q(u2)}")));
        Assert.Equal("\t\ta\nb\n\tc\n", Render(Run($"Invoke-BashComm {Q(u1)} {Q(u2)}")));
        Assert.Equal("c\n", Render(Run($"Invoke-BashComm '-13' {Q(u1)} {Q(u2)}")));
        Assert.Equal("a 1 x\nb 2 y\n", Render(Run($"Invoke-BashJoin {Q(j1)} {Q(j2)}")));
    }

    [Fact]
    public void Group1_Split_PartFilesMatchGnu()
    {
        var prefix = Path.Combine(_dir, "spl_");
        Run($"Invoke-BashPrintf 'a b\\nc d' | Invoke-BashSplit '-l1' - {Q(prefix)}");
        Assert.Equal("a b\n", File.ReadAllText(prefix + "aa"));
        Assert.Equal("c d", File.ReadAllText(prefix + "ab"));
    }

    // ---- group 2: column / xargs / jq / yq / diff, GNU oracle ----------------------------

    [Theory]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashColumn '-t'", "b\na\n")]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashXargs", "b a\n")]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashXargs echo", "b a\n")]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashXargs '-n1' echo", "b\na\n")]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashXargs '-I{}' echo '{}'", "b\na\n")]
    [InlineData("Invoke-BashPrintf '{\"a\":1}' | Invoke-BashJq '.'", "{\n  \"a\": 1\n}\n")]
    [InlineData("Invoke-BashPrintf '{\"a\":1}' | Invoke-BashJq '-r' '.a'", "1\n")]
    [InlineData("Invoke-BashPrintf '{\"a\":\"x\"}' | Invoke-BashJq '-j' '.a'", "x")]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashJq '-R' '.'", "\"b\"\n\"a\"\n")]
    [InlineData("Invoke-BashPrintf 'b\\na' | Invoke-BashJq '-Rr' '.'", "b\na\n")]
    public void Group2_StdinRendersGnuBytes(string script, string expected)
    {
        Assert.Equal(expected, Render(Run(script)));
    }

    [Fact]
    public void Group2_Diff_NoNewlineAtEndOfFile_MatchesGnu()
    {
        var u1 = Path.Combine(_dir, "d1"); var u2 = Path.Combine(_dir, "d2");
        File.WriteAllText(u1, "a\nb"); File.WriteAllText(u2, "a\nc");
        Assert.Equal(
            "2c2\n< b\n\\ No newline at end of file\n---\n> c\n\\ No newline at end of file\n",
            Render(Run($"Invoke-BashDiff {Q(u1)} {Q(u2)}")));
    }

    [Fact]
    public void Group2_DiffUnified_NoNewlineAtEndOfFile_MatchesGnu()
    {
        var u1 = Path.Combine(_dir, "e1"); var u2 = Path.Combine(_dir, "e2");
        File.WriteAllText(u1, "a\nb"); File.WriteAllText(u2, "a\nc");
        // GNU `diff -u a b | tail -n +3` (the two header lines carry timestamps).
        var body = string.Concat(Render(Run($"Invoke-BashDiff '-u' {Q(u1)} {Q(u2)}"))
            .Split('\n').Skip(2).Select(l => l + "\n")).TrimEnd('\n') + "\n";
        Assert.Equal(
            "@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+c\n\\ No newline at end of file\n", body);
    }

    [Fact]
    public void Group2_Diff_SameTextTerminatedVsNot_Differs()
    {
        // GNU: `printf 'a\nb' > x; printf 'a\nb\n' > y; diff x y` reports 2c2 (exit 1).
        var u1 = Path.Combine(_dir, "f1"); var u2 = Path.Combine(_dir, "f2");
        File.WriteAllText(u1, "a\nb"); File.WriteAllText(u2, "a\nb\n");
        Assert.Equal(
            "2c2\n< b\n\\ No newline at end of file\n---\n> b\n",
            Render(Run($"Invoke-BashDiff {Q(u1)} {Q(u2)}")));
    }

    [Fact]
    public void Group2_Yq_MatchesJqTerminators()
    {
        Assert.Equal("1\n", Render(Run("Invoke-BashPrintf 'a:\\n  b: 1\\n' | Invoke-BashYq '.a.b'")));
        Assert.Equal("1\n", Render(Run("Invoke-BashPrintf 'a:\\n  b: 1' | Invoke-BashYq '.a.b'")));
    }

    [Fact]
    public void Group2_Transformers_EmitTextNotLsEntry()
    {
        foreach (var cmd in new[] { "Invoke-BashColumn '-t'", "Invoke-BashXargs echo", "Invoke-BashJq '-R' '.'" })
        {
            var records = Run(LsIn(cmd));
            Assert.NotEmpty(records);
            Assert.All(records, r => Assert.DoesNotContain("PsBash.LsEntry", r.TypeNames));
        }
    }

    [Fact]
    public void Group1_Transformers_EmitTextNotLsEntry()
    {
        foreach (var cmd in new[] { "Invoke-BashFold '-w3'", "Invoke-BashExpand", "Invoke-BashUnexpand",
                                    "Invoke-BashPaste -", "Invoke-BashStrings '-n1'" })
        {
            var records = Run(LsIn(cmd));
            Assert.NotEmpty(records);
            Assert.All(records, r => Assert.DoesNotContain("PsBash.LsEntry", r.TypeNames));
        }
    }
}