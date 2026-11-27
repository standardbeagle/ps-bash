using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// The files a detached <c>nohup</c> child writes (<see cref="DetachedProcess"/>): <c>&gt;</c>/<c>2&gt;</c> truncate,
/// <c>&gt;&gt;</c>/<c>2&gt;&gt;</c> append, and any program can write them. Class (v0.15.1 gate): the Windows handles were
/// opened FILE_APPEND_DATA-only, which the MSYS2 runtime (Git for Windows' ls.exe, sh.exe) takes as not writable, so
/// <c>nohup ls /x &gt; f 2&gt;&amp;1</c> left f empty; and <c>2&gt; f</c> never truncated. Hand-written asserts: the
/// primitive's contract below the shell; the shell-level behavior is pinned by WrapperCommandDifferentialTests.
/// </summary>
public class DetachedProcessRedirectTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("psbash-detached-").FullName;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    /// <summary>A program that writes "o" to stdout and "e" to stderr: cmd.exe on Windows, /bin/sh elsewhere.</summary>
    private static (string Exe, string[] Args) OutAndErr() => OperatingSystem.IsWindows()
        ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/d", "/c", "echo o& echo e 1>&2" })
        : ("/bin/sh", new[] { "-c", "echo o; echo e >&2" });

    private int? Run(string exe, string[] args, string outPath, bool append, string? errPath, bool errAppend)
    {
        using var child = DetachedProcess.SpawnChild(exe, args, _dir, outPath, append, errPath, errAppend);
        using var stop = new CancellationTokenSource(Bound);
        return child.Wait(stop.Token);
    }

    private static string Text(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    [Fact]
    public void Truncate_ReplacesBothFiles()
    {
        File.WriteAllText(P("o"), "old-out-old-out\n");
        File.WriteAllText(P("e"), "old-err-old-err\n");
        var (exe, args) = OutAndErr();
        Assert.Equal(0, Run(exe, args, P("o"), append: false, P("e"), errAppend: false));
        Assert.Equal("o\n", Text(P("o")).Replace(" \n", "\n"));
        Assert.Equal("e\n", Text(P("e")).Replace(" \n", "\n"));
    }

    [Fact]
    public void Append_KeepsWhatBothFilesHeld()
    {
        File.WriteAllText(P("o"), "keep-o\n");
        File.WriteAllText(P("e"), "keep-e\n");
        var (exe, args) = OutAndErr();
        Assert.Equal(0, Run(exe, args, P("o"), append: true, P("e"), errAppend: true));
        Assert.Equal("keep-o\no\n", Text(P("o")).Replace(" \n", "\n"));
        Assert.Equal("keep-e\ne\n", Text(P("e")).Replace(" \n", "\n"));
    }

    [Fact]
    public void SharedFile_GetsBothStreams_AndTheStatus()
    {
        var (exe, args) = OutAndErr();
        Assert.Equal(0, Run(exe, args, P("both"), append: false, errPath: null, errAppend: false));
        var lines = Text(P("both")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
        Assert.Equal(new[] { "o", "e" }, lines);
    }

    // The runner-only failure: an MSYS2 program wrote nothing into the redirect file. Git for Windows ships the runtime.
    [SkippableFact]
    [Trait("Platform", "Windows")]
    public void MsysProgram_WritesTheRedirectFile()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "MSYS2 programs exist only on Windows");
        var sh = new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") }
            .Where(d => !string.IsNullOrEmpty(d)).Select(d => Path.Combine(d!, "Git", "usr", "bin", "sh.exe")).FirstOrDefault(File.Exists);
        Skip.If(sh is null, "Git for Windows (MSYS2 sh.exe) is not installed");
        File.WriteAllText(P("m"), "keep\n");
        Assert.Equal(3, Run(sh!, new[] { "-c", "echo O1; echo E1 >&2; exit 3" }, P("m"), append: true, errPath: null, errAppend: false));
        Assert.Equal("keep\nO1\nE1\n", Text(P("m")));
        Assert.Equal(0, Run(sh!, new[] { "-c", "echo E2 >&2" }, P("m2"), append: false, P("m3"), errAppend: false));
        Assert.Equal("E2\n", Text(P("m3")));
    }
}

/// <summary>
/// <c>nohup</c> on Unix starts <c>sh -c</c> to set its redirects. It handed sh the file names in <c>OUT</c> / <c>ERR</c>
/// and unset them, so the child lost an exported <c>OUT</c> of its own (bash: <c>export OUT=v; nohup sh -c 'echo $OUT'</c>
/// prints <c>v</c>). Hand-written: the primitive below the shell, with the variable in the process environment.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class DetachedProcessEnvironmentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("psbash-detached-env-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [SkippableFact]
    [Trait("Platform", "Unix")]
    public void TheChild_KeepsItsOwnOutAndErr_AndTheRedirectsGoWhereNohupSaid()
    {
        Skip.If(OperatingSystem.IsWindows(), "the sh -c redirect wrapper is the Unix path");
        string decoy = Path.Combine(_dir, "decoy");
        using var env = PsBash.Testing.ProcessEnv.Set(("OUT", decoy), ("ERR", decoy));
        string o = Path.Combine(_dir, "o"), e = Path.Combine(_dir, "e");
        using (var child = DetachedProcess.SpawnChild("/bin/sh", ["-c", "echo \"OUT=$OUT\"; echo \"ERR=$ERR\" >&2"], _dir, o, false, e))
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            Assert.Equal(0, child.Wait(stop.Token));
        Assert.Equal("OUT=" + decoy + "\n", File.ReadAllText(o));
        Assert.Equal("ERR=" + decoy + "\n", File.ReadAllText(e));
        Assert.False(File.Exists(decoy));
    }
}
