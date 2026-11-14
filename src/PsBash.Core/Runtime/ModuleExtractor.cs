using System.Security.Cryptography;

namespace PsBash.Core.Runtime;

public static class ModuleExtractor
{
    private const long ExtractionMarkerMaxBytes = 1024;

    private static readonly string[] ModuleFiles =
    [
        "PsBash.psd1",
        "PsBash.psm1",
        "PsBash.Format.ps1xml",
        // Canonical bash flag-spec data; the psm1 loads it from the extracted module dir
        // (single source of truth shared with the host's FlagSpecs.cs).
        "BashFlagSpecs.json",
    ];

    /// <summary>
    /// File name of the binary cmdlets module extracted alongside the psm1.
    /// </summary>
    public const string CmdletsDllFileName = "PsBash.Cmdlets.dll";

    /// <summary>
    /// Logical resource-name prefix for the embedded per-TFM Cmdlets DLLs.
    /// The full name is <c>{prefix}{tfm}/PsBash.Cmdlets.dll</c> — e.g.
    /// <c>PsBash.Module/cmdlets/net10.0/PsBash.Cmdlets.dll</c>.
    /// </summary>
    private const string CmdletsResourcePrefix = "PsBash.Module/cmdlets/";

    /// <summary>
    /// Returns the absolute path to the extracted TFM-matching PsBash.Cmdlets.dll
    /// for the module version embedded in this assembly. Does NOT trigger
    /// extraction — call <see cref="ExtractEmbedded"/> first. The path is
    /// deterministic so callers can hand it to PowerShell as a known import
    /// target with no probing.
    /// </summary>
    public static string GetCmdletsDllPath()
    {
        return Path.Combine(ExtractionDir(), CmdletsDllFileName);
    }

    private static string? _cachedDir;
    private static readonly object _dirLock = new();

    /// <summary>
    /// The version- AND content-stamped extraction directory:
    /// <c>{runtime-dir}/module-{version}-{hash}</c>, where the runtime directory
    /// is <see cref="PsBashRuntimeDirectory"/> (per-user and owner-only on POSIX).
    /// The content hash in the directory NAME is what
    /// keeps two builds that share a version string — most importantly a dev build running next to an
    /// installed ps-bash (which the Claude Code Bash tool spawns constantly) — from extracting into the
    /// SAME directory and clobbering each other. Without it, both invalidate the shared marker on every
    /// run (their embedded content differs), producing a constant re-extraction war where whoever ran
    /// last wins, so a dev host would silently load the installed build's stale cmdlets. Computed once
    /// (the hash covers the ~400 KB cmdlets DLL) and cached.
    /// </summary>
    private static string ExtractionDir()
    {
        if (_cachedDir != null) return _cachedDir;
        lock (_dirLock)
        {
            if (_cachedDir != null) return _cachedDir;
            var asm = typeof(ModuleExtractor).Assembly;
            var version = asm.GetName().Version?.ToString() ?? "0.0.0";
            var hash = ComputeEmbeddedHash(asm).Substring(0, 12).ToLowerInvariant();
            _cachedDir = Path.Combine(
                PsBashRuntimeDirectory.GetPath(), $"module-{version}-{hash}");
            return _cachedDir;
        }
    }

    /// <summary>
    /// Resolves the manifest-resource name of the Cmdlets DLL that matches the
    /// running framework. Prefers an exact TFM match (e.g. net10.0), falling
    /// back to the highest available embedded variant when the exact moniker
    /// is not embedded. Returns null when no Cmdlets DLL is embedded at all.
    /// </summary>
    private static string? ResolveCmdletsResourceName(System.Reflection.Assembly asm)
    {
        var cmdletsResources = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(CmdletsResourcePrefix, StringComparison.Ordinal) &&
                        n.EndsWith("/" + CmdletsDllFileName, StringComparison.Ordinal))
            .ToArray();

        if (cmdletsResources.Length == 0)
            return null;

        var runningTfm = GetRunningTfm();
        var exact = cmdletsResources.FirstOrDefault(n =>
            string.Equals(TfmOf(n), runningTfm, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        // No exact match: pick the highest netN.0 variant available.
        return cmdletsResources
            .OrderByDescending(n => ParseNetMajor(TfmOf(n)))
            .First();
    }

    /// <summary>
    /// Every embedded resource in the matched-TFM cmdlets folder as
    /// (resourceName, fileName) pairs — the PsBash.Cmdlets.dll plus any embedded
    /// dependency assemblies (Strata.* + Spectre.Console in UseStrata builds).
    /// Empty when no cmdlets DLL is embedded.
    /// </summary>
    private static IEnumerable<(string Resource, string FileName)> EnumerateCmdletResources(System.Reflection.Assembly asm)
    {
        var cmdletsResource = ResolveCmdletsResourceName(asm);
        if (cmdletsResource is null)
            yield break;

        var prefix = CmdletsResourcePrefix + TfmOf(cmdletsResource) + "/";
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                yield return (name, name.Substring(prefix.Length));
        }
    }

    /// <summary>Extracts the TFM segment from a Cmdlets resource name.</summary>
    private static string TfmOf(string resourceName)
    {
        // "PsBash.Module/cmdlets/net10.0/PsBash.Cmdlets.dll" -> "net10.0"
        var inner = resourceName.Substring(CmdletsResourcePrefix.Length);
        var slash = inner.IndexOf('/');
        return slash < 0 ? inner : inner.Substring(0, slash);
    }

    /// <summary>Parses the major version out of a "netN.0" moniker; 0 if unrecognized.</summary>
    private static int ParseNetMajor(string tfm)
    {
        if (!tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return 0;
        var rest = tfm.Substring(3);
        var dot = rest.IndexOf('.');
        if (dot > 0)
            rest = rest.Substring(0, dot);
        return int.TryParse(rest, out var major) ? major : 0;
    }

    /// <summary>
    /// Detects the running framework moniker (e.g. "net10.0", "net8.0") from
    /// <see cref="Environment.Version"/>. ps-bash only ships netN.0 TFMs, so
    /// the moniker is always "net{Major}.0".
    /// </summary>
    private static string GetRunningTfm()
    {
        // Environment.Version on .NET 5+ reports the runtime version
        // (e.g. 10.0.0). RuntimeInformation.FrameworkDescription would also
        // work but needs string parsing; Environment.Version is structured.
        return $"net{Environment.Version.Major}.0";
    }

    /// <summary>
    /// Extracts the embedded PsBash module to a temp directory and returns the path to PsBash.psd1.
    /// Also extracts the TFM-matching PsBash.Cmdlets.dll alongside it (see
    /// <see cref="GetCmdletsDllPath"/>). Uses a version-stamped directory so
    /// concurrent processes don't conflict.
    /// Thread-safe: uses a lock file to serialize extraction across processes.
    /// </summary>
    public static string ExtractEmbedded()
    {
        var asm = typeof(ModuleExtractor).Assembly;
        // Create AND validate the per-user runtime directory before reading the
        // marker or laying the module down: on POSIX a pre-existing dir owned by
        // another user — or one they can still write — must refuse, not load.
        PsBashRuntimeDirectory.EnsureDirectory();
        var dir = ExtractionDir();
        // Before the marker is read: a pruner that already holds the directory makes this wait, and the
        // extraction below then lays it down again.
        HoldInUse(dir);
        var marker = Path.Combine(dir, ".extracted");
        var psd1Path = Path.Combine(dir, "PsBash.psd1");

        // Invalidate cache if embedded resource content has changed.
        if (File.Exists(marker))
        {
            var storedHash = ReadExtractionMarker(marker);
            var currentHash = ComputeEmbeddedHash(asm);
            if (storedHash != currentHash)
            {
                try { File.Delete(marker); }
                catch (IOException) { /* another process may be extracting */ }
            }
        }

        // Skip if already extracted for this version
        if (File.Exists(marker))
            return psd1Path;

        Directory.CreateDirectory(dir);

        // Use a lock file to serialize extraction across concurrent processes.
        var lockPath = Path.Combine(dir, ".lock");

        // Step 1 — ACQUIRE the lock. A failure HERE means another process holds it
        // (and is extracting), so waiting for its marker is correct. Two distinct
        // failure shapes both mean "someone else owns the lock":
        //   • IOException — sharing violation (the classic held-lock case);
        //   • UnauthorizedAccessException — the lock file is in its DeleteOnClose
        //     pending-delete window (a prior holder is tearing down). The old
        //     IOException-only catch missed this and crashed host startup.
        FileStream lockFile;
        try
        {
            lockFile = new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            WaitForMarker(marker);
            return psd1Path;
        }
        catch (UnauthorizedAccessException)
        {
            WaitForMarker(marker);
            return psd1Path;
        }

        // Step 2 — we are the WINNER and hold the lock. A failure in this critical
        // section is NOT contention: no other process will write the marker, so
        // WaitForMarker would stall the full timeout and then return a
        // half-clobbered dir with no marker (the reported bug). Let it propagate so
        // the caller sees the real, actionable error (e.g. "Cmdlets.dll is mapped by
        // a running host" when a stale daemon has the file locked).
        try
        {
            // Re-check marker after acquiring lock — another process may have finished.
            if (File.Exists(marker))
                return psd1Path;

            foreach (var file in ModuleFiles)
            {
                var destPath = Path.Combine(dir, file);
                using var stream = asm.GetManifestResourceStream($"PsBash.Module/{file}")!;
                using var dest = new FileStream(
                    destPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                stream.CopyTo(dest);
            }

            // Extract the TFM-matching PsBash.Cmdlets.dll AND any embedded
            // dependency assemblies in the same cmdlets folder (e.g. Strata.* +
            // Spectre.Console, present only in UseStrata builds) alongside the
            // psm1. The host imports PsBash.Cmdlets.dll from this deterministic
            // path (see GetCmdletsDllPath); PowerShell's path-based Import-Module
            // (LoadFrom) then resolves the cmdlet's own dependencies from beside
            // it. Non-Strata builds embed only PsBash.Cmdlets.dll.
            foreach (var (resName, fileName) in EnumerateCmdletResources(asm))
            {
                var destPath = Path.Combine(dir, fileName);
                using var depStream = asm.GetManifestResourceStream(resName)!;
                using var depDest = new FileStream(
                    destPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                depStream.CopyTo(depDest);
            }

            // Write content hash as marker after all files extracted successfully
            var hash = ComputeEmbeddedHash(asm);
            File.WriteAllText(marker, hash);
        }
        finally
        {
            lockFile.Dispose();
        }

        return psd1Path;
    }

    // ---- Old extraction directories ------------------------------------------------------------------
    // Every build (each dev build, each release) extracts into its own module-{version}-{hash} directory,
    // about 5 MB, and nothing removed the old ones: one dev machine held 11 from a single day. A host
    // re-imports the psd1 from its directory for every runspace, so a directory may be removed only when
    // no process uses it. Each process holds {dir}/.inuse open SHARED for its life; the pruner must open
    // it EXCLUSIVELY (on Unix .NET maps these to flock LOCK_SH / LOCK_EX), and while it holds it a new
    // user's open fails, so that user waits, then re-extracts.

    private const string InUseFileName = ".inuse";
    private static FileStream? _inUse;
    private static int _pruneStarted;

    // A directory this old with no .inuse (a release from before the lock) is taken as unused. On
    // Windows a DLL still loaded from it is also checked; elsewhere the age is the only signal.
    internal static readonly TimeSpan LegacyMinAge = TimeSpan.FromDays(7);

    // module-{version}-{hash}: the shape ExtractionDir() makes (version digits and dots, 12 hex digits).
    internal static bool IsExtractionDirName(string name)
    {
        const string head = "module-";
        int dash = name.LastIndexOf('-');
        if (!name.StartsWith(head, StringComparison.Ordinal) || dash <= head.Length || name.Length - dash - 1 != 12) return false;
        for (int i = head.Length; i < dash; i++)
            if (!char.IsAsciiDigit(name[i]) && name[i] != '.') return false;
        for (int i = dash + 1; i < name.Length; i++)
            if (!char.IsAsciiHexDigitLower(name[i])) return false;
        return true;
    }

    private static void HoldInUse(string dir)
    {
        if (_inUse != null) return;
        lock (_dirLock)
        {
            if (_inUse != null) return;
            _inUse = OpenInUse(dir, TimeSpan.FromSeconds(10));
        }
        if (_inUse != null && Interlocked.Exchange(ref _pruneStarted, 1) == 0)
        {
            var runtimeDir = Path.GetDirectoryName(dir)!;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { PruneStaleExtractions(runtimeDir, dir, LegacyMinAge); } catch { /* best effort */ }
            });
        }
    }

    /// <summary>Opens <paramref name="dir"/>/.inuse shared, retrying while a pruner holds it. Null if it never could.</summary>
    internal static FileStream? OpenInUse(string dir, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                Directory.CreateDirectory(dir);
                return new FileStream(Path.Combine(dir, InUseFileName), FileMode.OpenOrCreate,
                    FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline) return null; // extraction still works; this process is just unprotected
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>
    /// Removes the extraction directories under <paramref name="runtimeDir"/> other than
    /// <paramref name="keepDir"/> that no process uses. Returns how many were removed.
    /// </summary>
    internal static int PruneStaleExtractions(string runtimeDir, string keepDir, TimeSpan legacyMinAge)
    {
        int removed = 0;
        string[] dirs;
        try { dirs = Directory.GetDirectories(runtimeDir, "module-*"); }
        catch { return 0; }

        foreach (var d in dirs)
        {
            try
            {
                if (string.Equals(Path.GetFullPath(d), Path.GetFullPath(keepDir), StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsExtractionDirName(Path.GetFileName(d))) continue;
                if (TryRemoveExtraction(d, legacyMinAge)) removed++;
            }
            catch { /* best effort per directory */ }
        }
        return removed;
    }

    private static bool TryRemoveExtraction(string dir, TimeSpan legacyMinAge)
    {
        var inUsePath = Path.Combine(dir, InUseFileName);
        if (!File.Exists(inUsePath))
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < legacyMinAge) return false;
            if (OperatingSystem.IsWindows() && AnyDllLoaded(dir)) return false;
            DeleteContents(dir, except: null);
            Directory.Delete(dir, recursive: true);
            return true;
        }

        FileStream exclusive;
        try
        {
            // DeleteOnClose: .inuse is unlinked while this handle still excludes everyone (on Unix .NET
            // unlinks before it closes), so no user can open it between "proved unused" and the unlink.
            exclusive = new FileStream(inUsePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // a process holds it
        }
        using (exclusive)
            DeleteContents(dir, except: inUsePath);
        // Not recursive: a user that arrived after the close has created a new .inuse here and is
        // extracting, and this then fails and leaves its directory alone.
        Directory.Delete(dir);
        return true;
    }

    // The marker first: if a later delete fails, the next user of the directory re-extracts instead of
    // importing a half-removed module.
    private static void DeleteContents(string dir, string? except)
    {
        File.Delete(Path.Combine(dir, ".extracted"));
        foreach (var f in Directory.GetFiles(dir))
            if (!string.Equals(f, except, StringComparison.OrdinalIgnoreCase)) File.Delete(f);
    }

    // A DLL mapped as an image cannot be opened for writing (Windows).
    private static bool AnyDllLoaded(string dir)
    {
        foreach (var dll in Directory.GetFiles(dir, "*.dll"))
        {
            try { using var _ = new FileStream(dll, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
        }
        return false;
    }

    private static string ReadExtractionMarker(string marker)
    {
        try
        {
            return BoundedTextFile.Read(
                marker,
                ExtractionMarkerMaxBytes,
                "Extraction marker exceeds the maximum supported size.").Trim();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Computes a SHA256 hash over all embedded module resources, including the
    /// TFM-matching Cmdlets DLL. Including the DLL means a rebuilt cmdlets
    /// assembly invalidates the extracted-module cache.
    /// </summary>
    private static string ComputeEmbeddedHash(System.Reflection.Assembly asm)
    {
        using var sha = SHA256.Create();
        using var combined = new MemoryStream();
        foreach (var file in ModuleFiles)
        {
            using var stream = asm.GetManifestResourceStream($"PsBash.Module/{file}")!;
            stream.CopyTo(combined);
        }

        // Hash every embedded cmdlet-folder resource (the DLL + any deps) so a
        // rebuilt cmdlets assembly OR a changed dependency invalidates the cache.
        foreach (var (resName, _) in EnumerateCmdletResources(asm))
        {
            using var depStream = asm.GetManifestResourceStream(resName)!;
            depStream.CopyTo(combined);
        }

        combined.Position = 0;
        var bytes = sha.ComputeHash(combined);
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Wait for another process to finish extraction (up to 10 seconds).
    /// </summary>
    private static void WaitForMarker(string marker)
    {
        for (int i = 0; i < 100; i++)
        {
            if (File.Exists(marker))
                return;
            Thread.Sleep(100);
        }
        // If marker never appears, proceed anyway — the files may be partially extracted
        // but pwsh will fail with a clear error rather than a mysterious lock exception.
    }
}
