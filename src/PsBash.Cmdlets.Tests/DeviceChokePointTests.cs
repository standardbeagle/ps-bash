using PsBash.Core;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// A <c>/dev/*</c> device name never reaches the filesystem (docs/specs/paths.md "Device names").
/// REGRESSION: <c>echo {v}&gt;/dev/stdout</c> (the fd table) opened <c>C:\dev\stdout</c> - "No such file or directory" on a
/// machine with no <c>C:\dev</c> (CI), and a junk file plus lost output on a machine with one, so the result depended on
/// the machine. These tests do not: the cmdlets run on a PSDrive whose root is a temp directory - the "drive root"
/// <c>/dev/x</c> resolves against - once with no <c>dev</c> folder and once with one, and nothing may appear in it;
/// and the two file choke points (<see cref="RedirectFileWriter.OpenFile"/>, <see cref="BashFileSystem.OpenRead"/>) are
/// asked directly, in every spelling a path mapper can produce, and must never hand back a file.
/// Oracle note (qa-rubric Directive 1): what each device does is compared with bash by DeviceNameDifferentialTests; the
/// "no file is touched" half has no bash equivalent (bash has a real /dev).
/// </summary>
public class DeviceChokePointTests : IClassFixture<SharedPwshFixture>, IDisposable
{
    private readonly SharedPwshFixture _fixture;
    private readonly string _root;

    public DeviceChokePointTests(SharedPwshFixture fixture)
    {
        _fixture = fixture;
        _root = Path.Combine(Path.GetTempPath(), $"psb-dev-{Guid.NewGuid():N}".Substring(0, 20));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        BashFdTable.CloseAll();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private static readonly string[] Names =
        ["null", "stdin", "stdout", "stderr", "fd/0", "fd/1", "fd/2", "fd/7", "full", "zero", "random", "urandom", "tty"];

    /// <summary>Every spelling of every device name a site can be handed on this OS.</summary>
    public static IEnumerable<object[]> Spellings()
    {
        foreach (var name in Names)
        {
            yield return ["/dev/" + name];
            if (!OperatingSystem.IsWindows()) continue;
            string drive = Path.GetPathRoot(Environment.CurrentDirectory)!;       // C:\
            yield return [drive + @"dev\" + name.Replace('/', '\\')];             // GetUnresolvedProviderPathFromPSPath
            yield return [drive.Replace('\\', '/') + "dev/" + name];              // BashPath.ToShell of it
            yield return [Path.GetFullPath("/dev/" + name)];                      // .NET's own resolution
        }
    }

    /// <summary>The <c>dev</c> directory of the drive the process is on: what a device name opened as a file would hit.</summary>
    private static string Snapshot()
    {
        string dev = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "dev");
        if (!OperatingSystem.IsWindows() || !Directory.Exists(dev)) return "<none>";
        return string.Join("|", Directory.EnumerateFileSystemEntries(dev).Order(StringComparer.Ordinal).Select(e =>
            $"{Path.GetFileName(e)}:{File.GetLastWriteTimeUtc(e).Ticks}:{(File.Exists(e) ? new FileInfo(e).Length : -1)}"));
    }

    /// <summary>The kinds served by a device of the OS itself: <c>NUL</c> / the real <c>/dev/*</c>, and the console for <c>/dev/tty</c>.</summary>
    private static bool IsOsDevice(string path)
    {
        var kind = DevicePath.Classify(path);
        return kind == DeviceKind.Tty || DevicePath.NativeDevice(kind) is not null;
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public void WriteChokePoint_NeverOpensADeviceNameAsAFile(string path)
    {
        Assert.NotEqual(DeviceKind.None, DevicePath.Classify(path));
        string before = Snapshot();
        foreach (bool append in new[] { false, true })
        {
            try
            {
                using var writer = RedirectFileWriter.OpenFile(path, append);
                // Only the OS's own device may be a FileStream (NUL, Linux /dev/full, the console; never a path under a drive).
                if (writer.FilePath is { } file)
                    Assert.True(IsOsDevice(path), $"{path} was opened as the file {file}");
            }
            catch (IOException)
            {
                // A stream device with no owning command, an unopened /dev/fd/7, no console for /dev/tty: a failed open,
                // which a site reports as an error. Never a file.
            }
        }
        Assert.Equal(before, Snapshot());
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public void ReadChokePoint_NeverOpensADeviceNameAsAFile(string path)
    {
        string before = Snapshot();
        try
        {
            using var stream = BashFileSystem.OpenRead(path);
            if (stream is FileStream fs)
                Assert.True(IsOsDevice(path), $"{path} was opened as the file {fs.Name}");
        }
        catch (IOException)
        {
            // /dev/stdin (the cmdlet's pipeline input, not a file), an unopened /dev/fd/7, no console for /dev/tty.
        }
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void WriteChokePoint_StreamDeviceWithoutAnOwner_IsAFailedOpen()
    {
        var ex = Assert.Throws<DeviceStreamException>(() => RedirectFileWriter.OpenFile("/dev/stdout", append: false));
        Assert.Contains("/dev/stdout", ex.Message);
        Assert.Throws<DeviceStreamException>(() => RedirectFileWriter.OpenText("/dev/stderr", append: true));
    }

    [Fact]
    public void ReadChokePoint_ZeroAndRandom_AreEndlessAndNullIsEmpty()
    {
        var buffer = new byte[64];
        Array.Fill(buffer, (byte)7);
        using (var zero = BashFileSystem.OpenRead("/dev/zero")) zero.ReadExactly(buffer);
        Assert.All(buffer, b => Assert.Equal(0, b));
        using (var random = BashFileSystem.OpenRead("/dev/urandom")) random.ReadExactly(buffer);
        Assert.Contains(buffer, b => b != 0);
        using var none = BashFileSystem.OpenRead("/dev/null");
        Assert.Equal(0, none.Read(buffer, 0, buffer.Length));
        Assert.Empty(BashFileSystem.ReadAllBytes("/dev/null"));
    }

    [Fact]
    public void WriteChokePoint_FullFailsEveryWrite_AndNullDiscards()
    {
        using (var sink = RedirectFileWriter.OpenFile("/dev/null", append: false)) sink.Write("x"u8.ToArray());
        Assert.ThrowsAny<IOException>(() =>
        {
            using var full = RedirectFileWriter.OpenFile("/dev/full", append: false);
            full.Write("x"u8.ToArray());
            full.Flush();
        });
    }

    // The exact failure: BashFdTable.Open got the provider-resolved spelling and opened it as a file.
    [Theory]
    [InlineData("/dev/stdout", 1)]
    [InlineData("/dev/stderr", 2)]
    [InlineData("/dev/fd/1", 1)]
    public void FdTable_StreamDevice_IsADupOfThatDescriptor(string name, int std)
    {
        string before = Snapshot();
        foreach (var path in OperatingSystem.IsWindows()
                     ? new[] { name, Path.GetFullPath(name) }
                     : new[] { name })
        {
            Assert.Null(BashFdTable.Open(10, path, ">"));
            Assert.Equal(std, BashFdTable.Write(10, "x\n"));   // "send it down the shell's own stream", as after `exec 10>&1`
            Assert.True(BashFdTable.Close(10));
        }
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void FdTable_NullDevice_OpensForWritingAndReading()
    {
        Assert.Null(BashFdTable.Open(11, "/dev/null", ">"));
        Assert.Equal(0, BashFdTable.Write(11, "gone\n"));
        Assert.Null(BashFdTable.Open(12, "/dev/null", "<"));
        Assert.Null(BashFdTable.ReadLine(12));
        BashFdTable.CloseAll();
    }

    [Fact]
    public void FdTable_DevFdN_NamesTheOpenDescriptor()
    {
        string file = Path.Combine(_root, "out3");
        Assert.Null(BashFdTable.Open(13, file, ">"));
        using (var viaName = RedirectFileWriter.OpenFile("/dev/fd/13", append: false)) viaName.Write("to13\n"u8.ToArray());
        Assert.Null(BashFdTable.Open(14, "/dev/fd/13", ">"));      // a dup, as `exec 14>&13`
        Assert.Equal(0, BashFdTable.Write(14, "dup\n"));
        BashFdTable.CloseAll();
        Assert.Equal("to13\ndup\n", File.ReadAllText(file));
        Assert.NotNull(BashFdTable.Open(15, "/dev/fd/9", ">"));    // not open: Bad file descriptor
    }

    private static string Q(string s) => s.Replace("'", "''");

    /// <summary>Run <paramref name="script"/> with the current location at the root of a PSDrive over <see cref="_root"/>.</summary>
    private CmdResult RunAtDriveRoot(string script) => CmdResult.Run(_fixture.AcquireFresh(),
        $"$__old = Get-Location; $null = New-PSDrive -Name psbdev -PSProvider FileSystem -Root '{Q(_root)}' -Scope Global; "
        + "try { Set-Location psbdev:\\; " + script + " } finally { Set-Location $__old; Remove-PSDrive psbdev -Force }");

    public static IEnumerable<object[]> CmdletCases()
    {
        // script, expected stdout lines, a fragment stderr must hold (or "")
        var cases = new (string Script, string[] Out, string Err)[]
        {
            ("Invoke-BashExecFd 10 '>' /dev/stdout; Invoke-BashEcho x | Write-BashFd 10; Invoke-BashExecFd 10 '>&' '-'", ["x"], ""),
            ("Invoke-BashExecFd 10 '>>' /dev/fd/1; Invoke-BashEcho x | Write-BashFd 10; Invoke-BashExecFd 10 '>&' '-'", ["x"], ""),
            ("Invoke-BashExecFd -Variable v 0 '>' /dev/stdout; Invoke-BashEcho x | Write-BashFd 10; Invoke-BashExecFd 10 '>&' '-'", ["x"], ""),
            ("Invoke-BashExecFd 10 '>' /dev/null; Invoke-BashEcho x | Write-BashFd 10; Invoke-BashExecFd 10 '>&' '-'", [], ""),
            ("Invoke-BashEcho x | Invoke-BashRedirect -Path /dev/stdout", ["x"], ""),
            ("Invoke-BashEcho x | Invoke-BashRedirect -Path /dev/null", [], ""),
            ("Invoke-BashEcho x | Invoke-BashRedirect -Path /dev/zero", [], ""),
            ("Invoke-BashEcho x | Invoke-BashTee /dev/stdout", ["x", "x"], ""),
            ("Invoke-BashEcho x | Invoke-BashTee /dev/null", ["x"], ""),
            ("Invoke-BashEcho x | Invoke-BashTee /dev/stderr", ["x"], "x"),
            ("Invoke-BashEcho x | Invoke-BashTee /dev/fd/2", ["x"], "x"),
            ("'b','a' | Invoke-BashSort '-o' /dev/stdout", ["a", "b"], ""),
            ("'b','a' | Invoke-BashSort '-o' /dev/null", [], ""),
            ("Invoke-BashEcho x | Invoke-BashSed '-n' 'w /dev/stdout'", ["x"], ""),
            ("Invoke-BashAwk 'BEGIN{print \"x\" > \"/dev/stdout\"}'", ["x"], ""),
            ("Invoke-BashAwk 'BEGIN{print \"x\" > \"/dev/null\"}'", [], ""),
            ("'a','a' | Invoke-BashUniq '-' /dev/stdout", ["a"], ""),
            ("Invoke-BashCat /dev/null", [], ""),
            ("Invoke-BashEcho in | Invoke-BashCat /dev/stdin", ["in"], ""),
            ("Invoke-BashEcho in | Invoke-BashCat /dev/fd/0", ["in"], ""),
            ("Invoke-BashOd '-An' '-tx1' '-N' 4 /dev/zero", [" 00 00 00 00"], ""),
            ("Invoke-BashTest '-e' /dev/stdout; [string]$global:LASTEXITCODE", ["0"], ""),
            ("Invoke-BashTest '-c' /dev/null; [string]$global:LASTEXITCODE", ["0"], ""),
            ("Invoke-BashTest '-e' /dev/fd/9; [string]$global:LASTEXITCODE", ["1"], ""),
        };
        foreach (var c in cases)
        foreach (bool withDevFolder in new[] { false, true })
            yield return [c.Script, c.Out, c.Err, withDevFolder];
    }

    [Theory]
    [MemberData(nameof(CmdletCases))]
    public void Cmdlets_DeviceName_NoFileUnderTheDriveRoot(string script, string[] expected, string stderrHas, bool withDevFolder)
    {
        string dev = Path.Combine(_root, "dev");
        if (withDevFolder)
        {
            Directory.CreateDirectory(Path.Combine(dev, "fd"));
            // What a machine with a stray C:\dev\stdout has: a real file of the device's name.
            foreach (var name in new[] { "stdout", "stderr", "null", "stdin", "zero" })
                File.WriteAllText(Path.Combine(dev, name), "JUNK\n");
        }
        string beforeReal = Snapshot();

        var r = RunAtDriveRoot(script);

        Assert.Equal(expected, r.Lines.Select(l => l.TrimEnd('\r', '\n')).ToArray());
        Assert.DoesNotContain("No such file", r.Stderr);
        Assert.DoesNotContain("JUNK", r.Stdout);
        if (stderrHas.Length == 0) Assert.Equal("", r.Stderr);
        else Assert.Contains(stderrHas, r.Stderr);

        if (!withDevFolder)
            Assert.False(Directory.Exists(dev), "a device name created " + dev);
        else
        {
            foreach (var name in new[] { "stdout", "stderr", "null", "stdin", "zero" })
                Assert.Equal("JUNK\n", File.ReadAllText(Path.Combine(dev, name)));
            Assert.Equal(6, Directory.EnumerateFileSystemEntries(dev).Count());
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dev, "fd")));
        }
        Assert.Equal(new[] { withDevFolder ? "dev" : null }.Where(n => n is not null),
            Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName));
        Assert.Equal(beforeReal, Snapshot());
    }
}
