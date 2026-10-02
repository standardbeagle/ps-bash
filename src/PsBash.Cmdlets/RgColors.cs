using System.Text;

namespace PsBash.Cmdlets;

/// <summary>
/// rg's colour model: the four output kinds (<c>path line column match</c>), ripgrep's default specs
/// (path magenta, line green, match bold red, column plain) and the <c>--colors TYPE:ATTR:VALUE</c> grammar, rendered
/// exactly like termcolor's ANSI writer: every coloured field is <c>ESC[0m</c> + attributes (bold, dimmed, italic,
/// underline, then foreground, then background) + text + <c>ESC[0m</c>. A match spec with no attributes at all
/// (<c>--colors match:none</c>) switches match highlighting off entirely; the other kinds are still wrapped.
/// </summary>
internal sealed class RgColors
{
    internal enum ColorKind { None, Named, Ansi256, Rgb }

    internal readonly record struct ColorValue(ColorKind Kind, int A, int B, int C);

    internal sealed class Spec
    {
        internal ColorValue Fg, Bg;
        internal bool Bold, Intense, Underline, Italic;

        internal bool IsNone => Fg.Kind == ColorKind.None && Bg.Kind == ColorKind.None && !Bold && !Intense && !Underline && !Italic;

        internal void Clear() { Fg = default; Bg = default; Bold = Intense = Underline = Italic = false; }

        internal string Open()
        {
            var sb = new StringBuilder("\u001b[0m");
            if (Bold) sb.Append("\u001b[1m");
            if (Italic) sb.Append("\u001b[3m");
            if (Underline) sb.Append("\u001b[4m");
            AppendColor(sb, Fg, background: false);
            AppendColor(sb, Bg, background: true);
            return sb.ToString();
        }

        private void AppendColor(StringBuilder sb, ColorValue c, bool background)
        {
            switch (c.Kind)
            {
                case ColorKind.Named:
                    sb.Append("\u001b[").Append(background ? (Intense ? "10" : "4") : (Intense ? "9" : "3")).Append(c.A).Append('m');
                    break;
                case ColorKind.Ansi256:
                    sb.Append("\u001b[").Append(background ? "48" : "38").Append(";5;").Append(c.A).Append('m');
                    break;
                case ColorKind.Rgb:
                    sb.Append("\u001b[").Append(background ? "48" : "38").Append(";2;").Append(c.A).Append(';').Append(c.B).Append(';').Append(c.C).Append('m');
                    break;
            }
        }
    }

    internal readonly Spec Path = new(), Line = new(), Column = new(), Match = new();

    private const string Reset = "\u001b[0m";

    internal RgColors()
    {
        Path.Fg = new ColorValue(ColorKind.Named, 5, 0, 0);     // magenta
        Line.Fg = new ColorValue(ColorKind.Named, 2, 0, 0);     // green
        Match.Fg = new ColorValue(ColorKind.Named, 1, 0, 0);    // red
        Match.Bold = true;
    }

    internal string Paint(Spec spec, string text) => spec.Open() + text + Reset;

    private static readonly string[] ColorNames = { "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white" };

    /// <summary>Apply every <c>--colors</c> spec in order. Returns null on success, else ripgrep's message text.</summary>
    internal string? Apply(IEnumerable<string> specs)
    {
        foreach (var raw in specs)
        {
            var parts = raw.Split(':');
            Spec? target = parts[0] switch
            {
                "path" => Path,
                "line" => Line,
                "column" => Column,
                "match" => Match,
                _ => null,
            };
            if (target is null)
                return $"unrecognized output type '{parts[0]}'. Choose from: path, line, column, match.";
            if (parts.Length == 2 && parts[1] == "none") { target.Clear(); continue; }
            if (parts.Length != 3)
                return $"invalid color spec format: '{raw}'. Valid format is '(path|line|column|match):(fg|bg|style):(value)'.";
            switch (parts[1])
            {
                case "fg":
                case "bg":
                {
                    if (!TryColor(parts[2], out var cv, out var err)) return err;
                    if (parts[1] == "fg") target.Fg = cv; else target.Bg = cv;
                    break;
                }
                case "style":
                    switch (parts[2])
                    {
                        case "bold": target.Bold = true; break;
                        case "nobold": target.Bold = false; break;
                        case "intense": target.Intense = true; break;
                        case "nointense": target.Intense = false; break;
                        case "underline": target.Underline = true; break;
                        case "nounderline": target.Underline = false; break;
                        case "italic": target.Italic = true; break;
                        case "noitalic": target.Italic = false; break;
                        default:
                            return $"unrecognized style attribute '{parts[2]}'. Choose from: nobold, bold, nointense, intense, nounderline, underline, noitalic, italic.";
                    }
                    break;
                default:
                    return $"unrecognized spec attribute '{parts[1]}'. Choose from: fg, bg, style, none.";
            }
        }
        return null;
    }

    private static bool TryColor(string text, out ColorValue value, out string? error)
    {
        value = default;
        error = null;
        int named = Array.IndexOf(ColorNames, text);
        if (named >= 0) { value = new ColorValue(ColorKind.Named, named, 0, 0); return true; }
        var comps = text.Split(',');
        if (comps.Length == 1 && TryByte(comps[0], out int one)) { value = new ColorValue(ColorKind.Ansi256, one, 0, 0); return true; }
        if (comps.Length == 3 && TryByte(comps[0], out int r) && TryByte(comps[1], out int g) && TryByte(comps[2], out int b))
        {
            value = new ColorValue(ColorKind.Rgb, r, g, b);
            return true;
        }
        error = $"unrecognized color name '{text}'. Choose from: black, blue, green, red, cyan, magenta, yellow, white";
        return false;
    }

    private static bool TryByte(string s, out int n)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out n) && n is >= 0 and <= 255;
        return int.TryParse(s, out n) && n is >= 0 and <= 255;
    }
}
