using System.Linq;
using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashTouch</c>
/// (REFACTOR-2). Updates the access / modification timestamps of each
/// operand file, creating an empty file when the operand does not exist
/// (unless <c>-c</c> is set). Matches GNU coreutils <c>touch</c> for the
/// supported flag subset: <c>-d DATE</c> (parse date string), <c>-r FILE</c>,
/// <c>-a</c> (update access time only), <c>-m</c> (update mod time only; both
/// together update both), <c>-c</c> (no-create), <c>-f</c> (ignored, as in GNU).
/// GNU touch has no <c>-v</c>, so it is now a usage error.
///
/// Behavioral parity oracle: the original psm1 function. The cmdlet
/// reproduces its exact branches:
/// <list type="bullet">
/// <item>No operands → "missing file operand" error.</item>
/// <item><c>-d DATE</c> parses with <see cref="DateTime.TryParse(string, out DateTime)"/>;
/// on failure, emit "invalid date format" and return without touching any
/// operand (matches the psm1 oracle's early return).</item>
/// <item>Operand missing, parent missing, no <c>-c</c> → "No such file or
/// directory" error, continue with next operand.</item>
/// <item>Operand missing, parent present, no <c>-c</c> → create an empty
/// file, then set timestamps.</item>
/// <item>Operand missing with <c>-c</c> → silent skip.</item>
/// <item>Operand exists → set <see cref="FileInfo.LastWriteTime"/> unless
/// <c>-a</c>, set <see cref="FileInfo.LastAccessTime"/> unless <c>-m</c>.
/// (Default — neither <c>-a</c> nor <c>-m</c> — sets both.)</item>
/// </list>
/// <para>
/// <b>Argv</b> is parsed by the shared ordered parser (<see cref="ArgParser"/>, spec
/// <c>TouchSpec</c>). The transpiler single-quotes every dash-leading word for touch
/// (<c>PsEmitter.OrderedArgCommands</c>) so flags arrive in <c>Arguments</c> in order; the
/// <c>a</c>/<c>c</c>/<c>v</c> switch decoys and the value-bearing <c>D</c> decoy exist ONLY for
/// direct calls (bare <c>-a</c>/<c>-c</c>/<c>-v</c>/<c>-d</c> collide with <c>-Arguments</c>,
/// <c>-Confirm</c>, <c>-Verbose</c>, <c>-Debug</c>) and are re-injected first.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTouch")]
[OutputType(typeof(string))]
public sealed class InvokeBashTouchCommand : PSCmdlet
{
    [Parameter] public SwitchParameter a { get; set; }
    [Parameter] public SwitchParameter c { get; set; }
    [Parameter] public SwitchParameter v { get; set; }

    // -d prefix-collides with -Debug. Declared as value-bearing string so
    // 'touch -d "2024-01-01" file' binds the date correctly. (Without this
    // declaration, -Debug consumes the bare -d as a switch and the date
    // string lands in Arguments as a bare positional, indistinguishable
    // from a file operand.)
    [Parameter] public string? D { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// Valid GNU <c>touch</c> options ps-bash does not implement; refused loudly (exit 2).
    /// <c>-t STAMP</c> (the [[CC]YY]MMDDhhmm[.ss] format), <c>-h/--no-dereference</c> (touch the
    /// symlink itself) and <c>--time=WORD</c> have no implementation here. (A string[] on purpose:
    /// CommonParameterCollisionGuardTests enumerates each cmdlet's static string sets.)
    /// </summary>
    private static readonly string[] TouchValidButUnsupported = Array.Empty<string>();

    private const string OptAccess = "access", OptModify = "modify", OptNoCreate = "no-create",
        OptIgnored = "ignored", OptDate = "date", OptReference = "reference",
        OptStamp = "stamp", OptNoDereference = "no-dereference", OptTime = "time";

    /// <summary>GNU touch long_options[] order; getopt_long lists ambiguous-prefix candidates in it.</summary>
    private static readonly string[] TouchLongOptionOrder = { "no-create", "no-dereference" };

    /// <summary>
    /// touch's whole option surface, built once for the shared ordered parser. GNU touch has NO
    /// <c>-v</c> (`touch -v` is "invalid option -- 'v'"); the old scan accepted it as a silent
    /// no-op inherited from the psm1 oracle, which no GNU-targeted script ever passes.
    /// <c>-f</c> is accepted and ignored, exactly as GNU documents it.
    /// </summary>
    private static readonly OptSpecSet TouchSpec = new(
        new[]
        {
            new OptSpec(OptAccess, 'a', null),
            new OptSpec(OptModify, 'm', null),
            new OptSpec(OptNoCreate, 'c', "no-create"),
            new OptSpec(OptIgnored, 'f', null),
            new OptSpec(OptDate, 'd', "date", OptKind.Value),
            new OptSpec(OptReference, 'r', "reference", OptKind.Value),
            new OptSpec(OptStamp, 't', null, OptKind.Value),
            new OptSpec(OptNoDereference, 'h', "no-dereference"),
            new OptSpec(OptTime, '\0', "time", OptKind.Value),
        },
        validButUnsupported: TouchValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        longOptionOrder: TouchLongOptionOrder);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, TouchSpec);

    protected override void ProcessRecord()
    {
        // Re-inject every decoy-bound flag. The transpiler single-quotes each dash-leading word
        // for touch (PsEmitter.OrderedArgCommands) so they arrive in Arguments in order; a DIRECT
        // call (`Invoke-BashTouch -c f`, `-d DATE f`, Pester) binds the decoys instead. Prepending
        // is safe: a decoy can only have been bound before any `--`. -d's value decoy becomes the
        // two elements "-d" VALUE.
        var args = BashRuntime.PrependDecoys(Arguments, (a.IsPresent, "-a"), (c.IsPresent, "-c"), (v.IsPresent, "-v"));
        if (D is not null) args = new[] { "-d", D }.Concat(args).ToArray();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "touch", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "touch"))
            {
                WriteObject(line);
            }
            return;
        }

        // Shared ordered parser: bundles in any order (-am, -cm, -fa), attached values (-d2020-01-01,
        // --date=..), `--`, unique-prefix long options, and the unsupported/unknown classifier in
        // ONE scan. Before it, touch had NO classifier: `touch -t 202401011200 f` created files
        // named "-t" and "202401011200" at exit 0, and a trailing `-d` with no value was ignored.
        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "touch", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "touch", parsed)) return;

        // GNU: -a alone = access time only, -m alone = modification time only, and BOTH (-a -m /
        // -am) = both — the old scan set the two "only" flags together and updated NOTHING.
        // --time=WORD names a timestamp exactly like -a / -m, and all three ACCUMULATE
        // (`--time=atime -m` changes both). An invalid WORD is a usage error before anything is touched.
        bool wantAccess = parsed.Has(OptAccess);
        bool wantModify = parsed.Has(OptModify);
        foreach (var t in parsed.All(OptTime))
        {
            if (!TouchStamp.TryParseTimeWord(t.Value ?? "", out var word, out var wordError))
            {
                FileSystemHelpers.WriteBashError(this, wordError!);
                return;
            }
            if (word == TouchTimeWord.Access) wantAccess = true; else wantModify = true;
        }
        bool accessOnly = wantAccess && !wantModify;
        bool modOnly = wantModify && !wantAccess;
        bool noCreate = parsed.Has(OptNoCreate);
        bool noDereference = parsed.Has(OptNoDereference);

        // Last occurrence wins, as in getopt_long.
        string? dateStr = parsed.Last(OptDate)?.Value;
        // -r FILE / --reference=FILE: take timestamps from a reference file.
        string? refFile = parsed.Last(OptReference)?.Value;
        // -t [[CC]YY]MMDDhhmm[.ss]
        string? stampStr = parsed.Last(OptStamp)?.Value;
        var operands = parsed.Operands();

        // GNU: -t cannot be combined with another time source (-d / -r), and it says so before it
        // creates or touches anything.
        if (stampStr is not null && (dateStr is not null || refFile is not null))
        {
            FileSystemHelpers.WriteBashError(this,
                "touch: cannot specify times from more than one source\nTry 'touch --help' for more information.");
            return;
        }

        if (operands.Count == 0)
        {
            FileSystemHelpers.WriteBashError(this, "touch: missing file operand");
            return;
        }

        // Resolve the access/modify timestamps to apply. With -r the two come
        // from the reference file's own atime/mtime (they can differ); -d/none
        // use one value for both.
        DateTime mtime = DateTime.Now;
        DateTime atime = mtime;
        if (refFile is not null)
        {
            var refAbs = SessionState.Path.GetUnresolvedProviderPathFromPSPath(refFile);
            if (!File.Exists(refAbs) && !Directory.Exists(refAbs))
            {
                FileSystemHelpers.WriteBashError(this,
                    $"touch: failed to get attributes of '{refFile}': No such file or directory");
                return;
            }
            mtime = File.GetLastWriteTime(refAbs);
            atime = File.GetLastAccessTime(refAbs);
        }
        else if (stampStr is not null)
        {
            if (!TouchStamp.TryParse(stampStr, DateTime.Now, out mtime))
            {
                FileSystemHelpers.WriteBashError(this, $"touch: invalid date format '{stampStr}'");
                return;
            }
            atime = mtime;
        }
        else if (dateStr is not null)
        {
            if (!GnuDateParser.TryParse(dateStr, DateTimeOffset.Now, TimeZoneInfo.Local, out var parsedDate))
            {
                FileSystemHelpers.WriteBashError(this, $"touch: invalid date format '{dateStr}'");
                return;
            }
            mtime = parsedDate.LocalDateTime;
            atime = mtime;
        }

        foreach (var file in operands)
        {
            var absolute = SessionState.Path.GetUnresolvedProviderPathFromPSPath(file);
            bool isLink = FileTimes.IsLink(absolute);
            // A dangling link is a real entry for -h (it stamps the link), but not otherwise.
            bool exists = File.Exists(absolute) || Directory.Exists(absolute) || (noDereference && isLink);

            if (!exists)
            {
                if (noCreate) continue;

                // -h never creates: GNU reports the failed stamping of the missing name instead.
                if (noDereference)
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"touch: setting times of '{file}': No such file or directory");
                    continue;
                }

                var parent = Path.GetDirectoryName(absolute);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    FileSystemHelpers.WriteBashError(this,
                        $"touch: cannot touch '{file}': No such file or directory");
                    continue;
                }

                try
                {
                    // Empty-file create. Closing the FileStream immediately
                    // mirrors the psm1 oracle's New-Item -ItemType File.
                    using (File.Create(absolute)) { }
                }
                catch (Exception ex)
                {
                    if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                    FileSystemHelpers.WriteBashError(this,
                        $"touch: cannot touch '{file}': {ex.Message}");
                    continue;
                }
            }

            // Set timestamps. FileTimes follows a link to its target (the default) or stamps the link
            // itself (-h); a null time leaves that timestamp alone (-a / -m / --time).
            try
            {
                FileTimes.Set(absolute,
                    access: modOnly ? null : atime,
                    modify: accessOnly ? null : mtime,
                    noFollow: noDereference);
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                FileSystemHelpers.WriteBashError(this,
                    $"touch: setting times of '{file}': {ex.Message}");
            }
        }
    }
}
