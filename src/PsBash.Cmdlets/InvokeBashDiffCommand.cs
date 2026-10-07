using System.Management.Automation;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet <c>Invoke-BashDiff</c>: GNU diffutils 3.10 <c>diff</c>. The comparison core is <see cref="DiffEngine"/> (a port
/// of GNU's <c>diffseq.h</c> / <c>shift_boundaries</c>, so the edit scripts come out hunk for hunk the same), the output formats
/// are <see cref="DiffFormatter"/>, the option rules <see cref="DiffPlan"/>; this class owns what touches the world: the operand
/// shapes (file, directory, <c>-</c>), the directory walk (<c>Only in</c>, <c>Common subdirectories</c>, <c>-r</c>, <c>-N</c>), the
/// per-file header of a directory comparison and the exit status (0 same, 1 different, 2 trouble).
/// <para>
/// Implemented: <c>-q -s -c -C N -u -U N -r -N -i -w -b -B -a</c>, <c>--strip-trailing-cr</c>, <c>--label</c> (and their long
/// forms). Every other GNU option is valid-but-unsupported (exit 2). INTENTIONAL DIFFERENCES: long-option ambiguity lists use an
/// alphabetical candidate order, and a binary file is recognised by a NUL in its first 4096 characters.
/// </para>
/// <para>
/// <b>Argv</b> is parsed by the shared ordered parser; the transpiler single-quotes every dash-leading word for diff
/// (<c>PsEmitter.OrderedArgCommands</c>). The <c>I</c>/<c>W</c>/<c>C</c> decoy switches exist ONLY for direct calls and are
/// re-injected as <c>-i</c>/<c>-w</c>/<c>-c</c>; a bare <c>-C N</c> typed at PowerShell binds <c>C</c> (case-insensitive) — quote it.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashDiff")]
[OutputType(typeof(string))]
public sealed class InvokeBashDiffCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>The <c>-</c> operand's text: the pipeline records (read once, in <see cref="EndProcessing"/>).</summary>
    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _stdin = new();

    /// <summary><c>-i</c> ignore-case — the bare token would prefix-match <c>-InformationAction</c>/<c>-InformationVariable</c>.</summary>
    [Parameter]
    public SwitchParameter I { get; set; }

    /// <summary><c>-w</c> ignore-all-space — the bare token would prefix-match <c>-WarningAction</c>/<c>-WarningVariable</c>.</summary>
    [Parameter]
    public SwitchParameter W { get; set; }

    /// <summary><c>-c</c> context format — prefix-collides with <c>-Confirm</c> (and, case-folded, is also what a bare <c>-C</c> binds).</summary>
    [Parameter]
    public SwitchParameter C { get; set; }

    private static readonly string[] DiffLongNames =
    {
        "binary", "brief", "changed-group-format", "color", "context", "ed", "exclude", "exclude-from", "expand-tabs", "from-file",
        "horizon-lines", "ifdef", "ignore-all-space", "ignore-blank-lines", "ignore-case", "ignore-file-name-case",
        "ignore-matching-lines", "ignore-space-change", "ignore-tab-expansion", "ignore-trailing-space", "initial-tab", "label",
        "left-column", "line-format", "minimal", "new-file", "new-group-format", "new-line-format", "no-dereference",
        "no-ignore-file-name-case", "normal", "old-group-format", "old-line-format", "paginate", "palette", "rcs", "recursive",
        "report-identical-files", "show-c-function", "show-function-line", "side-by-side", "speed-large-files", "starting-file",
        "strip-trailing-cr", "suppress-blank-empty", "suppress-common-lines", "tabsize", "text", "to-file", "unchanged-group-format",
        "unchanged-line-format", "unidirectional-new-file", "unified", "version", "width",
    };

    private static readonly string[] DiffImplementedLongNames =
    {
        "brief", "context", "ignore-all-space", "ignore-blank-lines", "ignore-case", "ignore-space-change", "label", "new-file",
        "recursive", "report-identical-files", "strip-trailing-cr", "text", "unified", "version",
    };

    private static string[] BuildValidButUnsupported()
    {
        var implemented = new HashSet<string>(DiffImplementedLongNames, StringComparer.Ordinal);
        var list = new List<string>();
        foreach (char c in "efnyDpFtTlWIxXSdEZPH") list.Add("-" + c);
        foreach (var name in DiffLongNames) if (!implemented.Contains(name)) list.Add("--" + name);
        return list.ToArray();
    }

    /// <summary>GNU diff options ps-bash refuses (exit 2). (A string[] on purpose: CommonParameterCollisionGuardTests enumerates it.)</summary>
    private static readonly string[] DiffValidButUnsupported = BuildValidButUnsupported();

    private static readonly OptSpecSet DiffSpec = new(
        new[]
        {
            new OptSpec(DiffPlan.OptContext, 'c', null),
            new OptSpec(DiffPlan.OptContextLong, 'C', null, OptKind.Value),
            new OptSpec(DiffPlan.OptContextLong, '\0', "context", OptKind.OptionalValue),
            new OptSpec(DiffPlan.OptUnified, 'u', null),
            new OptSpec(DiffPlan.OptUnifiedLong, 'U', null, OptKind.Value),
            new OptSpec(DiffPlan.OptUnifiedLong, '\0', "unified", OptKind.OptionalValue),
            new OptSpec(DiffPlan.OptBrief, 'q', "brief"),
            new OptSpec(DiffPlan.OptReport, 's', "report-identical-files"),
            new OptSpec(DiffPlan.OptRecursive, 'r', "recursive"),
            new OptSpec(DiffPlan.OptNewFile, 'N', "new-file"),
            new OptSpec(DiffPlan.OptIgnoreCase, 'i', "ignore-case"),
            new OptSpec(DiffPlan.OptIgnoreAllSpace, 'w', "ignore-all-space"),
            new OptSpec(DiffPlan.OptIgnoreSpaceChange, 'b', "ignore-space-change"),
            new OptSpec(DiffPlan.OptIgnoreBlankLines, 'B', "ignore-blank-lines"),
            new OptSpec(DiffPlan.OptStripCr, '\0', "strip-trailing-cr"),
            new OptSpec(DiffPlan.OptLabel, 'L', "label", OptKind.Value),
            new OptSpec(DiffPlan.OptText, 'a', "text"),
        },
        validButUnsupported: DiffValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true,
        usageExitCode: 2,
        longOptionOrder: DiffLongNames);

    /// <summary>Pure argv scan (unit-test seam): options, operands and the first error.</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, DiffSpec);

    protected override void ProcessRecord()
    {
        if (InputObject != null) _stdin.Add(InputObject);
    }

    private int _status;
    private DiffPlan _plan = null!;

    protected override void EndProcessing()
    {
        // Re-inject decoy-bound flags (the transpiler single-quotes every dash word, so they only bind on a direct call).
        var args = BashRuntime.PrependDecoys(Arguments, (I.IsPresent, "-i"), (W.IsPresent, "-w"), (C.IsPresent, "-c"));

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "diff", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "diff"))
                WriteObject(line);
            return;
        }

        var parsed = ScanArgs(args);
        if (FileSystemHelpers.TryWriteParseError(this, "diff", parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "diff", parsed)) return;
        if (!DiffPlan.TryBuild(args, parsed, out _plan, out var planError))
        {
            FileSystemHelpers.WriteBashError(this, planError!);
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        CompareOperands(_plan.Operands[0], _plan.Operands[1]);
        FileSystemHelpers.SetLastExitCode(this, _status);
    }

    private void Say(string line) => WriteObject(BashRuntime.NewBashObject(line));

    private void Trouble(string message)
    {
        FileSystemHelpers.WriteStderr(this, message);
        _status = 2;
    }

    private void Differ() { if (_status < 1) _status = 1; }

    private enum Kind { Missing, File, Directory, Stdin }

    private string FullPath(string name) => FileSystemHelpers.ProviderPath(this, name);

    private Kind KindOf(string name)
    {
        if (name == "-") return Kind.Stdin;
        if (FileSystemHelpers.IsNullDevice(name)) return Kind.File;
        var path = FullPath(name);
        if (Directory.Exists(path)) return Kind.Directory;
        if (File.Exists(path)) return Kind.File;
        return Kind.Missing;
    }

    /// <summary>GNU <c>file_name_concat</c>: a trailing slash on the directory is not doubled.</summary>
    private static string Join(string dir, string name) =>
        dir.EndsWith('/') || dir.EndsWith('\\') ? dir + name : dir + "/" + name;

    private static string Leaf(string name) => Path.GetFileName(name.TrimEnd('/', '\\'));

    // ───────────── operands ─────────────

    private void CompareOperands(string op0, string op1)
    {
        var k0 = KindOf(op0);
        var k1 = KindOf(op1);

        // -N makes ONE missing operand an empty file; two missing operands are still errors.
        bool bothMissing = k0 == Kind.Missing && k1 == Kind.Missing;
        if (k0 == Kind.Missing && (!_plan.NewFile || bothMissing)) { Trouble($"diff: {op0}: No such file or directory"); }
        if (k1 == Kind.Missing && (!_plan.NewFile || bothMissing)) { Trouble($"diff: {op1}: No such file or directory"); }
        if (_status == 2) return;

        if (k0 == Kind.Stdin && k1 == Kind.Stdin)
        {
            // The same stream twice is identical.
            if (_plan.ReportIdentical) Say("Files - and - are identical");
            return;
        }
        if ((k0 == Kind.Stdin && k1 == Kind.Directory) || (k0 == Kind.Directory && k1 == Kind.Stdin))
        {
            Trouble("diff: cannot compare '-' to a directory");
            return;
        }

        if (k0 == Kind.Directory && k1 == Kind.Directory)
        {
            CompareDirectories(op0, op1);
        }
        else if (k0 == Kind.Directory)
        {
            // `diff DIR FILE` compares DIR/FILE with FILE.
            CompareFiles(Join(op0, Leaf(op1)), op1, listing: false);
        }
        else if (k1 == Kind.Directory)
        {
            CompareFiles(op0, Join(op1, Leaf(op0)), listing: false);
        }
        else
        {
            CompareFiles(op0, op1, listing: false);
        }
    }

    // ───────────── directories ─────────────

    private List<string> NamesIn(string dir, Kind kind)
    {
        if (kind != Kind.Directory) return new List<string>();
        try
        {
            var names = Directory.EnumerateFileSystemEntries(FullPath(dir)).Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();
            names.Sort(StringComparer.Ordinal);
            return names;
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            Trouble($"diff: {dir}: {ex.Message}");
            return new List<string>();
        }
    }

    private void CompareDirectories(string dir0, string dir1)
    {
        var kind0 = KindOf(dir0);
        var kind1 = KindOf(dir1);
        var names0 = NamesIn(dir0, kind0);
        var names1 = NamesIn(dir1, kind1);
        var set0 = new HashSet<string>(names0, StringComparer.Ordinal);
        var set1 = new HashSet<string>(names1, StringComparer.Ordinal);
        var all = new SortedSet<string>(names0, StringComparer.Ordinal);
        all.UnionWith(names1);

        foreach (var name in all)
        {
            var n0 = Join(dir0, name);
            var n1 = Join(dir1, name);
            bool in0 = set0.Contains(name), in1 = set1.Contains(name);
            if ((!in0 || !in1) && !_plan.NewFile)
            {
                Say($"Only in {(in0 ? dir0 : dir1)}: {name}");
                Differ();
                continue;
            }

            // -N: a name only on one side is compared with an empty file / directory of the same kind.
            var k0 = in0 ? KindOf(n0) : Kind.Missing;
            var k1 = in1 ? KindOf(n1) : Kind.Missing;
            if (k0 == Kind.Missing) k0 = k1 == Kind.Directory ? Kind.Directory : Kind.Missing;
            if (k1 == Kind.Missing) k1 = k0 == Kind.Directory ? Kind.Directory : Kind.Missing;

            if (k0 == Kind.Directory && k1 == Kind.Directory)
            {
                if (_plan.Recursive) CompareDirectories(n0, n1);
                else Say($"Common subdirectories: {n0} and {n1}");
            }
            else if (k0 == Kind.Directory || k1 == Kind.Directory)
            {
                Say($"File {n0} is a {Describe(k0)} while file {n1} is a {Describe(k1)}");
                Differ();
            }
            else
            {
                CompareFiles(n0, n1, listing: true);
            }
        }
    }

    private static string Describe(Kind kind) => kind == Kind.Directory ? "directory" : "regular file";

    // ───────────── files ─────────────

    private string ReadAll(string name, Kind kind)
    {
        if (kind == Kind.Missing) return "";
        if (kind == Kind.Stdin)
        {
            return BashRuntime.RecordStreamText(_stdin.Cast<object>());
        }
        if (FileSystemHelpers.IsNullDevice(name)) return "";
        return BashFileSystem.ReadAllTextRaw(FullPath(name));
    }

    private string HeaderFor(string name, Kind kind, int labelIndex)
    {
        if (_plan.Labels.Count > labelIndex) return _plan.Labels[labelIndex];
        DateTimeOffset time = kind switch
        {
            Kind.Missing => DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.Zero),
            Kind.Stdin => DateTimeOffset.Now,
            _ => FileSystemHelpers.IsNullDevice(name) ? DateTimeOffset.UnixEpoch : TimeZoneInfo.ConvertTime(new DateTimeOffset(File.GetLastWriteTimeUtc(FullPath(name)), TimeSpan.Zero), TimeZoneInfo.Local),
        };
        return name + "\t" + DiffFormatter.FormatTimestamp(time);
    }

    private void CompareFiles(string name0, string name1, bool listing)
    {
        var k0 = KindOf(name0);
        var k1 = KindOf(name1);
        if ((k0 == Kind.Missing || k1 == Kind.Missing) && !_plan.NewFile)
        {
            if (k0 == Kind.Missing) Trouble($"diff: {name0}: No such file or directory");
            if (k1 == Kind.Missing) Trouble($"diff: {name1}: No such file or directory");
            return;
        }
        if (k0 == Kind.Directory || k1 == Kind.Directory)
        {
            Say($"File {name0} is a {Describe(k0)} while file {name1} is a {Describe(k1)}");
            Differ();
            return;
        }

        string raw0, raw1;
        try
        {
            raw0 = ReadAll(name0, k0);
            raw1 = ReadAll(name1, k1);
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            Trouble($"diff: {name0}: {ex.Message}");
            return;
        }

        bool identicalBytes = string.Equals(raw0, raw1, StringComparison.Ordinal);
        var t0 = DiffText.Parse(raw0, _plan.StripTrailingCr, _plan.Text);
        var t1 = DiffText.Parse(raw1, _plan.StripTrailingCr, _plan.Text);

        if (t0.Binary || t1.Binary)
        {
            if (identicalBytes) { ReportIdentical(name0, name1); return; }
            Say(_plan.Brief ? $"Files {name0} and {name1} differ" : $"Binary files {name0} and {name1} differ");
            Differ();
            return;
        }

        var script = identicalBytes ? new List<DiffChange>() : DiffCompare.Script(t0, t1, _plan);
        if (!DiffCompare.HasRealChange(script))
        {
            ReportIdentical(name0, name1);
            return;
        }

        Differ();
        if (_plan.Brief)
        {
            Say($"Files {name0} and {name1} differ");
            return;
        }

        if (listing) Say($"diff{_plan.SwitchString} {name0} {name1}");
        DiffFormatter.Write(_plan, t0, t1, script, HeaderFor(name0, k0, 0), HeaderFor(name1, k1, 1), Say);
    }

    private void ReportIdentical(string name0, string name1)
    {
        if (_plan.ReportIdentical) Say($"Files {name0} and {name1} are identical");
    }
}
