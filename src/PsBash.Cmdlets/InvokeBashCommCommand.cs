using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashComm</c> function
/// (REFACTOR-2 follow-on). Two-pointer walk over two sorted files emitting a
/// 3-column tab-prefixed output: column 1 = lines unique to file1,
/// column 2 = lines unique to file2, column 3 = lines in both. The
/// <c>-1</c> / <c>-2</c> / <c>-3</c> digit flags suppress the corresponding
/// column (and remove its leading tab from later columns). Comparison is
/// <see cref="System.StringComparison.Ordinal"/> — the exact slice the psm1
/// oracle used via <c>[string]::Compare(..., Ordinal)</c>.
///
/// Behavioral parity oracle: the original psm1 function. The flag set
/// (<c>-1</c>, <c>-2</c>, <c>-3</c>) and their digit-bundle form (<c>-12</c>,
/// <c>-123</c>) reproduces the oracle's <c>^-[123]+$</c> match exactly.
///
/// No PowerShell common-parameter prefix collision: <c>-1</c> / <c>-2</c> /
/// <c>-3</c> are digit-prefixed tokens; no PowerShell common parameter starts
/// with a digit, so they stay in <see cref="Arguments"/>.
///
/// Glob expansion routes through <see cref="FileSystemHelpers.ResolveOperandPaths"/>;
/// a missing file emits a bash-style <c>comm: PATH: No such file or directory</c>
/// error via <see cref="FileSystemHelpers.WriteBashError"/> (parameter-bound
/// <c>InvokeScript</c>, AOT-safe) and the cmdlet returns with no further
/// output, matching the oracle's early-return-on-null contract from
/// <c>Read-BashFileLines</c>.
///
/// Output: each emitted record goes through
/// <see cref="BashRuntime.NewBashObject(string)"/> — the same default
/// <c>PsBash.TextOutput</c> shape the psm1 oracle produced (under <c>-z</c>: a NUL-terminated
/// exact record, <see cref="NulRecords"/>).
///
/// <para><b>Input-order check</b> (GNU comm.c <c>check_order</c>, oracle coreutils 9.4): every line READ
/// after the first of a file is compared with the previous one of the same file. By default
/// (neither option, or <c>--nocheck-order</c> last = never) the check only runs once an UNPAIRABLE line
/// has been seen; the first disorder per file prints <c>comm: file N is not in sorted order</c> and the
/// run continues, ending with <c>comm: input is not in sorted order</c> and exit 1.
/// <c>--check-order</c> checks always and stops at the first disorder (exit 1). The last of
/// <c>--check-order</c>/<c>--nocheck-order</c> wins.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashComm")]
[OutputType(typeof(string))]
public sealed class InvokeBashCommCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// Valid GNU comm options ps-bash does not implement, refused loudly (exit 2). (A string[] on
    /// purpose: CommonParameterCollisionGuardTests enumerates static string sets.)
    /// </summary>
    private static readonly string[] CommValidButUnsupported = Array.Empty<string>();

    private const string OptSup1 = "s1", OptSup2 = "s2", OptSup3 = "s3",
        OptTotal = "total", OptDelim = "delim",
        OptCheck = "check", OptNoCheck = "nocheck", OptZero = "zero";

    /// <summary>
    /// comm's option surface (GNU coreutils 9.4): <c>-1 -2 -3</c> (bundle in any order, <c>-123</c>),
    /// <c>--total</c>, <c>--output-delimiter=STR</c>, <c>--check-order</c>/<c>--nocheck-order</c>,
    /// <c>-z</c>/<c>--zero-terminated</c>, unique long prefixes.
    /// </summary>
    private static readonly OptSpecSet CommSpec = new(
        new[]
        {
            new OptSpec(OptSup1, '1', null),
            new OptSpec(OptSup2, '2', null),
            new OptSpec(OptSup3, '3', null),
            new OptSpec(OptTotal, '\0', "total"),
            new OptSpec(OptDelim, '\0', "output-delimiter", OptKind.Value),
            new OptSpec(OptCheck, '\0', "check-order"),
            new OptSpec(OptNoCheck, '\0', "nocheck-order"),
            new OptSpec(OptZero, 'z', "zero-terminated"),
        },
        validButUnsupported: CommValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, CommSpec);

    /// <summary>Input-order checking mode: GNU's CHECK_ORDER_DEFAULT / ENABLED / DISABLED.</summary>
    public enum OrderCheck { Default, Enabled, Disabled }

    /// <summary>The last of <c>--check-order</c> / <c>--nocheck-order</c> in command-line order.</summary>
    internal static OrderCheck ResolveOrderCheck(ParsedArgs parsed)
    {
        var mode = OrderCheck.Default;
        foreach (var tok in parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            if (tok.OptId == OptCheck) mode = OrderCheck.Enabled;
            else if (tok.OptId == OptNoCheck) mode = OrderCheck.Disabled;
        }
        return mode;
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "comm", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "comm"))
            {
                WriteObject(line);
            }
            return;
        }

        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "comm", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "comm", parsed)) return;

        bool suppress1 = parsed.Has(OptSup1);
        bool suppress2 = parsed.Has(OptSup2);
        bool suppress3 = parsed.Has(OptSup3);
        bool total = parsed.Has(OptTotal);
        bool zero = parsed.Has(OptZero);
        var checkMode = ResolveOrderCheck(parsed);
        // GNU: an empty --output-delimiter is a NUL byte (oracle-checked, coreutils 9.4).
        string delim = parsed.Last(OptDelim) is { } d ? (d.Value!.Length == 0 ? "\0" : d.Value) : "\t";
        var operands = parsed.Operands();

        if (operands.Count < 2)
        {
            FileSystemHelpers.WriteBashError(this, operands.Count == 0
                ? "comm: missing operand"
                : $"comm: missing operand after '{operands[0]}'");
            return;
        }
        if (operands.Count > 2)
        {
            FileSystemHelpers.WriteBashError(this, $"comm: extra operand '{operands[2]}'");
            return;
        }
        // First operand path: route through glob expansion for symmetry with
        // the wider migrated set (the oracle used GetUnresolvedProviderPathFromPSPath
        // directly; ResolveOperandPaths falls through to that for non-glob
        // literals, so behavior is byte-identical for literal operands).
        string? path1 = ResolveSingleOperand(operands[0]);
        if (path1 == null) return;
        string? path2 = ResolveSingleOperand(operands[1]);
        if (path2 == null) return;

        object Out(string text) => zero ? NulRecords.Record(text) : BashRuntime.NewBashObject(text);
        IEnumerable<string> Read(string path) => zero ? NulRecords.ReadFile(path) : BashFileSystem.ReadLines(path);

        IEnumerator<string>? file1 = null;
        IEnumerator<string>? file2 = null;
        string currentReadPath = path1;

        try
        {
            file1 = Read(path1).GetEnumerator();
            file2 = Read(path2).GetEnumerator();

            var prev = new string?[2];
            var issued = new bool[2];
            bool seenUnpairable = false;
            bool aborted = false;

            // Read the next line of file i (GNU readlinebuffer + check_order): a line that sorts BEFORE
            // its predecessor is a disorder when checking is enabled, or by default once an unpairable
            // line has been output. Returns whether a line was read; sets aborted for --check-order.
            bool Next(int i)
            {
                var e = i == 0 ? file1! : file2!;
                currentReadPath = i == 0 ? path1 : path2;
                if (!e.MoveNext()) return false;
                string cur = e.Current;
                if (checkMode != OrderCheck.Disabled && (checkMode == OrderCheck.Enabled || seenUnpairable)
                    && prev[i] is { } p && string.CompareOrdinal(cur, p) < 0
                    && (!issued[i] || checkMode == OrderCheck.Enabled))
                {
                    FileSystemHelpers.WriteBashError(this, $"comm: file {i + 1} is not in sorted order");
                    issued[i] = true;
                    if (checkMode == OrderCheck.Enabled) { aborted = true; return false; }
                }
                prev[i] = cur;
                return true;
            }

            bool has1 = Next(0);
            if (aborted) return;
            bool has2 = Next(1);
            if (aborted) return;

            // Column prefixes depend only on the suppress flags — constant for the
            // whole run, so build them once instead of concatenating per output line.
            string col2Prefix = suppress1 ? "" : delim;                       // "only in file2"
            string col3Prefix = (suppress1 ? "" : delim) + (suppress2 ? "" : delim); // "in both"

            // --total counts each category regardless of column suppression.
            long n1 = 0, n2 = 0, n3 = 0;

            while (has1 || has2)
            {
                // An exhausted file sorts after everything (GNU: order = 1 / -1).
                int order = !has1 ? 1 : !has2 ? -1 : Math.Sign(string.CompareOrdinal(file1!.Current, file2!.Current));
                if (order == 0)
                {
                    n3++;
                    if (!suppress3) WriteObject(Out(col3Prefix + file1!.Current));
                }
                else if (order < 0)
                {
                    seenUnpairable = true;
                    n1++;
                    if (!suppress1) WriteObject(Out(file1!.Current));
                }
                else
                {
                    seenUnpairable = true;
                    n2++;
                    if (!suppress2) WriteObject(Out(col2Prefix + file2!.Current));
                }

                if (order <= 0) { has1 = Next(0); if (aborted) return; }
                if (order >= 0) { has2 = Next(1); if (aborted) return; }
            }

            // --total: trailing summary line "n1<TAB>n2<TAB>n3<TAB>total"
            // (GNU prints all three counts regardless of -1/-2/-3 suppression).
            if (total)
            {
                WriteObject(Out($"{n1}{delim}{n2}{delim}{n3}{delim}total"));
            }

            if (issued[0] || issued[1])
                FileSystemHelpers.WriteBashError(this, "comm: input is not in sorted order");
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            WriteReadError(currentReadPath, ex);
            return;
        }
        finally
        {
            file1?.Dispose();
            file2?.Dispose();
        }
    }

    private string? ResolveSingleOperand(string raw)
    {
        foreach (var p in FileSystemHelpers.ResolveOperandPaths(this, raw))
        {
            // Take the first match; the psm1 oracle used unresolved-provider-path
            // (literal) — glob expansion is a superset that lands on the literal
            // for non-wildcard inputs.
            return p;
        }
        return raw;
    }

    private void WriteReadError(string path, Exception ex)
    {
        string msg = FileSystemHelpers.ReadErrorMessage(ex);
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"comm: {normalized}: {msg}");
    }
}
