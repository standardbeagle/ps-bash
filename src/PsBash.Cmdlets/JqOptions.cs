using System.Collections.Specialized;

namespace PsBash.Cmdlets;

/// <summary>
/// jq's command line (jq 1.7 <c>main.c</c>): short options may be bundled (<c>-nr</c>), options and operands may be interleaved, the first
/// non-option word is the program (or, with <c>-f</c>, the program FILE), the rest are input files — or positional arguments after
/// <c>--args</c> / <c>--jsonargs</c>. Pure: file reads for <c>--slurpfile</c>/<c>--rawfile</c> go through the supplied reader.
/// </summary>
internal sealed class JqOptions
{
    public bool NullInput, RawOutput, JoinOutput, AsciiOutput, Slurp, SortKeys, Compact, ExitStatus, RawInput, Tab, Seq, Stream, FromFile;
    public int Indent = 2;
    public string? Program;
    public readonly List<string> Files = new();
    public readonly OrderedDictionary Named = new();
    public readonly List<object?> Positional = new();

    /// <summary>Set when the run is only <c>--help</c> / <c>--version</c>.</summary>
    public bool Help, Version;

    /// <summary>A usage error: the message lines for stderr and the exit status.</summary>
    public sealed record UsageError(string Message, int ExitCode);

    public const string UsageHint = "Use jq --help for help with command-line options,\nor see the jq manpage, or online docs  at https://jqlang.github.io/jq";

    public const string Usage =
        "Usage:\tjq [OPTIONS] FILTER [FILES...]\n\tjq [OPTIONS] --args FILTER [STRINGS...]\n\tjq [OPTIONS] --jsonargs FILTER [JSON_TEXTS...]";

    /// <summary>Parses <paramref name="args"/>; <paramref name="readFile"/> returns a file's text or throws <see cref="IOException"/>.</summary>
    public static bool TryParse(string[] args, Func<string, string> readFile, out JqOptions options, out UsageError? error)
    {
        options = new JqOptions();
        error = null;
        bool programGiven = false;
        bool dashDash = false;
        // after --args / --jsonargs, non-option words after the program are positional (text / JSON)
        string positionalMode = "";

        UsageError Fail(string message, int code = 2) => new(message + "\n" + UsageHint, code);

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (!dashDash && a == "--") { dashDash = true; continue; }
            bool isOption = !dashDash && a.Length > 1 && a[0] == '-';
            if (!isOption)
            {
                if (!programGiven) { options.Program = a; programGiven = true; continue; }
                if (positionalMode == "args") options.Positional.Add(a);
                else if (positionalMode == "jsonargs")
                {
                    try { options.Positional.Add(JqJsonReader.ParseSingle(a)); }
                    catch (JqJsonException)
                    {
                        error = Fail("jq: invalid JSON text passed to --jsonargs");
                        return false;
                    }
                }
                else options.Files.Add(a);
                continue;
            }

            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                switch (a)
                {
                    case "--null-input": options.NullInput = true; break;
                    case "--raw-output": options.RawOutput = true; break;
                    case "--join-output": options.RawOutput = true; options.JoinOutput = true; break;
                    case "--ascii-output": options.AsciiOutput = true; break;
                    case "--slurp": options.Slurp = true; break;
                    case "--sort-keys": options.SortKeys = true; break;
                    case "--compact-output": options.Compact = true; break;
                    case "--exit-status": options.ExitStatus = true; break;
                    case "--raw-input": options.RawInput = true; break;
                    case "--tab": options.Tab = true; break;
                    case "--seq": options.Seq = true; break;
                    case "--stream": options.Stream = true; break;
                    case "--from-file": options.FromFile = true; break;
                    case "--monochrome-output": case "--unbuffered": break;
                    case "--help": options.Help = true; break;
                    case "--version": options.Version = true; break;
                    case "--args": positionalMode = "args"; break;
                    case "--jsonargs": positionalMode = "jsonargs"; break;
                    case "--indent":
                        {
                            if (i + 1 >= args.Length) { error = Fail("jq: --indent takes one parameter"); return false; }
                            int n = Atoi(args[++i]);
                            if (n < -1) { error = Fail("jq: Cannot indent less than -1 characters"); return false; }
                            if (n > 7) { error = Fail("jq: --indent takes a number between -1 and 7"); return false; }
                            options.Indent = n;
                            options.Tab = n == -1;
                            if (n == -1) options.Indent = 1;
                            break;
                        }
                    case "--arg":
                        {
                            if (i + 2 >= args.Length) { error = Fail("jq: --arg takes two parameters (e.g. --arg varname value)"); return false; }
                            string name = args[++i];
                            string value = args[++i];
                            if (!options.Named.Contains(name)) options.Named[name] = value;   // the first of a repeated name wins
                            break;
                        }
                    case "--argjson":
                        {
                            if (i + 2 >= args.Length) { error = Fail("jq: --argjson takes two parameters (e.g. --argjson varname text)"); return false; }
                            string name = args[++i];
                            string text = args[++i];
                            try { var parsedValue = JqJsonReader.ParseSingle(text); if (!options.Named.Contains(name)) options.Named[name] = parsedValue; }
                            catch (JqJsonException) { error = Fail("jq: invalid JSON text passed to --argjson"); return false; }
                            break;
                        }
                    case "--slurpfile":
                    case "--rawfile":
                        {
                            if (i + 2 >= args.Length) { error = Fail($"jq: {a} takes two parameters (e.g. {a} varname filename)"); return false; }
                            string name = args[++i];
                            string file = args[++i];
                            string text;
                            try { text = readFile(file); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                error = new UsageError($"jq: Bad JSON in {a} {name} {file}: Could not open {file}: No such file or directory", 2);
                                return false;
                            }
                            if (a == "--rawfile") options.Named[name] = text;
                            else
                            {
                                try { options.Named[name] = JqJsonReader.ParseAll(text).ToArray(); }
                                catch (JqJsonException ex) { error = new UsageError($"jq: Bad JSON in --slurpfile {name} {file}: {ex.Message}", 2); return false; }
                            }
                            break;
                        }
                    default:
                        error = Fail($"jq: Unknown option {a}");
                        return false;
                }
                continue;
            }

            // bundled short options
            for (int k = 1; k < a.Length; k++)
            {
                switch (a[k])
                {
                    case 'n': options.NullInput = true; break;
                    case 'r': options.RawOutput = true; break;
                    case 'j': options.RawOutput = true; options.JoinOutput = true; break;
                    case 'a': options.AsciiOutput = true; break;
                    case 's': options.Slurp = true; break;
                    case 'S': options.SortKeys = true; break;
                    case 'c': options.Compact = true; break;
                    case 'e': options.ExitStatus = true; break;
                    case 'R': options.RawInput = true; break;
                    case 'f': options.FromFile = true; break;
                    case 'M': case 'b': break;
                    case 'h': options.Help = true; break;
                    case 'V': options.Version = true; break;
                    default:
                        error = Fail($"jq: Unknown option {a}");
                        return false;
                }
            }
        }

        if (options.Help || options.Version) return true;
        // No program: jq reads "." when it is not talking to a terminal (a script never is).
        if (options.Program == null) { options.Program = "."; options.FromFile = false; }
        if (options.FromFile)
        {
            string file = options.Program;
            try { options.Program = readFile(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = new UsageError($"jq: error: Could not open {file}: No such file or directory", 2);
                return false;
            }
        }
        return true;
    }

    /// <summary>C <c>atoi</c>: leading blanks, an optional sign and digits; anything else is 0.</summary>
    private static int Atoi(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        bool negative = false;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) { negative = s[i] == '-'; i++; }
        long n = 0;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9' && n < 100000) { n = n * 10 + (s[i] - '0'); i++; }
        return (int)(negative ? -n : n);
    }

    /// <summary>The output layout for <see cref="JqValue.ToJson"/>.</summary>
    public JqValue.WriteOptions WriteOptions => new(Compact ? 0 : Indent, Tab && !Compact, SortKeys, AsciiOutput);
}
