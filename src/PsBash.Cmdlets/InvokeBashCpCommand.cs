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

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var args = Arguments ?? Array.Empty<string>();

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
        var operands = new List<string>();

        foreach (var a in args)
        {
            // De-bundle combined short flags (-rf, -rfv) — only when every char is a known
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
                    }
                }
                continue;
            }

            switch (a)
            {
                case "-r": case "-R": recursive = true; break;
                case "-n": noClobber = true; break;
                case "-f": force = true; break;
                case "-v": verbose = true; break;
                default: operands.Add(a); break;
            }
        }

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
                    CopyDirectoryRecursive(src, targetPath);
                }
                else
                {
                    var parent = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }
                    File.Copy(src, targetPath, overwrite: true);
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
            if (s[i] is not ('r' or 'R' or 'n' or 'f' or 'v')) return false;
        }
        return true;
    }

    private static void CopyDirectoryRecursive(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var sub in Directory.EnumerateDirectories(src))
        {
            CopyDirectoryRecursive(sub, Path.Combine(dest, Path.GetFileName(sub)));
        }
    }
}
