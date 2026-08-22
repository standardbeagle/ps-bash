using System.Globalization;

namespace PsBash.Cmdlets.Media;

/// <summary>
/// The parsed command line of one <c>psav</c> invocation: the subcommand, its file operands, and its
/// long-form options. Pure — no filesystem, no process — so every dispatch decision is unit-testable.
/// </summary>
/// <remarks>
/// <para><b>Why long flags only.</b> ps-bash cmdlets are bound by PowerShell's parameter binder, where
/// a single-dash token is a parameter name: bare <c>-i</c> / <c>-t</c> / <c>-o</c> either hard-crash
/// the binder or are silently swallowed by a common parameter (see
/// <c>docs/specs/runtime-migrated-cmdlets.md</c>). A <c>--name</c> token, by contrast, is parsed as
/// the parameter name <c>-name</c> — which matches nothing, so it lands in
/// <c>ValueFromRemainingArguments</c> intact. <c>psav</c> therefore speaks <c>--at</c> / <c>--out</c> /
/// <c>--fps</c>, and ffmpeg's own single-dash flags are reachable only through
/// <c>psav raw -- -i in.mp4 …</c>, where <c>--</c> ends binding.</para>
/// <para>Both <c>--name value</c> and <c>--name=value</c> spellings are accepted.</para>
/// </remarks>
internal sealed class AvOptions
{
    /// <summary>Options that are switches (present = true) and never consume the next token.</summary>
    private static readonly HashSet<string> Switches = new(StringComparer.OrdinalIgnoreCase)
    {
        "mute", "audio", "dry-run", "quiet", "verbose", "keep", "help", "version", "no-loop", "overwrite",
    };

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _operands = new();
    private readonly List<string> _passthrough = new();

    private AvOptions(string subcommand)
    {
        Subcommand = subcommand;
    }

    /// <summary>The subcommand word (<c>probe</c>, <c>shot</c>, <c>gif</c>, …), lower-cased; empty when none was given.</summary>
    public string Subcommand { get; }

    /// <summary>File / directory operands, in order.</summary>
    public IReadOnlyList<string> Operands => _operands;

    /// <summary>Everything after a literal <c>--</c>: handed to ffmpeg verbatim by <c>raw</c>.</summary>
    public IReadOnlyList<string> Passthrough => _passthrough;

    /// <summary>The unknown option that made parsing fail, or null when the line is well-formed.</summary>
    public string? UnknownOption { get; private init; }

    public bool Has(string name) => _flags.Contains(name) || _values.ContainsKey(name);

    public string? Value(string name, string? fallback = null)
        => _values.TryGetValue(name, out var v) ? v : fallback;

    public bool Flag(string name) => _flags.Contains(name);

    /// <summary>An integer option, or <paramref name="fallback"/> when absent / unparseable.</summary>
    public int Int(string name, int fallback)
        => _values.TryGetValue(name, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : fallback;

    /// <summary>A double option, or <paramref name="fallback"/> when absent / unparseable.</summary>
    public double Double(string name, double fallback)
        => _values.TryGetValue(name, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n
            : fallback;

    /// <summary>A timecode option in seconds, or null when absent / unparseable.</summary>
    public double? Time(string name)
        => _values.TryGetValue(name, out var v) && AvTime.TryParse(v, out var secs) ? secs : null;

    /// <summary>
    /// Split an argv into subcommand + operands + options. The first non-option token is the
    /// subcommand; a literal <c>--</c> ends option parsing and sends the rest to
    /// <see cref="Passthrough"/>.
    /// </summary>
    public static AvOptions Parse(IReadOnlyList<string> args)
    {
        var subcommand = string.Empty;
        var i = 0;
        while (i < args.Count && args[i].StartsWith("--", StringComparison.Ordinal) && args[i] != "--")
        {
            // A leading option (psav --version) leaves the subcommand empty; keep scanning for one.
            break;
        }

        if (i < args.Count && !args[i].StartsWith('-'))
        {
            subcommand = args[i].ToLowerInvariant();
            i++;
        }

        var opts = new AvOptions(subcommand);
        string? unknown = null;

        for (; i < args.Count; i++)
        {
            var arg = args[i];

            if (arg == "--")
            {
                for (var j = i + 1; j < args.Count; j++)
                {
                    opts._passthrough.Add(args[j]);
                }

                break;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                opts._operands.Add(arg);
                continue;
            }

            var body = arg[2..];
            string? inlineValue = null;
            var eq = body.IndexOf('=');
            if (eq >= 0)
            {
                inlineValue = body[(eq + 1)..];
                body = body[..eq];
            }

            if (body.Length == 0)
            {
                unknown ??= arg;
                continue;
            }

            if (Switches.Contains(body))
            {
                opts._flags.Add(body);
                continue;
            }

            if (inlineValue is not null)
            {
                opts._values[body] = inlineValue;
                continue;
            }

            if (i + 1 < args.Count)
            {
                opts._values[body] = args[++i];
                continue;
            }

            unknown ??= arg;   // a value option with no value
        }

        return unknown is null
            ? opts
            : Clone(opts, unknown);
    }

    /// <summary>Re-emit an instance carrying <paramref name="unknown"/> (the init-only field).</summary>
    private static AvOptions Clone(AvOptions src, string unknown)
    {
        var copy = new AvOptions(src.Subcommand) { UnknownOption = unknown };
        foreach (var kv in src._values) copy._values[kv.Key] = kv.Value;
        foreach (var f in src._flags) copy._flags.Add(f);
        copy._operands.AddRange(src._operands);
        copy._passthrough.AddRange(src._passthrough);
        return copy;
    }
}
