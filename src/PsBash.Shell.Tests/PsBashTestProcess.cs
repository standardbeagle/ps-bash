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

        if (ipcEndpoint is not null)
        {
            psi.Environment[IpcTransportFactory.EndpointEnvVar] = ipcEndpoint;
        }
        else if (isolatedIpc)
        {
            psi.Environment[IpcTransportFactory.EndpointEnvVar] = CreateEndpoint();
        }

        return psi;
    }

    public static string CreateEndpoint()
        => OperatingSystem.IsWindows()
            ? "pipe:psbash-test-" + Guid.NewGuid().ToString("N")
            : "unix:" + Path.Combine(Path.GetTempPath(), "ps-bash", "test-" + Guid.NewGuid().ToString("N") + ".sock");
}
