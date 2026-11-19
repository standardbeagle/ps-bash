using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// GNU coreutils <c>readlink [-f|-e|-m] [-n] [-q|-s|-v] [-z] FILE...</c>.
/// <list type="bullet">
/// <item>Default: FILE must be a symbolic link; its target is printed as stored. Anything else fails (status 1)
/// SILENTLY: "Invalid argument" (not a link) or "No such file or directory" print only under <c>-v</c>.</item>
/// <item><c>-f</c> / <c>-e</c> / <c>-m</c>: the canonical name, every link followed (<see cref="CanonicalPath"/>):
/// all but the last component must exist / every one / none. The last of the three given wins. Failures are silent
/// unless <c>-v</c>; the last of <c>-q</c>/<c>-s</c>/<c>-v</c> wins.</item>
/// <item><c>-n</c>: no newline after the name (ignored with a warning for more than one FILE); <c>-z</c>: NUL after
/// each name.</item>
/// </list>
/// Flags arrive in <see cref="Arguments"/> in order (<c>PsEmitter.OrderedArgCommands</c>); the
/// <see cref="f"/>/<see cref="E"/>/<see cref="V"/> switches exist only for a DIRECT call (<c>-e</c> collides with
/// <c>-ErrorAction</c>, <c>-v</c> with <c>-Verbose</c>) and are re-injected first.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashReadlink")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashReadlinkCommand : BashCmdlet
{
    /// <summary>Decoy for a direct call's <c>-f</c>.</summary>
    [Parameter] public SwitchParameter f { get; set; }

    /// <summary>Decoy: a direct call's bare <c>-e</c> is ambiguous between <c>-ErrorAction</c>/<c>-ErrorVariable</c>.</summary>
    [Parameter] public SwitchParameter E { get; set; }

    /// <summary>Decoy: a direct call's bare <c>-v</c> would bind <c>-Verbose</c>.</summary>
    [Parameter] public SwitchParameter V { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    private const string OptF = "canonicalize", OptE = "canonicalize-existing", OptM = "canonicalize-missing",
        OptN = "no-newline", OptQ = "quiet", OptS = "silent", OptV = "verbose", OptZ = "zero";

    private static readonly OptSpecSet ReadlinkSpec = new(
        new[]
        {
            new OptSpec(OptF, 'f', OptF),
            new OptSpec(OptE, 'e', OptE),
            new OptSpec(OptM, 'm', OptM),
            new OptSpec(OptN, 'n', OptN),
            new OptSpec(OptQ, 'q', OptQ),
            new OptSpec(OptS, 's', OptS),
            new OptSpec(OptV, 'v', OptV),
            new OptSpec(OptZ, 'z', OptZ),
        },
        allowAbbrev: true,
        gnuInfoOptions: true);

    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, ReadlinkSpec);

    protected override void ProcessRecord()
    {
        var args = BashRuntime.PrependDecoys(Arguments, (f.IsPresent, "-f"), (E.IsPresent, "-e"), (V.IsPresent, "-v"));
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "readlink", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "readlink"))
                WriteObject(line);
            return;
        }

        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "readlink", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "readlink", parsed)) return;

        // The last of -f/-e/-m picks the mode; the last of -q/-s/-v picks verbosity (GNU: each option overwrites).
        CanonicalPath.Mode? mode = null;
        bool verbose = false, noNewline = false, zero = false;
        foreach (var t in parsed.Tokens)
        {
            if (t.Kind != ArgTokKind.Option) continue;
            switch (t.OptId)
            {
                case OptF: mode = CanonicalPath.Mode.AllButLast; break;
                case OptE: mode = CanonicalPath.Mode.Existing; break;
                case OptM: mode = CanonicalPath.Mode.Missing; break;
                case OptN: noNewline = true; break;
                case OptQ or OptS: verbose = false; break;
                case OptV: verbose = true; break;
                case OptZ: zero = true; break;
            }
        }

        var operands = parsed.Operands();
        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteMissingOperand(this, "readlink");
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }
        if (noNewline && operands.Count > 1)
        {
            FileSystemHelpers.WriteBashError(this, "readlink: ignoring --no-newline with multiple arguments");
            noNewline = false;
        }

        bool failed = false;
        foreach (var operand in operands)
        {
            // The message names the operand as typed (an absolute one inside the working directory too).
            OperandDisplay.Remember(this, operand, operand);
            var r = mode is { } m ? CanonicalPath.Canonicalize(this, operand, m) : CanonicalPath.ReadLink(this, operand);
            if (!r.Ok)
            {
                failed = true;
                if (verbose) FileSystemHelpers.WriteBashError(this, $"readlink: {GnuQuote.IfNeeded(operand)}: {r.Error}");
                continue;
            }
            string text = PsBash.Core.BashPath.ToShell(r.Path!);
            WriteObject(zero ? NulRecords.Record(text)
                : noNewline ? BashRuntime.NewBashObject(text, noTrailingNewline: true)
                : BashRuntime.NewBashObject(text));
        }
        if (failed) FileSystemHelpers.SetLastExitCode(this, 1);
    }
}
