using System.Diagnostics;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashBash</c> function
/// (REFACTOR-2 follow-on). Implements the bash builtin <c>bash</c> — runs a
/// nested ps-bash transpiler against a script string (<c>-c "..."</c>), a
/// script file operand, stdin (<c>-</c>), or emits <c>--version</c> info.
///
/// Behavioral parity oracle: the original psm1 function. The oracle's exact
/// dispatch — locate the ps-bash executable (parent process path → on-PATH
/// Get-Command → sibling of the current PowerShell binary), then shell out
/// to it via <c>&amp; $psBashExe @Arguments 2>&amp;1</c> and re-emit each
/// non-error output object's <c>BashText</c> via <c>Emit-BashLine</c>, with
/// <c>ErrorRecord</c> items routed to <c>[Console]::Error</c> — is preserved
/// byte-for-byte. Forwarding to a fresh ps-bash child rather than the
/// in-process <c>Invoke-BashEval</c> matches the oracle and keeps a nested
/// <c>bash</c> invocation isolated from the caller's runspace state (no
/// LASTEXITCODE crosstalk, no errexit / variable leakage).
///
/// <para><b>Colliding flag:</b> bash's <c>-c "script"</c> prefix-collides
/// with the PowerShell common parameter <c>-Confirm</c> under the cmdlet
/// binder. The remedy from the playbook collision table — declare a
/// value-bearing parameter with the literal single-letter name <c>C</c> so
/// exact-name match beats the common-parameter prefix match — is applied
/// here.</para>
///
/// <para><b>Directive 12:</b> the script body passed via <c>-c</c> is
/// forwarded as a single positional argument to a child ps-bash process via
/// <see cref="ProcessStartInfo.ArgumentList"/> — never concatenated into a
/// shell or script body. The nested ps-bash performs its own transpile +
/// execute, where bash-level quoting rules apply to the inner script
/// (e.g. <c>$(throw)</c> is evaluated as a bash command substitution of the
/// command <c>throw</c>, which fails inside ps-bash and is reported by the
/// child — the host cmdlet itself does not re-parse the string). The
/// <c>--help</c> path delegates to the psm1 <c>Show-BashHelp</c> via a
/// parameter-bound <c>InvokeCommand.InvokeScript</c> (AOT-safe).</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashBash")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashBashCommand : PSCmdlet
{
    /// <summary>
    /// Bash's <c>-c SCRIPT</c> one-shot flag. Declared as a literal
    /// single-letter parameter name <c>C</c> so the cmdlet binder routes
    /// the bare token by exact-name match — beating the common-parameter
    /// prefix-match against <c>-Confirm</c>.
    /// </summary>
    [Parameter]
    public string? C { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        // Reconstruct the argument vector exactly as the oracle saw it. The
        // binder may have consumed the bare token "-c <value>" into the C
        // property; if so, splice the equivalent ["-c", value] tokens back
        // into the forwarded args at the front (oracle parity).
        var forwarded = new List<string>();
        if (!string.IsNullOrEmpty(C))
        {
            forwarded.Add("-c");
            forwarded.Add(C);
        }
        if (Arguments != null)
        {
            forwarded.AddRange(Arguments);
        }

        if (forwarded.Contains("--help", StringComparer.Ordinal))
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "bash"))
            {
                WriteObject(line);
            }
            return;
        }

        // --version short-circuit: emit the ps-bash version banner without
        // spawning a child. Matches the oracle's `Emit-BashLine -Text`
        // shape (one bare TextOutput per line of the banner).
        if (forwarded.Contains("--version", StringComparer.Ordinal))
        {
            var version = ResolveModuleVersion() ?? "0.7.6";
            var banner = $"ps-bash, version {version}\nBash-to-PowerShell transpiler";
            foreach (var obj in BashRuntime.EmitBashLines(banner))
            {
                WriteObject(obj);
            }
            return;
        }

        // Resolve the ps-bash executable, matching the oracle's three-tier
        // preference: 1) the parent process binary (via $__parentPid global
        // set by the host); 2) Get-Command ps-bash; 3) sibling of the
        // current PowerShell process's MainModule path.
        string? psBashExe = ResolvePsBashExecutable();
        if (string.IsNullOrEmpty(psBashExe))
        {
            FileSystemHelpers.WriteBashError(this, "bash: ps-bash executable not found");
            return;
        }

        // Shell out. UseShellExecute=false + redirected streams keeps every
        // user-controlled token bound through ArgumentList (Directive 12).
        var psi = new ProcessStartInfo
        {
            FileName = psBashExe!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in forwarded)
        {
            psi.ArgumentList.Add(a);
        }

        int exitCode;
        string stdout;
        string stderr;
        try
        {
            // Bounded spawn + concurrent stdout/stderr drain + kill-tree on
            // timeout, with stdin closed to EOF (RunChildProcess). A no-args /
            // REPL-mode child sees EOF and exits instead of hanging on a read,
            // and a nested ps-bash that itself wedges can no longer block the
            // parent host runspace forever (the #3 wedge, recursively).
            var spawn = BashRuntime.RunChildProcess(psi);
            stdout = spawn.Stdout;
            stderr = spawn.Stderr;
            exitCode = spawn.ExitCode;
        }
        catch (Exception ex)
        {
            FileSystemHelpers.WriteBashError(this, "bash: " + ex.Message);
            SessionState.PSVariable.Set("global:LASTEXITCODE", 1);
            return;
        }

        // Forward child exit code to the caller's $LASTEXITCODE — oracle parity.
        SessionState.PSVariable.Set("global:LASTEXITCODE", exitCode);

        if (!string.IsNullOrEmpty(stderr))
        {
            Console.Error.Write(stderr);
        }

        if (!string.IsNullOrEmpty(stdout))
        {
            // Strip a single trailing newline so Emit-BashLine does not
            // synthesize a spurious empty trailing line. EmitBashLines
            // splits on '\n' and emits one object per slice — a trailing
            // '\n' would otherwise produce an extra empty record.
            var normalized = stdout.Replace("\r\n", "\n");
            if (normalized.EndsWith('\n'))
                normalized = normalized.Substring(0, normalized.Length - 1);
            foreach (var obj in BashRuntime.EmitBashLines(normalized))
            {
                WriteObject(obj);
            }
        }
    }

    private string? ResolveModuleVersion()
    {
        try
        {
            // Two probe sources: (1) the imported PsBash module's Version
            // property when the manifest path was used; (2) the
            // $global:BashVersion string the psm1 sets at the bottom of its
            // body when loaded as a script. Either is acceptable — the
            // oracle picked whichever was available, defaulting to "0.7.6".
            var result = InvokeCommand.InvokeScript(
                "$m = Get-Module PsBash -ErrorAction SilentlyContinue | Select-Object -First 1 ; " +
                "if ($m -and $m.Version) { $m.Version.ToString() } " +
                "elseif ($global:BashVersion) { $global:BashVersion } " +
                "else { $null }");
            if (result.Count > 0 && result[0] != null)
            {
                var s = result[0].BaseObject as string;
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }
        catch
        {
            // Best-effort; fall through to the default below.
        }
        return null;
    }

    private string? ResolvePsBashExecutable()
    {
        // Tier 1: $global:__parentPid → parent process MainModule.FileName.
        // This is the exact binary the user spawned; preferred so a dev
        // build's nested `bash -c` re-enters the same dev build, not a
        // PATH-installed one.
        try
        {
            var pidObj = SessionState.PSVariable.GetValue("global:__parentPid");
            if (pidObj != null && LanguagePrimitives.TryConvertTo<int>(pidObj, out int parentPid) && parentPid > 0)
            {
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    var path = parent.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                        return path;
                }
                catch
                {
                    // Parent gone / not accessible — fall through.
                }
            }
        }
        catch
        {
            // Variable lookup failed — fall through.
        }

        // Tier 2: Get-Command ps-bash on PATH.
        try
        {
            var probe = InvokeCommand.InvokeScript(
                "Get-Command ps-bash -ErrorAction SilentlyContinue | Select-Object -First 1");
            if (probe.Count > 0 && probe[0] != null)
            {
                var src = probe[0].Properties["Source"]?.Value as string;
                if (!string.IsNullOrEmpty(src))
                    return src;
            }
        }
        catch
        {
            // Get-Command failure — fall through.
        }

        // Tier 3: sibling of the current PowerShell process.
        try
        {
            using var cur = Process.GetCurrentProcess();
            var curMain = cur.MainModule?.FileName;
            if (!string.IsNullOrEmpty(curMain))
            {
                var dir = System.IO.Path.GetDirectoryName(curMain);
                if (!string.IsNullOrEmpty(dir))
                {
                    var candidate = System.IO.Path.Combine(dir, "ps-bash");
                    if (OperatingSystem.IsWindows())
                        candidate += ".exe";
                    if (System.IO.File.Exists(candidate))
                        return candidate;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
