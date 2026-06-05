using System.Management.Automation;
using System.Net.NetworkInformation;

namespace PsBash.Cmdlets;

/// <summary>
/// <c>ping</c> as styled objects. Emits one <c>PsBash.PingReply</c> object per probe carrying both a
/// native-style <c>BashText</c> line (so <c>ping host</c> reads like the real tool and pipes as text)
/// and typed properties (<c>Seq</c> / <c>Address</c> / <c>Time</c> / <c>Ttl</c> / <c>Status</c>) plus
/// a latency <c>class</c> (<c>ok</c> / <c>slow</c> / <c>high</c> / <c>timeout</c>). Pipe to
/// <c>Show-Styled</c> — or set <c>PSBASH_DEFAULT_FORMAT=interactive</c> — for the navigable styled
/// viewer (the <c>net</c> stylesheet colours replies by latency).
/// </summary>
/// <remarks>Uses <see cref="Ping"/> (cross-platform managed ICMP), so it needs no external binary.</remarks>
[Cmdlet(VerbsLifecycle.Invoke, "BashPing")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashPingCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "ping", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            WriteObject(BashRuntime.NewBashObject("usage: ping [-c count] [-W timeout] HOST"));
            return;
        }

        string? host = null;
        int count = 4;
        double timeoutSec = 4;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            // `-c N` / `-n N` (count, GNU / Windows), `-W secs` (timeout). `-t` (continuous) caps at
            // 100 so a non-interactive snapshot can never run unbounded.
            if ((a == "-c" || a == "-n") && i + 1 < args.Length && int.TryParse(args[++i], out var c))
            {
                count = Math.Max(1, c);
            }
            else if (a == "-W" && i + 1 < args.Length && double.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out var w))
            {
                timeoutSec = w;
            }
            else if (a == "-t")
            {
                count = 100;
            }
            else if (!a.StartsWith('-'))
            {
                host ??= a;
            }
        }

        if (string.IsNullOrEmpty(host))
        {
            FileSystemHelpers.WriteBashError(this, "ping: usage error: Destination address required");
            return;
        }

        var timeoutMs = (int)Math.Max(1, timeoutSec * 1000);
        using var ping = new Ping();
        for (var seq = 1; seq <= count; seq++)
        {
            if (Stopping)
            {
                break;
            }

            PingReply? reply = null;
            string? error = null;
            try
            {
                reply = ping.Send(host, timeoutMs);
            }
            catch (Exception ex)
            {
                error = (ex.InnerException ?? ex).Message;
            }

            WriteObject(BuildReply(host!, seq, reply, error));
        }
    }

    private static PSObject BuildReply(string host, int seq, PingReply? reply, string? error)
    {
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.PingReply");

        if (reply is { Status: IPStatus.Success })
        {
            var ms = reply.RoundtripTime;
            var addr = reply.Address?.ToString() ?? host;
            var ttl = reply.Options?.Ttl ?? 0;
            obj.Properties.Add(new PSNoteProperty("Seq", seq));
            obj.Properties.Add(new PSNoteProperty("Target", host));
            obj.Properties.Add(new PSNoteProperty("Address", addr));
            obj.Properties.Add(new PSNoteProperty("Time", ms));
            obj.Properties.Add(new PSNoteProperty("Ttl", ttl));
            obj.Properties.Add(new PSNoteProperty("Status", "ok"));
            obj.Properties.Add(new PSNoteProperty("class", LatencyClass(ms)));
            obj.Properties.Add(new PSNoteProperty("BashText",
                $"reply from {addr}: icmp_seq={seq} ttl={ttl} time={ms} ms"));
        }
        else
        {
            var status = error ?? reply?.Status.ToString() ?? "TimedOut";
            obj.Properties.Add(new PSNoteProperty("Seq", seq));
            obj.Properties.Add(new PSNoteProperty("Target", host));
            obj.Properties.Add(new PSNoteProperty("Address", host));
            obj.Properties.Add(new PSNoteProperty("Time", -1L));
            obj.Properties.Add(new PSNoteProperty("Ttl", 0));
            obj.Properties.Add(new PSNoteProperty("Status", status));
            obj.Properties.Add(new PSNoteProperty("class", "timeout"));
            obj.Properties.Add(new PSNoteProperty("BashText", $"request timeout for icmp_seq {seq} ({status})"));
        }

        return obj;
    }

    /// <summary>Latency band for the <c>class</c> hook the <c>net</c> stylesheet colours.</summary>
    private static string LatencyClass(long ms) => ms switch
    {
        < 80 => "ok",
        < 200 => "slow",
        _ => "high",
    };
}
