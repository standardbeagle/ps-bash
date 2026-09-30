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
/// <c>PsBash.TextOutput</c> shape the psm1 oracle produced.
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
    private static readonly string[] CommValidButUnsupported =
    {
        "--check-order", "--nocheck-order",
        "-z", "--zero-terminated",
    };

    private const string OptSup1 = "s1", OptSup2 = "s2", OptSup3 = "s3",
        OptTotal = "total", OptDelim = "delim";

    /// <summary>
    /// comm's option surface (GNU coreutils 9.4): <c>-1 -2 -3</c> (bundle in any order, <c>-123</c>),
    /// <c>--total</c>, <c>--output-delimiter=STR</c> (previously refused), unique long prefixes.
    /// </summary>
    private static readonly OptSpecSet CommSpec = new(
        new[]
        {
            new OptSpec(OptSup1, '1', null),
            new OptSpec(OptSup2, '2', null),
            new OptSpec(OptSup3, '3', null),
            new OptSpec(OptTotal, '\0', "total"),
            new OptSpec(OptDelim, '\0', "output-delimiter", OptKind.Value),
        },
        validButUnsupported: CommValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, CommSpec);

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

        IEnumerator<string>? file1 = null;
        IEnumerator<string>? file2 = null;
        string currentReadPath = path1;

        try
        {
            file1 = BashFileSystem.ReadLines(path1).GetEnumerator();
            file2 = BashFileSystem.ReadLines(path2).GetEnumerator();

            currentReadPath = path1;
            bool has1 = file1.MoveNext();
            currentReadPath = path2;
            bool has2 = file2.MoveNext();

            // Column prefixes depend only on the suppress flags — constant for the
            // whole run, so build them once instead of concatenating per output line.
            string col2Prefix = suppress1 ? "" : delim;                       // "only in file2"
            string col3Prefix = (suppress1 ? "" : delim) + (suppress2 ? "" : delim); // "in both"

            // --total counts each category regardless of column suppression.
            long n1 = 0, n2 = 0, n3 = 0;

            while (has1 && has2)
            {
                int cmp = string.CompareOrdinal(file1.Current, file2.Current);
                if (cmp == 0)
                {
                    n3++;
                    if (!suppress3)
                    {
                        WriteObject(BashRuntime.NewBashObject(col3Prefix + file1.Current));
                    }

                    currentReadPath = path1;
                    has1 = file1.MoveNext();
                    currentReadPath = path2;
                    has2 = file2.MoveNext();
                }
                else if (cmp < 0)
                {
                    n1++;
                    if (!suppress1)
                    {
                        WriteObject(BashRuntime.NewBashObject(file1.Current));
                    }

                    currentReadPath = path1;
                    has1 = file1.MoveNext();
                }
                else
                {
                    n2++;
                    if (!suppress2)
                    {
                        WriteObject(BashRuntime.NewBashObject(col2Prefix + file2.Current));
                    }

                    currentReadPath = path2;
                    has2 = file2.MoveNext();
                }
            }

            while (has1)
            {
                n1++;
                if (!suppress1)
                {
                    WriteObject(BashRuntime.NewBashObject(file1.Current));
                }

                currentReadPath = path1;
                has1 = file1.MoveNext();
            }

            while (has2)
            {
                n2++;
                if (!suppress2)
                {
                    WriteObject(BashRuntime.NewBashObject(col2Prefix + file2.Current));
                }

                currentReadPath = path2;
                has2 = file2.MoveNext();
            }

            // --total: trailing summary line "n1<TAB>n2<TAB>n3<TAB>total"
            // (GNU prints all three counts regardless of -1/-2/-3 suppression).
            if (total)
            {
                WriteObject(BashRuntime.NewBashObject($"{n1}{delim}{n2}{delim}{n3}{delim}total"));
            }
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
        bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
            || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"comm: {normalized}: {msg}");
    }
}
