using System.Management.Automation;
using PsBash.Core;
using System.Text;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashFile</c> function
/// (REFACTOR-2 follow-on). The content classification (ELF, PE, gzip, zip, PNG,
/// JPEG, PDF, <c>#!</c> scripts, the text scan) lives in <see cref="FileMagic"/>
/// and follows file-5.45; this class owns the argv, operands, padding and output.
///
/// <list type="bullet">
/// <item>Classification: see <see cref="FileMagic"/>.</item>
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
        OptPrint0 = "print0", OptSep = "separator", OptNameFile = "files-from";

    /// <summary>GNU <c>file</c> options ps-bash refuses (exit 2). <c>-s</c>/<c>-e</c>/<c>-m</c>/<c>-f</c>/<c>-P</c>
    /// take values or need the magic database, so they cannot be silently ignored.</summary>
    private static readonly string[] FileValidButUnsupported =
    {
        "-z", "--uncompress", "-Z", "--uncompress-noreport", "-s", "--special-files",
        "-k", "--keep-going", "-r", "--raw", "-c", "--checking-printout",
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
            new OptSpec(OptNameFile, 'f', "files-from", OptKind.Value),
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

    /// <summary>The output-affecting options. A <c>-f NAMEFILE</c> is processed when it is PARSED (file 5.45), so it
    /// carries a snapshot of the options given before it; options after it only affect the later operands.</summary>
    internal class FileOpts
    {
        public bool Brief, WantMimeType, WantMimeEncoding, Follow, Print0, NoPad;
        public string Separator = ":";
        public bool AnyMime => WantMimeType || WantMimeEncoding;
        public FileOpts Snapshot() => (FileOpts)MemberwiseClone();
    }

    internal sealed class FileArgs : FileOpts
    {
        public ParsedArgs Parsed = null!;
        public List<string> Operands = new();

        /// <summary>Each <c>-f NAMEFILE</c> in command-line order, with the options in force when it was parsed.</summary>
        public List<(string NameFile, FileOpts Opts)> NameFiles = new();
        public string? Error;
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
                case "no-pad": f.NoPad = true; break;
                case OptSep: f.Separator = tok.Value!; break;
                case OptNameFile: f.NameFiles.Add((tok.Value!, f.Snapshot())); break;
            }
        }
        if (f.Operands.Count == 0 && f.NameFiles.Count == 0 && !f.Parsed.Has(OptSpecSet.HelpId) && !f.Parsed.Has(OptSpecSet.VersionId))
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

    /// <summary>Standard input: the `-` operand (`/dev/stdin`) and `-f -` read the pipeline.</summary>
    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<object> _pipeline = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null) _pipeline.Add(InputObject);
    }

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

        // `-f NAMEFILE` is processed when parsed (file 5.45), before the plain operands, each list as its own
        // batch with the options that preceded it; a name file that cannot be opened is fatal (exit 1).
        foreach (var (nameFile, opts) in plan.NameFiles)
        {
            if (!TryReadNames(nameFile, out var names))
            {
                FileSystemHelpers.WriteBashError(this, $"file: Cannot open `{nameFile}' (No such file or directory)");
                FileSystemHelpers.SetLastExitCode(this, 1);
                return;
            }
            var batch = new List<Target>();
            foreach (var n in names) batch.Add(NameTarget(n));
            EmitBatch(batch, opts);
        }

        if (plan.Operands.Count == 0) return;
        // Expand every operand first: GNU pads the name column to the widest operand (file.c `nlen`),
        // missing ones included, so the width is known only after the whole list is resolved.
        var targets = new List<Target>();
        foreach (var raw in plan.Operands)
        {
            if (raw == "-") { targets.Add(new Target(null, raw, "/dev/stdin", false)); continue; }
            bool glob = raw.IndexOfAny(new[] { '*', '?', '[' }) >= 0;
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
            {
                string shown = glob ? RelativeName(filePath) : raw;
                targets.Add(new Target(filePath, shown, shown, false));
            }
        }
        EmitBatch(targets, plan);
    }

    /// <summary>One operand: <c>Path</c> null = standard input. <c>Typed</c> is what pads the column
    /// (file pads stdin by the typed <c>-</c>, not by <c>/dev/stdin</c>); <c>Shown</c> is what is printed.</summary>
    private readonly record struct Target(string? Path, string Typed, string Shown, bool Missing);

    /// <summary>A name from a <c>-f</c> list: taken verbatim (no globbing, no tilde); the empty name is a missing file;
    /// <c>-</c> is stdin.</summary>
    private Target NameTarget(string name)
    {
        if (name == "-") return new Target(null, name, "/dev/stdin", false);
        string shown = EscapeName(name);
        if (name.Length == 0) return new Target("", name, shown, true);
        return new Target(SessionState.Path.GetUnresolvedProviderPathFromPSPath(name), name, shown, false);
    }

    /// <summary>file prints control characters of a name as octal (<c>a.txt\015</c>).</summary>
    private static string EscapeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c < 0x20 || c == 0x7f) sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private bool _stdinConsumed;

    private string StdinText()
    {
        if (_stdinConsumed) return string.Empty;
        _stdinConsumed = true;
        return BashRuntime.RecordStreamText(_pipeline);
    }

    private bool TryReadNames(string nameFile, out List<string> names)
    {
        names = new List<string>();
        try
        {
            if (nameFile == "-")
            {
                string all = StdinText();
                if (all.Length == 0) return true;
                foreach (var l in all.Split('\n')) names.Add(l);
                if (all.EndsWith('\n')) names.RemoveAt(names.Count - 1);
                return true;
            }
            string path = SessionState.Path.GetUnresolvedProviderPathFromPSPath(nameFile);
            if (!File.Exists(path)) return false;
            foreach (var line in BashFileSystem.ReadTextLines(path, exact: true)) names.Add(line.Text);
            return true;
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            return false;
        }
    }

    private void EmitBatch(List<Target> targets, FileOpts plan)
    {
        int nameWidth = 0;
        foreach (var t in targets) nameWidth = Math.Max(nameWidth, t.Typed.Length);

        foreach (var t in targets)
        {
            bool missing = t.Missing;
            string fileType = "", mimeType = "", encoding = "";
            if (!missing)
            {
                (fileType, mimeType, encoding) = t.Path is null
                    ? ClassifyStdin()
                    : Classify(t.Path, plan.Follow, out missing);
            }
            // file(1) reports an unopenable operand on STDOUT, in the operand's own line and with exit
            // status 0 (file-5.45: `nosuch: cannot open `nosuch' (No such file or directory)`).
            string display = missing
                ? $"cannot open `{t.Shown}' (No such file or directory)"
                : plan.AnyMime
                    ? (plan.WantMimeType && plan.WantMimeEncoding ? $"{mimeType}; charset={encoding}"
                        : plan.WantMimeType ? mimeType : encoding)
                    : fileType;
            int pad = plan.NoPad ? 0 : nameWidth - t.Typed.Length;
            string bashText = plan.Brief
                ? display
                : $"{t.Shown}{(plan.Print0 ? "\0" : "")}{plan.Separator}{new string(' ', pad)} {display}";

            var obj = new PSObject();
            obj.TypeNames.Insert(0, "PsBash.TextOutput");
            obj.Properties.Add(new PSNoteProperty("BashText", bashText));
            obj.Properties.Add(new PSNoteProperty("FileName", t.Path ?? "/dev/stdin"));
            obj.Properties.Add(new PSNoteProperty("FileType", missing ? display : fileType));
            obj.Properties.Add(new PSNoteProperty("MimeType", mimeType));
            obj.Properties.Add(new PSNoteProperty("MimeEncoding", encoding));
            WriteObject(obj);
        }
    }

    /// <summary>Standard input (the pipeline's byte stream) classified like a file; empty input is <c>empty</c>.</summary>
    private (string FileType, string MimeType, string Encoding) ClassifyStdin()
    {
        byte[] bytes = RawBytes.GetBytes(StdinTextShared());
        if (bytes.Length == 0) return ("empty", "inode/x-empty", "binary");
        return ClassifyContent(() => new MemoryStream(bytes));
    }

    private string? _stdinSnapshot;

    /// <summary>The same stdin bytes serve every <c>-</c> operand (`file - -` classifies both), unless a
    /// <c>-f -</c> name list already drained it.</summary>
    private string StdinTextShared()
    {
        if (_stdinSnapshot is not null) return _stdinSnapshot;
        _stdinSnapshot = StdinText();
        return _stdinSnapshot;
    }
    /// <summary>The name a glob match is listed under: relative to the working directory when it lives below it.</summary>
    private string RelativeName(string resolved)
    {
        try
        {
            var rel = Path.GetRelativePath(SessionState.Path.CurrentLocation.Path, resolved);
            return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? resolved : rel.Replace('\\', '/');
        }
        catch { return resolved; }
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

        if (info is FileInfo { Exists: true, Length: 0 })
            return ("empty", "inode/x-empty", "binary");
        return ClassifyContent(() => BashFileSystem.OpenRead(filePath));
    }

    /// <summary>Content classification over any re-openable byte source: see <see cref="FileMagic"/>.</summary>
    private static (string FileType, string MimeType, string Encoding) ClassifyContent(Func<Stream> open)
    {
        var k = FileMagic.Classify(open);
        return (k.Type, k.Mime, k.Encoding);
    }
}
