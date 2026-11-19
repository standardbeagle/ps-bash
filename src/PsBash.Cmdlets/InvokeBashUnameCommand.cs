using System;
using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// GNU coreutils <c>uname</c>. On Linux and macOS it reports the HOST kernel
/// (<see cref="PsBash.Core.Platform.KernelIdentity"/>, <see cref="FormatHost"/>); on Windows it answers as Git
/// Bash's MSYS does (<see cref="FormatWindows"/>): system name <c>MINGW64_NT-{release}</c>, the lowercased machine
/// name, the OS <c>Major.Minor.Build</c>, <c>x86_64</c> / <c>i686</c>, <c>-p</c> / <c>-i</c> <c>unknown</c>,
/// <c>-o</c> <c>Msys</c>, <c>-v</c> the OS description. <c>-a</c> keeps its documented short form
/// (<c>{sys} {host} {release} {arch} MINGW64</c>; docs/specs/intentional-differences.md) so scripts that grep it
/// keep working.
///
/// ARGV: the shared ordered parser (<see cref="UnameSpec"/>): fields in GNU's order <c>-s -n -r -v -m -p -i -o</c>
/// whatever order they are asked in, long forms, an unknown option or an operand is a usage error (status 1).
/// REGRESSION: <c>-o</c> / <c>-p</c> / <c>-i</c> failed in the binder as ambiguous common-parameter prefixes,
/// <c>-v</c> was eaten as <c>-Verbose</c>, and an unknown option was ignored. The transpiler passes every word
/// quoted (<c>PsEmitter.OrderedArgCommands</c>); the decoys exist for DIRECT calls only and are put back in front
/// of the arguments.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashUname")]
[OutputType(typeof(string))]
public sealed class InvokeBashUnameCommand : BashCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Decoy: a bare <c>-a</c> prefix-matches <c>-Arguments</c>.</summary>
    [Parameter] public SwitchParameter a { get; set; }

    /// <summary>Decoy: a bare <c>-v</c> is <c>-Verbose</c>.</summary>
    [Parameter] public SwitchParameter v { get; set; }

    /// <summary>Decoy: a bare <c>-p</c> is ambiguous (<c>-PipelineVariable</c> / <c>-ProgressAction</c>).</summary>
    [Parameter] public SwitchParameter p { get; set; }

    /// <summary>Decoy: a bare <c>-i</c> is ambiguous (<c>-InformationAction</c> / <c>-InformationVariable</c>).</summary>
    [Parameter] public SwitchParameter i { get; set; }

    /// <summary>Decoy: a bare <c>-o</c> is ambiguous (<c>-OutVariable</c> / <c>-OutBuffer</c>).</summary>
    [Parameter] public SwitchParameter o { get; set; }

    // Field ids in GNU's print order: s n r v m p i o.
    private const string Fields = "snrvmpio";
    private const string OptAll = "all";

    /// <summary>uname's whole option surface, built once for the shared ordered parser.</summary>
    internal static readonly OptSpecSet UnameSpec = new(
        new[]
        {
            new OptSpec(OptAll, 'a', "all"),
            new OptSpec("s", 's', "kernel-name"),
            new OptSpec("n", 'n', "nodename"),
            new OptSpec("r", 'r', "kernel-release"),
            new OptSpec("v", 'v', "kernel-version"),
            new OptSpec("m", 'm', "machine"),
            new OptSpec("p", 'p', "processor"),
            new OptSpec("i", 'i', "hardware-platform"),
            new OptSpec("o", 'o', "operating-system"),
        },
        allowAbbrev: true,
        gnuInfoOptions: true);

    protected override void ProcessRecord()
    {
        var args = BashRuntime.PrependDecoys(Arguments,
            (a.IsPresent, "-a"), (v.IsPresent, "-v"), (p.IsPresent, "-p"), (i.IsPresent, "-i"), (o.IsPresent, "-o"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        var parsed = ArgParser.Parse(args, UnameSpec);
        if (FileSystemHelpers.TryWriteParseError(this, "uname", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "uname", parsed)) return;
        var operands = parsed.Operands();
        if (operands.Count > 0)
        {
            FileSystemHelpers.WriteExtraOperand(this, "uname", operands[0]);
            return;
        }

        var (want, all) = Selection(parsed);
        string line = PsBash.Core.Platform.KernelIdentity.Host() is { } uts
            ? Format(HostValues(uts, OperatingSystem.IsMacOS()), want, all, OperatingSystem.IsMacOS() ? 5 : 8)
            : FormatWindows(want, all);
        WriteObject(BashRuntime.NewBashObject(line));
    }

    /// <summary>The fields asked for (in <see cref="Fields"/> order) and whether <c>-a</c> was given.</summary>
    private static (bool[] Want, bool All) Selection(ParsedArgs parsed)
    {
        var want = new bool[Fields.Length];
        for (int k = 0; k < Fields.Length; k++) want[k] = parsed.Has(Fields[k].ToString());
        return (want, parsed.Has(OptAll));
    }

    /// <summary>The asked-for values joined in GNU's order; no field = <c>-s</c>; <c>-a</c> = the first
    /// <paramref name="allCount"/>. Pure.</summary>
    private static string Format(string[] values, bool[] want, bool all, int allCount)
    {
        if (all) for (int k = 0; k < allCount; k++) want[k] = true;
        else if (Array.IndexOf(want, true) < 0) want[0] = true;
        var parts = new List<string>(Fields.Length);
        for (int k = 0; k < Fields.Length; k++)
            if (want[k]) parts.Add(values[k]);
        return string.Join(' ', parts);
    }

    private static string[] HostValues(PsBash.Core.Platform.KernelIdentity.Uts uts, bool darwin) =>
        [uts.SysName, uts.NodeName, uts.Release, uts.Version, uts.Machine, uts.Machine, uts.Machine,
            darwin ? uts.SysName : "GNU/Linux"];

    /// <summary>
    /// The Windows (MSYS-style) answer. <c>-a</c> alone keeps ps-bash's documented short form; any field asked
    /// beside it, or any single field, prints in GNU's order. Pure but for the machine facts it reads.
    /// </summary>
    internal static string FormatWindows(bool[] want, bool all)
    {
        var ver = Environment.OSVersion.Version;
        var release = $"{ver.Major}.{ver.Minor}.{ver.Build}";
        var sysName = $"MINGW64_NT-{release}";
        var hostName = Environment.MachineName.ToLowerInvariant();
        var arch = Environment.Is64BitProcess ? "x86_64" : "i686";
        if (all) return $"{sysName} {hostName} {release} {arch} MINGW64";
        string[] values = [sysName, hostName, release, System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            arch, "unknown", "unknown", "Msys"];
        return Format(values, want, all: false, Fields.Length);
    }

    /// <summary>
    /// coreutils <c>uname</c> over a host kernel identity: fields in GNU's order <c>-s -n -r -v -m -p -i -o</c>
    /// (long forms too), <c>-s</c> when none is asked for. <c>-a</c> is all of them on Linux (<c>-p</c> / <c>-i</c> =
    /// the machine, as Debian, Ubuntu and Fedora build it); on macOS <c>-s -n -r -v -m</c> (BSD uname). A usage error
    /// (unknown option, operand) yields null: the cmdlet reports it. Pure (unit-test seam).
    /// </summary>
    internal static string? FormatHost(PsBash.Core.Platform.KernelIdentity.Uts uts, bool darwin, IReadOnlyList<string> args)
    {
        var argv = new string[args.Count];
        for (int k = 0; k < argv.Length; k++) argv[k] = args[k];
        var parsed = ArgParser.Parse(argv, UnameSpec);
        if (parsed.HasError || parsed.Operands().Count > 0) return null;
        var (want, all) = Selection(parsed);
        return Format(HostValues(uts, darwin), want, all, darwin ? 5 : 8);
    }
}
