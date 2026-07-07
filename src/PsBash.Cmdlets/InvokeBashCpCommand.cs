using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashCp</c> (REFACTOR-2).
/// Copies each source operand to the destination, matching GNU coreutils
/// <c>cp</c> semantics. Supports <c>-r</c> / <c>-R</c> (recursive),
/// <c>-v</c> (verbose), <c>-n</c> (no-clobber), <c>-f</c> (force).
///
/// Behavioral parity oracle: the original psm1 function. Branches preserved
/// byte for byte:
/// <list type="bullet">
/// <item>Fewer than 2 operands → "missing file operand" error and no copy.</item>
/// <item>Last operand is the destination; everything else is a source.
/// Sources are glob-expanded.</item>
/// <item>Source is a directory and <c>-r</c> not set → "omitting directory"
/// error and $LASTEXITCODE=1.</item>
/// <item>Destination is an existing directory → copy each source as a child
/// of the dest dir (preserving the source's basename).</item>
/// <item>With <c>-n</c>, skip if target already exists. With <c>-f</c>, an
/// existing target directory is removed before recursive copy.</item>
/// <item>Verbose mode emits <c>'src' -> 'dest'\n</c> per copy.</item>
/// </list>
/// <para>
/// <b>One colliding flag</b> declared explicitly: <c>-v</c> prefix-collides
/// with <c>-Verbose</c>. <c>-r</c> / <c>-R</c> / <c>-n</c> / <c>-f</c> /
/// <c>-p</c> stay in <c>Arguments</c> and are recovered post-parse —
/// PowerShell parameter binding is case-insensitive, so <c>-r</c> and
/// <c>-R</c> are not distinguishable as separate cmdlet parameters; both
/// map to <c>recursive</c> in the body.
/// </para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashCp")]
[OutputType(typeof(string))]
public sealed class InvokeBashCpCommand : PSCmdlet
{
    [Parameter] public SwitchParameter v { get; set; }

    /// <summary>
    /// Bash <c>-p</c> (preserve) decoy. The bare token <c>-p</c> prefix-collides
    /// with the value-bearing common parameter <c>-PipelineVariable</c>, which
    /// would otherwise consume the next token (the source) as its value. An exact
    /// single-letter parameter name beats the common-parameter prefix, so a bare
    /// <c>-p</c> binds here; bundled forms (<c>-rp</c>) still flow through
    /// <see cref="Arguments"/>.
    /// </summary>
    [Parameter] public SwitchParameter p { get; set; }

    /// <summary>
    /// Decoy for the valid-but-unsupported <c>-i</c> (interactive). The bare <c>-i</c>
    /// prefix-collides with <c>-InformationAction</c>/<c>-InformationVariable</c> and
    /// the binder crashes ("ambiguous") before the classifier could emit its exit-2
    /// "recognized but not supported" message. Re-injected below so the classifier fires.
    /// </summary>
    [Parameter] public SwitchParameter I { get; set; }

    /// <summary>
    /// Decoy for the valid-but-unsupported <c>-d</c> (copy-as-is / no-dereference). Bare
    /// <c>-d</c> silently bound <c>-Debug</c> before this, so the classifier never fired.
    /// </summary>
    [Parameter] public SwitchParameter D { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Valid GNU <c>cp</c> flags ps-bash does not implement. An
    /// option-looking operand matching one of these gets the bash-parity
    /// "recognized but not supported" diagnostic; anything else option-looking
    /// gets "unrecognized/invalid option" (see
    /// <see cref="FileSystemHelpers.TryWriteOperandOptionError"/>). Short forms
    /// that prefix-collide with a PowerShell common parameter never reach this
    /// list (the binder eats them first) so the long form is the catchable
    /// one.</summary>
    private static readonly HashSet<string> CpValidButUnsupported = new(StringComparer.Ordinal)
    {
        "-i", "--interactive", "-l", "--link", "-s", "--symbolic-link",
        "-b", "--backup", "--reflink", "-P", "--no-dereference",
        "-L", "--dereference", "-H", "-t", "--target-directory",
        "-T", "--no-target-directory", "-x", "--one-file-system",
        "--sparse", "--strip-trailing-slashes", "-Z", "--context",
        "--attributes-only", "-d",
    };

    protected override void ProcessRecord()
    {
        // Re-inject decoy-bound classifier flags (bare -i/-d never reach Arguments —
        // the binder crashes/silent-drops them) so TryWriteOperandOptionError still
        // emits the exit-2 "recognized but not supported" message.
        var argsList = new List<string>();
        if (I.IsPresent) argsList.Add("-i");
        if (D.IsPresent) argsList.Add("-d");
        argsList.AddRange(Arguments ?? Array.Empty<string>());
        var args = argsList.ToArray();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "cp", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "cp"))
            {
                WriteObject(line);
            }
            return;
        }

        bool recursive = false;
        bool noClobber = false;
        bool force = false;
        bool verbose = v.IsPresent;
        bool preserve = p.IsPresent;  // bare -p arrives via the decoy parameter
        bool update = false;
        var operands = new List<string>();
        bool pastDoubleDash = false;
        int preDashCount = -1; // count of operands collected before `--` (-1 = no `--`)

        foreach (var a in args)
        {
            if (pastDoubleDash) { operands.Add(a); continue; }
            if (a == "--") { pastDoubleDash = true; preDashCount = operands.Count; continue; }

            // De-bundle combined short flags (-rf, -rpv) — only when every char is a known
            // cp short flag, so unknown tokens (and filenames starting with '-') stay operands.
            if (a.Length > 2 && a[0] == '-' && a[1] != '-' && IsCpShortBundle(a))
            {
                foreach (var c in a.AsSpan(1))
                {
                    switch (c)
                    {
                        case 'r': case 'R': recursive = true; break;
                        case 'n': noClobber = true; break;
                        case 'f': force = true; break;
                        case 'v': verbose = true; break;
                        case 'p': preserve = true; break;
                        case 'u': update = true; break;
                        case 'a': recursive = true; preserve = true; break;
                    }
                }
                continue;
            }

            switch (a)
            {
                case "-r": case "-R": case "--recursive": recursive = true; break;
                case "-n": case "--no-clobber": noClobber = true; break;
                case "-f": case "--force": force = true; break;
                case "-v": case "--verbose": verbose = true; break;
                case "-p": preserve = true; break;
                case "-u": case "--update": update = true; break;
                // -a / --archive == -dR --preserve=all; on Windows we honor the
                // recursive + timestamp/attribute preservation that maps.
                case "-a": case "--archive": recursive = true; preserve = true; break;
                default: operands.Add(a); break;
            }
        }

        // An option-looking token that survived flag parsing is an unknown or
        // valid-but-unsupported flag, not a file — classify it (exit 2) before it
        // is mistaken for a source/dest path. Only operands collected BEFORE `--`
        // are classified; tokens after `--` are real filenames (even if they look
        // like flags), so a `-leading` file passes through.
        var cpToClassify = preDashCount < 0 ? operands : operands.GetRange(0, preDashCount);
        if (FileSystemHelpers.TryWriteOperandOptionError(this, "cp", cpToClassify, CpValidButUnsupported))
            return;

        if (operands.Count < 2)
        {
            FileSystemHelpers.WriteBashError(this, "cp: missing file operand");
            return;
        }

        var destRaw = operands[^1];
        var sourceOperands = operands.GetRange(0, operands.Count - 1);

        // Expand globs on the source list, preserving order.
        var sources = new List<string>();
        foreach (var s in sourceOperands)
        {
            foreach (var expanded in FileSystemHelpers.ResolveOperandPaths(this, s))
            {
                sources.Add(expanded);
            }
        }

        bool hadError = false;
        var destAbs = SessionState.Path.GetUnresolvedProviderPathFromPSPath(destRaw);
        bool destIsExistingDir = Directory.Exists(destAbs);

        foreach (var src in sources)
        {
            bool srcIsFile = File.Exists(src);
            bool srcIsDir = !srcIsFile && Directory.Exists(src);

            if (!srcIsFile && !srcIsDir)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"cp: cannot stat '{src}': No such file or directory");
                hadError = true;
                continue;
            }

            if (srcIsDir && !recursive)
            {
                FileSystemHelpers.WriteBashError(this,
                    $"cp: -r not specified; omitting directory '{src}'");
                hadError = true;
                continue;
            }

            string targetPath = destAbs;
            if (destIsExistingDir)
            {
                targetPath = Path.Combine(destAbs, Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            }

            if (noClobber && (File.Exists(targetPath) || Directory.Exists(targetPath)))
            {
                continue;
            }

            try
            {
                if (srcIsDir)
                {
                    if (Directory.Exists(targetPath) && force)
                    {
                        // Read-only-aware force delete (Windows .git packs / node_modules)
                        // — was a plain Directory.Delete that threw on read-only descendants.
                        FileSystemHelpers.DeleteDirectoryForce(targetPath);
                    }
                    CopyDirectoryRecursive(src, targetPath, preserve, update);
                }
                else
                {
                    // -u: skip when the destination exists and is not older than the source.
                    if (update && File.Exists(targetPath)
                        && File.GetLastWriteTimeUtc(src) <= File.GetLastWriteTimeUtc(targetPath))
                    {
                        continue;
                    }
                    var parent = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }
                    File.Copy(src, targetPath, overwrite: true);
                    if (preserve) PreserveMetadata(src, targetPath, isDir: false);
                }
            }
            catch (Exception ex)
            {
                if (FileSystemHelpers.IsPipelineStop(ex)) throw;
                FileSystemHelpers.WriteBashError(this,
                    $"cp: cannot copy '{src}' to '{targetPath}': {ex.Message}");
                hadError = true;
                continue;
            }

            if (verbose)
            {
                WriteObject(BashRuntime.NewBashObject(
                    $"'{FileSystemHelpers.ToBashPath(src)}' -> '{FileSystemHelpers.ToBashPath(targetPath)}'\n"));
            }
        }

        if (hadError) FileSystemHelpers.SetLastExitCode(this, 1);
    }

    /// <summary>True when <paramref name="s"/> (a multi-char <c>-xyz</c> token) is a bundle of
    /// only known cp short flags, so it can be safely split into individual switches.</summary>
    private static bool IsCpShortBundle(string s)
    {
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] is not ('r' or 'R' or 'n' or 'f' or 'v' or 'p' or 'u' or 'a')) return false;
        }
        return true;
    }

    private static void CopyDirectoryRecursive(string src, string dest, bool preserve, bool update)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            var target = Path.Combine(dest, Path.GetFileName(file));
            if (update && File.Exists(target)
                && File.GetLastWriteTimeUtc(file) <= File.GetLastWriteTimeUtc(target))
            {
                continue;
            }
            File.Copy(file, target, overwrite: true);
            if (preserve) PreserveMetadata(file, target, isDir: false);
        }
        foreach (var sub in Directory.EnumerateDirectories(src))
        {
            var subDest = Path.Combine(dest, Path.GetFileName(sub));
            // A directory junction / symlink must be copied as a LINK, never recursed into:
            // recursing would copy the link TARGET's contents (a destructive escape out of the
            // source tree), and a cycle (link -> ancestor) would recurse until the stack overflows.
            if (FileSystemHelpers.IsReparsePoint(sub))
            {
                FileSystemHelpers.TryCopyDirectoryLink(sub, subDest);
                continue;
            }
            CopyDirectoryRecursive(sub, subDest, preserve, update);
        }
        // Apply directory timestamps LAST — writing children bumps the dir mtime,
        // so GNU cp -p restores it after the contents are in place.
        if (preserve) PreserveMetadata(src, dest, isDir: true);
    }

    /// <summary>
    /// Best-effort <c>cp -p</c>: copy timestamps and attributes from source to
    /// destination. Unix mode bits and ownership have no faithful Windows
    /// representation, so those parts of <c>--preserve=all</c> are silently not
    /// applied; timestamps and the read-only/hidden/archive attributes are.
    /// </summary>
    private static void PreserveMetadata(string src, string dest, bool isDir)
    {
        try
        {
            if (isDir)
            {
                var s = new DirectoryInfo(src);
                var d = new DirectoryInfo(dest);
                d.CreationTimeUtc = s.CreationTimeUtc;
                d.LastWriteTimeUtc = s.LastWriteTimeUtc;
                d.LastAccessTimeUtc = s.LastAccessTimeUtc;
                d.Attributes = s.Attributes;
            }
            else
            {
                File.SetCreationTimeUtc(dest, File.GetCreationTimeUtc(src));
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(src));
                File.SetLastAccessTimeUtc(dest, File.GetLastAccessTimeUtc(src));
                new FileInfo(dest) { Attributes = new FileInfo(src).Attributes };
            }
        }
        catch
        {
            // Preservation is best-effort; a locked attribute or unsupported
            // timestamp must not fail the copy itself.
        }
    }
}
