using System.Diagnostics;

namespace PsBash.Spec;

/// <summary>
/// The oracle: real bash 5.x under WSL (<c>wsl.exe -d DISTRO -e bash FILE</c>; never <c>--</c>, which re-parses the line in the
/// login shell). Used only by <c>record</c>; a normal run reads the committed expectations and needs no WSL.
/// </summary>
public sealed class WslBashExecutor : IScriptExecutor
{
    private readonly string _distro;
    public WslBashExecutor(string distro = "Ubuntu-24.04") => _distro = distro;

    /// <summary>Closes every inherited descriptor above 2 (the glob is expanded before the first one closes).</summary>
    internal const string HermeticFds = "for fd in /proc/$$/fd/*; do fd=${fd##*/}; case $fd in 0|1|2) ;; *) eval \"exec $fd>&-\" 2>/dev/null;; esac; done; ";

    public async Task<ExecResult> RunAsync(string script, TimeSpan timeout, CancellationToken cancel)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ps-bash", "spec-record");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".sh");
        // Oils cases run "the shell under test" as $SH; for the oracle that is bash itself. Set on the script's first line
        // (as PsBashExecutor does): a line of its own shifted every recorded $LINENO by one.
        await File.WriteAllTextAsync(file, SpecImporter.UsesShellVariable(script) ? "SH=bash; " + script : script, cancel);
        try
        {
            var psi = new ProcessStartInfo("wsl.exe") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
            // wsl.exe hands the shell descriptors of its own (7 and 10); a script would inherit them, so its recording would
            // depend on the recording host. Only 0-2 stay open, as for a shell started from a terminal.
            foreach (var a in new[] { "-d", _distro, "-e", "bash", "-c", HermeticFds + "d=$(mktemp -d); cd \"$d\" && " + (script.Contains("_tmp", StringComparison.Ordinal) ? "mkdir _tmp; " : "") + "bash \"$1\"; s=$?; cd /; rm -rf \"$d\"; exit $s", "spec", ToWslPath(file) }) psi.ArgumentList.Add(a);
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync(cancel);
            var stderr = p.StandardError.ReadToEndAsync(cancel);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timer.CancelAfter(timeout);
            try { await p.WaitForExitAsync(timer.Token); }
            catch (OperationCanceledException) { p.Kill(entireProcessTree: true); throw; }
            return new ExecResult(p.ExitCode, await stdout, await stderr, sw.ElapsedMilliseconds);
        }
        finally { try { File.Delete(file); } catch { /* best effort */ } }
    }

    internal static string ToWslPath(string windowsPath)
        => "/mnt/" + char.ToLowerInvariant(windowsPath[0]) + windowsPath[2..].Replace('\\', '/');
}
