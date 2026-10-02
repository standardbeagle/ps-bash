using System.Management.Automation;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// GNU <c>cat -A -e -t -v --show-all --show-nonprinting</c>. Expected text is GNU coreutils 9.4 (<c>wsl</c>)
/// on the fixture bytes below: a tab, a CR before the LF, SOH, DEL, a UTF-8 "é" (C3 A9), the invalid
/// bytes E9 FF 89, form feed, US, and a last line with no newline.
/// </summary>
public class CatNonprintingTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    private static readonly byte[] Fixture =
    {
        (byte)'a', 9, (byte)'b', 13, 10,
        1, 127, 0xC3, 0xA9, 0xE9, 0xFF, 0x89, 10,
        12, 31, (byte)' ', (byte)'x',
    };

    public CatNonprintingTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), "psb-catv-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
        File.WriteAllBytes(Path.Combine(_tmp, "f"), Fixture);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Bytes the command writes: every record's BashText plus a newline unless it is exact.</summary>
    private string Bytes(string args)
    {
        var pwsh = _fixture.AcquireFresh();
        var res = pwsh.AddScript($"Push-Location '{_tmp.Replace("'", "''")}'; try {{ {args} }} finally {{ Pop-Location }}").Invoke();
        pwsh.Commands.Clear();
        var errs = pwsh.Streams.Error.ToArray();
        pwsh.Streams.ClearStreams();
        Assert.True(errs.Length == 0, "unexpected error: " + string.Join("; ", errs.Select(e => e.ToString())));
        var sb = new System.Text.StringBuilder();
        foreach (var o in res)
        {
            var bt = o?.Properties["BashText"]?.Value?.ToString() ?? o?.ToString() ?? "";
            bool exact = o?.Properties["NoTrailingNewline"]?.Value is true;
            sb.Append(bt.TrimEnd('\n'));
            if (!exact) sb.Append('\n');
        }
        return sb.ToString();
    }

    private const string Body = "^A^?M-CM-)M-iM-^?M-^I";

    [Fact]
    public void V_ControlAsCaret_HighBytesAsMeta_TabAndNewlineUntouched() =>
        Assert.Equal("a\tb^M\n" + Body + "\n^L^_ x", Bytes("Invoke-BashCat '-v' f"));

    [Fact]
    public void ShowNonprinting_Long_SameAsV() =>
        Assert.Equal(Bytes("Invoke-BashCat '-v' f"), Bytes("Invoke-BashCat '--show-nonprinting' f"));

    [Fact]
    public void A_IsVET_AndLastLineWithoutNewlineGetsNoDollar() =>
        Assert.Equal("a^Ib^M$\n" + Body + "$\n^L^_ x", Bytes("Invoke-BashCat '-A' f"));

    [Fact]
    public void ShowAll_Long_SameAsA() =>
        Assert.Equal(Bytes("Invoke-BashCat '-A' f"), Bytes("Invoke-BashCat '--show-all' f"));

    [Fact]
    public void E_IsVE_TabsStayTabs() =>
        Assert.Equal("a\tb^M$\n" + Body + "$\n^L^_ x", Bytes("Invoke-BashCat '-e' f"));

    [Fact]
    public void T_IsVT_NoDollar() =>
        Assert.Equal("a^Ib^M\n" + Body + "\n^L^_ x", Bytes("Invoke-BashCat '-t' f"));

    [Fact]
    public void Bundle_nvb_NumbersNonBlankAndShows() =>
        Assert.Equal("     1\ta\tb^M\n     2\t" + Body + "\n     3\t^L^_ x", Bytes("Invoke-BashCat '-vb' f"));

    [Fact]
    public void V_OnPipelineRecords_UsesTheSameRendering()
    {
        // printf '\001\n\200' | cat -v  ==  "^A\nM-^@" (the last record has no newline)
        Assert.Equal("^A\nM-^@",
            Bytes("Invoke-BashPrintf '\\001\\n\\200' | Invoke-BashCat '-v'"));
    }

    [Fact]
    public void ScanArgs_AcceptsTheShowOptions()
    {
        foreach (var a in new[] { "-A", "-e", "-t", "-v", "--show-all", "--show-nonprinting", "--show-n" })
            Assert.Null(InvokeBashCatCommand.ScanArgs(new[] { a }).Error);
        Assert.True(InvokeBashCatCommand.Plan(new[] { "-A" }).ShowNonprinting);
        Assert.True(InvokeBashCatCommand.Plan(new[] { "-A" }).ShowEnds);
        Assert.True(InvokeBashCatCommand.Plan(new[] { "-A" }).ShowTabs);
        Assert.False(InvokeBashCatCommand.Plan(new[] { "-e" }).ShowTabs);
        Assert.False(InvokeBashCatCommand.Plan(new[] { "-t" }).ShowEnds);
    }
}
