using System.Globalization;
using System.Text.Json;

namespace PsBash.Cmdlets.Media;

/// <summary>
/// One media file as ps-bash sees it: the fields worth putting in a pipeline, lifted out of
/// ffprobe's JSON. <c>Class</c> is the styling hook the <c>av</c> stylesheet colours by
/// (<c>video</c> / <c>audio</c> / <c>image</c> / <c>unknown</c>).
/// </summary>
internal sealed record MediaSummary(
    string Path,
    string Format,
    double Duration,
    long Size,
    long Bitrate,
    string? VideoCodec,
    int Width,
    int Height,
    double Fps,
    string? AudioCodec,
    int Channels,
    int SampleRate,
    string Class)
{
    /// <summary>The native-tool-style one-liner carried as <c>BashText</c>, so <c>psav probe</c> pipes as text.</summary>
    public string Text
    {
        get
        {
            var parts = new List<string> { System.IO.Path.GetFileName(Path) };
            if (Class != "image")
            {
                parts.Add(AvTime.Humanize(Duration));
            }

            if (VideoCodec is not null)
            {
                var geometry = Width > 0 && Height > 0 ? $"{Width}x{Height}" : "?";
                parts.Add(Fps > 0 ? $"{VideoCodec} {geometry} @{Fps.ToString("0.##", CultureInfo.InvariantCulture)}fps" : $"{VideoCodec} {geometry}");
            }

            if (AudioCodec is not null)
            {
                parts.Add(SampleRate > 0 ? $"{AudioCodec} {Channels}ch {SampleRate}Hz" : $"{AudioCodec} {Channels}ch");
            }

            parts.Add(AvFormat.HumanSize(Size));
            return string.Join("  ", parts);
        }
    }
}

/// <summary>
/// Parser for <c>ffprobe -print_format json -show_format -show_streams</c>. Pure text in, typed record
/// out — no process, so the whole probe surface (fractional frame rates, missing streams, image
/// files, truncated JSON) is unit-testable against captured fixtures with no ffmpeg on the machine.
/// </summary>
internal static class FfprobeReport
{
    /// <summary>Container / codec names that mean "a still image", not a zero-length video.</summary>
    private static readonly HashSet<string> ImageCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "mjpeg", "bmp", "webp", "tiff", "gif", "ppm", "jpeg2000", "apng",
    };

    /// <summary>
    /// Parse ffprobe JSON for <paramref name="path"/>. Returns null when the payload is not JSON or
    /// carries neither a format nor a stream section (a corrupt / non-media file), so the caller can
    /// report a real error rather than emit a row of zeroes.
    /// </summary>
    public static MediaSummary? Parse(string path, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var hasFormat = root.TryGetProperty("format", out var format);
            var hasStreams = root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array;
            if (!hasFormat && !hasStreams)
            {
                return null;
            }

            var formatName = hasFormat ? Str(format, "format_name") ?? string.Empty : string.Empty;
            var duration = hasFormat ? Num(format, "duration") : 0;
            var size = hasFormat ? (long)Num(format, "size") : 0;
            var bitrate = hasFormat ? (long)Num(format, "bit_rate") : 0;

            JsonElement? video = null;
            JsonElement? audio = null;
            if (hasStreams)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = Str(stream, "codec_type");
                    if (video is null && string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        video = stream;
                    }
                    else if (audio is null && string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        audio = stream;
                    }
                }
            }

            var videoCodec = video is null ? null : Str(video.Value, "codec_name");
            var width = video is null ? 0 : (int)Num(video.Value, "width");
            var height = video is null ? 0 : (int)Num(video.Value, "height");
            var fps = video is null ? 0 : ParseRational(Str(video.Value, "r_frame_rate"));
            var audioCodec = audio is null ? null : Str(audio.Value, "codec_name");
            var channels = audio is null ? 0 : (int)Num(audio.Value, "channels");
            var sampleRate = audio is null ? 0 : (int)Num(audio.Value, "sample_rate");

            // ffprobe reports a still image as a one-frame video stream; separate the two by
            // codec + absent duration so a PNG is not styled (or trimmed) as a zero-length movie.
            var cls = videoCodec is not null && audioCodec is null && duration <= 0 && ImageCodecs.Contains(videoCodec)
                ? "image"
                : videoCodec is not null ? "video"
                : audioCodec is not null ? "audio"
                : "unknown";

            return new MediaSummary(
                path, formatName, duration, size, bitrate,
                videoCodec, width, height, fps,
                audioCodec, channels, sampleRate, cls);
        }
    }

    /// <summary>
    /// ffprobe reports frame rates as an exact rational (<c>30000/1001</c> for NTSC 29.97). Dividing
    /// keeps the real value instead of the 30 a naive int parse would report; <c>0/0</c> (a stream
    /// with no fixed rate) is 0, not a divide-by-zero.
    /// </summary>
    public static double ParseRational(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var slash = text.IndexOf('/');
        if (slash < 0)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) ? plain : 0;
        }

        var okNum = double.TryParse(text[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator);
        var okDen = double.TryParse(text[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator);
        if (!okNum || !okDen || denominator == 0)
        {
            return 0;
        }

        return Math.Round(numerator / denominator, 3);
    }

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>ffprobe writes numbers as JSON strings in <c>format</c> and as numbers in <c>streams</c>; accept both.</summary>
    private static double Num(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var v))
        {
            return 0;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : 0,
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0,
            _ => 0,
        };
    }
}

/// <summary>Small display formatters shared by the media rows.</summary>
internal static class AvFormat
{
    /// <summary>A byte count as a short human string (<c>4.2 MB</c>).</summary>
    public static string HumanSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
