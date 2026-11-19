using System.Management.Automation;
using System.Security.Cryptography;

namespace PsBash.Cmdlets;

/// <summary>
/// GNU coreutils <c>mktemp</c>: create a uniquely-named file (or directory with <c>-d</c>) from a TEMPLATE whose
/// last run of 3+ <c>X</c>s is replaced with random <c>[A-Za-z0-9]</c>, and print its name.
/// <list type="bullet">
/// <item>No TEMPLATE: <c>tmp.XXXXXXXXXX</c> under the temp directory.</item>
/// <item>A TEMPLATE alone is relative to the current directory (it may name a directory: <c>p/q.XXX</c>).</item>
/// <item><c>-t</c> / <c>-p DIR</c> / <c>--tmpdir[=DIR]</c>: the TEMPLATE is a name under DIR, else <c>$TMPDIR</c>,
/// else the temp directory (<c>-t</c>: <c>$TMPDIR</c> first).</item>
/// </list>
/// The temp directory when <c>$TMPDIR</c> is unset or empty is ps-bash's <c>/tmp</c> stand-in,
/// <c>{host temp}/ps-bash/proc-sub</c> (the host reaps stale entries there). The decision is
/// <see cref="Plan"/> (pure). REGRESSION: <c>$TMPDIR</c> was ignored, a TEMPLATE's directory was dropped, and the
/// X run was kept with a random name appended.
/// <para>
/// Output: a typed <c>PsBash.MktempOutput</c> with <c>Path</c> (native) and <c>BashText</c> (the name as GNU prints
/// it: the template's own spelling, separators as <c>/</c>).
/// </para>
/// <para>
/// <b>Colliding flags</b>: <c>-d</c> (<c>-Debug</c>) is a switch; <c>-p</c> (<c>-ProgressAction</c>) is the
/// value-bearing <see cref="P"/> decoy.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashMktemp")]
[OutputType("PsBash.MktempOutput")]
public sealed class InvokeBashMktempCommand : PSCmdlet
{
    [Parameter] public SwitchParameter d { get; set; }

    /// <summary>
    /// GNU mktemp's <c>-p DIR</c>. Declared as a decoy value-bearing parameter because the bare <c>-p</c>
    /// prefix-collides with <c>-ProgressAction</c> (PS 7.4) and the binder crashes ("ambiguous") before
    /// <see cref="Arguments"/> sees it.
    /// </summary>
    [Parameter] public string? P { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>The default template (GNU <c>default_template</c>).</summary>
    internal const string DefaultTemplate = "tmp.XXXXXXXXXX";

    /// <summary>
    /// Where the name goes: <see cref="Dir"/> (null = the template is a path on its own), the template with its X
    /// run at <see cref="XStart"/> / <see cref="XCount"/>, or the GNU diagnostic in <see cref="Error"/>.
    /// </summary>
    internal readonly record struct MktempPlan(string? Dir, string Template, int XStart, int XCount, string? Error);

    /// <summary>
    /// GNU mktemp's choice of directory and template (coreutils <c>mktemp.c</c>). <paramref name="dirArg"/> is
    /// <c>-p</c>'s value (null when not given, "" for <c>--tmpdir</c> / <c>-p ''</c>); <paramref name="tmpdir"/>
    /// the exported <c>TMPDIR</c>; <paramref name="defaultDir"/> the temp directory used when both are empty.
    /// </summary>
    internal static MktempPlan Plan(string? template, bool tOption, string? dirArg, string? suffix, string? tmpdir, string defaultDir)
    {
        bool useDir = template is null || tOption || dirArg is not null;
        template ??= DefaultTemplate;

        string body;
        if (suffix is not null)
        {
            if (template.Length == 0 || template[^1] != 'X')
                return Fail($"mktemp: with --suffix, template {GnuQuote.Locale(template)} must end in X");
            body = template;
            template += suffix;
        }
        else
        {
            int lastX = template.LastIndexOf('X');
            body = lastX < 0 ? template : template[..(lastX + 1)];
        }
        int count = 0;
        while (count < body.Length && body[body.Length - 1 - count] == 'X') count++;
        if (count < 3)
            return Fail($"mktemp: too few X's in template {GnuQuote.Locale(template)}");

        string? dir = null;
        if (useDir)
        {
            if (tOption)
            {
                dir = !string.IsNullOrEmpty(tmpdir) ? tmpdir : !string.IsNullOrEmpty(dirArg) ? dirArg : defaultDir;
                if (template.Contains('/') || (OperatingSystem.IsWindows() && template.Contains('\\')))
                    return Fail($"mktemp: invalid template, {GnuQuote.Locale(template)}, contains directory separator");
            }
            else
            {
                dir = !string.IsNullOrEmpty(dirArg) ? dirArg : !string.IsNullOrEmpty(tmpdir) ? tmpdir : defaultDir;
                if (template.StartsWith('/') || Path.IsPathRooted(template))
                    return Fail($"mktemp: invalid template, {GnuQuote.Locale(template)}; with --tmpdir, it may not be absolute");
            }
        }
        return new MktempPlan(dir, template, body.Length - count, count, null);

        static MktempPlan Fail(string error) => new(null, "", 0, 0, error);
    }

    /// <summary>GNU's alphabet for the X run.</summary>
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>The plan's name (directory as typed + template) with the X run filled at random.</summary>
    internal static string Fill(MktempPlan plan)
    {
        var random = RandomNumberGenerator.GetString(Alphabet, plan.XCount);
        var name = string.Concat(plan.Template.AsSpan(0, plan.XStart), random, plan.Template.AsSpan(plan.XStart + plan.XCount));
        if (plan.Dir is null) return name;
        var dir = plan.Dir.Length > 1 ? plan.Dir.TrimEnd('/', '\\') : plan.Dir;
        return dir.Length > 0 && (dir[^1] == '/' || dir[^1] == '\\') ? dir + name : dir + "/" + name;
    }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "mktemp", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "mktemp"))
                WriteObject(line);
            return;
        }

        bool makeDir = d.IsPresent, dryRun = false, quiet = false, tOption = false, endOfOptions = false;
        string? dirArg = MyInvocation.BoundParameters.ContainsKey(nameof(P)) ? P ?? "" : null;
        string? suffix = null;
        var operands = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (endOfOptions || arg.Length < 2 || arg[0] != '-') { operands.Add(arg); continue; }
            if (arg == "--") { endOfOptions = true; continue; }
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                int eq = arg.IndexOf('=');
                string name = eq < 0 ? arg : arg[..eq];
                switch (name)
                {
                    case "--directory": makeDir = true; continue;
                    case "--dry-run": dryRun = true; continue;
                    case "--quiet": quiet = true; continue;
                    case "--tmpdir": dirArg = eq < 0 ? "" : arg[(eq + 1)..]; continue;
                    case "--suffix":
                        if (eq >= 0) { suffix = arg[(eq + 1)..]; continue; }
                        if (i + 1 < args.Length) { suffix = args[++i]; continue; }
                        Usage("option '--suffix' requires an argument");
                        return;
                }
                Usage($"unrecognized option '{arg}'");
                return;
            }
            for (int k = 1; k < arg.Length; k++)
            {
                char c = arg[k];
                switch (c)
                {
                    case 'd': makeDir = true; continue;
                    case 'u': dryRun = true; continue;
                    case 'q': quiet = true; continue;
                    case 't': tOption = true; continue;
                    case 'p':
                        if (k + 1 < arg.Length) dirArg = arg[(k + 1)..];
                        else if (i + 1 < args.Length) dirArg = args[++i];
                        else { Usage("option requires an argument -- 'p'"); return; }
                        k = arg.Length;
                        continue;
                }
                Usage($"invalid option -- '{c}'");
                return;
            }
        }
        if (operands.Count > 1)
        {
            Usage("too many templates");
            return;
        }

        var defaultDir = Path.Combine(PsBash.Core.HostTempPath.Get(), "ps-bash", "proc-sub");
        var plan = Plan(operands.Count == 1 ? operands[0] : null, tOption, dirArg, suffix,
            BashVariableStore.GetExported("TMPDIR"), defaultDir);
        if (plan.Error is not null)
        {
            Fail(plan.Error, quiet);
            return;
        }
        if (plan.Dir == defaultDir) Directory.CreateDirectory(defaultDir);

        // mktemp's contract is a PRIVATE scratch target (it routinely holds secrets), so create it exclusively
        // (O_EXCL — never adopt a pre-existing path) and, on Unix, owner-only (0700 dir / 0600 file). A name
        // that exists is retried with fresh random characters, as GNU's gen_tempname does.
        string shown = "", full = "";
        for (int attempt = 0; ; attempt++)
        {
            shown = Fill(plan);
            full = FileSystemHelpers.ProviderPath(this, shown);
            try
            {
                if (dryRun)
                {
                    if (File.Exists(full) || Directory.Exists(full)) throw new IOException("exists");
                }
                else if (makeDir)
                {
                    if (File.Exists(full) || Directory.Exists(full)) throw new IOException("exists");
                    var parent = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                        throw new DirectoryNotFoundException();
                    if (OperatingSystem.IsWindows())
                        Directory.CreateDirectory(full);
                    else
                        Directory.CreateDirectory(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                else
                {
                    var fileOpts = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                    };
                    if (!OperatingSystem.IsWindows())
                        fileOpts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using var fs = new FileStream(full, fileOpts);
                }
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                bool collided = ex is IOException && ex is not DirectoryNotFoundException && ex is not FileNotFoundException
                    && (File.Exists(full) || Directory.Exists(full));
                if (collided && attempt < 100) continue;
                var what = dryRun ? "generate a name" : makeDir ? "create directory" : "create file";
                Fail($"mktemp: failed to {what} via template {GnuQuote.Locale(TemplateDisplay(plan))}: "
                    + FileSystemHelpers.ReadErrorMessage(ex, full), quiet);
                return;
            }
        }

        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.MktempOutput");
        obj.Properties.Add(new PSNoteProperty("Path", full));
        // The script sees the shell form (C:/x, docs/specs/paths.md); Path stays native for PowerShell callers.
        obj.Properties.Add(new PSNoteProperty("BashText", PsBash.Core.BashPath.ToShell(shown)));
        WriteObject(obj);
    }

    /// <summary>The unfilled template with its directory, as GNU names it in a failure.</summary>
    private static string TemplateDisplay(MktempPlan plan)
    {
        if (plan.Dir is null) return plan.Template;
        var dir = plan.Dir.Length > 1 ? plan.Dir.TrimEnd('/', '\\') : plan.Dir;
        return PsBash.Core.BashPath.ToShell(dir.EndsWith('/') ? dir + plan.Template : dir + "/" + plan.Template);
    }

    private void Usage(string what)
    {
        var message = "mktemp: " + what;
        if (FileSystemHelpers.UsageHint("mktemp") is { } hint) message += "\n" + hint;
        FileSystemHelpers.WriteStderr(this, message);
        FileSystemHelpers.SetLastExitCode(this, 1);
    }

    private void Fail(string message, bool quiet)
    {
        if (!quiet) FileSystemHelpers.WriteStderr(this, message);
        FileSystemHelpers.SetLastExitCode(this, 1);
    }
}
