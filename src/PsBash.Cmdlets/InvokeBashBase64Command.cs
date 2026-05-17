using System.Management.Automation;
using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashBase64</c> function
/// (REFACTOR-2 follow-on). Reproduces GNU coreutils <c>base64</c>: encodes
/// bytes to base64 by default, decodes with <c>-d</c> / <c>--decode</c>, and
/// wraps the encoded output at <c>-w N</c> columns (default 76; <c>-w 0</c>
/// disables wrapping).
///
/// Behavioral parity oracle: the original psm1 <c>Invoke-BashBase64</c>.
/// File + pipeline dual mode:
/// <list type="bullet">
/// <item><b>File mode</b> — only the first operand is consumed (the psm1
/// oracle indexes <c>$operands[0]</c> directly; later operands are ignored).
/// For encoding, the file is read via <see cref="File.ReadAllBytes"/> so the
/// raw bytes are base64-encoded unchanged. For decoding, the file is read
/// via <see cref="File.ReadAllText"/> with CRLF normalization (the psm1
/// oracle's <c>Read-BashFileBytes</c> slice) and the result is whitespace-
/// trimmed before <see cref="Convert.FromBase64String"/>.</item>
/// <item><b>Pipeline mode</b> — pipeline items' <c>BashText</c> values are
/// joined with <c>\n</c> separators plus a trailing <c>\n</c> if absent.
/// For encoding, the joined text is UTF-8 encoded and base64'd. For decoding,
/// the joined text is whitespace-trimmed and base64-decoded.</item>
/// </list>
///
/// Encoded output is wrapped at <c>-w N</c> columns by joining wrap-sized
/// substrings with <see cref="Environment.NewLine"/> (matching the psm1
/// oracle's <c>StringBuilder.AppendLine</c> slice) and stripping trailing
/// <c>\r</c> / <c>\n</c>. The wrapped string is emitted as a single
/// <c>PsBash.TextOutput</c> object (BashText preserves the embedded line
/// endings). <c>-w 0</c> emits the unwrapped string in one piece.
///
/// Decoded output is interpreted as UTF-8 text with a single trailing <c>\n</c>
/// stripped (the oracle's <c>$output -replace "`n$", ''</c>).
///
/// Flag binding: <c>-d</c> prefix-collides with the <c>-Debug</c> common
/// parameter and <c>-w</c> prefix-collides with <c>-WarningAction</c> /
/// <c>-WarningVariable</c>. Both are therefore declared as explicit
/// single-letter parameters (<see cref="D"/> as a
/// <see cref="SwitchParameter"/>, <see cref="W"/> as a nullable
/// <see cref="int"/>) — the binder routes a bare token by exact parameter
/// name, which beats a common-parameter prefix match. A
/// <see cref="System.Management.Automation.AliasAttribute"/> on a longer
/// name would NOT be sufficient here (aliases lose to common-parameter
/// prefix matches under the cmdlet binder). The long forms
/// <c>--decode</c> and <c>--wrap=N</c> are recovered post-parse out of
/// <c>Arguments</c>. Empty operand + empty pipeline yields no output,
/// matching the oracle.
///
/// AOT safety: no <see cref="ScriptBlock"/> construction;
/// <c>--help</c> delegates to psm1 <c>Show-BashHelp</c> via parameter-bound
/// <see cref="CommandInvocationIntrinsics.InvokeScript(string, object[])"/>.
/// File-read failures route through <see cref="FileSystemHelpers.WriteBashError"/>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashBase64")]
[OutputType(typeof(string))]
public sealed class InvokeBashBase64Command : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// The bash <c>-d</c> (decode) switch — declared explicitly because the
    /// bare token <c>-d</c> prefix-collides with the <c>-Debug</c> common
    /// parameter. Exact parameter-name match beats a common-parameter prefix
    /// match, so the parameter is literally named <c>D</c>. The long form
    /// <c>--decode</c> lands in <see cref="Arguments"/> and is recovered
    /// post-parse.
    /// </summary>
    [Parameter]
    public SwitchParameter D { get; set; }

    /// <summary>
    /// The bash <c>-w N</c> (wrap column) value flag — declared explicitly
    /// because the bare token <c>-w</c> prefix-collides with
    /// <c>-WarningAction</c> / <c>-WarningVariable</c>. Declared as nullable
    /// so the unset state falls back to the default wrap of 76; the GNU long
    /// form <c>--wrap=N</c> lands in <see cref="Arguments"/> and is recovered
    /// post-parse.
    /// </summary>
    [Parameter]
    public int? W { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

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

        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "base64"))
            {
                WriteObject(line);
            }
            return;
        }

        // The bare `-d` / `-w N` tokens bind to the declared `Decode` /
        // `Wrap` parameters above (necessary to dodge -Debug / -WarningAction
        // prefix collision). Any remaining `--decode` / `--wrap=N` long-form
        // tokens land in Arguments and are recovered here. Bare positional
        // tokens are operands.
        bool decode = D.IsPresent;
        int wrapCol = W ?? 76;
        var operands = new List<string>();
        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];
            if (a == "--decode")
            {
                decode = true; i++; continue;
            }
            if (a.StartsWith("--wrap=", StringComparison.Ordinal))
            {
                if (int.TryParse(a.Substring("--wrap=".Length), out var parsed)) wrapCol = parsed;
                i++; continue;
            }
            operands.Add(a);
            i++;
        }

        byte[]? rawBytes = null;
        string? rawText = null;

        if (operands.Count > 0)
        {
            // Oracle uses operands[0] directly — later operands are ignored.
            string filePath = SessionState.Path.GetUnresolvedProviderPathFromPSPath(operands[0]);
            if (decode)
            {
                string? fileText = ReadFileTextCrlfNormalized(filePath);
                if (fileText == null) return;
                rawText = fileText.Trim();
            }
            else
            {
                try
                {
                    rawBytes = File.ReadAllBytes(filePath);
                }
                catch (Exception ex)
                {
                    string normalized = filePath.Replace('\\', '/');
                    FileSystemHelpers.WriteBashError(this, $"base64: {normalized}: {ex.Message}");
                    return;
                }
            }
        }
        else if (_pipeline.Count > 0)
        {
            var sb = new StringBuilder();
            for (int p = 0; p < _pipeline.Count; p++)
            {
                if (p > 0) sb.Append('\n');
                sb.Append(BashRuntime.GetBashText(_pipeline[p]));
            }
            string text = sb.ToString();
            if (!text.EndsWith("\n", StringComparison.Ordinal)) text += "\n";
            if (decode)
            {
                rawText = text.Trim();
            }
            else
            {
                rawBytes = Encoding.UTF8.GetBytes(text);
            }
        }
        else
        {
            // No operand, no pipeline -> oracle returns nothing.
            return;
        }

        if (decode)
        {
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(rawText ?? string.Empty);
            }
            catch (FormatException ex)
            {
                FileSystemHelpers.WriteBashError(this, $"base64: invalid input: {ex.Message}");
                return;
            }
            string output = Encoding.UTF8.GetString(decoded);
            if (output.EndsWith("\n", StringComparison.Ordinal))
            {
                output = output.Substring(0, output.Length - 1);
            }
            WriteObject(BashRuntime.NewBashObject(output));
        }
        else
        {
            string encoded = Convert.ToBase64String(rawBytes ?? Array.Empty<byte>());
            string output;
            if (wrapCol > 0)
            {
                // The oracle joins wrap-sized substrings with
                // StringBuilder.AppendLine (Environment.NewLine on the host
                // platform), then strips trailing CR/LF. Mirror exactly.
                var wrapped = new StringBuilder();
                for (int c = 0; c < encoded.Length; c += wrapCol)
                {
                    int len = Math.Min(wrapCol, encoded.Length - c);
                    wrapped.Append(encoded, c, len);
                    wrapped.Append(Environment.NewLine);
                }
                output = wrapped.ToString().TrimEnd('\r', '\n');
            }
            else
            {
                output = encoded;
            }
            WriteObject(BashRuntime.NewBashObject(output));
        }
    }

    /// <summary>
    /// Reads a file as UTF-8 text with BOM detection (delegated to
    /// <see cref="File.ReadAllText(string)"/>) and CRLF normalization.
    /// On failure emits a bash-style error matching the psm1
    /// <c>Read-BashFileBytes</c> oracle (No such file or directory vs the
    /// exception message). Returns <c>null</c> on error.
    /// </summary>
    private string? ReadFileTextCrlfNormalized(string path)
    {
        try
        {
            string raw = File.ReadAllText(path);
            return raw.Replace("\r\n", "\n");
        }
        catch (Exception ex)
        {
            bool notFound = ex is FileNotFoundException or DirectoryNotFoundException
                || ex.InnerException is FileNotFoundException or DirectoryNotFoundException;
            string msg = notFound ? "No such file or directory" : ex.Message;
            string normalized = path.Replace('\\', '/');
            FileSystemHelpers.WriteBashError(this, $"base64: {normalized}: {msg}");
            return null;
        }
    }
}
