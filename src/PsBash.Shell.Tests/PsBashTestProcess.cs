using System.Diagnostics;
using PsBash.Core.Runtime.Ipc;

namespace PsBash.Shell.Tests;

internal static class PsBashTestProcess
{
    public static ProcessStartInfo Create(
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? env = null,
        bool isolatedIpc = true,
        string? ipcEndpoint = null)
    {
        var binary = InteractiveShellHarness.FindPsBashBinary()
            ?? throw new InvalidOperationException("ps-bash binary not found");

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        if (env is not null)
        {
            foreach (var (key, value) in env)
                psi.Environment[key] = value;
        }

        // Host lifetime. The launcher default is a shared daemon that idles for 600 s,
        // so every test endpoint left a dev-build host alive after the run, holding
        // memory and locking the bin DLLs for the next build.
        if (ipcEndpoint is not null)
        {
            // A class-shared endpoint stays a warm daemon across that class's
            // tests, then exits shortly after the last one instead of lingering.
            psi.Environment[IpcTransportFactory.EndpointEnvVar] = ipcEndpoint;
            SetUnlessCallerDid(psi, env, "PSBASH_HOST_IDLE_SECS", SharedHostIdleSecs);
        }
        else if (isolatedIpc)
        {
            // A single-use endpoint never benefits from a daemon: a private host
            // that dies with its launcher is the same isolation with no survivor.
            SetUnlessCallerDid(psi, env, "PSBASH_PER_INVOCATION", "1");
        }

        return psi;
    }

    /// <summary>
    /// Idle window for a class-shared test daemon: long enough to stay warm between
    /// that class's back-to-back tests, short enough not to outlive the suite.
    /// </summary>
    private const string SharedHostIdleSecs = "30";

    private static void SetUnlessCallerDid(
        ProcessStartInfo psi, IReadOnlyDictionary<string, string?>? env, string key, string value)
    {
        if (env is null || !env.ContainsKey(key))
            psi.Environment[key] = value;
    }

    public static string CreateEndpoint()
        => OperatingSystem.IsWindows()
            ? "pipe:psbash-test-" + Guid.NewGuid().ToString("N")
            : "unix:" + Path.Combine(Path.GetTempPath(), "ps-bash", "test-" + Guid.NewGuid().ToString("N") + ".sock");
}
