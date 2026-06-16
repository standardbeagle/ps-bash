using System.Management.Automation;

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

    // Valid GNU comm flags not implemented by ps-bash. Single dash (-) is
    // stdin and is not option-like; digit flags -1/-2/-3 ARE implemented.
    private static readonly HashSet<string> CommValidButUnsupported =
        new(StringComparer.Ordinal)
        {
            "--check-order",
            "--nocheck-order",
            "--output-delimiter",
            "-z",
            "--zero-terminated",
            "--total",
        };

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

        bool suppress1 = false;
        bool suppress2 = false;
        bool suppress3 = false;
        var operands = new List<string>();
        bool pastDoubleDash = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (pastDoubleDash)
            {
                operands.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                pastDoubleDash = true;
                continue;
            }

            // Match the oracle's `^-[123]+$` digit-bundle predicate.
            if (arg.Length > 1 && arg[0] == '-' && IsDigitBundle123(arg, 1))
            {
                for (int k = 1; k < arg.Length; k++)
                {
                    switch (arg[k])
                    {
                        case '1': suppress1 = true; break;
                        case '2': suppress2 = true; break;
                        case '3': suppress3 = true; break;
                    }
                }
                continue;
            }

            operands.Add(arg);
        }

        if (operands.Count < 2)
        {
            FileSystemHelpers.WriteBashError(this, "comm: missing operand");
            return;
        }

        if (FileSystemHelpers.TryWriteOperandOptionError(
                this, "comm", operands, CommValidButUnsupported)) return;

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
            string col2Prefix = suppress1 ? "" : "\t";                       // "only in file2"
            string col3Prefix = (suppress1 ? "" : "\t") + (suppress2 ? "" : "\t"); // "in both"

            while (has1 && has2)
            {
                int cmp = string.CompareOrdinal(file1.Current, file2.Current);
                if (cmp == 0)
                {
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
                    if (!suppress1)
                    {
                        WriteObject(BashRuntime.NewBashObject(file1.Current));
                    }

                    currentReadPath = path1;
                    has1 = file1.MoveNext();
                }
                else
                {
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
                if (!suppress1)
                {
                    WriteObject(BashRuntime.NewBashObject(file1.Current));
                }

                currentReadPath = path1;
                has1 = file1.MoveNext();
            }

            while (has2)
            {
                if (!suppress2)
                {
                    WriteObject(BashRuntime.NewBashObject(col2Prefix + file2.Current));
                }

                currentReadPath = path2;
                has2 = file2.MoveNext();
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

    private static bool IsDigitBundle123(string s, int start)
    {
        if (start >= s.Length) return false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '1' && c != '2' && c != '3') return false;
        }
        return true;
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
