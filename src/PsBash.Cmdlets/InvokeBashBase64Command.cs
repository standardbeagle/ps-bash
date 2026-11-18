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
/// Both directions STREAM (<see cref="ByteFilterStreaming"/>): encoding feeds
/// <see cref="Base64LineEncoder"/> chunk by chunk, decoding feeds a
/// <see cref="ByteRecordEmitter"/> one quartet at a time, and output is written while
/// the file is read - so a downstream <c>head</c> ends the read, and
/// <c>base64 /dev/urandom | head -c 32</c> ends as GNU's does.</item>
/// <item><b>Pipeline mode</b> — the record byte stream (each item's <c>BashText</c>
/// plus a boundary <c>\n</c> unless unterminated) is consumed RECORD BY RECORD in
/// <c>ProcessRecord</c>: encoding feeds <see cref="Base64LineEncoder"/> (wrapped lines
/// are emitted as they fill), decoding feeds <see cref="Base64StreamDecoder"/> (decoded
/// bytes become records as they arrive). Nothing but a carry and one line is retained.</item>
/// </list>
///
/// Encoded output is one record per wrapped line (<c>-w N</c>); <c>-w 0</c> is one
/// line written in unterminated pieces (<see cref="Base64LineEncoder.UnwrappedPiece"/>).
/// Decoded output is the exact bytes (escaped-byte markers for non-UTF-8), one record
/// per line, an unterminated tail kept unterminated.
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
        catch (FormatException)
        {
            _failed = true;
            FileSystemHelpers.WriteBashError(this, "base64: invalid input");
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
        catch (FormatException)
        {
            FileSystemHelpers.WriteBashError(this, "base64: invalid input");
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
            // STREAMED both ways (ByteFilterStreaming): output goes out while the file is read, so a downstream
            // `head` stops the read through WriteObject, as SIGPIPE stops GNU's. Buffering to EOF first never
            // ended on an endless device (`base64 /dev/urandom | head -c 32`).
            if (decode)
            {
                var decoded = new ByteRecordEmitter(WriteObject);
                try
                {
                    DecodeFile(filePath, ignoreGarbage, decoded.Append);
                    decoded.Finish();
                }
                catch (FormatException)
                {
                    // Like GNU (and the stdin path), what decoded before the bad character is still written.
                    decoded.Finish();
                    FileSystemHelpers.WriteBashError(this, "base64: invalid input");
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    WriteReadError(filePath, ex);
                }
                return;
            }
            try
            {
                var encoder = new Base64LineEncoder(wrapCol, WriteObject);
                using var stream = BashFileSystem.OpenRead(filePath);
                ByteFilterStreaming.Pump(stream, this, encoder.Append);
                encoder.Finish();
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                WriteReadError(filePath, ex);
            }
            return;
        }

        // The pipeline was consumed record by record in ProcessRecord (the exact byte stream the
        // upstream wrote — base64 is byte-oriented: a missing final newline stays missing, so
        // `printf 'b\na' | base64` is `Ygph`, not `YgphCg==`). No operand, no pipeline -> nothing.
        FinishStdinStream();
    }

    /// <summary>
    /// Decode the base64 text of <paramref name="path"/> into <paramref name="sink"/> one quartet at a time, as it is
    /// read (whitespace anywhere is skipped; <paramref name="ignoreGarbage"/> also skips any non-alphabet char). Throws
    /// <see cref="FormatException"/> at the first bad quartet, after the bytes before it reached the sink.
    /// </summary>
    private void DecodeFile(string path, bool ignoreGarbage, ByteSink sink)
    {
        using var stream = BashFileSystem.OpenRead(path);
        using var reader = BashFileSystem.OpenRawReader(stream, leaveOpen: true);
        var chars = new char[16384];
        var quartet = new char[4];
        int quartetLen = 0;

        int read;
        while ((read = reader.Read(chars, 0, chars.Length)) > 0)
        {
            if (Stopping) throw new PipelineStoppedException();
            for (int i = 0; i < read; i++)
            {
                char ch = chars[i];
                if (char.IsWhiteSpace(ch)) continue;
                // -i: silently drop any non-alphabet char instead of throwing.
                if (ignoreGarbage && !IsBase64Char(ch)) continue;
                quartet[quartetLen++] = ch;
                if (quartetLen != 4) continue;
                sink(Convert.FromBase64CharArray(quartet, 0, quartetLen));
                quartetLen = 0;
            }
        }

        if (quartetLen > 0) sink(Convert.FromBase64CharArray(quartet, 0, quartetLen));
    }

    /// <summary>True for a standard base64 alphabet char (incl. <c>=</c> padding).</summary>
    private static bool IsBase64Char(char ch)
        => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')
           || (ch >= '0' && ch <= '9') || ch == '+' || ch == '/' || ch == '=';

    private void WriteReadError(string path, Exception ex)
    {
        FileSystemHelpers.WriteBashError(this,
            $"base64: {BashPath.ToShell(path)}: {FileSystemHelpers.ReadErrorMessage(ex)}");
    }
}
