using System.Management.Automation;
using System.Net.NetworkInformation;
using PsBash.Cmdlets.Args;

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

    /// <summary>
    /// Decoy for a DIRECT <c>-W SECS</c>: a bare <c>-W</c> is ambiguous (<c>-WarningAction</c> /
    /// <c>-WarningVariable</c>). PowerShell names are case-insensitive, so a direct <c>-w</c> lands here too and is
    /// read as <c>-W</c>; the transpiler passes every word quoted (<c>PsEmitter.OrderedArgCommands</c>) and keeps them
    /// apart.
    /// </summary>
    [Parameter] public string? W { get; set; }

    private const string OptCount = "count", OptTimeout = "timeout", OptContinuous = "continuous";

    // iputils options ps-bash refuses (a string[] on purpose: CommonParameterCollisionGuardTests reads it).
    private static readonly string[] PingValidButUnsupported =
    {
        "-w", "-i", "-s", "-q", "-4", "-6", "-I", "-l", "-p", "-D", "-O", "-a", "-f",
    };

    /// <summary>ping's option surface for the shared ordered parser: <c>-c N</c> / <c>-n N</c> (count, iputils /
    /// Windows), <c>-W SECS</c> (reply timeout), <c>-t</c> (continuous, capped at 100 probes).</summary>
    internal static readonly OptSpecSet PingSpec = new(
        new[]
        {
            new OptSpec(OptCount, 'c', null, OptKind.Value),
            new OptSpec(OptCount, 'n', null, OptKind.Value),
            new OptSpec(OptTimeout, 'W', null, OptKind.Value),
            new OptSpec(OptContinuous, 't', null),
        },
        validButUnsupported: PingValidButUnsupported,
        gnuInfoOptions: true,
        usageExitCode: 2);

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();
        if (W is not null)
        {
            var list = new List<string>(args.Length + 2) { "-W", W };
            list.AddRange(args);
            args = list.ToArray();
        }
        FileSystemHelpers.SetLastExitCode(this, 0);
        var parsed = ArgParser.Parse(args, PingSpec);
        if (FileSystemHelpers.TryWriteParseError(this, "ping", parsed)) return;
        if (parsed.Has(OptSpecSet.VersionId))
        {
            FileSystemHelpers.TryHandleVersion(this, "ping", new[] { "--version" });
            return;
        }
        if (parsed.Has(OptSpecSet.HelpId))
        {
            WriteObject(BashRuntime.NewBashObject("usage: ping [-c count] [-W timeout] [-t] HOST"));
            return;
        }

        int count = 4;
        double timeoutSec = 4;
        // ArgParser yields options in argv order, so the last -c / -W wins as in iputils.
        foreach (var t in parsed.Tokens)
        {
            if (t.Kind != ArgTokKind.Option) continue;
            switch (t.OptId)
            {
                case OptCount:
                    if (!int.TryParse(t.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var c) || c < 1)
                    {
                        Fail($"ping: invalid argument: {GnuQuote.Always(t.Value ?? "")}");
                        return;
                    }
                    count = c;
                    break;
                case OptTimeout:
                    // iputils: a garbage -W is warned about and ignored (the default timeout stays).
                    if (double.TryParse(t.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) && w >= 0)
                        timeoutSec = w;
                    else
                        FileSystemHelpers.WriteStderr(this, $"ping: option argument contains garbage: {t.Value}\nping: this will become fatal error in the future");
                    break;
                case OptContinuous:
                    count = 100;
                    break;
            }
        }

        var operands = parsed.Operands();
        string? host = operands.Count > 0 ? operands[0] : null;
        if (string.IsNullOrEmpty(host))
        {
            Fail("ping: usage error: Destination address required");
            return;
        }

        // iputils: a name that does not resolve is reported once, before any probe, at status 2.
        if (!System.Net.IPAddress.TryParse(host, out _))
        {
            try { _ = System.Net.Dns.GetHostAddresses(host!); }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
            {
                FileSystemHelpers.WriteStderr(this, $"ping: {host}: Name or service not known");
                FileSystemHelpers.SetLastExitCode(this, ExitStatus(replies: 0, unresolved: true));
                return;
            }
        }

        var timeoutMs = (int)Math.Max(1, timeoutSec * 1000);
        int replies = 0;
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
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                error = (ex.InnerException ?? ex).Message;
            }

            if (reply is { Status: IPStatus.Success }) replies++;
            WriteObject(BuildReply(host!, seq, reply, error));
        }
        FileSystemHelpers.SetLastExitCode(this, ExitStatus(replies, unresolved: false));
    }

    /// <summary>
    /// iputils ping's status: 0 when any probe was answered, 1 when none was (every probe timed out), 2 when the
    /// name did not resolve. REGRESSION: it was 0 whatever happened, so <c>ping -c1 host || alert</c> never fired.
    /// Pure (unit-test seam).
    /// </summary>
    internal static int ExitStatus(int replies, bool unresolved) => unresolved ? 2 : replies > 0 ? 0 : 1;

    /// <summary>iputils' argument errors (bad count, no destination): the message, status 1. An unknown option is
    /// the parser's usage error, status 2.</summary>
    private void Fail(string message)
    {
        FileSystemHelpers.WriteBashError(this, message);
        FileSystemHelpers.SetLastExitCode(this, 1);
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
