using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashHostname</c> function
/// (REFACTOR-2). Prints the local host name, matching the bash <c>hostname</c>
/// builtin.
///
/// Behavioral parity oracle: the original psm1 function. The hostname surface is
/// trivial — no flags besides <c>--help</c>, no pipeline input. The cmdlet calls
/// <see cref="System.Net.Dns.GetHostName"/>, which is the same call the psm1
/// oracle dispatched to. On failure the cmdlet emits a bash-style error via
/// <see cref="FileSystemHelpers.WriteBashError"/> and returns, exactly as the oracle did.
///
/// Output goes through <see cref="BashRuntime.NewBashObject"/> with the default
/// <c>PsBash.TextOutput</c> type, which short-circuits to a bare
/// <see cref="string"/> for the fast path. Both <c>--help</c> and the error
/// surface delegate to psm1 via <c>InvokeCommand.InvokeScript</c> with a
/// parameter-bound script body, so no AOT-incompatible
/// <see cref="ScriptBlock"/> is constructed on the hot path.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashHostname")]
[OutputType(typeof(string))]
public sealed class InvokeBashHostnameCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private static readonly OptSpecSet HostnameSpec = new(
        new[]
        {
            new OptSpec("alias", 'a', "alias"), new OptSpec("all-fqdns", 'A', "all-fqdns"), new OptSpec("boot", 'b', "boot"),
            new OptSpec("domain", 'd', "domain"), new OptSpec("fqdn", 'f', "fqdn"), new OptSpec("fqdn", '\0', "long"),
            new OptSpec("file", 'F', "file", OptKind.Value), new OptSpec("ip-address", 'i', "ip-address"),
            new OptSpec("all-ip-addresses", 'I', "all-ip-addresses"), new OptSpec("short", 's', "short"),
            new OptSpec("yp", 'y', "yp"), new OptSpec("yp", '\0', "nis"),
        },
        allowAbbrev: true,
        gnuInfoOptions: true);

    /// <summary>Debian hostname's usage text (<c>usage()</c>), which it prints on stdout after an unknown option.</summary>
    internal const string HostnameUsage =
        "Usage: hostname [-b] {hostname|-F file}         set host name (from file)\n" +
        "       hostname [-a|-A|-d|-f|-i|-I|-s|-y]       display formatted name\n" +
        "       hostname                                 display host name\n" +
        "\n" +
        "       {yp,nis,}domainname {nisdomain|-F file}  set NIS domain name (from file)\n" +
        "       {yp,nis,}domainname                      display NIS domain name\n" +
        "\n" +
        "       dnsdomainname                            display dns domain name\n" +
        "\n" +
        "       hostname -V|--version|-h|--help          print info and exit\n" +
        "\n" +
        "Program name:\n" +
        "       {yp,nis,}domainname=hostname -y\n" +
        "       dnsdomainname=hostname -d\n" +
        "\n" +
        "Program options:\n" +
        "    -a, --alias            alias names\n" +
        "    -A, --all-fqdns        all long host names (FQDNs)\n" +
        "    -b, --boot             set default hostname if none available\n" +
        "    -d, --domain           DNS domain name\n" +
        "    -f, --fqdn, --long     long host name (FQDN)\n" +
        "    -F, --file             read host name or NIS domain name from given file\n" +
        "    -i, --ip-address       addresses for the host name\n" +
        "    -I, --all-ip-addresses all addresses for the host\n" +
        "    -s, --short            short host name\n" +
        "    -y, --yp, --nis        NIS/YP domain name\n" +
        "\n" +
        "Description:\n" +
        "   This command can get or set the host name or the NIS domain name. You can\n" +
        "   also get the DNS domain or the FQDN (fully qualified domain name).\n" +
        "   Unless you are using bind or NIS for host lookups you can change the\n" +
        "   FQDN (Fully Qualified Domain Name) and the DNS domain name (which is\n" +
        "   part of the FQDN) in the /etc/hosts file.\n";

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "hostname", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "hostname"))
            {
                WriteObject(line);
            }
            return;
        }

        // Debian hostname's getopt_long: an unknown option is getopt's message on stderr, its usage on stdout, and
        // exit(-1) = 255 (REGRESSION: `hostname --bogus` printed the name, status 0). -s is the short name; the other
        // display options keep printing the host name.
        var parsed = ArgParser.Parse(args, HostnameSpec);
        if (parsed.Error is { } err)
        {
            // The status first: a reader that stops after the usage's first line (`| head -1`) ends this command there.
            FileSystemHelpers.WriteBashError(this, err.Message("hostname"));
            FileSystemHelpers.SetLastExitCode(this, 255);
            WriteObject(BashRuntime.NewBashObject(HostnameUsage));
            return;
        }

        string name;
        try
        {
            name = System.Net.Dns.GetHostName();
            if (parsed.Has("short") && name.IndexOf('.') is > 0 and var dot) name = name[..dot];
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            FileSystemHelpers.WriteBashError(this, $"hostname: {ex.Message}");
            return;
        }

        WriteObject(BashRuntime.NewBashObject(name));
    }
}
