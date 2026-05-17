using System.Diagnostics;
using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashLess</c> pager
/// (REFACTOR-2 follow-on). Behavioral parity oracle: the psm1 function.
///
/// Non-interactive (the only path this cmdlet actively exercises in
/// programmatic / pipeline contexts): pass input through unchanged.
/// Pipeline-mode emits one <c>PsBash.TextOutput</c> per pipeline item;
/// file-mode reads each operand line-by-line and emits per-line text
/// objects.
///
/// Interactive (PSBASH_INTERACTIVE=1 and the std handles are TTYs): try
/// to resolve a native <c>less</c> via <c>Get-Command less -CommandType
/// Application</c> and shell out via <see cref="Process"/> with operands
/// bound through <see cref="ProcessStartInfo.ArgumentList"/> (Directive 12
/// — no shell, no string concat into a script body). If pipeline input is
/// present, write it to a temp file first and prepend that path to the
/// argument list.
///
/// Flag surface (passes through to native): <c>-N</c> line numbers,
/// <c>-i</c> ignore-case, <c>-S</c> chop-long-lines plus any other token
/// the caller passes. <c>-i</c> prefix-collides with
/// <c>-InformationAction</c> / <c>-InformationVariable</c> so it is
/// declared as a <c>SwitchParameter I</c>; <c>-N</c> and <c>-S</c> have
/// no PowerShell common-parameter prefix overlap but are declared as
/// switches for symmetry / cleaner binder routing (the bare token form
/// stays out of <c>Arguments</c>). The values are re-injected into the
/// passthrough arg list before the native spawn.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashLess")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashLessCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    // Colliding-flag declarations (see class summary).
    [Parameter] public SwitchParameter I { get; set; }
    [Parameter] public SwitchParameter N { get; set; }
    [Parameter] public SwitchParameter S { get; set; }

    private readonly List<PSObject?> _pipelineItems = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null) _pipelineItems.Add(InputObject);
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "less"))
            {
                WriteObject(line);
            }
            return;
        }

        // Rebuild a passthrough flag list reflecting any switches the
        // binder consumed. Order does not matter for less.
        var passthrough = new List<string>(args.Length + 3);
        if (I.IsPresent) passthrough.Add("-i");
        if (N.IsPresent) passthrough.Add("-N");
        if (S.IsPresent) passthrough.Add("-S");
        foreach (var a in args) passthrough.Add(a);

        bool isInteractive = string.Equals(
            Environment.GetEnvironmentVariable("PSBASH_INTERACTIVE"),
            "1", StringComparison.Ordinal)
            && !Console.IsInputRedirected
            && !Console.IsOutputRedirected;

        bool hasPipeline = _pipelineItems.Count > 0;

        if (!isInteractive)
        {
            // Pass-through path: pipeline first (preserves typed objects),
            // then file operands rendered as line objects.
            if (hasPipeline)
            {
                foreach (var item in _pipelineItems)
                {
                    if (item != null) WriteObject(item);
                }
                FileSystemHelpers.SetLastExitCode(this, 0);
                return;
            }

            foreach (var path in passthrough)
            {
                if (path.StartsWith("-", StringComparison.Ordinal)) continue;
                string full;
                try
                {
                    full = SessionState.Path.GetUnresolvedProviderPathFromPSPath(path);
                }
                catch
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"less: {path}: No such file or directory");
                    FileSystemHelpers.SetLastExitCode(this, 1);
                    return;
                }
                if (!System.IO.File.Exists(full))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"less: {path}: No such file or directory");
                    FileSystemHelpers.SetLastExitCode(this, 1);
                    return;
                }
                string text;
                try
                {
                    text = System.IO.File.ReadAllText(full).Replace("\r\n", "\n");
                }
                catch (System.Exception ex)
                {
                    FileSystemHelpers.WriteBashError(this, $"less: {path}: {ex.Message}");
                    FileSystemHelpers.SetLastExitCode(this, 1);
                    return;
                }
                // Emit one TextOutput per line, no spurious trailing empty.
                var lines = text.Split('\n');
                int limit = lines.Length;
                if (limit > 0 && lines[limit - 1].Length == 0) limit--;
                for (int i = 0; i < limit; i++)
                {
                    WriteObject(BashRuntime.NewBashObject(lines[i]));
                }
            }
            FileSystemHelpers.SetLastExitCode(this, 0);
            return;
        }

        // Interactive path. Try native less; otherwise fall back to
        // pass-through so we never hang an interactive shell waiting for
        // a non-existent binary.
        string? nativeSource = null;
        try
        {
            var probe = InvokeCommand.InvokeScript(
                "Get-Command less -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1");
            if (probe.Count > 0 && probe[0] != null)
            {
                nativeSource = probe[0].Properties["Source"]?.Value as string;
            }
        }
        catch { /* fall through */ }

        if (string.IsNullOrEmpty(nativeSource))
        {
            // No native less — degrade to passthrough emission.
            foreach (var item in _pipelineItems)
            {
                if (item != null) WriteObject(item);
            }
            return;
        }

        string? tempFile = null;
        try
        {
            var pagerArgs = new List<string>(passthrough);
            if (hasPipeline)
            {
                tempFile = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"ps-bash-less-{Guid.NewGuid():N}.txt");
                var sb = new StringBuilder();
                foreach (var item in _pipelineItems)
                {
                    var text = BashRuntime.GetBashText(item);
                    sb.Append(text);
                    if (!text.EndsWith("\n", StringComparison.Ordinal)) sb.Append('\n');
                }
                System.IO.File.WriteAllText(tempFile, sb.ToString(), new UTF8Encoding(false));
                pagerArgs.Insert(0, tempFile);
            }

            var psi = new ProcessStartInfo
            {
                FileName = nativeSource!,
                UseShellExecute = false,
                WorkingDirectory = SessionState.Path.CurrentLocation.Path,
            };
            foreach (var a in pagerArgs) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                FileSystemHelpers.WriteBashError(this,
                    "less: failed to start native less executable");
                FileSystemHelpers.SetLastExitCode(this, 126);
                return;
            }
            proc.WaitForExit();
            FileSystemHelpers.SetLastExitCode(this, proc.ExitCode);
        }
        finally
        {
            if (tempFile != null)
            {
                try { System.IO.File.Delete(tempFile); } catch { /* best-effort */ }
            }
        }
    }
}
