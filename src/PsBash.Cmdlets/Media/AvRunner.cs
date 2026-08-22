using System.Diagnostics;

namespace PsBash.Cmdlets.Media;

/// <summary>
/// The cmdlet-free half of <c>psav</c>: spawn a plan, probe a file, scan a directory for media.
/// Split out of <see cref="InvokeBashFfmpegCommand"/> so the interactive gallery (<c>avtui</c>) can
/// take screenshots and build GIFs through the exact same code path as the non-interactive command,
/// without needing a live <c>PSCmdlet</c> to write through.
/// </summary>
internal static class AvRunner
{
    /// <summary>Extensions the gallery treats as media worth probing.</summary>
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".gif",
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma",
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff",
    };

    /// <summary>A directory scan is capped so a gallery opened in a media library cannot probe forever.</summary>
    public const int ScanLimit = 200;

    /// <summary>
    /// Spawn a plan with the shared bounded + kill-tree contract. Throws
    /// <see cref="System.ComponentModel.Win32Exception"/> when the tool is not on PATH — callers turn
    /// that into the install hint.
    /// </summary>
    public static BashRuntime.ChildProcessResult RunPlan(AvPlan plan, string? workingDir)
    {
        var psi = new ProcessStartInfo(plan.Tool);
        foreach (var arg in plan.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrEmpty(workingDir))
        {
            psi.WorkingDirectory = workingDir;
        }

        return BashRuntime.RunChildProcess(psi, plan.Timeout);
    }

    /// <summary>Probe one file, or null when ffprobe is absent, errors, or does not recognise it.</summary>
    public static MediaSummary? Probe(string path, string? workingDir)
    {
        try
        {
            var result = RunPlan(FfmpegPlan.Probe(path), workingDir);
            return result.ExitCode == 0 ? FfprobeReport.Parse(path, result.Stdout) : null;
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            return null;
        }
    }

    /// <summary>Is this path one the gallery should probe?</summary>
    public static bool IsMediaFile(string path)
        => MediaExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// The media files under <paramref name="target"/> (a directory, or a single file), newest first
    /// and capped at <see cref="ScanLimit"/>. Non-recursive: a gallery is a working directory view,
    /// not a library crawl.
    /// </summary>
    public static IReadOnlyList<string> ScanMedia(string target)
    {
        try
        {
            if (File.Exists(target))
            {
                return new[] { target };
            }

            if (!Directory.Exists(target))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateFiles(target)
                .Where(IsMediaFile)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(ScanLimit)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception ex) when (!FileSystemHelpers.IsPipelineStop(ex))
        {
            return Array.Empty<string>();
        }
    }
}
