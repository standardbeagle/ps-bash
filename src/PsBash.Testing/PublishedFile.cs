namespace PsBash.Testing;

/// <summary>
/// Reads a file that ANOTHER process publishes while the test runs (a pid file, a marker). Seeing the
/// name on disk does not mean it can be read yet: the writer may still hold it, and on Windows a
/// freshly renamed file can be held for a moment by whatever inspects new files. A bare
/// <c>File.Exists</c> followed by <c>File.ReadAllText</c> therefore fails now and then with "being
/// used by another process" (fleet g43, ProcessTreeStopTests). "Not readable yet" is the same answer
/// as "not there yet", and both are retried on one limit.
/// Guard: <c>Core.Tests/PublishedFileGuardTests</c>.
/// </summary>
public static class PublishedFile
{
    /// <summary>The content, or null when the file is absent, still held by its writer, or empty.</summary>
    public static string? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The content once the writer has published it, or null when <paramref name="limit"/> passes first.</summary>
    public static string? ReadWhenReady(string path, TimeSpan limit)
    {
        string? text = null;
        SpinWait.SpinUntil(() => (text = TryRead(path)) is not null, limit);
        return text;
    }
}
