using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using PsBash.Core.Runtime;
using PsBash.Core.Runtime.Processes;

namespace PsBash.Cmdlets;

/// <summary>
/// How a spawned child's stdin and stderr are wired. The one place a child's stdio shape is chosen; every spawn
/// goes through <see cref="ChildStdio.Start"/> (via <see cref="BashRuntime.RunChildProcess(ProcessStartInfo, System.TimeSpan?, string?, ChildStdioMode)"/>
/// and <see cref="BashRuntime.StartChildProcessStreaming"/>).
/// </summary>
/// <param name="MergeStderr">
/// stderr is merged into stdout (<c>2&gt;&amp;1</c>, <c>&amp;&gt;</c>, <c>|&amp;</c>, <c>&gt;f 2&gt;&amp;1</c>): the child gets ONE pipe as both
/// fd 1 and fd 2, so the order it wrote in is the order we read. Never two pipes read and interleaved afterwards.
/// </param>
/// <param name="InheritStdin">
/// The child's stdin is the process stdin (what a native program run by PowerShell gets when it has no pipeline
/// input: the launcher's forwarded stdin through <see cref="NativeStdinBridge"/>, or nothing) instead of a pipe the
/// caller writes and closes.
/// </param>
/// <param name="Stdin">
/// The child's stdin is this pipe read end, which the caller owns and keeps (<see cref="NativeStdinBridge.ForChild"/>:
/// a compound's stdin for a program started outside the foreground). Wins over <paramref name="InheritStdin"/>.
/// </param>
public readonly record struct ChildStdioMode(bool MergeStderr = false, bool InheritStdin = false,
    System.Runtime.InteropServices.SafeHandle? Stdin = null)
{
    /// <summary>Separate stdout/stderr pipes, a stdin pipe: the default.</summary>
    public static ChildStdioMode Separate => default;

    /// <summary>One pipe for stdout and stderr, a stdin pipe.</summary>
    public static ChildStdioMode Merged => new(MergeStderr: true);
}

/// <summary>
/// A started child, its whole process tree, and the caller's ends of its stdio: <see cref="StandardInput"/> (null
/// when it inherited the process stdin), <see cref="StandardOutput"/>, and <see cref="StandardError"/> (null when
/// stderr is merged into stdout).
/// <para>
/// <b>Lifetime.</b> The child and everything it starts are one unit (<see cref="JobProcess"/>: a Job Object on
/// Windows, a process group on POSIX). <see cref="KillTree"/> ends all of it in one call - a grandchild cannot be
/// missed because its parent already exited. <see cref="Dispose"/> after a normal exit lets what the child left
/// running in the background live on, as bash does (<c>sh -c 'server &amp;'</c>); <see cref="Dispose"/> of a child
/// that is still running, or was killed, ends the tree. See <c>docs/specs/stdio-messaging.md</c> §4.
/// </para>
/// </summary>
public sealed class SpawnedChild : IDisposable
{
    private readonly JobProcess _job;
    private int _killed;
    private int _disposed;

    internal SpawnedChild(JobProcess job, StreamWriter? stdin, StreamReader stdout, StreamReader? stderr)
    {
        _job = job;
        StandardInput = stdin;
        StandardOutput = stdout;
        StandardError = stderr;
    }

    /// <summary>OS process id of the child.</summary>
    public int Id => _job.Pid;

    /// <summary>The child has exited (what it started may still run).</summary>
    public bool HasExited => _job.HasExited;

    /// <summary>The child's exit status; only after <see cref="HasExited"/>.</summary>
    public int ExitCode => _job.ExitCode;

    /// <summary><see cref="KillTree"/> was called.</summary>
    public bool Killed => Volatile.Read(ref _killed) != 0;

    /// <summary>
    /// Wait for the child to exit: up to <paramref name="milliseconds"/> (<see cref="Timeout.Infinite"/> allowed), or
    /// until <paramref name="stop"/> is cancelled. True when it has exited. The pipes are NOT part of the wait.
    /// </summary>
    public bool WaitForExit(int milliseconds = Timeout.Infinite, CancellationToken stop = default)
        => _job.WaitForExit(milliseconds, stop);

    /// <summary>
    /// End the child and every process it started, now. Safe from any thread; idempotent. THE way a child is
    /// stopped (Ctrl+C, a pipeline stop, <c>timeout</c>, a budget): never <c>Process.Kill</c>
    /// (guard: <c>ProcessTreeKillGuardTests</c>).
    /// </summary>
    public void KillTree()
    {
        Interlocked.Exchange(ref _killed, 1);
        _job.KillTree();
    }

    /// <summary>Pids of the tree alive now (Windows: the Job Object's; Linux: the process group's). Diagnostics and tests.</summary>
    public IReadOnlyList<int> TreePids() => _job.TreePids();

    /// <summary>The child's stdin pipe (write end); null when the child inherited the process stdin.</summary>
    public StreamWriter? StandardInput { get; }

    /// <summary>The child's stdout — and, when <see cref="StderrMerged"/>, its stderr in write order.</summary>
    public StreamReader StandardOutput { get; }

    /// <summary>The child's stderr pipe; null when merged into <see cref="StandardOutput"/>.</summary>
    public StreamReader? StandardError { get; }

    public bool StderrMerged => StandardError is null;

    /// <summary>
    /// Closes our ends of the pipes without waiting for anyone (a reader blocked on a pipe some surviving process
    /// still holds is abandoned, never joined), then ends the tree unless the child exited on its own.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Exited on its own and nobody asked for a stop: what it left running is its business (bash does not hunt
        // down `cmd &` either). Otherwise the tree goes with the child.
        if (!Killed && _job.HasExited) _job.Release();
        _job.Dispose();
        try { StandardInput?.Dispose(); } catch { /* broken pipe on the final flush */ }
        try { StandardOutput.Dispose(); } catch { }
        try { StandardError?.Dispose(); } catch { }
    }
}

/// <summary>
/// THE child-process spawn primitive: stdio shape and process-tree containment, both by construction.
/// <list type="bullet">
/// <item><b>Stdio.</b> Under <see cref="ChildStdioMode.MergeStderr"/> the child's fd 1 and fd 2 are the SAME OS pipe,
/// so the merge happens in the kernel as the child writes and the order is preserved. The child inherits exactly its
/// three standard handles and nothing else.</item>
/// <item><b>Tree.</b> The child is started through <see cref="JobProcess"/>: on Windows suspended, assigned to a
/// kill-on-close Job Object, then resumed (no descendant can exist outside the job); on POSIX as the leader of its
/// own process group. <see cref="SpawnedChild.KillTree"/> ends every process of it.</item>
/// </list>
/// A process that deliberately detaches survives a stop, as under bash: POSIX <c>setsid</c> leaves the group, and on
/// Windows <c>CREATE_BREAKAWAY_FROM_JOB</c> (what <c>nohup</c>'s <see cref="DetachedProcess"/> uses) leaves the job.
/// See <c>docs/specs/stdio-messaging.md</c> §4.
/// </summary>
public static class ChildStdio
{
    /// <summary>
    /// Test seam: overrides "this process is its terminal's foreground job" (<see cref="JobProcess.IsTerminalForegroundJob"/>),
    /// which decides on POSIX whether a child may lead its own process group.
    /// </summary>
    internal static Func<bool>? TerminalForegroundOverride;

    /// <summary>
    /// Start <paramref name="startInfo"/> (FileName + ArgumentList/Arguments, Environment, WorkingDirectory,
    /// CreateNoWindow are honored; its Redirect* flags are ignored: the wiring comes from <paramref name="mode"/>).
    /// Output is decoded byte-faithfully (<see cref="PsBash.Core.RawBytes.Encoding"/>) unless the caller chose an
    /// encoding. Throws <see cref="System.ComponentModel.Win32Exception"/> when the program cannot be started.
    /// </summary>
    public static SpawnedChild Start(ProcessStartInfo startInfo, ChildStdioMode mode)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        BashVariableStore.ApplyTo(startInfo);
        var outEncoding = startInfo.StandardOutputEncoding ?? PsBash.Core.RawBytes.Encoding;
        var errEncoding = startInfo.StandardErrorEncoding ?? PsBash.Core.RawBytes.Encoding;

        IReadOnlyList<string> arguments;
        string? rawArguments = null;
        if (startInfo.ArgumentList.Count > 0) arguments = [.. startInfo.ArgumentList];
        else if (string.IsNullOrEmpty(startInfo.Arguments)) arguments = [];
        else if (OperatingSystem.IsWindows()) { arguments = []; rawArguments = startInfo.Arguments; }
        else arguments = WindowsCommandLine.Split(startInfo.Arguments);

        var environment = new Dictionary<string, string?>(startInfo.Environment.Count,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var kv in startInfo.Environment) environment[kv.Key] = kv.Value;

        // Our ends are never inherited. The child's ends: on Windows inheritable, and the handle list names exactly
        // them; on POSIX close-on-exec, dup2'ed onto fd 0/1/2 by the spawn (so no other child picks them up).
        var childEnd = OperatingSystem.IsWindows() ? HandleInheritability.Inheritable : HandleInheritability.None;
        AnonymousPipeServerStream? output = null, error = null, input = null;
        bool ok = false;
        try
        {
            output = new AnonymousPipeServerStream(PipeDirection.In, childEnd);
            if (!mode.MergeStderr) error = new AnonymousPipeServerStream(PipeDirection.In, childEnd);
            if (!mode.InheritStdin && mode.Stdin is null) input = new AnonymousPipeServerStream(PipeDirection.Out, childEnd);

            IntPtr childOut = output.ClientSafePipeHandle.DangerousGetHandle();
            var spec = new JobProcessStart(startInfo.FileName, arguments, environment)
            {
                EnvironmentIsComplete = true,
                RawArguments = rawArguments,
                WorkingDirectory = string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                CreateNoWindow = startInfo.CreateNoWindow,
                NewConsoleProcessGroup = false,
                AllowBreakaway = true,
                OwnProcessGroup = !(TerminalForegroundOverride?.Invoke() ?? JobProcess.IsTerminalForegroundJob()),
                Stdio = new JobStdio(
                    mode.Stdin?.DangerousGetHandle() ?? input?.ClientSafePipeHandle.DangerousGetHandle(),
                    childOut,
                    // THE merge: one handle for fd 1 and fd 2.
                    error is null ? childOut : error.ClientSafePipeHandle.DangerousGetHandle(),
                    InheritStdin: mode.InheritStdin && mode.Stdin is null),
            };

            JobProcess job;
            try { job = JobProcess.Start(spec); }
            catch (HostUnavailableException ex) when (ex.InnerException is System.ComponentModel.Win32Exception os)
            {
                // What Process.Start threw, so callers keep telling "no such program" from other failures.
                throw new System.ComponentModel.Win32Exception(os.NativeErrorCode,
                    $"An error occurred trying to start process '{startInfo.FileName}' with working directory '{startInfo.WorkingDirectory}'. {os.Message}");
            }

            // The child holds its ends now; drop ours so EOF arrives when it (and its descendants) close them.
            output.DisposeLocalCopyOfClientHandle();
            error?.DisposeLocalCopyOfClientHandle();
            input?.DisposeLocalCopyOfClientHandle();
            try
            {
                var stdinWriter = input is null ? null : new StreamWriter(AdoptOurEnd(input, FileAccess.Write), PsBash.Core.RawBytes.Encoding) { AutoFlush = true };
                var stdoutReader = new StreamReader(AdoptOurEnd(output, FileAccess.Read), outEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096);
                var stderrReader = error is null ? null : new StreamReader(AdoptOurEnd(error, FileAccess.Read), errEncoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096);
                ok = true;
                return new SpawnedChild(job, stdinWriter, stdoutReader, stderrReader);
            }
            catch
            {
                job.Dispose(); // started but not handed out: end it
                throw;
            }
        }
        finally
        {
            if (!ok)
            {
                output?.Dispose();
                error?.Dispose();
                input?.Dispose();
            }
        }
    }

    /// <summary>
    /// Our end of a pipe as a plain <see cref="FileStream"/> that owns the handle. Closing a FileStream while another
    /// thread is blocked reading it returns at once (the handle is released when that read ends). A pipe stream's
    /// Dispose on Unix WAITS for the blocked read - i.e. for whoever still holds the write end - which is exactly the
    /// wait the lifetime invariant forbids (docs/specs/stdio-messaging.md §4.6).
    /// </summary>
    private static FileStream AdoptOurEnd(AnonymousPipeServerStream pipe, FileAccess access)
    {
        var stream = new FileStream(
            new Microsoft.Win32.SafeHandles.SafeFileHandle(pipe.SafePipeHandle.DangerousGetHandle(), ownsHandle: true),
            access, bufferSize: 0, isAsync: false);
        pipe.SafePipeHandle.SetHandleAsInvalid(); // ownership moved: the pipe object must not close it
        pipe.Dispose();
        return stream;
    }

    /// <summary>CommandLineToArgvW-compatible quoting (the rules ProcessStartInfo.ArgumentList applies).</summary>
    internal static void AppendQuoted(StringBuilder sb, string arg) => WindowsCommandLine.AppendQuoted(sb, arg);
}
