using System.Management.Automation;
using System.Text;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashFile</c> function
/// (REFACTOR-2 follow-on). Detects the type of each operand file via a small
/// magic-byte table (PNG, JPEG, PDF, Zip, ELF, GIF, RIFF) plus an
/// ASCII-text / data fallback derived from a control-byte scan of the full
/// content, matching GNU coreutils <c>file</c> behavior as implemented by the
/// psm1 oracle.
///
/// Behavioral parity oracle: the original psm1 function. This cmdlet
/// reproduces its exact behavior:
/// <list type="bullet">
/// <item>Reads the first 16 bytes of each operand for magic-byte detection.
/// On a magic-byte match (PNG/JPEG/PDF/Zip/ELF/GIF/RIFF) emits the matching
/// type description (and MIME type with <c>-i</c>).</item>
/// <item>On no magic-byte match, reads the full file and scans every byte:
/// bytes &lt; 0x07 or in [0x0E..0x1F] excluding 0x1B (ESC) mark the file as
/// non-text. All-text → "ASCII text" (<c>text/plain</c>); else → "data"
/// (<c>application/octet-stream</c>). This is the psm1 oracle's exact rule.</item>
/// <item><c>-b</c> brief: emits just the type description without the
/// <c>PATH: </c> prefix.</item>
/// <item><c>-i</c> / <c>--mime</c>: emits MIME type instead of the type
/// description.</item>
/// <item><c>-L</c> / <c>--dereference</c>: accepted (silently — psm1 oracle
/// follows symlinks by default via <see cref="File.OpenRead"/>; the flag is a
/// no-op there).</item>
/// <item>Missing operands emit a bash-style <c>file: cannot open 'PATH' (No
/// such file or directory)</c> error via <see cref="FileSystemHelpers.WriteBashError"/>
/// and continue.</item>
/// </list>
///
/// Output: typed <c>PsBash.TextOutput</c> PSObject with <c>BashText</c>,
/// <c>FileName</c>, <c>FileType</c>, and <c>MimeType</c> note properties,
/// matching the psm1 oracle's <c>[PSCustomObject]</c> shape.
///
/// Common-parameter collisions (per the playbook table): <c>-i</c>
/// prefix-collides with <c>-InformationAction</c> / <c>-InformationVariable</c>,
/// declared here as an explicit <see cref="SwitchParameter"/>. <c>-b</c> and
/// <c>-L</c> have no prefix collision and stay in <see cref="Arguments"/>.
///
/// psm1-only dependencies: glob expansion via
/// <see cref="FileSystemHelpers.ResolveOperandPaths"/> (same slice
/// cat/strings/checksum use); <c>--help</c> delegates to psm1
/// <c>Show-BashHelp</c>; bash-style errors go through
/// <see cref="FileSystemHelpers.WriteBashError"/> (one ErrorRecord).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashFile")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashFileCommand : PSCmdlet
{
    private const string OptBrief = "brief", OptMime = "mime", OptMimeType = "mime-type",
        OptMimeEnc = "mime-encoding", OptFollow = "dereference", OptNoFollow = "no-dereference",
        OptPrint0 = "print0", OptSep = "separator";

    /// <summary>GNU <c>file</c> options ps-bash refuses (exit 2). <c>-s</c>/<c>-e</c>/<c>-m</c>/<c>-f</c>/<c>-P</c>
    /// take values or need the magic database, so they cannot be silently ignored.</summary>
    private static readonly string[] FileValidButUnsupported =
    {
        "-z", "--uncompress", "-Z", "--uncompress-noreport", "-s", "--special-files",
        "-k", "--keep-going", "-f", "--files-from", "-r", "--raw", "-c", "--checking-printout",
        "-C", "--compile", "-d", "--debug", "-E", "-l", "--list", "-m", "--magic-file",
        "-e", "--exclude", "--exclude-quiet", "-P", "--parameter", "--apple", "--extension",
    };

    /// <summary>
    /// file's option surface (file 5.45, which has its own getopt_long table): <c>-b/--brief</c>,
    /// <c>-i/--mime</c>, <c>--mime-type</c>, <c>--mime-encoding</c>, <c>-L/--dereference</c>,
    /// <c>-h/--no-dereference</c>, <c>-F/--separator STR</c>, <c>-0/--print0</c>, <c>-v/--version</c>,
    /// and accepted no-ops <c>-n</c> (no-buffer), <c>-N</c> (no-pad: ps-bash never pads), <c>-p</c>
    /// (preserve-date), <c>-S</c> (no-sandbox). Abbreviations as getopt_long; ambiguity lists follow
    /// file's table order (<c>--m</c> = magic-file, mime, mime-type, mime-encoding).
    /// </summary>
    private static readonly OptSpecSet FileSpec = new(
        new[]
        {
            new OptSpec(OptBrief, 'b', "brief"),
            new OptSpec(OptMime, 'i', "mime"),
            new OptSpec(OptMimeType, '\0', "mime-type"),
            new OptSpec(OptMimeEnc, '\0', "mime-encoding"),
            new OptSpec(OptFollow, 'L', "dereference"),
            new OptSpec(OptNoFollow, 'h', "no-dereference"),
            new OptSpec(OptSep, 'F', "separator", OptKind.Value),
            new OptSpec(OptPrint0, '0', "print0"),
            new OptSpec("no-buffer", 'n', "no-buffer"),
            new OptSpec("no-pad", 'N', "no-pad"),
            new OptSpec("preserve-date", 'p', "preserve-date"),
            new OptSpec("no-sandbox", 'S', "no-sandbox"),
            new OptSpec(OptSpecSet.VersionId, 'v', "version"),
        },
        FileValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: new[]
        {
            "magic-file", "uncompress", "uncompress-noreport", "brief", "checking-printout", "exclude",
            "exclude-quiet", "files-from", "separator", "mime", "apple", "extension", "mime-type",
            "mime-encoding", "keep-going", "list", "dereference", "no-dereference", "no-buffer",
            "no-pad", "print0", "preserve-date", "parameter", "raw", "special-files", "no-sandbox",
            "compile", "debug",
        });

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, FileSpec);

    internal sealed class FileArgs
    {
        public ParsedArgs Parsed = null!;
        public bool Brief, WantMimeType, WantMimeEncoding, Follow, Print0;
        public string Separator = ":";
        public List<string> Operands = new();
        public string? Error;
        public bool AnyMime => WantMimeType || WantMimeEncoding;
    }

    private const string FileUsage =
        "Usage: file [-bcCdEhikLlNnprsSvzZ0] [--apple] [--extension] [--mime-encoding]\n"
        + "            [--mime-type] [-e <testname>] [-F <separator>]  [-f <namefile>]\n"
        + "            [-m <magicfiles>] [-P <parameter=value>] [--exclude-quiet]\n"
        + "            <file> ...\n"
        + "       file -C [-m <magicfiles>]\n"
        + "       file [--help]";

    /// <summary>
    /// Scan + interpret. <c>-i</c> is GNU's <c>--mime</c> = <c>--mime-type</c> + <c>--mime-encoding</c>
    /// (<c>text/plain; charset=us-ascii</c>; the old code printed only the type). The last of
    /// <c>-L</c>/<c>-h</c> wins; GNU's default is NOT to follow symlinks. No operand is a usage error
    /// (exit 1; it used to succeed silently).
    /// </summary>
    internal static FileArgs Plan(string[] args)
    {
        var f = new FileArgs { Parsed = ScanArgs(args) };
        f.Operands = f.Parsed.Operands();
        if (f.Parsed.HasError) return f;

        foreach (var tok in f.Parsed.Tokens)
        {
            if (tok.Kind != ArgTokKind.Option) continue;
            switch (tok.OptId)
            {
                case OptBrief: f.Brief = true; break;
                case OptMime: f.WantMimeType = true; f.WantMimeEncoding = true; break;
                case OptMimeType: f.WantMimeType = true; break;
                case OptMimeEnc: f.WantMimeEncoding = true; break;
                case OptFollow: f.Follow = true; break;
                case OptNoFollow: f.Follow = false; break;
                case OptPrint0: f.Print0 = true; break;
                case OptSep: f.Separator = tok.Value!; break;
            }
        }
        if (f.Operands.Count == 0 && !f.Parsed.Has(OptSpecSet.HelpId) && !f.Parsed.Has(OptSpecSet.VersionId))
            f.Error = FileUsage;
        return f;
    }

    /// <summary>
    /// GNU file's <c>-i</c>. <c>-i</c> prefix-collides with
    /// <c>-InformationAction</c> / <c>-InformationVariable</c>, so it MUST be
    /// declared as an explicit <see cref="SwitchParameter"/> (re-injected as <c>-i</c> for direct
    /// calls; from the transpiler <c>file</c> is on <c>PsEmitter.OrderedArgCommands</c>).
    /// </summary>
    [Parameter]
    public SwitchParameter i { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var args = BashRuntime.PrependDecoys(Arguments, (i.IsPresent, "-i"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "file", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "file"))
            {
                WriteObject(line);
            }
            return;
        }

        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "file", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "file", plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError);
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        foreach (var raw in plan.Operands)
        {
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
            {
                var (fileType, mimeType, encoding) = Classify(filePath, plan.Follow, out bool missing);
                if (missing)
                {
                    // Match the psm1 oracle's "cannot open" wording exactly.
                    // The error path uses the resolved-but-missing path
                    // string, which on Windows may contain backslashes; the
                    // psm1 oracle did not normalize, so neither do we.
                    FileSystemHelpers.WriteBashError(
                        this,
                        $"file: cannot open '{filePath}' (No such file or directory)");
                    continue;
                }

                string display = plan.AnyMime
                    ? (plan.WantMimeType && plan.WantMimeEncoding ? $"{mimeType}; charset={encoding}"
                        : plan.WantMimeType ? mimeType : encoding)
                    : fileType;
                string bashText = plan.Brief
                    ? display
                    : $"{filePath}{(plan.Print0 ? "\0" : "")}{plan.Separator} {display}";

                var obj = new PSObject();
                obj.TypeNames.Insert(0, "PsBash.TextOutput");
                obj.Properties.Add(new PSNoteProperty("BashText", bashText));
                obj.Properties.Add(new PSNoteProperty("FileName", filePath));
                obj.Properties.Add(new PSNoteProperty("FileType", fileType));
                obj.Properties.Add(new PSNoteProperty("MimeType", mimeType));
                obj.Properties.Add(new PSNoteProperty("MimeEncoding", encoding));
                WriteObject(obj);
            }
        }
    }

    /// <summary>
    /// Type description, MIME type and MIME encoding of one path: symlink (unless
    /// <paramref name="follow"/>), directory, empty file, the magic-byte table (PNG, JPEG, PDF, Zip,
    /// ELF, GIF, RIFF), then the control-byte text/data scan with the ASCII / UTF-8 / ISO-8859 split.
    /// </summary>
    private static (string FileType, string MimeType, string Encoding) Classify(
        string filePath, bool follow, out bool missing)
    {
        missing = false;
        bool isDir = Directory.Exists(filePath);
        FileSystemInfo info = isDir ? new DirectoryInfo(filePath) : new FileInfo(filePath);

        if (!follow)
        {
            string? link = null;
            try { link = info.LinkTarget; } catch { /* not a link / unreadable */ }
            if (link is not null)
            {
                bool broken = !File.Exists(filePath) && !Directory.Exists(filePath);
                return ($"{(broken ? "broken " : "")}symbolic link to {link}", "inode/symlink", "binary");
            }
        }

        if (!File.Exists(filePath) && !isDir)
        {
            missing = true;
            return ("", "", "");
        }
        if (isDir) return ("directory", "inode/directory", "binary");

        byte[] headBytes;
        try
        {
            using var stream = BashFileSystem.OpenRead(filePath);
            var buf = new byte[16];
            int read = stream.Read(buf, 0, 16);
            headBytes = read <= 0 ? Array.Empty<byte>() : buf.AsSpan(0, read).ToArray();
        }
        catch
        {
            // psm1 oracle: catch -> $bytes = @() then fall through to the content scan.
            headBytes = Array.Empty<byte>();
        }

        if (headBytes.Length == 0 && info is FileInfo { Exists: true, Length: 0 })
            return ("empty", "inode/x-empty", "binary");

        if (headBytes.Length >= 8 && headBytes[0] == 0x89 && headBytes[1] == 0x50
            && headBytes[2] == 0x4E && headBytes[3] == 0x47)
            return ("PNG image data", "image/png", "binary");
        if (headBytes.Length >= 2 && headBytes[0] == 0xFF && headBytes[1] == 0xD8)
            return ("JPEG image data", "image/jpeg", "binary");
        if (headBytes.Length >= 4 && headBytes[0] == 0x25 && headBytes[1] == 0x50
            && headBytes[2] == 0x44 && headBytes[3] == 0x46)
            return ("PDF document", "application/pdf", "binary");
        if (headBytes.Length >= 4 && headBytes[0] == 0x50 && headBytes[1] == 0x4B
            && headBytes[2] == 0x03 && headBytes[3] == 0x04)
            return ("Zip archive data", "application/zip", "binary");
        if (headBytes.Length >= 4 && headBytes[0] == 0x7F && headBytes[1] == 0x45
            && headBytes[2] == 0x4C && headBytes[3] == 0x46)
            return ("ELF executable", "application/x-executable", "binary");
        if (headBytes.Length >= 4 && headBytes[0] == 0x47 && headBytes[1] == 0x49
            && headBytes[2] == 0x46 && headBytes[3] == 0x38)
            return ("GIF image data", "image/gif", "binary");
        if (headBytes.Length >= 4 && headBytes[0] == 0x52 && headBytes[1] == 0x49
            && headBytes[2] == 0x46 && headBytes[3] == 0x46)
            return ("RIFF data", "application/octet-stream", "binary");

        // Stream-scan the bytes (same test the psm1 oracle applied to the whole array) and stop at
        // the first non-text byte — a binary is classified after a few KB, and an all-text file is
        // never held in memory. An unreadable path reports "data" rather than throwing.
        bool allText = true, sawHigh = false, utf8Ok = true;
        try
        {
            using var s = BashFileSystem.OpenRead(filePath);
            var buffer = new byte[65536];
            var decoder = new UTF8Encoding(false, true).GetDecoder();
            int read;
            while (allText && (read = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int k = 0; k < read; k++)
                {
                    byte b = buffer[k];
                    // psm1 oracle: b < 0x07 OR (b > 0x0D and b < 0x20 and b != 0x1B)
                    if (b < 0x07 || (b > 0x0D && b < 0x20 && b != 0x1B))
                    {
                        allText = false;
                        break;
                    }
                    if (b >= 0x80) sawHigh = true;
                }
                if (allText && utf8Ok)
                {
                    try { decoder.GetCharCount(buffer, 0, read, flush: false); }
                    catch (DecoderFallbackException) { utf8Ok = false; }
                }
            }
        }
        catch
        {
            allText = false;
        }

        if (!allText) return ("data", "application/octet-stream", "binary");
        if (!sawHigh) return ("ASCII text", "text/plain", "us-ascii");
        return utf8Ok
            ? ("Unicode text, UTF-8 text", "text/plain", "utf-8")
            : ("ISO-8859 text", "text/plain", "iso-8859-1");
    }
}