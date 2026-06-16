using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTee</c> function
/// (REFACTOR-2 follow-on). Mirrors GNU coreutils <c>tee</c>: copy pipeline
/// input both to stdout (by passing the original pipeline items through) and
/// to every named file operand.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet
/// reproduces its three-step structure byte-for-byte:
/// <list type="bullet">
/// <item>Collect every pipeline item's <c>BashText</c> via
/// <see cref="BashRuntime.GetBashText"/>.</item>
/// <item>Build the file body. If the first item's BashText already ends in
/// <c>\n</c> (the oracle's <c>$hasTrailingNewlines</c> heuristic — true for
/// <c>echo</c>/<c>printf</c> output that already includes record separators),
/// concatenate parts directly. Otherwise join with <c>\n</c> and append a
/// trailing <c>\n</c> (the <c>ls</c>/<c>grep</c>-from-pipeline shape).</item>
/// <item>Write to each operand via <see cref="System.IO.File.WriteAllText"/>
/// (default) or <see cref="System.IO.File.AppendAllText"/> (with <c>-a</c>).
/// A missing parent directory yields a bash-style
/// <c>tee: PATH: No such file or directory</c> error and the operand is
/// skipped — matching the oracle's <c>Test-Path -LiteralPath $parentDir</c>
/// branch.</item>
/// </list>
/// Then every pipeline item is re-emitted (preserving typed objects on the
/// pipeline pass-through path), exactly like the psm1 oracle's trailing
/// <c>foreach ($item in $pipelineInput) { $item }</c> loop.
///
/// Common-parameter collision: <c>-a</c> prefix-matches the cmdlet's own
/// <c>-Arguments</c> catch-all parameter (the only declared parameter
/// starting with the letter 'a'), so the binder would otherwise reject a bare
/// <c>-a</c> as "missing an argument for parameter 'Arguments'". <c>-a</c> is
/// therefore declared as an explicit <see cref="SwitchParameter"/> named
/// <see cref="A"/> — same hazard the <c>ls</c> and <c>uname</c> migrations
/// hit. Operand-only invocations (literal file paths) and the <c>--</c>
/// end-of-flags marker continue to flow through <see cref="Arguments"/>.
///
/// Glob expansion routes through
/// <see cref="FileSystemHelpers.ResolveOperandPaths"/> — the same
/// <c>SessionState.Path</c> slice <c>cat</c> / checksum / the mutators use,
/// so a literal path missing on disk reaches the parent-dir check unchanged
/// (literal passthrough) and a wildcard with no match likewise passes through
/// unmolested. The oracle's <c>Resolve-BashGlob</c> dependency is therefore
/// removed from this hot path.
///
/// Directive 12 (injection): operands are bound positionally through
/// <see cref="ProcessRecord"/> / <see cref="Arguments"/> and resolved by
/// <see cref="SessionState"/> — never concatenated into a script body — so a
/// file name containing <c>;</c>, <c>$()</c>, scriptblock chars, or backticks
/// remains a literal path lookup.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTee")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashTeeCommand : PSCmdlet
{
    /// <summary>
    /// The bash <c>-a</c> (append) switch — declared explicitly because the
    /// bare token <c>-a</c> prefix-matches the cmdlet's own
    /// <see cref="Arguments"/> catch-all under PowerShell parameter binding.
    /// See the class remarks.
    /// </summary>
    [Parameter]
    public SwitchParameter A { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    // Valid GNU tee flags not implemented by ps-bash. Note: -a / --append IS
    // implemented (the explicit SwitchParameter A plus the --append long form
    // handled in the manual scan), so it is NOT listed here. Bare -i collides
    // with the PS binder so it is represented by its long form only.
    private static readonly HashSet<string> TeeValidButUnsupported =
        new(StringComparer.Ordinal)
        {
            "--ignore-interrupts",
            "--output-error",
            "-p",
        };

    private readonly List<PSObject> _pipeline = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null)
        {
            _pipeline.Add(InputObject);
        }
    }

    protected override void EndProcessing()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "tee", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "tee"))
            {
                WriteObject(line);
            }
            return;
        }

        // Manual scan: support -a (also bound by the explicit A switch — both
        // paths set append) and `--` end-of-flags marker. Anything else is an
        // operand.
        bool append = A.IsPresent;
        var operands = new List<string>();
        bool pastDoubleDash = false;

        foreach (var arg in args)
        {
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
            if (arg == "-a" || arg == "--append")
            {
                append = true;
                continue;
            }
            operands.Add(arg);
        }

        if (FileSystemHelpers.TryWriteOperandOptionError(
                this, "tee", operands, TeeValidButUnsupported)) return;

        // Collect BashText for file output (oracle: $textParts loop).
        var textParts = new List<string>(_pipeline.Count);
        foreach (var item in _pipeline)
        {
            textParts.Add(BashRuntime.GetBashText(item));
        }

        // Oracle's $hasTrailingNewlines heuristic: if the first item already
        // carries a trailing newline, concatenate directly; otherwise join
        // with \n and add a single trailing \n.
        string textContent = string.Empty;
        if (textParts.Count > 0)
        {
            bool hasTrailingNewlines = textParts[0].EndsWith("\n");
            if (hasTrailingNewlines)
            {
                textContent = string.Concat(textParts);
            }
            else
            {
                textContent = string.Join("\n", textParts) + "\n";
            }
        }

        // Write to each operand. Skip null / empty (oracle: Where-Object).
        var validOperands = operands
            .Where(o => !string.IsNullOrEmpty(o))
            .ToList();

        foreach (var rawPath in validOperands)
        {
            // /dev/null (or NUL) as a tee target: discard the write (bash sends it to the
            // null device — nowhere) but still pass stdin through to stdout below. Without
            // this the resolved path (e.g. C:\dev\null on Windows) has no parent dir and
            // tee would wrongly report "No such file or directory".
            if (FileSystemHelpers.IsNullDevice(rawPath))
            {
                continue;
            }

            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, rawPath))
            {
                if (FileSystemHelpers.IsNullDevice(filePath))
                {
                    continue;
                }
                string normalized = FileSystemHelpers.ToBashPath(filePath);
                string? parentDir;
                try
                {
                    parentDir = Path.GetDirectoryName(filePath);
                }
                catch
                {
                    parentDir = null;
                }

                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    FileSystemHelpers.WriteBashError(
                        this, $"tee: {normalized}: No such file or directory");
                    continue;
                }

                try
                {
                    if (append)
                    {
                        File.AppendAllText(filePath, textContent);
                    }
                    else
                    {
                        File.WriteAllText(filePath, textContent);
                    }
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    FileSystemHelpers.WriteBashError(
                        this, $"tee: {normalized}: {ex.Message}");
                }
            }
        }

        // Pass through original pipeline objects (oracle: trailing foreach).
        foreach (var item in _pipeline)
        {
            WriteObject(item);
        }
    }
}
