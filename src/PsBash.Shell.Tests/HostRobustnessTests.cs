using System.Diagnostics;
using PsBash.Core.Runtime.Ipc;
using Xunit;

namespace PsBash.Shell.Tests;

/// <summary>
/// No input may crash the shared host. A ps-bash daemon serves every command of a session; when bash
/// input killed it, every concurrent command on it died too and the launcher could only say
/// "connection reset". These run against a PRIVATE daemon endpoint, so a regression fails an
/// assertion here instead of taking down anything else.
/// </summary>
[Trait("Category", "Spawn")]
public class HostRobustnessTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(60);

    private static string Rep(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    private static string DeepPayload(string shape) => shape switch
    {
        "cmdsub" => "echo " + Rep("$(echo ", 5000) + "x" + Rep(")", 5000),
        "arith" => "echo $((" + Rep("(", 5000) + "1" + Rep(")", 5000) + "))",
        "param" => "echo " + Rep("${x:-", 5000) + "d" + Rep("}", 5000),
        "if" => Rep("if true; then ", 5000) + "echo deep; " + Rep("fi; ", 5000),
        "subshell" => Rep("( ", 5000) + "echo s" + Rep(" )", 5000),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    // Before NestingGuard each of these overflowed the stack INSIDE the host (eval/source run the
    // transpiler there): the daemon died (no exit line in its log), and the launcher crashed with
    // "Unhandled exception … host process exited".
    [SkippableTheory]
    [InlineData("cmdsub", "eval")]
    [InlineData("arith", "eval")]
    [InlineData("param", "eval")]
    [InlineData("if", "source")]
    [InlineData("subshell", "source")]
    public async Task DeepNestingTranspiledInHost_FailsOnlyThatCommand_HostAndOtherSessionSurvive(string shape, string via)
    {
        Skip.If(InteractiveShellHarness.FindPsBashBinary() is null, "ps-bash binary not built");
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash", "robust-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var endpoint = PsBashTestProcess.CreateEndpoint();
        try
        {
            var payload = Path.Combine(dir, "deep.sh");
            await File.WriteAllTextAsync(payload, DeepPayload(shape));
            var p = payload.Replace('\\', '/');

            var (warmExit, _, _) = await RunAsync(endpoint, "-c", "echo warm");
            Assert.Equal(0, warmExit);
            int hostPid = ReadHostPid(endpoint);

            var otherBegun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var other = RunAsync(endpoint, new[] { "-c", "echo begun; sleep 8; echo other-ok" }, "begun", otherBegun);
            await otherBegun.Task.WaitAsync(Step); // the other session is running on the host now

            string cmd = via == "eval"
                ? $"eval \"$(cat '{p}')\"; echo \"rc=$?\""
                : $"source '{p}'; echo \"rc=$?\"";
            var (exit, stdout, stderr) = await RunAsync(endpoint, "-c", cmd);

            Assert.Contains("nesting too deep", stderr);
            Assert.DoesNotContain("Unhandled exception", stderr);
            Assert.Contains("rc=", stdout);         // the failure ended that statement, not the script
            Assert.NotEqual(125, exit);

            var (otherExit, otherOut, _) = await other;
            Assert.Equal(0, otherExit);
            Assert.Contains("other-ok", otherOut);  // the concurrent session on the same host finished

            Assert.True(IsAlive(hostPid), $"host pid {hostPid} died");
            var (afterExit, afterOut, _) = await RunAsync(endpoint, "-c", "echo alive");
            Assert.Equal(0, afterExit);
            Assert.Contains("alive", afterOut);
            Assert.Equal(hostPid, ReadHostPid(endpoint)); // served by the SAME host, not a respawn
        }
        finally
        {
            KillHost(endpoint);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // Evaluated at RUN time inside the host, not by the transpiler:
    //  - `kill -9 $$`: $$ is the shared host's pid, and Process.Kill on it ended every session.
    //    bash's outcome — the script dies of the signal (137) — now applies to that command only.
    //  - `[ \( \( … x \) \) ]` / a long -o chain: the test evaluator recursed per level/operator on
    //    the host's pipeline thread and overflowed.
    //  - `$(( $e ))` with a deep expression in $e: the runtime arithmetic parser, same.
    [SkippableTheory]
    [InlineData("kill-self", "kill -9 $$; echo after", "137", null)]
    // rc is 1, not bash's 2: every ps-bash `test` syntax error returns 1 today (a separate parity gap).
    [InlineData("test-paren", "@TESTPAREN", "rc=", "too deeply nested")]
    // 50,000 operands: emitted as a PowerShell -or chain, PowerShell's own compiler overflowed its
    // pipeline thread (VariableAnalysis.VisitBinaryExpression) and the host died; now a parse error.
    [InlineData("test-or-chain", "@TESTOR", null, "test expression too long")]
    // Survival only: a runtime arithmetic error is currently swallowed as 0 (a separate parity gap).
    [InlineData("arith-runtime", "@ARITH", "rc=", null)]
    public async Task RuntimeHostileInput_FailsOnlyThatCommand_HostSurvives(string name, string script, string? expectOut, string? expectErr)
    {
        Skip.If(InteractiveShellHarness.FindPsBashBinary() is null, "ps-bash binary not built");
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash", "robust-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var endpoint = PsBashTestProcess.CreateEndpoint();
        try
        {
            string body = script switch
            {
                // 3000: under the 8192-word breadth cap (so it reaches the RUNTIME evaluator) yet far deeper
                // than the evaluator's nesting bound.
                "@TESTPAREN" => "[ " + Rep("\\( ", 3000) + "x" + Rep(" \\)", 3000) + " ]; echo rc=$?",
                "@TESTOR" => "[ " + Rep("a -o ", 50000) + "a ]; echo rc=$?",
                "@ARITH" => "e='" + Rep("(", 20000) + "1" + Rep(")", 20000) + "'; echo $(( $e )); echo rc=$?",
                _ => script,
            };
            var file = Path.Combine(dir, name + ".sh");
            await File.WriteAllTextAsync(file, body + "\n");

            var (warmExit, _, _) = await RunAsync(endpoint, "-c", "echo warm");
            Assert.Equal(0, warmExit);
            int hostPid = ReadHostPid(endpoint);

            var (exit, stdout, stderr) = await RunAsync(endpoint, file);
            Assert.DoesNotContain("Unhandled exception", stderr);
            if (name == "kill-self")
            {
                Assert.Equal(137, exit);              // bash: killed by SIGKILL
                Assert.DoesNotContain("after", stdout);
            }
            else if (expectOut is not null)
            {
                Assert.Contains(expectOut, stdout);   // the failure ended that statement only
            }
            if (expectErr is not null) Assert.Contains(expectErr, stderr);

            Assert.True(IsAlive(hostPid), $"host pid {hostPid} died");
            var (afterExit, afterOut, _) = await RunAsync(endpoint, "-c", "echo alive");
            Assert.Equal(0, afterExit);
            Assert.Contains("alive", afterOut);
            Assert.Equal(hostPid, ReadHostPid(endpoint));
        }
        finally
        {
            KillHost(endpoint);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // `yes | sort` buffers without bound inside the shared host (~1 GB/min): left alone it ends in an
    // OutOfMemory crash of every session. Past the host's limit the COMMAND is stopped instead.
    [SkippableFact]
    public async Task UnboundedMemoryCommand_IsStoppedAtTheLimit_HostSurvives()
    {
        Skip.If(InteractiveShellHarness.FindPsBashBinary() is null, "ps-bash binary not built");
        var endpoint = PsBashTestProcess.CreateEndpoint();
        // An idle host is ~70 MB private; 500 MB leaves margin yet trips within seconds.
        var env = new Dictionary<string, string?> { ["PSBASH_HOST_MAX_MB"] = "500" };
        try
        {
            var (warmExit, _, _) = await RunAsync(endpoint, new[] { "-c", "echo warm" }, null, null, env);
            Assert.Equal(0, warmExit);
            int hostPid = ReadHostPid(endpoint);

            // Growth rate depends on machine load (≈15 MB/s idle): allow for a slow box.
            var (exit, _, stderr) = await RunAsync(endpoint, new[] { "-c", "yes | sort | head -n1" }, null, null, env,
                timeout: TimeSpan.FromSeconds(180));
            Assert.Equal(137, exit);
            Assert.Contains("exceeded its memory limit", stderr);

            Assert.True(IsAlive(hostPid), $"host pid {hostPid} died");
            var (afterExit, afterOut, _) = await RunAsync(endpoint, new[] { "-c", "echo alive" }, null, null, env);
            Assert.Equal(0, afterExit);
            Assert.Contains("alive", afterOut);
            Assert.Equal(hostPid, ReadHostPid(endpoint));
        }
        finally { KillHost(endpoint); }
    }

    // A host that dies mid-command (here: killed externally) must surface as ONE diagnostic line
    // naming the host pid, exit 125 — for script files too. The .sh path ran the command without the
    // launcher's failure mapping, so it crashed with "Unhandled exception. System.IO.IOException".
    [SkippableTheory]
    [InlineData("script")]
    [InlineData("-c")]
    public async Task HostKilledMidCommand_OneLineDiagnosticNamingHostExit_Exit125(string mode)
    {
        Skip.If(InteractiveShellHarness.FindPsBashBinary() is null, "ps-bash binary not built");
        var dir = Path.Combine(Path.GetTempPath(), "ps-bash", "robust-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var endpoint = PsBashTestProcess.CreateEndpoint();
        try
        {
            var (warmExit, _, _) = await RunAsync(endpoint, "-c", "echo warm");
            Assert.Equal(0, warmExit);
            int hostPid = ReadHostPid(endpoint);

            var script = Path.Combine(dir, "long.sh");
            await File.WriteAllTextAsync(script, "echo started; sleep 30; echo never\n");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = mode == "script"
                ? RunAsync(endpoint, new[] { script }, "started", started)
                : RunAsync(endpoint, new[] { "-c", "echo started; sleep 30; echo never" }, "started", started);
            await started.Task.WaitAsync(Step); // the command is executing on the host
            using (var host = Process.GetProcessById(hostPid)) host.Kill();

            var (exit, stdout, stderr) = await run;
            Assert.Equal(125, exit);
            Assert.DoesNotContain("Unhandled exception", stderr);
            Assert.DoesNotContain("   at ", stderr);  // no stack trace
            Assert.Contains($"host process (pid {hostPid}) exited", stderr);
            Assert.DoesNotContain("never", stdout);
        }
        finally
        {
            KillHost(endpoint);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static Task<(int Exit, string Stdout, string Stderr)> RunAsync(string endpoint, params string[] args)
        => RunAsync(endpoint, args, marker: null, onMarker: null);

    /// <summary>Run the launcher to exit. When <paramref name="marker"/> is given,
    /// <paramref name="onMarker"/> completes as soon as a stdout line equals it — a condition wait,
    /// never a fixed delay.</summary>
    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        string endpoint, string[] args, string? marker, TaskCompletionSource? onMarker,
        IReadOnlyDictionary<string, string?>? env = null, TimeSpan? timeout = null)
    {
        var limit = timeout ?? Step;
        var psi = PsBashTestProcess.Create(args, env: env, ipcEndpoint: endpoint);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        using var proc = new Process { StartInfo = psi };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            if (marker is not null && e.Data.Trim() == marker) onMarker?.TrySetResult();
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.StandardInput.Close();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(limit);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"ps-bash {string.Join(' ', args)} did not exit within {limit.TotalSeconds}s");
        }
        proc.WaitForExit(); // drain the async readers
        onMarker?.TrySetException(new InvalidOperationException($"'{marker}' never appeared on stdout"));
        lock (stdout) lock (stderr) return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static int ReadHostPid(string endpoint)
    {
        var colon = endpoint.IndexOf(':');
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var pid = HostMetadata.TryRead(endpoint[..colon], endpoint[(colon + 1)..])?.Pid;
            if (pid is > 0) return pid.Value;
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("host sidecar never appeared");
            Thread.Sleep(50);
        }
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void KillHost(string endpoint)
    {
        var colon = endpoint.IndexOf(':');
        var pid = HostMetadata.TryRead(endpoint[..colon], endpoint[(colon + 1)..])?.Pid;
        if (pid is > 0)
        {
            try { using var p = Process.GetProcessById(pid.Value); p.Kill(entireProcessTree: true); } catch { }
        }
    }
}
