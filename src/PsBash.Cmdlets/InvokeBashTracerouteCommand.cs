using System.Diagnostics;
using System.Management.Automation;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>traceroute</c> / <c>tracert</c> as styled objects. Each hop becomes a <c>PsBash.TraceHop</c>
/// object carrying the native hop line as <c>BashText</c> plus typed properties (<c>Hop</c> /
/// <c>Address</c> / <c>Time</c>) and a latency <c>class</c> (<c>ok</c> / <c>slow</c> / <c>high</c> /
/// <c>timeout</c>). Pipe to <c>Show-Styled</c> (or use <c>PSBASH_DEFAULT_FORMAT=interactive</c>) for
/// the navigable styled viewer; the <c>net</c> stylesheet colours hops by latency.
/// </summary>
/// <remarks>
/// Prefers the native <c>traceroute</c> (POSIX) / <c>tracert</c> (Windows) binary — TTL-based
/// tracing needs raw sockets, which managed <see cref="Ping"/> cannot open unprivileged on Linux.
/// When no native binary is on PATH it falls back to a managed TTL probe (works only when the process
/// has <c>cap_net_raw</c> / runs privileged; otherwise each hop reports the privilege error).
/// </remarks>
[Cmdlet(VerbsLifecycle.Invoke, "BashTraceroute")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashTracerouteCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    // First "<n> ms" / "<n.n> ms" round-trip time on a hop line.
    private static readonly Regex s_latency = new(@"([0-9]+(?:\.[0-9]+)?)\s*ms", RegexOptions.Compiled);
    private static readonly Regex s_hopNum = new(@"^\s*(\d+)", RegexOptions.Compiled);

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "traceroute", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            WriteObject(BashRuntime.NewBashObject("usage: traceroute [-m maxhops] [-w timeout] HOST"));
            return;
        }

        string? host = null;
        int maxHops = 30;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if ((a == "-m" || a == "-h") && i + 1 < args.Length && int.TryParse(args[++i], out var m))
            {
                maxHops = Math.Clamp(m, 1, 64);
            }
            else if ((a == "-w" || a == "-W") && i + 1 < args.Length)
            {
                i++; // timeout — consumed, applied only on the managed fallback
            }
            else if (!a.StartsWith('-'))
            {
                host ??= a;
            }
        }

        if (string.IsNullOrEmpty(host))
        {
            FileSystemHelpers.WriteBashError(this, "traceroute: usage error: Destination address required");
            return;
        }

        if (!TryNativeTrace(host!, maxHops))
        {
            ManagedTrace(host!, maxHops);
        }
    }

    /// <summary>Run the native traceroute/tracert binary and emit one styled hop object per hop line. Returns false when no native binary is available.</summary>
    private bool TryNativeTrace(string host, int maxHops)
    {
        var (exe, hopFlag) = OperatingSystem.IsWindows() ? ("tracert", "-h") : ("traceroute", "-m");
        var resolved = ResolveOnPath(exe);
        if (resolved is null)
        {
            return false;
        }

        var psi = new ProcessStartInfo(resolved)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(hopFlag);
        psi.ArgumentList.Add(maxHops.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(host);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            // Drain stderr concurrently. It is redirected but we only read stdout
            // synchronously below; once the child writes more than the ~4KB stderr
            // pipe buffer it blocks on the write, which stops it producing stdout,
            // which wedges our ReadLine forever (and the unbounded WaitForExit after
            // it). An async drain empties the buffer so the child keeps flowing.
            proc.ErrorDataReceived += static (_, _) => { /* discard: stderr is noise here */ };
            proc.BeginErrorReadLine();

            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                if (Stopping)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    break;
                }

                var match = s_hopNum.Match(line);
                if (!match.Success)
                {
                    continue; // header / blank — only emit numbered hop lines
                }

                WriteObject(BuildHopFromLine(host, int.Parse(match.Groups[1].Value), line.TrimEnd()));
            }

            // Stdout has reached EOF (child closed it), so the process is done or
            // nearly so — but bound the wait anyway so a child that closes stdout
            // without exiting cannot hang the host runspace. Kill on timeout.
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static PSObject BuildHopFromLine(string host, int hop, string line)
    {
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.TraceHop");

        var lat = s_latency.Match(line);
        var timedOut = line.Contains('*');
        double ms = lat.Success ? double.Parse(lat.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;

        obj.Properties.Add(new PSNoteProperty("Hop", hop));
        obj.Properties.Add(new PSNoteProperty("Target", host));
        obj.Properties.Add(new PSNoteProperty("Time", ms));
        obj.Properties.Add(new PSNoteProperty("Status", lat.Success ? "ok" : (timedOut ? "timeout" : "unknown")));
        obj.Properties.Add(new PSNoteProperty("class", lat.Success ? LatencyClass(ms) : "timeout"));
        obj.Properties.Add(new PSNoteProperty("BashText", line.Trim()));
        return obj;
    }

    /// <summary>Managed TTL probe fallback (needs raw-socket privilege; emits the privilege error per hop otherwise).</summary>
    private void ManagedTrace(string host, int maxHops)
    {
        var buffer = Encoding.ASCII.GetBytes("ps-bash-traceroute");
        using var ping = new Ping();
        for (var hop = 1; hop <= maxHops; hop++)
        {
            if (Stopping)
            {
                break;
            }

            PingReply? reply = null;
            string? error = null;
            try
            {
                reply = ping.Send(host, 4000, buffer, new PingOptions(hop, true));
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                error = (ex.InnerException ?? ex).Message;
            }

            var obj = new PSObject();
            obj.TypeNames.Insert(0, "PsBash.TraceHop");
            var responded = reply is { Status: IPStatus.TtlExpired or IPStatus.Success };
            var reached = reply is { Status: IPStatus.Success };
            var addr = responded ? (reply!.Address?.ToString() ?? "*") : "*";
            var ms = responded ? reply!.RoundtripTime : -1;

            obj.Properties.Add(new PSNoteProperty("Hop", hop));
            obj.Properties.Add(new PSNoteProperty("Target", host));
            obj.Properties.Add(new PSNoteProperty("Address", addr));
            obj.Properties.Add(new PSNoteProperty("Time", ms));
            obj.Properties.Add(new PSNoteProperty("Status", responded ? (reached ? "reached" : "ok") : (error ?? reply?.Status.ToString() ?? "TimedOut")));
            obj.Properties.Add(new PSNoteProperty("class", responded ? LatencyClass(ms) : "timeout"));
            obj.Properties.Add(new PSNoteProperty("BashText", responded ? $"{hop,2}  {addr}  {ms} ms" : $"{hop,2}  *  ({error ?? reply?.Status.ToString() ?? "TimedOut"})"));
            WriteObject(obj);

            if (reached)
            {
                break;
            }
        }
    }

    private static string? ResolveOnPath(string exe)
    {
        var names = OperatingSystem.IsWindows() ? new[] { exe + ".exe", exe } : new[] { exe };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch { /* malformed PATH entry */ }
            }
        }

        return null;
    }

    private static string LatencyClass(double ms) => ms switch
    {
        < 80 => "ok",
        < 200 => "slow",
        _ => "high",
    };
}
