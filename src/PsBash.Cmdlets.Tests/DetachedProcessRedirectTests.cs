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

    /// <summary>A program that copies its stdin to its stdout: sort.exe on Windows, cat elsewhere.</summary>
    private static (string Exe, string[] Args) Copy() => OperatingSystem.IsWindows()
        ? (Path.Combine(Environment.SystemDirectory, "sort.exe"), Array.Empty<string>())
        : ("/bin/cat", Array.Empty<string>());

    // REGRESSION (gap 2): `echo in | { nohup cat > o & wait; }` wrote nothing: the child's stdin was always the null
    // device. GNU nohup leaves a stdin that is not a terminal alone; the child reads the pipe it is given.
    [Fact]
    public void GivenStdin_IsWhatTheChildReads()
    {
        var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out,
            OperatingSystem.IsWindows() ? HandleInheritability.Inheritable : HandleInheritability.None);
        using var readEnd = pipe.ClientSafePipeHandle; // exposed: disposing the writer leaves it open
        pipe.Write("in\n"u8);
        pipe.Dispose(); // end of input once the child has read it
        var (exe, args) = Copy();
        using var child = DetachedProcess.SpawnChild(exe, args, _dir, P("o"), append: false, errPath: null, errAppend: false, stdin: readEnd);
        using var stop = new CancellationTokenSource(Bound);
        Assert.Equal(0, child.Wait(stop.Token));
        Assert.Equal("in\n", Text(P("o")));
    }

    [Fact]
    public void NoStdin_IsEndOfInput()
    {
        var (exe, args) = Copy();
        Assert.Equal(0, Run(exe, args, P("o"), append: false, errPath: null, errAppend: false));
        Assert.Equal("", Text(P("o")));
    }
}
