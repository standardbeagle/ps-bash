using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// GNU coreutils <c>realpath [-e|-m] [-L|-P] [-s] [-q] [-z] FILE...</c>: the canonical name of each FILE, every link
/// followed (<see cref="CanonicalPath"/>). Default: all but the last component must exist; <c>-e</c> every one;
/// <c>-m</c> none (the last of the two wins). <c>-s</c> reads no link; <c>-L</c> folds <c>..</c> before following
/// links; <c>-P</c> (default) follows each link where it is met; the last of the three wins. A failure prints <c>realpath: FILE: reason</c> unless
/// <c>-q</c>, and the status is 1. <c>-z</c> ends each name with NUL. <c>--relative-to</c>/<c>--relative-base</c> are
/// reported as unsupported (exit 2), never silently ignored.
/// Flags arrive in <see cref="Arguments"/> in order (<c>PsEmitter.OrderedArgCommands</c>); the decoy switches exist only
/// for a DIRECT call (<c>-e</c> collides with <c>-ErrorAction</c>, <c>-P</c> with <c>-PipelineVariable</c>) and are re-injected first.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashRealpath")]
[OutputType(typeof(string))]
public sealed class InvokeBashRealpathCommand : PSCmdlet
{
    /// <summary>Decoy: a direct call's bare <c>-e</c> is ambiguous between <c>-ErrorAction</c>/<c>-ErrorVariable</c>.</summary>
    [Parameter] public SwitchParameter e { get; set; }

    /// <summary>Decoy for a direct call's <c>-m</c>.</summary>
    [Parameter] public SwitchParameter m { get; set; }

    /// <summary>Decoy for a direct call's <c>-s</c>.</summary>
    [Parameter] public SwitchParameter s { get; set; }

    /// <summary>Decoy: a direct call's bare <c>-P</c> is ambiguous between <c>-PipelineVariable</c>/<c>-ProgressAction</c>.</summary>
    [Parameter] public SwitchParameter P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    // (A string[] on purpose: CommonParameterCollisionGuardTests enumerates each cmdlet's static string sets.)
    private static readonly string[] RealpathValidButUnsupported = { "--relative-to", "--relative-base" };

    private const string OptE = "canonicalize-existing", OptM = "canonicalize-missing", OptL = "logical",
        OptP = "physical", OptQ = "quiet", OptS = "strip", OptNoSymlinks = "no-symlinks", OptZ = "zero";

    private static readonly OptSpecSet RealpathSpec = new(
        new[]
        {
            new OptSpec(OptE, 'e', OptE),
            new OptSpec(OptM, 'm', OptM),
            new OptSpec(OptL, 'L', OptL),
            new OptSpec(OptP, 'P', OptP),
            new OptSpec(OptQ, 'q', OptQ),
            new OptSpec(OptS, 's', OptS),
            new OptSpec(OptNoSymlinks, '\0', OptNoSymlinks),
            new OptSpec(OptZ, 'z', OptZ),
        },
        validButUnsupported: RealpathValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true);

    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, RealpathSpec);

    protected override void ProcessRecord()
    {
        var args = BashRuntime.PrependDecoys(Arguments, (e.IsPresent, "-e"), (m.IsPresent, "-m"), (s.IsPresent, "-s"), (P.IsPresent, "-P"));
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "realpath", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "realpath"))
                WriteObject(line);
            return;
        }

        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "realpath", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "realpath", parsed)) return;

        var mode = CanonicalPath.Mode.AllButLast;
        // -L / -s / -P each set both "read no link on the first pass" and "follow links on a second pass" (coreutils).
        bool logical = false, noLinks = false, quiet = false, zero = false;
        foreach (var t in parsed.Tokens)
        {
            if (t.Kind != ArgTokKind.Option) continue;
            switch (t.OptId)
            {
                case OptE: mode = CanonicalPath.Mode.Existing; break;
                case OptM: mode = CanonicalPath.Mode.Missing; break;
                case OptL: noLinks = true; logical = true; break;
                case OptP: noLinks = false; logical = false; break;
                case OptS or OptNoSymlinks: noLinks = true; logical = false; break;
                case OptQ: quiet = true; break;
                case OptZ: zero = true; break;
            }
        }

        var operands = parsed.Operands();
        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteMissingOperand(this, "realpath");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        bool failed = false;
        foreach (var operand in operands)
        {
            OperandDisplay.Remember(this, operand, operand);
            var r = Resolve(operand, mode, logical, noLinks);
            if (!r.Ok)
            {
                failed = true;
                if (!quiet) FileSystemHelpers.WriteBashError(this, $"realpath: {GnuQuote.IfNeeded(operand)}: {r.Error}");
                continue;
            }
            string text = PsBash.Core.BashPath.ToShell(r.Path);
            WriteObject(zero ? NulRecords.Record(text) : BashRuntime.NewBashObject(text));
        }
        if (failed) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    /// <summary>
    /// coreutils <c>realpath_canon</c>: one walk (reading no link under <c>-s</c>/<c>-L</c>); <c>-L</c> then walks the
    /// result again following links, so <c>..</c> is folded before a link is followed. Both walks check existence.
    /// </summary>
    private CanonicalPath.Result Resolve(string operand, CanonicalPath.Mode mode, bool logical, bool noLinks)
    {
        var r = CanonicalPath.Canonicalize(this, operand, mode, followLinks: !noLinks);
        return logical && r.Ok ? CanonicalPath.Canonicalize(this, r.Path!, mode) : r;
    }
}
