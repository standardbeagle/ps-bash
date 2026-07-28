namespace PsBash.Cmdlets;

/// <summary>
/// <c>cat FILE…</c> as a fused-lane PRODUCER (S2 follow-up). Before this, only a
/// bare <c>cat</c> had a core, so the epic's stated 90% shape —
/// <c>cat f | grep x | sort</c> — still declined all-or-nothing even after
/// <see cref="SortStage"/> landed: the blocker had merely moved from <c>sort</c>
/// onto <c>cat FILE</c>.
///
/// <para><b>Producer semantics.</b> As stage 0 there is no upstream. If it is NOT
/// first, <see cref="Run"/> still ignores its input — which is the CMDLET's behavior,
/// not a shortcut: <c>InvokeBashCatCommand.ParseOnce</c> sets
/// <c>_readStdin = operands.Count == 0 || operands.Contains("-")</c> and suppresses the
/// pipeline entirely when file operands are present, so <c>… | cat FILE</c> discards
/// stdin on both lanes. Identical by construction.</para>
///
/// <para><b>Reads go through <see cref="BashFileSystem.ReadTextLines"/></b> — the same
/// call the cmdlet's fast path makes (streaming, BOM-aware UTF-8, CRLF→LF, no spurious
/// trailing empty line). Never a raw <c>File.ReadAllText</c>/<c>StreamReader</c>
/// (`.claude/rules/os-interface.md`): re-deriving BOM or CRLF handling here would
/// silently corrupt the first line of every file in the fused lane.</para>
///
/// <para><b>Certified argv:</b> one or more plain file operands, no flags. Everything
/// else DECLINES to the real cmdlet, which owns the error text, the exit code, and the
/// output shapes this lane cannot express:</para>
/// <list type="bullet">
/// <item><b>Any flag</b> (<c>-n -b -s -E -T</c>, long forms, <c>--</c>, or any token
/// starting with <c>-</c>) — the flagged path emits typed <c>PsBash.CatLine</c> objects
/// with numbering/squeeze state, not bare lines.</item>
/// <item><b><c>-</c> (the stdin marker), alone or mixed with files</b> — interleaving
/// stdin with file reads is the cmdlet's stdin-first-then-files ordering, which a
/// producer stage cannot reproduce.</item>
/// <item><b>Glob operands</b> (<c>*</c>, <c>?</c>, <c>[</c>) — expansion is the
/// provider's (<c>SessionState.Path</c>), which this stage has no access to.</item>
/// <item><b>A missing or unreadable file.</b> Checked at BUILD time, before a single
/// line is emitted, so the fallback runs the cmdlet and produces the exact
/// <c>cat: f: No such file or directory</c> on stderr AND
/// <c>$global:LASTEXITCODE = 1</c>. The streaming lane has no stderr channel at all
/// (<c>RunStreaming</c> only yields lines and propagates the LAST stage's
/// <see cref="ILineStreamStage.ExitCode"/>), so emitting the error is impossible here —
/// declining is the only way to keep the exit code honest.</item>
/// <item><b>A file whose last line has NO trailing newline.</b> The lane's contract is
/// one string per line, rendered as <c>line + Environment.NewLine</c>; the cmdlet marks
/// that final line <c>noTrailingNewline</c> and the renderer appends nothing. The
/// streaming lane cannot express "this line has no terminator", so such a file would
/// gain a byte. Declining is not a nicety — it is the difference between byte parity
/// and a silent one-byte corruption on every no-final-newline file.</item>
/// </list>
///
/// <para><b>Path resolution — the one assumption, stated plainly.</b> The cmdlet resolves
/// operands through <c>SessionState.Path.GetUnresolvedProviderPathFromPSPath</c> (the
/// PowerShell current location). A stage receives no <c>PSCmdlet</c>, so a relative
/// operand is resolved here against <see cref="Environment.CurrentDirectory"/>. Those
/// agree everywhere the fused lane actually runs, by three independent constructions:
/// a bash command's working directory IS <c>[System.Environment]::CurrentDirectory</c>
/// (<c>SdkWorker.cs</c> §exec-gate remarks); <c>SdkRunspace.cs:126</c> seeds the
/// runspace location FROM it at creation; and the emitter's <c>cd</c> writes BOTH on
/// every change (<c>docs/specs/emitter-strategy.md</c> §4 <c>EmitCd</c>). The residual
/// gap is a caller who moves the PS location with a bare <c>Set-Location</c> and never
/// goes through bash <c>cd</c> — outside the lane's own runtime. Closing it properly
/// means threading the <c>PSCmdlet</c> into <c>LineStreamRegistry.TryCreate</c>, a
/// change to <c>InvokeBashFusedPipelineCommand</c> — outside this slice's file scope
/// and recorded on the task rather than guessed at here.</para>
/// </summary>
internal sealed class CatFileStage : ILineStreamStage
{
    private readonly string[] _paths;

    private CatFileStage(string[] paths) { _paths = paths; }

    /// <summary>
    /// Always 0 — and that is the ACCURATE value, not a placeholder. <c>cat</c>'s only
    /// non-zero exit is a file it could not read, and every such case is DECLINED in
    /// <see cref="TryCreate"/> before a line is emitted, so the real cmdlet produces
    /// both the stderr text and <c>LASTEXITCODE = 1</c> on the fallback. The single
    /// remaining failure mode (a TOCTOU race, see <see cref="Run"/>) surfaces as a
    /// propagating exception rather than a quietly wrong exit code. Valid after full
    /// enumeration, like every stage.
    /// </summary>
    public int ExitCode => 0;

    /// <summary>
    /// Gate the file form. Returns null (decline) for every argv shape outside the
    /// certified subset documented on the class — including a file that does not exist
    /// or does not end in a newline, both of which are checked HERE so the decline
    /// happens before any output is produced.
    /// </summary>
    internal static ILineStreamStage? TryCreate(string[] argv)
    {
        if (argv.Length == 0) return null;                 // bare cat: CatStage's job

        var resolved = new string[argv.Length];
        for (int i = 0; i < argv.Length; i++)
        {
            var raw = argv[i];
            // Any dash-led token: a flag, `--`, or the `-` stdin marker. All declined.
            if (raw.Length == 0 || raw[0] == '-') return null;
            // Glob expansion belongs to the PowerShell provider, which a stage cannot reach.
            if (raw.IndexOf('*') >= 0 || raw.IndexOf('?') >= 0 || raw.IndexOf('[') >= 0) return null;

            string full;
            try
            {
                // NormalizeOperandPath is the same unix→drive translation the cmdlet
                // applies before resolving. See the class remarks for why the relative
                // root is Environment.CurrentDirectory.
                full = Path.GetFullPath(FileSystemHelpers.NormalizeOperandPath(raw));
            }
            catch
            {
                return null;                               // malformed path → cmdlet reports it
            }

            // Missing / not-a-file → decline so the cmdlet emits the error + exit 1.
            if (!File.Exists(full)) return null;
            // No final newline → the lane would add one byte. Decline.
            if (!EndsWithNewline(full)) return null;

            resolved[i] = full;
        }

        return new CatFileStage(resolved);
    }

    /// <summary>
    /// True when the file is empty (no lines at all, so nothing to terminate) or its
    /// last byte is <c>\n</c>. Reads exactly one byte from the end — never the file.
    /// A read failure answers false, which declines: safe direction.
    /// </summary>
    private static bool EndsWithNewline(string path)
    {
        try
        {
            using var fs = BashFileSystem.OpenRead(path);
            if (!fs.CanSeek) return false;
            if (fs.Length == 0) return true;               // empty file yields no lines
            fs.Seek(-1, SeekOrigin.End);
            return fs.ReadByte() == '\n';
        }
        catch
        {
            return false;                                  // unreadable → decline
        }
    }

    /// <summary>
    /// Stream every operand's lines in order, concatenated — <paramref name="input"/> is
    /// ignored (producer; see the class remarks on why that matches the cmdlet). Lazy:
    /// a downstream <c>head</c> stops the read mid-file, and no file is ever held in
    /// memory.
    ///
    /// <para>No try/catch around the read: a file that vanished or became unreadable
    /// BETWEEN <see cref="TryCreate"/>'s existence check and here is a TOCTOU race, and
    /// the lane has no stderr to report it on. Letting the IO exception propagate makes
    /// that race LOUD; swallowing it would silently truncate the output, which is the
    /// one outcome worse than an error. (C# also forbids <c>yield</c> inside a
    /// <c>try</c> with a <c>catch</c>, so the alternative would mean buffering the file
    /// — giving up the streaming this stage exists for.)</para>
    /// </summary>
    public IEnumerable<string> Run(IEnumerable<string> input)
    {
        foreach (var path in _paths)
        {
            foreach (var line in BashFileSystem.ReadTextLines(path))
            {
                yield return line.Text;
            }
        }
    }
}
