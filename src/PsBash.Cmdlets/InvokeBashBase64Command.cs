using System.Linq;
using System.Management.Automation;
using PsBash.Cmdlets.Args;
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
/// For encoding, the file stream is encoded in chunks so raw bytes are never
/// materialized as one array. For decoding, base64 characters are consumed
/// incrementally while whitespace is ignored, matching
/// <see cref="Convert.FromBase64String(string)"/>.</item>
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
    private const string OptDecode = "decode", OptIgnore = "ignore", OptWrap = "wrap";

    /// <summary>
    /// base64's option surface (GNU coreutils 9.4): -d/--decode, -i/--ignore-garbage, -w/--wrap=COLS,
    /// unique long prefixes (<c>--dec</c>, <c>--ig</c>, <c>--wr 20</c>), a bundle such as <c>-dw0</c>
    /// (<c>w</c> takes the rest). GNU base64 has no other options, so nothing is valid-but-unsupported.
    /// </summary>
    private static readonly OptSpecSet Base64Spec = new(
        new[]
        {
            new OptSpec(OptDecode, 'd', "decode"),
            new OptSpec(OptIgnore, 'i', "ignore-garbage"),
            new OptSpec(OptWrap, 'w', "wrap", OptKind.Value),
        },
        allowAbbrev: true,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, Base64Spec);

    internal sealed class Base64Args
    {
        public ParsedArgs Parsed = null!;
        public bool Decode, IgnoreGarbage;
        public int Wrap = 76;
        public List<string> Operands = new();
        public string? Error;
    }

    /// <summary>
    /// Scan + validate. GNU accepts a decimal wrap size only (<c>-w x</c>, <c>-w -1</c>, <c>-w 1K</c>
    /// are "invalid wrap size", exit 1; the old scan silently kept 76 for the separated form and
    /// misread <c>-w0</c> after a bundle) and exactly one FILE ("extra operand"; the old code
    /// silently ignored the rest).
    /// </summary>
    internal static Base64Args Plan(string[] args)
    {
        var b = new Base64Args { Parsed = ScanArgs(args) };
        b.Operands = b.Parsed.Operands();
        if (b.Parsed.HasError) return b;

        b.Decode = b.Parsed.Has(OptDecode);
        b.IgnoreGarbage = b.Parsed.Has(OptIgnore);
        foreach (var tok in b.Parsed.All(OptWrap))
        {
            string v = tok.Value!;
            long n = 0;
            bool ok = v.Length > 0;
            foreach (char c in v)
            {
                if (c < '0' || c > '9') { ok = false; break; }
                n = Math.Min(n * 10 + (c - '0'), int.MaxValue);
            }
            if (!ok) { b.Error = $"base64: invalid wrap size: '{v}'"; return b; }
            b.Wrap = (int)n;
        }
        if (b.Operands.Count > 1) b.Error = $"base64: extra operand '{b.Operands[1]}'";
        return b;
    }

    /// <summary>Arguments with the decoy-bound flags re-injected (<c>-d</c>, <c>-i</c>, <c>-w N</c>).</summary>
    private string[] ArgsWithDecoys()
    {
        var args = Arguments ?? Array.Empty<string>();
        var pre = new List<string>();
        if (D.IsPresent) pre.Add("-d");
        if (I.IsPresent) pre.Add("-i");
        if (W.HasValue) { pre.Add("-w"); pre.Add(W.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        return pre.Count == 0 ? args : pre.Concat(args).ToArray();
    }
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

    /// <summary>
    /// The bash <c>-i</c> (ignore-garbage) switch — declared explicitly because
    /// the bare token <c>-i</c> prefix-collides with the <c>-InformationAction</c>
    /// / <c>-InformationVariable</c> common parameters. When decoding, any
    /// non-alphabet byte is skipped. The long form <c>--ignore-garbage</c> and
    /// bundled forms are recovered post-parse from <see cref="Arguments"/>.
    /// </summary>
    [Parameter]
    public SwitchParameter I { get; set; }

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
        var args = ArgsWithDecoys();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "base64", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "base64"))
            {
                WriteObject(line);
            }
            return;
        }

        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "base64", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "base64", plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError);
            return;
        }

        bool decode = plan.Decode;
        bool ignoreGarbage = plan.IgnoreGarbage;
        int wrapCol = plan.Wrap;
        var operands = plan.Operands;
        // A lone `-` operand is stdin (GNU), i.e. the pipeline path below.
        if (operands.Count > 0 && operands[0] != "-")
        {
            // Oracle uses operands[0] directly — later operands are ignored.
            string filePath = SessionState.Path.GetUnresolvedProviderPathFromPSPath(operands[0]);
            if (decode)
            {
                string output;
                try
                {
                    output = DecodeBase64FileToOutput(filePath, ignoreGarbage);
                }
                catch (FormatException ex)
                {
                    FileSystemHelpers.WriteBashError(this, $"base64: invalid input: {ex.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    WriteReadError(filePath, ex, normalizeNotFound: true);
                    return;
                }
                WriteObject(BashRuntime.NewBashObject(output));
                return;
            }
            else
            {
                string output;
                try
                {
                    output = EncodeFileToBase64String(filePath, wrapCol);
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    WriteReadError(filePath, ex, normalizeNotFound: false);
                    return;
                }
                WriteObject(BashRuntime.NewBashObject(output));
                return;
            }
        }

        if (_pipeline.Count == 0)
        {
            // No operand, no pipeline -> oracle returns nothing.
            return;
        }

        string pipelineText;
        {
            var sb = new StringBuilder();
            for (int p = 0; p < _pipeline.Count; p++)
            {
                if (p > 0) sb.Append('\n');
                sb.Append(BashRuntime.GetBashText(_pipeline[p]));
            }
            pipelineText = sb.ToString();
            if (!pipelineText.EndsWith("\n", StringComparison.Ordinal)) pipelineText += "\n";
        }

        if (decode)
        {
            string output;
            try
            {
                output = DecodeBase64TextToOutput(pipelineText.Trim(), ignoreGarbage);
            }
            catch (FormatException ex)
            {
                FileSystemHelpers.WriteBashError(this, $"base64: invalid input: {ex.Message}");
                return;
            }
            WriteObject(BashRuntime.NewBashObject(output));
        }
        else
        {
            string output = EncodeBytesToBase64String(Encoding.UTF8.GetBytes(pipelineText), wrapCol);
            WriteObject(BashRuntime.NewBashObject(output));
        }
    }

    private static string EncodeFileToBase64String(string path, int wrapCol)
    {
        using var stream = BashFileSystem.OpenRead(path);
        return EncodeByteStream(stream, wrapCol);
    }

    private static string EncodeBytesToBase64String(byte[] bytes, int wrapCol)
    {
        using var stream = new MemoryStream(bytes);
        return EncodeByteStream(stream, wrapCol);
    }

    private static string EncodeByteStream(Stream stream, int wrapCol)
    {
        var output = new Base64OutputBuilder(wrapCol);
        var buffer = new byte[49152]; // Multiple of 3, so most chunks encode independently.
        var carry = new byte[2];
        int carryLen = 0;

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            int offset = 0;
            if (carryLen > 0)
            {
                int needed = 3 - carryLen;
                if (read < needed)
                {
                    Array.Copy(buffer, 0, carry, carryLen, read);
                    carryLen += read;
                    continue;
                }

                var triple = new byte[3];
                Array.Copy(carry, 0, triple, 0, carryLen);
                Array.Copy(buffer, 0, triple, carryLen, needed);
                output.Append(Convert.ToBase64String(triple));
                offset = needed;
                carryLen = 0;
            }

            int fullLen = ((read - offset) / 3) * 3;
            if (fullLen > 0)
            {
                output.Append(Convert.ToBase64String(buffer, offset, fullLen));
                offset += fullLen;
            }

            carryLen = read - offset;
            if (carryLen > 0)
            {
                Array.Copy(buffer, offset, carry, 0, carryLen);
            }
        }

        if (carryLen > 0)
        {
            var final = new byte[carryLen];
            Array.Copy(carry, final, carryLen);
            output.Append(Convert.ToBase64String(final));
        }

        return output.ToString();
    }

    private static string DecodeBase64FileToOutput(string path, bool ignoreGarbage)
    {
        using var stream = BashFileSystem.OpenRead(path);
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var decoded = new MemoryStream();
        var chars = new char[16384];
        var quartet = new char[4];
        int quartetLen = 0;

        int read;
        while ((read = reader.Read(chars, 0, chars.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char ch = chars[i];
                if (char.IsWhiteSpace(ch)) continue;
                // -i: silently drop any non-alphabet char instead of throwing.
                if (ignoreGarbage && !IsBase64Char(ch)) continue;
                quartet[quartetLen++] = ch;
                if (quartetLen != 4) continue;

                byte[] bytes = Convert.FromBase64CharArray(quartet, 0, quartetLen);
                decoded.Write(bytes, 0, bytes.Length);
                quartetLen = 0;
            }
        }

        if (quartetLen > 0)
        {
            byte[] bytes = Convert.FromBase64CharArray(quartet, 0, quartetLen);
            decoded.Write(bytes, 0, bytes.Length);
        }

        return DecodeBytesToOutput(decoded.GetBuffer(), (int)decoded.Length);
    }

    private static string DecodeBase64TextToOutput(string text, bool ignoreGarbage)
    {
        if (ignoreGarbage)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                if (IsBase64Char(ch)) sb.Append(ch);
            }
            text = sb.ToString();
        }
        byte[] decoded = Convert.FromBase64String(text);
        return DecodeBytesToOutput(decoded, decoded.Length);
    }

    /// <summary>True for a standard base64 alphabet char (incl. <c>=</c> padding).</summary>
    private static bool IsBase64Char(char ch)
        => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')
           || (ch >= '0' && ch <= '9') || ch == '+' || ch == '/' || ch == '=';

    private static string DecodeBytesToOutput(byte[] decoded, int count)
    {
        string output = Encoding.UTF8.GetString(decoded, 0, count);
        if (output.EndsWith("\n", StringComparison.Ordinal))
        {
            output = output.Substring(0, output.Length - 1);
        }
        return output;
    }

    private void WriteReadError(string path, Exception ex, bool normalizeNotFound)
    {
        bool notFound = normalizeNotFound
            && (ex is FileNotFoundException or DirectoryNotFoundException
                || ex.InnerException is FileNotFoundException or DirectoryNotFoundException);
        string msg = notFound ? "No such file or directory" : ex.Message;
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"base64: {normalized}: {msg}");
    }

    private sealed class Base64OutputBuilder
    {
        private readonly int _wrapCol;
        private readonly StringBuilder _builder = new();
        private int _lineLen;

        public Base64OutputBuilder(int wrapCol)
        {
            _wrapCol = wrapCol;
        }

        public void Append(string encoded)
        {
            if (_wrapCol <= 0)
            {
                _builder.Append(encoded);
                return;
            }

            int offset = 0;
            while (offset < encoded.Length)
            {
                if (_lineLen == _wrapCol)
                {
                    _builder.Append(Environment.NewLine);
                    _lineLen = 0;
                }

                int take = Math.Min(_wrapCol - _lineLen, encoded.Length - offset);
                _builder.Append(encoded, offset, take);
                _lineLen += take;
                offset += take;
            }
        }

        public override string ToString() => _builder.ToString();
    }
}
