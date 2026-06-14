using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Regression tests for <c>Invoke-BashAwk</c> file mode (psm1).
///
/// HEADLINE REGRESSION (Directive 13): the arg loop assigned the first non-flag
/// token to the program and DROPPED every later operand, so file mode did not
/// exist — <c>awk '{print $1}' data.txt</c> read nothing and exited 0 with no
/// output, while docs claimed File=Yes. The loop now collects operands; with no
/// <c>-f</c> the first is the program and the rest are input files, and the data
/// is read through the streaming <c>Read-BashFileLines</c> primitive.
///
/// Oracle: GNU awk. These are hand-asserted (M5 in-process cmdlet surface);
/// byte-level bash parity for the same scripts lives in the differential suite.
/// </summary>
public class InvokeBashAwkFileModeTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _tmpDir;

    public InvokeBashAwkFileModeTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmpDir = Path.Combine(Path.GetTempPath(), "psbash-awk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string Q(string path) => "'" + path.Replace("'", "''") + "'";

    private string Mk(string name, string content)
    {
        var p = Path.Combine(_tmpDir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private string[] RunBashText(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        var result = pwsh.AddScript(script).Invoke();
        pwsh.Commands.Clear();
        return result
            .Select(o =>
            {
                var prop = o?.Properties["BashText"];
                return (prop != null ? prop.Value?.ToString() ?? "" : o?.ToString() ?? "")
                    .TrimEnd('\n', '\r');
            })
            .ToArray();
    }

    private string[] RunErrors(string script)
    {
        var pwsh = _fixture.AcquireFresh();
        pwsh.AddScript("$ErrorActionPreference='Continue'").Invoke();
        pwsh.Commands.Clear();
        pwsh.AddScript(script).Invoke();
        var errs = pwsh.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString()).ToArray();
        pwsh.Commands.Clear();
        return errs;
    }

    [Fact]
    public void Awk_SingleFileOperand_PrintsField()
    {
        var f = Mk("data.txt", "alpha 1\nbeta 2\ngamma 3\n");

        var lines = RunBashText($"Invoke-BashAwk '{{print $1}}' {Q(f)}");

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, lines);
    }

    [Fact]
    public void Awk_FileOperand_NotPipeline_NeverSilentlyEmpty()
    {
        // The exact regression shape: a program + file, NO pipeline input.
        var f = Mk("nums.txt", "10\n20\n30\n");

        var lines = RunBashText($"Invoke-BashAwk '{{print $1 * 2}}' {Q(f)}");

        Assert.Equal(new[] { "20", "40", "60" }, lines);
    }

    [Fact]
    public void Awk_MultipleFiles_ConcatenatedWithCumulativeNR()
    {
        var a = Mk("a.txt", "x\ny\n");
        var b = Mk("b.txt", "z\n");

        // NR is cumulative across files in awk: 1,2,3.
        var lines = RunBashText($"Invoke-BashAwk '{{print NR, $1}}' {Q(a)} {Q(b)}");

        Assert.Equal(new[] { "1 x", "2 y", "3 z" }, lines);
    }

    [Fact]
    public void Awk_FieldSeparator_WithFile()
    {
        var f = Mk("csv.txt", "a,b,c\nd,e,f\n");

        // '-F,' is single-quoted exactly as PsEmitter's passthrough quoting
        // delivers it (a bare -F, would be split by PowerShell's comma operator).
        var lines = RunBashText($"Invoke-BashAwk '-F,' '{{print $2}}' {Q(f)}");

        Assert.Equal(new[] { "b", "e" }, lines);
    }

    [Fact]
    public void Awk_ProgramFile_WithDataFile()
    {
        var prog = Mk("prog.awk", "{ print $1 }\n");
        var data = Mk("data.txt", "one two\nthree four\n");

        // With -f, the program comes from a file and ALL operands are data files.
        var lines = RunBashText($"Invoke-BashAwk -f {Q(prog)} {Q(data)}");

        Assert.Equal(new[] { "one", "three" }, lines);
    }

    [Fact]
    public void Awk_MissingFile_EmitsError_NotSilentSuccess()
    {
        var missing = Path.Combine(_tmpDir, "does-not-exist.txt");

        var errs = RunErrors($"Invoke-BashAwk '{{print}}' {Q(missing)}");

        Assert.NotEmpty(errs);
        Assert.Contains(errs, m =>
            m.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("cannot open", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("does-not-exist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Awk_EndBlock_RunsWithFileInput_NRReflectsFileLines()
    {
        var f = Mk("count.txt", "a\nb\nc\nd\n");

        var lines = RunBashText($"Invoke-BashAwk 'END {{ print NR }}' {Q(f)}");

        Assert.Equal(new[] { "4" }, lines);
    }

    [Fact]
    public void Awk_PipelineStillWorks_WhenNoFileOperand()
    {
        // Guard against the file-mode change breaking stdin mode.
        var lines = RunBashText("\"p q`nr s\" | Invoke-BashAwk '{print $2}'");
        Assert.Equal(new[] { "q", "s" }, lines);
    }

    [Fact]
    public void Awk_PipelineOfSeparateObjects_OneRecordEach_NotConcatenated()
    {
        // ls / find / grep emit one typed object per line whose BashText has NO
        // trailing newline. Each object must be its own awk record — regression:
        // a streaming reader that joined on \n concatenated them into one record
        // (`ls | awk '{print $NF}'` printed all names run together).
        var lines = RunBashText("@('alpha','beta','gamma') | Invoke-BashAwk '{print $1}'");
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, lines);
    }

    [Fact]
    public void Awk_PipelineOfSeparateObjects_NRCountsEachObject()
    {
        var lines = RunBashText("@('a','b','c','d') | Invoke-BashAwk 'END{print NR}'");
        Assert.Equal(new[] { "4" }, lines);
    }
}
