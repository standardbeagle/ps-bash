using System.Diagnostics;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// A script variable named <c>tmp</c> is <c>TMP</c> on Windows (environment names are case-insensitive). The host's
/// own scratch files must not follow it: the first unknown command in a fresh host wrote its autoload index under
/// <c>Path.GetTempPath()</c>, i.e. created a DIRECTORY named after the script's value in the cwd, and the script's
/// next <c>cmd &gt;&amp;$tmp</c> failed with "Is a directory" (oils redirect.spec.sh). See PsBash.Core.HostTempPath.
/// </summary>
[Trait("Category", "Integration")]
public class ScriptTmpVariableTests
{
    private static readonly string? PsBashExe = InteractiveShellHarness.FindPsBashBinary();

    [SkippableFact]
    public async Task TmpVariable_ThenUnknownCommand_CreatesNoDirectoryAndRedirectWorks()
    {
        Skip.If(PsBashExe is null, "built ps-bash required");
        var dir = Path.Combine(Path.GetTempPath(), "psb-tmpvar-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // A fresh host (own endpoint) so the autoload index is not already cached in memory.
            var psi = PsBashTestProcess.Create(
                new[] { "-c", "tmp=out-1.txt; nosuch_cmd_psb_tmpvar >&$tmp; echo rc=$?; cat $tmp" },
                ipcEndpoint: PsBashTestProcess.CreateEndpoint());
            psi.Environment["PSBASH_HOST_IDLE_SECS"] = "3";
            psi.WorkingDirectory = dir;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await p.WaitForExitAsync(cts.Token);
            var (stdout, stderr) = (await so, await se);

            Assert.False(Directory.Exists(Path.Combine(dir, "out-1.txt")), $"a directory was created. stderr={stderr}");
            Assert.Contains("rc=127", stdout);
            Assert.Contains("nosuch_cmd_psb_tmpvar: command not found", stdout); // went to the file, then cat
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
