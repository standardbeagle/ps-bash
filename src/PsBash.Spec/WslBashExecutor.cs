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
            foreach (var a in new[] { "-d", _distro, "-e", "bash", "-c", "d=$(mktemp -d); cd \"$d\" && " + (script.Contains("_tmp", StringComparison.Ordinal) ? "mkdir _tmp; " : "") + "bash \"$1\"; s=$?; cd /; rm -rf \"$d\"; exit $s", "spec", ToWslPath(file) }) psi.ArgumentList.Add(a);
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
