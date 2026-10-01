using System.Management.Automation;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// GNU <c>ls</c> LAYOUT: <c>-a</c> lists "." and "..", <c>-R</c> prints "dir:" headers with blank-line
/// separators (depth first), several operands get headers, <c>-l</c>/<c>-s</c> directory sections start
/// with "total N", <c>-s</c>/<c>-i</c> prefix each entry. Expected text is GNU coreutils 9.4
/// (<c>LC_ALL=C ls ...</c> under wsl on the same fixture tree); long lines are normalised to
/// "[blocks ]TYPE SIZE NAME" so owners, dates and column widths (which are not under test) drop out.
/// </summary>
public class LsGnuLayoutTests : IDisposable, IClassFixture<SharedPwshFixture>
{
    private readonly string _tmp;
    private readonly SharedPwshFixture _fixture;

    public LsGnuLayoutTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _tmp = Path.Combine(Path.GetTempPath(), "psb-lsl-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
        Directory.CreateDirectory(P("d/sub/deep"));
        Directory.CreateDirectory(P("e"));
        File.WriteAllText(P("a.txt"), "hello\n");
        File.WriteAllText(P("d/b.txt"), "x\n");
        File.WriteAllBytes(P("d/big.bin"), new byte[5000]);
        File.WriteAllBytes(P("d/sub/empty"), Array.Empty<byte>());
        File.WriteAllText(P("d/sub/deep/q"), "q\n");
        File.WriteAllText(P(".hid"), "h\n");
        File.WriteAllText(P("d/.dh"), "z\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best-effort */ }
    }

    private string P(string rel) => Path.Combine(_tmp, rel);

    private static readonly Regex LongLine = new(
        @"^(?<pre>(?:\d+\s+)*)(?<t>[-dl])[rwxsStT-]{9}\s+\d+\s+\S*\s+\S*\s+(?<sz>\S+)\s+\w{3}\s+\d+\s+[\d:]+\s+(?<n>.*)$",
        RegexOptions.Compiled);

    private string Ls(string args)
    {
        var pwsh = _fixture.AcquireFresh();
        var res = pwsh.AddScript($"Push-Location '{_tmp.Replace("'", "''")}'; try {{ {args} }} finally {{ Pop-Location }}").Invoke();
        pwsh.Commands.Clear();
        var errs = pwsh.Streams.Error.ToArray();
        pwsh.Streams.ClearStreams();
        Assert.True(errs.Length == 0, "unexpected error: " + string.Join("; ", errs.Select(e => e.ToString())));
        var lines = res.Select(o =>
        {
            var bt = o?.Properties["BashText"]?.Value?.ToString();
            var line = bt ?? o?.ToString() ?? "";
            var m = LongLine.Match(line);
            return m.Success
                ? $"{Regex.Replace(m.Groups["pre"].Value.Trim(), @"\s+", " ")}{(m.Groups["pre"].Length > 0 ? " " : "")}{m.Groups["t"].Value} {m.Groups["sz"].Value} {m.Groups["n"].Value}"
                : line;
        });
        return string.Join("\n", lines);
    }

    [Fact]
    public void A_ListsDotAndDotDot() =>
        Assert.Equal(".\n..\n.dh\nb.txt\nbig.bin\nsub", Ls("Invoke-BashLs '-a' d"));

    [Fact]
    public void AlmostAll_HasHiddenButNoDotEntries() =>
        Assert.Equal(".dh\nb.txt\nbig.bin\nsub", Ls("Invoke-BashLs '-A' d"));

    [Fact]
    public void Recursive_PrintsHeadersAndBlankLinesDepthFirst() =>
        Assert.Equal("d:\nb.txt\nbig.bin\nsub\n\nd/sub:\ndeep\nempty\n\nd/sub/deep:\nq",
            Ls("Invoke-BashLs '-R' d"));

    [Fact]
    public void OnePerLineRecursive_SameAsRecursive() =>
        Assert.Equal("d:\nb.txt\nbig.bin\nsub\n\nd/sub:\ndeep\nempty\n\nd/sub/deep:\nq",
            Ls("Invoke-BashLs '-1R' d"));

    [Fact]
    public void Recursive_NoOperand_HeadersStartWithDot() =>
        Assert.Equal(".:\na.txt\nd\ne\n\n./d:\nb.txt\nbig.bin\nsub\n\n./d/sub:\ndeep\nempty\n\n./d/sub/deep:\nq\n\n./e:",
            Ls("Invoke-BashLs '-R'"));

    [Fact]
    public void Recursive_TrailingSlashOperand_NoDoubledSlash() =>
        Assert.StartsWith("d/:\nb.txt\nbig.bin\nsub\n\nd/sub:\n", Ls("Invoke-BashLs '-R' d/"));

    [Fact]
    public void LongRecursive_EachSectionHasTotal() =>
        Assert.Equal(
            "d:\ntotal 16\n- 2 b.txt\n- 5000 big.bin\nd 4096 sub\n\n" +
            "d/sub:\ntotal 4\nd 4096 deep\n- 0 empty\n\n" +
            "d/sub/deep:\ntotal 4\n- 2 q",
            Ls("Invoke-BashLs '-lR' d"));

    [Fact]
    public void LongAll_TotalCountsDotEntries() =>
        Assert.Equal("total 28\nd 4096 .\nd 4096 ..\n- 2 .dh\n- 2 b.txt\n- 5000 big.bin\nd 4096 sub",
            Ls("Invoke-BashLs '-la' d"));

    [Fact]
    public void LongAll_EmptyDir() =>
        Assert.Equal("e:\ntotal 8\nd 4096 .\nd 4096 ..", Ls("Invoke-BashLs '-laR' e"));

    [Fact]
    public void Long_HumanTotal() =>
        Assert.Equal("total 16K\n- 2 b.txt\n- 4.9K big.bin\nd 4.0K sub", Ls("Invoke-BashLs '-lh' d"));

    [Fact]
    public void Size_PrefixesAllocatedBlocksAndTotal() =>
        Assert.Equal("total 16\n4 b.txt\n8 big.bin\n4 sub", Ls("Invoke-BashLs '-s' d"));

    [Fact]
    public void SizeRecursive_PerSectionTotals() =>
        Assert.Equal(
            "d:\ntotal 16\n4 b.txt\n8 big.bin\n4 sub\n\nd/sub:\ntotal 4\n4 deep\n0 empty\n\nd/sub/deep:\ntotal 4\n4 q",
            Ls("Invoke-BashLs '-sR' d"));

    [Fact]
    public void LongSize_BlocksBeforePermissions() =>
        Assert.Equal("total 16\n4 - 2 b.txt\n8 - 5000 big.bin\n4 d 4096 sub", Ls("Invoke-BashLs '-ls' d"));

    [Fact]
    public void TwoDirectories_GetHeadersAndBlankLine() =>
        Assert.Equal("d:\nb.txt\nbig.bin\nsub\n\ne:", Ls("Invoke-BashLs d e"));

    [Fact]
    public void TwoDirectoriesRecursive() =>
        Assert.Equal("d:\nb.txt\nbig.bin\nsub\n\nd/sub:\ndeep\nempty\n\nd/sub/deep:\nq\n\ne:",
            Ls("Invoke-BashLs '-R' d e"));

    [Fact]
    public void FileOperandComesFirst_ThenDirectorySection() =>
        Assert.Equal("a.txt\n\nd:\nb.txt\nbig.bin\nsub", Ls("Invoke-BashLs a.txt d"));

    [Fact]
    public void LongFileAndDirectories() =>
        Assert.Equal("- 6 a.txt\n\nd:\ntotal 16\n- 2 b.txt\n- 5000 big.bin\nd 4096 sub\n\ne:\ntotal 0",
            Ls("Invoke-BashLs '-l' a.txt d e"));

    [Fact]
    public void DirectoryFlag_ListsOperandsThemselves_NoHeaders() =>
        Assert.Equal("d\ne", Ls("Invoke-BashLs '-d' d e"));

    [Fact]
    public void SingleDirectory_NoHeaderNoTotalWithoutLongOrSize() =>
        Assert.Equal("b.txt\nbig.bin\nsub", Ls("Invoke-BashLs d"));

    [Fact]
    public void Inode_IsANumberPerEntry_DistinctForDistinctFiles()
    {
        var lines = Ls("Invoke-BashLs '-i' d").Split('\n');
        Assert.Equal(3, lines.Length);
        var ids = new List<string>();
        foreach (var (line, name) in lines.Zip(new[] { "b.txt", "big.bin", "sub" }))
        {
            var m = Regex.Match(line, @"^\s*(\d+) " + Regex.Escape(name) + "$");
            Assert.True(m.Success, $"'{line}' is not '<inode> {name}'");
            ids.Add(m.Groups[1].Value);
        }
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public void Inode_SameNumberInLongListing()
    {
        var plain = Ls("Invoke-BashLs '-i' d").Split('\n')[0].Trim().Split(' ')[0];
        var raw = _fixture.AcquireFresh().AddScript(
            $"Push-Location '{_tmp.Replace("'", "''")}'; try {{ Invoke-BashLs '-li' d }} finally {{ Pop-Location }}").Invoke();
        Assert.StartsWith(plain + " ", raw[1].Properties["BashText"].Value!.ToString()!.TrimStart());
    }
}
