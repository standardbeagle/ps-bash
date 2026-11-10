using System.Text;

namespace PsBash.Spec;

/// <summary>
/// Reads and writes the Oils spec-test format (see docs/specs/spec-suite.md section 2): <c>####</c> starts a case, the
/// script follows, and <c>## stdout:</c>, <c>## status:</c>, <c>## STDOUT: ... ## END</c> give the expected result.
/// <c>## OK bash ...</c> / <c>## BUG bash ...</c> add an acceptable alternative for bash; annotations naming another
/// shell are kept out of the model. <c>## XFAIL ps-bash ID reason</c>, <c>## timeout: N</c>, <c>## real-launcher</c> are ps-bash's own.
/// </summary>
public static class SpecParser
{
    public static SpecFile Parse(string path, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var header = new StringBuilder();
        var cases = new List<SpecCase>();
        int i = 0;
        while (i < lines.Length && !lines[i].StartsWith("#### ", StringComparison.Ordinal))
            header.Append(lines[i++]).Append('\n');

        while (i < lines.Length)
        {
            string name = lines[i++][5..].Trim();
            var script = new StringBuilder();
            var blocks = new List<Block>();
            XfailMark? xfail = null;
            TimeSpan? timeout = null;
            bool real = false;

            while (i < lines.Length && !lines[i].StartsWith("#### ", StringComparison.Ordinal))
            {
                string line = lines[i];
                if (!line.StartsWith("## ", StringComparison.Ordinal) && line != "##")
                {
                    script.Append(line).Append('\n');
                    i++;
                    continue;
                }

                string body = line.Length > 3 ? line[3..] : "";
                i++;
                if (body.StartsWith("XFAIL ps-bash ", StringComparison.Ordinal))
                {
                    var rest = body["XFAIL ps-bash ".Length..].Split(' ', 2);
                    xfail = new XfailMark(rest[0], rest.Length > 1 ? rest[1] : "");
                }
                else if (body.StartsWith("timeout:", StringComparison.Ordinal))
                    timeout = TimeSpan.FromSeconds(double.Parse(body["timeout:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture));
                else if (body == "real-launcher")
                    real = true;
                else
                {
                    var block = ParseDirective(body, lines, ref i);
                    if (block is not null) blocks.Add(block);
                }
            }

            cases.Add(new SpecCase(name, TrimTrailingBlank(script.ToString()), Combine(blocks, primary: true),
                Alternatives(blocks), xfail, timeout, real));
        }
        return new SpecFile(path, header.ToString(), cases);
    }

    /// <summary>One <c>## key: value</c> line or a <c>## KEY:</c> ... <c>## END</c> block; null when it names another shell.</summary>
    private static Block? ParseDirective(string body, string[] lines, ref int i)
    {
        string[] words = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool alt = words.Length > 0 && words[0] is "OK" or "BUG";
        int skip = 0;
        if (alt)
        {
            // `## OK bash stdout: x` / `## OK osh/bash STDOUT:`; only those that name bash apply here.
            if (words.Length < 3 || !words[1].Split('/').Contains("bash")) { SkipBlock(body, lines, ref i); return null; }
            skip = 2;
        }
        else if (words.Length > 0 && words[0] is "N-I") { SkipBlock(body, lines, ref i); return null; }

        string rest = body.TrimStart();
        for (int w = 0; w < skip; w++) rest = rest[words[w].Length..].TrimStart();
        string key = rest.Split(':')[0];
        string inline = rest.Contains(':') ? rest[(rest.IndexOf(':') + 1)..].TrimStart(' ') : "";

        switch (key)
        {
            case "stdout": return new Block(alt, "stdout", inline.Length == 0 ? "" : inline + "\n");
            case "stderr": return new Block(alt, "stderr", inline.Length == 0 ? "" : inline + "\n");
            case "stdout-json": return new Block(alt, "stdout", System.Text.Json.JsonSerializer.Deserialize<string>(inline) ?? "");
            case "stderr-json": return new Block(alt, "stderr", System.Text.Json.JsonSerializer.Deserialize<string>(inline) ?? "");
            case "status": return new Block(alt, "status", inline.Trim());
            case "STDOUT":
            case "STDERR":
                var sb = new StringBuilder();
                while (i < lines.Length && lines[i] != "## END") sb.Append(lines[i++]).Append('\n');
                i++; // ## END
                return new Block(alt, key.ToLowerInvariant(), sb.ToString());
            default:
                return null; // `## compare_shells:` and other upstream keys
        }
    }

    private static void SkipBlock(string body, string[] lines, ref int i)
    {
        if (!body.TrimEnd().EndsWith("STDOUT:", StringComparison.Ordinal) && !body.TrimEnd().EndsWith("STDERR:", StringComparison.Ordinal)) return;
        while (i < lines.Length && lines[i] != "## END") i++;
        i++;
    }

    private sealed record Block(bool Alt, string Key, string Value);

    private static Expectation Combine(List<Block> blocks, bool primary)
    {
        string? stdout = null, stderr = null; int? status = null;
        foreach (var b in blocks.Where(b => b.Alt != primary))
        {
            if (b.Key == "stdout") stdout = b.Value;
            else if (b.Key == "stderr") stderr = b.Value;
            else if (b.Key == "status" && int.TryParse(b.Value, out var s)) status = s;
        }
        return new Expectation(stdout, status, stderr);
    }

    private static IReadOnlyList<Expectation> Alternatives(List<Block> blocks)
        => blocks.Any(b => b.Alt) ? new[] { Combine(blocks, primary: false) } : Array.Empty<Expectation>();

    private static string TrimTrailingBlank(string s) => s.TrimEnd('\n') + "\n";

    /// <summary>Writes a file back in canonical form (used by <c>record</c>, which fills expectations from real bash).</summary>
    public static string Format(SpecFile file)
    {
        var sb = new StringBuilder(file.Header);
        foreach (var c in file.Cases)
        {
            sb.Append("#### ").Append(c.Name).Append('\n').Append(c.Script);
            AppendExpectation(sb, c.Primary);
            foreach (var alt in c.Alternatives) AppendExpectation(sb, alt, "OK bash ");
            if (c.Xfail is { } x) sb.Append("## XFAIL ps-bash ").Append(x.Id).Append(' ').Append(x.Reason).Append('\n');
            if (c.Timeout is { } t) sb.Append("## timeout: ").Append(t.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            if (c.RealLauncher) sb.Append("## real-launcher\n");
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    private static void AppendExpectation(StringBuilder sb, Expectation e, string prefix = "")
    {
        AppendStream(sb, prefix, "stdout", "STDOUT", e.Stdout);
        AppendStream(sb, prefix, "stderr", "STDERR", e.Stderr);
        if (e.Status is { } s) sb.Append("## ").Append(prefix).Append("status: ").Append(s).Append('\n');
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void AppendStream(StringBuilder sb, string prefix, string small, string big, string? value)
    {
        if (value is null) return;
        if (value.Length == 0) { sb.Append("## ").Append(prefix).Append(small).Append(":\n"); return; }
        // Output with no final newline cannot be a STDOUT block (blocks always end in one): keep it exact as JSON.
        if (!value.EndsWith('\n'))
        {
            sb.Append("## ").Append(prefix).Append(small).Append("-json: ").Append(System.Text.Json.JsonSerializer.Serialize(value, JsonOpts)).Append('\n');
            return;
        }
        // The one-line form drops leading blanks when read back (`## stdout:  ff` is "ff"), so only a line that
        // starts with a non-blank may use it; anything else goes in a block, which is exact.
        if (value.Length > 1 && value.IndexOf('\n') == value.Length - 1 && value[0] != ' ')
            sb.Append("## ").Append(prefix).Append(small).Append(": ").Append(value[..^1]).Append('\n');
        else
            sb.Append("## ").Append(prefix).Append(big).Append(":\n").Append(value.EndsWith('\n') ? value : value + "\n").Append("## END\n");
    }
}
