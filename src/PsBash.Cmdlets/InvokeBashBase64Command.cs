using PsBash.Core;
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
/// <item><b>Pipeline mode</b> — the record byte stream (each item's <c>BashText</c>
/// plus a boundary <c>\n</c> unless unterminated) is consumed RECORD BY RECORD in
/// <c>ProcessRecord</c>: encoding feeds <see cref="Base64LineEncoder"/> (wrapped lines
/// are emitted as they fill), decoding feeds <see cref="Base64StreamDecoder"/> (decoded
/// bytes become records as they arrive). Nothing but a carry and one line is retained.</item>
/// </list>
///
/// Encoded output is wrapped at <c>-w N</c> columns by joining wrap-sized
/// substrings with <c>\n</c> (the record boundary; <see cref="Environment.NewLine"/> would put CRs into
/// the bytes on Windows) and stripping trailing
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

    // ---- stdin pipeline (streamed): each record's bytes are encoded / decoded as it arrives, so
    // retained memory is a 0-2 byte carry plus one output line, not the input (it used to be
    // RecordStreamText joined string + byte copy + the output string = 3-5x the input).
    private bool _planned, _streamable, _failed;
    private RecordByteEncoder? _recordBytes;
    private Base64LineEncoder? _encoder;
    private Base64StreamDecoder? _decoder;
    private ByteRecordEmitter? _decodedRecords;

    /// <summary>Records fed to the stdin stream (test seam; the stream itself retains none of them).</summary>
    internal int StreamedRecords { get; private set; }

    protected override void ProcessRecord()
    {
        if (InputObject is null) return;
        if (!_planned)
        {
            _planned = true;
            var args = ArgsWithDecoys();
            var plan = Plan(args);
            // Only a plain stdin run streams; help / version / errors / a file operand leave the
            // pipeline unread (EndProcessing handles them exactly as before).
            _streamable = !plan.Parsed.HasError && plan.Error is null
                          && !plan.Parsed.Has(OptSpecSet.HelpId) && !plan.Parsed.Has(OptSpecSet.VersionId)
                          && Array.IndexOf(args, "--help") < 0
                          && (plan.Operands.Count == 0 || plan.Operands[0] == "-");
            if (_streamable)
            {
                _recordBytes = new RecordByteEncoder();
                if (plan.Decode)
                {
                    _decodedRecords = new ByteRecordEmitter(WriteObject);
                    _decoder = new Base64StreamDecoder(plan.IgnoreGarbage, _decodedRecords.Append);
                }
                else
                {
                    _encoder = new Base64LineEncoder(plan.Wrap, WriteObject);
                }
            }
        }
        if (!_streamable || _failed) return;

        StreamedRecords++;
        try
        {
            if (_decoder is not null)
            {
                string text = BashRuntime.GetBashText(InputObject);
                _decoder.Append(text);
                _decoder.Append("\n");
            }
            else
            {
                _recordBytes!.Encode(InputObject, _encoder!.Append);
            }
        }
        catch (FormatException ex)
        {
            _failed = true;
            FileSystemHelpers.WriteBashError(this, $"base64: invalid input: {ex.Message}");
        }
    }

    /// <summary>
    /// The stdin trailer: flush the encoder / decoder. Returns false when no pipeline input was
    /// streamed (the caller falls through to its "nothing to do" path).
    /// </summary>
    private bool FinishStdinStream()
    {
        if (!_streamable || StreamedRecords == 0) return false;
        if (_failed)
        {
            // Like GNU, what decoded before the bad character is still written.
            _decodedRecords?.Finish();
            FileSystemHelpers.SetLastExitCode(this, 1);
            return true;
        }
        try
        {
            if (_decoder is not null)
            {
                // Trailing whitespace is trimmed by the decoder; the final partial quartet is validated here.
                try { _decoder.Finish(); }
                finally { _decodedRecords!.Finish(); }
            }
            else
            {
                _recordBytes!.Finish(_encoder!.Append);
                _encoder.Finish();
            }
        }
        catch (FormatException ex)
        {
            FileSystemHelpers.WriteBashError(this, $"base64: invalid input: {ex.Message}");
        }
        return true;
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
            string filePath = FileSystemHelpers.ProviderPath(this, operands[0]);
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
                WriteDecoded(output);
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
                WriteEncoded(output, wrapCol);
                return;
            }
        }

        // The pipeline was consumed record by record in ProcessRecord (the exact byte stream the
        // upstream wrote — base64 is byte-oriented: a missing final newline stays missing, so
        // `printf 'b\na' | base64` is `Ygph`, not `YgphCg==`). No operand, no pipeline -> nothing.
        FinishStdinStream();
    }

    // base64 is a TRANSFORMER: one fresh text record carrying exactly GNU's bytes.
    // Decoded bytes are written as-is (no newline added); encoded text gets GNU's final
    // newline only when wrapping (`-w 0` writes none).
    private void WriteDecoded(string output)
    {
        // EmitBashLines splits the exact byte stream into records and marks an unterminated tail.
        foreach (var rec in BashRuntime.EmitBashLines(output)) WriteObject(rec);
    }

    private void WriteEncoded(string output, int wrapCol)
    {
        if (output.Length == 0) return;
        WriteObject(BashRuntime.TextRecord(output, unterminated: wrapCol <= 0));
    }

    private static string EncodeFileToBase64String(string path, int wrapCol)
    {
        using var stream = BashFileSystem.OpenRead(path);
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
        using var reader = BashFileSystem.OpenRawReader(stream, leaveOpen: true);
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

    /// <summary>True for a standard base64 alphabet char (incl. <c>=</c> padding).</summary>
    private static bool IsBase64Char(char ch)
        => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')
           || (ch >= '0' && ch <= '9') || ch == '+' || ch == '/' || ch == '=';

    private static string DecodeBytesToOutput(byte[] decoded, int count)
    {
        // The decoded bytes EXACTLY (escaped-byte markers for non-UTF-8 bytes): a final newline is data, not
        // something to strip (`printf 'a\n' | base64 | base64 -d | wc -c` is 2).
        return RawBytes.GetString(decoded, 0, count);
    }

    private void WriteReadError(string path, Exception ex, bool normalizeNotFound)
    {
        // normalizeNotFound is kept for the two call sites; both now map a missing file to GNU's text
        // (the encode path used to leak the .NET "Could not find file 'C:\...'" message).
        _ = normalizeNotFound;
        FileSystemHelpers.WriteBashError(this,
            $"base64: {path.Replace('\\', '/')}: {FileSystemHelpers.ReadErrorMessage(ex)}");
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
                    _builder.Append('\n'); // a record boundary is always LF (Environment.NewLine put CRs into the bytes on Windows)
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
