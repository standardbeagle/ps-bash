using System.Text;

namespace PsBash.Host.Runtime;

internal static class BoundedTextFile
{
    public static string Read(string path, long maxBytes, string tooLargeMessage)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (bytes.Length + read > maxBytes)
                throw new IOException(tooLargeMessage);
            bytes.Write(buffer, 0, read);
        }

        bytes.Position = 0;
        using var reader = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static async Task<string> ReadAsync(
        string path,
        long maxChars,
        string tooLargeMessage,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 8192,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await ReadAsync(reader, maxChars, tooLargeMessage, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ReadAsync(
        TextReader reader,
        long maxChars,
        string tooLargeMessage,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (sb.Length + read > maxChars)
                throw new IOException(tooLargeMessage);
            sb.Append(buffer, 0, read);
        }

        return sb.ToString();
    }
}
