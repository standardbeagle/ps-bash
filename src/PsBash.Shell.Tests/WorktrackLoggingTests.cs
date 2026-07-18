using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace PsBash.Shell.Tests;

public sealed class WorktrackLoggingTests
{
    [SkippableFact]
    public async Task SliceGuardedRunner_PreservesStreamsExitCodeAndTimestampedLogPath()
    {
        var repoRoot = FindRepoRoot();
        var runner = Path.Combine(repoRoot, "scripts", "run-worktrack-test.ps1");
        var fixture = Path.Combine(repoRoot, "scripts", "fixtures", "failing-child.sh");
        var bash = FindBash();
        Skip.If(bash is null, "bash was not found in Git for Windows or PATH");
        var logDirectory = Path.Combine(Path.GetTempPath(), $"psbash-worktrack-{Guid.NewGuid():N}");
        const string timestamp = "20301231-235959";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pwsh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[]
            {
                "-NoProfile", "-File", runner,
                "-BashExecutable", bash!,
                "-TestScript", fixture,
                "-LogDirectory", logDirectory,
                "-Timestamp", timestamp,
            })
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi)!;
            var stdoutRead = process.StandardOutput.ReadToEndAsync();
            var stderrRead = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutRead;
            var stderr = await stderrRead;

            Assert.Equal(23, process.ExitCode);
            var normalizedStdout = stdout.Replace("\r\n", "\n");
            Assert.Equal("fixture-stderr\n", stderr.Replace("\r\n", "\n"));

            var status = Regex.Match(normalizedStdout, @"worktrack-test-log=(.+) exit=(\d+)\n$");
            Assert.True(status.Success, $"Missing worktrack status in stdout: {normalizedStdout}");
            var reportedLog = status.Groups[1].Value;
            Assert.Equal("23", status.Groups[2].Value);
            Assert.Equal(
                $"fixture-stdout\nworktrack-test-log={reportedLog} exit=23\n",
                normalizedStdout);
            Assert.Matches(new Regex(@"^test-all-\d{8}-\d{6}\.log$"), Path.GetFileName(reportedLog));
            Assert.Equal($"test-all-{timestamp}.log", Path.GetFileName(reportedLog));
            Assert.Equal(Path.GetFullPath(logDirectory), Path.GetDirectoryName(Path.GetFullPath(reportedLog)));

            var log = (await File.ReadAllTextAsync(reportedLog, timeout.Token)).Replace("\r\n", "\n");
            Assert.Equal("fixture-stdout\nfixture-stderr\n", log);
        }
        finally
        {
            if (Directory.Exists(logDirectory))
                Directory.Delete(logDirectory, recursive: true);
        }
    }

    private static string? FindBash()
    {
        if (OperatingSystem.IsWindows())
        {
            var gitBash = @"C:\Program Files\Git\bin\bash.exe";
            if (File.Exists(gitBash))
                return gitBash;
        }

        var executable = OperatingSystem.IsWindows() ? "bash.exe" : "bash";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ps-bash.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find ps-bash.sln");
    }
}
