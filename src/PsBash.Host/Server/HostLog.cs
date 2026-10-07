using System.Reflection;

namespace PsBash.Host.Server;

/// <summary>
/// The host's diagnostic log, <c>~/.psbash/host.log</c> (override: <c>PSBASH_HOST_LOG</c>).
/// <para>
/// Every host on the machine — installed, dev, test — appends to this one file, so each line
/// carries <c>pid=</c>, <c>ep=</c> (endpoint) and <c>v=</c> (version): without them a
/// "connection error" could not be attributed to any process. Each host also logs its start
/// and, from a <see cref="AppDomain.ProcessExit"/> hook, its EXIT with the reason recorded by
/// whichever shutdown path ran (<see cref="SetExitReason"/>; first wins). An exit with no
/// recorded reason ran no host shutdown path — an external <c>Environment.Exit</c> (a user
/// command) or a signal — and is logged as such. Unhandled exceptions are logged before the
/// runtime ends the process. A StackOverflow, FailFast or kill cannot be logged from inside;
/// their absence (a start line with no exit line) is itself the signal.
/// </para>
/// Best-effort throughout: logging never throws into the host.
/// </summary>
internal static class HostLog
{
    /// <summary>Rotate to <c>host.log.1</c> beyond this size so the shared file stays bounded.</summary>
    internal const long MaxBytes = 4L * 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _endpoint;
    private static string? _exitReason;
    private static int _handlersInstalled;

    internal static readonly string Version =
        typeof(HostLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(HostLog).Assembly.GetName().Version?.ToString() ?? "?";

    /// <summary>Test seam: a log path that wins over the environment (env vars are process-wide).</summary>
    internal static string? PathOverride { get; set; }

    /// <summary>Test seam: forget the recorded exit reason (it is first-wins per process).</summary>
    internal static void ResetExitReasonForTest() => Volatile.Write(ref _exitReason, null);

    internal static string FilePath =>
        PathOverride ??
        (Environment.GetEnvironmentVariable("PSBASH_HOST_LOG") is { Length: > 0 } p
            ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".psbash", "host.log"));

    /// <summary>The endpoint this host serves, stamped on every later line.</summary>
    public static void SetEndpoint(string scheme, string endpoint) => _endpoint = scheme + ":" + endpoint;

    /// <summary>Record why the host is shutting down. The first reason wins: the trigger, not
    /// the teardown that follows it.</summary>
    public static void SetExitReason(string reason) => Interlocked.CompareExchange(ref _exitReason, reason, null);

    internal static string? ExitReason => Volatile.Read(ref _exitReason);

    /// <summary>One log line; pure so the format is unit-testable.</summary>
    internal static string FormatLine(DateTime utc, int pid, string? endpoint, string version, string message)
        => $"{utc:O} pid={pid} ep={endpoint ?? "-"} v={version} {message}";

    public static void Write(string message)
    {
        try
        {
            var path = FilePath;
            var line = FormatLine(DateTime.UtcNow, Environment.ProcessId, _endpoint, Version, message) + Environment.NewLine;
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                        File.Move(path, path + ".1", overwrite: true);
                }
                catch { /* another host rotated it first, or it is held: append anyway */ }
                File.AppendAllText(path, line);
            }
        }
        catch { /* never let diagnostics take the host down */ }
    }

    /// <summary>
    /// Hook process-level failure/exit events once. Unhandled exceptions are logged (the runtime
    /// still terminates); unobserved task exceptions are logged and marked observed; process exit
    /// logs the code and recorded reason.
    /// </summary>
    public static void InstallProcessHandlers()
    {
        if (Interlocked.Exchange(ref _handlersInstalled, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            SetExitReason("unhandled exception");
            Write($"FATAL unhandled exception (terminating={e.IsTerminating}): {Describe(e.ExceptionObject as Exception)}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write($"unobserved task exception: {Describe(e.Exception)}");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Write($"exit code={Environment.ExitCode} reason={ExitReason ?? UnrecordedExitReason}");
    }

    /// <summary>Logged when the process exits without any host shutdown path having run.</summary>
    internal const string UnrecordedExitReason =
        "none recorded (no host shutdown path ran: an Environment.Exit outside the host, or a signal)";

    private static string Describe(Exception? ex)
    {
        if (ex is null) return "<non-exception object>";
        var s = ex.ToString().ReplaceLineEndings(" | ");
        return s.Length > 4000 ? s[..4000] + "…" : s;
    }
}
