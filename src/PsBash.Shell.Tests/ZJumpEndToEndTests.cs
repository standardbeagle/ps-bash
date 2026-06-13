using System.Diagnostics;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// End-to-end coverage for the zoxide `z` jump driven through the real ps-bash
/// interactive process: visiting a directory records it in the frecency DB, and a
/// later `z &lt;keyword&gt;` from elsewhere jumps back to it. PSBASH_HOME is isolated to a
/// temp dir so the test's frecency.db never touches the real one.
/// </summary>
[Trait("Category", "Integration")]
public class ZJumpEndToEndTests
{
    [SkippableFact]
    public async Task Z_JumpsToPreviouslyVisitedDirectory()
    {
        var stamp = Guid.NewGuid().ToString("N");
        var home = Path.Combine(Path.GetTempPath(), "psbash-zhome-" + stamp);
        var root = Path.Combine(Path.GetTempPath(), "psbash-zroot-" + stamp);
        var target = Path.Combine(root, "zztarget" + stamp);   // unique basename keyword
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(target);

        try
        {
            var psi = PsBashTestProcess.Create(
                ["-i"],
                workingDirectory: root,
                env: new Dictionary<string, string?> { ["PSBASH_HOME"] = home });
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;

            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ps-bash");

            var stdout = process.StandardOutput.ReadToEndAsync();

            // Visit the target (records it), go back to root, then jump by keyword.
            await process.StandardInput.WriteLineAsync($"cd '{target}'");
            await process.StandardInput.WriteLineAsync($"cd '{root}'");
            await process.StandardInput.WriteLineAsync($"z zztarget{stamp}");
            await process.StandardInput.WriteLineAsync("echo \"LANDED:$PWD\"");
            await process.StandardInput.WriteLineAsync("exit 0");
            process.StandardInput.Close();

            await process.WaitForExitAsync();
            var output = await stdout;

            // The marker line should report the jumped-to directory, not root.
            Assert.Contains("LANDED:", output);
            Assert.Contains("zztarget" + stamp, output);
            // Specifically: PWD ends with the target basename (the jump worked).
            var marker = output.Split('\n').FirstOrDefault(l => l.Contains("LANDED:"));
            Skip.If(marker is null, "no LANDED marker captured (interactive output race)");
            Assert.Contains(Path.GetFileName(target), marker!);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }
}
