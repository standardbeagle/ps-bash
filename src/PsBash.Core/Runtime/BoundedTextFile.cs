using System.Text;

namespace PsBash.Core.Runtime;

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
}
