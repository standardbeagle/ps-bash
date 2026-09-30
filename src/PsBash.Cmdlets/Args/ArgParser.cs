using System.Text;

namespace PsBash.Cmdlets.Args;

/// <summary>
/// Shared ORDERED getopt-style argument parser. Pure and AOT-safe: no PSCmdlet, SessionState or
/// reflection, so it is unit-testable in isolation and callable from any cmdlet or line-stream core.
/// <para>
/// One left-to-right pass over argv that classifies each element AS IT GOES, so the error rules
/// can never disagree with the flag rules:
/// </para>
/// <list type="bullet">
/// <item><c>--</c> ends option parsing; everything after it is an operand and is NEVER an option or
/// an error (<c>tee -- -zz</c> writes a file named <c>-zz</c>).</item>
/// <item>A lone <c>-</c> is an operand.</item>
/// <item>Short bundles <c>-abc</c>, <c>-abn5</c>: one token per letter; a value option ends the
/// bundle and takes the rest of it (or the next element) as its value.</item>
/// <item>Long options: exact name, <c>=value</c>, separate value, optional unique-prefix
/// abbreviation (ambiguous prefix = error).</item>
/// <item>Options may follow operands (GNU permutation): <c>cp a b -r</c>.</item>
/// <item>The first problem becomes <see cref="ParsedArgs.Error"/> and scanning stops.</item>
/// </list>
/// Migrating a cmdlet: declare a static readonly <see cref="OptSpecSet"/>, call
/// <see cref="Parse"/> on the argv (decoy-bound flags re-injected first), report
/// <see cref="ParsedArgs.Error"/> via <c>FileSystemHelpers.TryWriteParseError</c>, then read
/// <c>Has/Last/All/Operands</c>. See docs/specs/runtime-functions.md "Shared argument parser".
/// </summary>
public static class ArgParser
{
    public static ParsedArgs Parse(ReadOnlySpan<string> argv, OptSpecSet spec)
    {
        var tokens = new List<ArgToken>(argv.Length);
        bool afterDd = false;

        for (int i = 0; i < argv.Length; i++)
        {
            string arg = argv[i];

            if (afterDd)
            {
                tokens.Add(new ArgToken(ArgTokKind.Operand, i, null, null, arg, true));
                continue;
            }

            if (arg == "--")
            {
                tokens.Add(new ArgToken(ArgTokKind.DoubleDash, i, null, null, arg, false));
                afterDd = true;
                continue;
            }

            if (arg.Length < 2 || arg[0] != '-')
            {
                // "" , "-" (stdin/file named -), and plain words.
                tokens.Add(new ArgToken(ArgTokKind.Operand, i, null, null, arg, false));
                continue;
            }

            ArgError? error = arg[1] == '-'
                ? ParseLong(argv, ref i, arg, spec, tokens)
                : ParseShortBundle(argv, ref i, arg, spec, tokens);

            if (error is not null) return new ParsedArgs(tokens, error, spec.UsageExitCode);
        }

        return new ParsedArgs(tokens, null, spec.UsageExitCode);
    }

    private static ArgError? ParseLong(
        ReadOnlySpan<string> argv, ref int i, string arg, OptSpecSet spec, List<ArgToken> tokens)
    {
        int argIndex = i;
        int eq = arg.IndexOf('=');
        string name = eq >= 0 ? arg.Substring(2, eq - 2) : arg.Substring(2);
        string? attached = eq >= 0 ? arg.Substring(eq + 1) : null;

        if (!spec.TryGetLong(name, out var opt))
        {
            string full = "--" + name;
            if (spec.IsUnsupported(full))
                return new ArgError(ArgErrorKind.ValidButUnsupported, full, '\0', argIndex);

            if (!spec.AllowAbbrev || name.Length == 0)
                return new ArgError(ArgErrorKind.Unrecognized, arg, '\0', argIndex);

            var hits = spec.LongNamesWithPrefix(name);
            if (hits.Count == 0)
                return new ArgError(ArgErrorKind.Unrecognized, arg, '\0', argIndex);

            // getopt_long: several candidates are NOT ambiguous when they all name the same option
            // (grep --colo = color/colour, --fixed = fixed-regexp/fixed-strings).
            if (hits.Count > 1 && SameOption(spec, hits))
                hits.RemoveRange(1, hits.Count - 1);

            if (hits.Count > 1)
            {
                var sb = new StringBuilder();
                foreach (var h in hits) sb.Append(" '--").Append(h).Append('\'');
                // GNU (glibc getopt_long) echoes the WHOLE argument, `=VALUE` included: `--li=3`.
                return new ArgError(ArgErrorKind.Ambiguous, arg, '\0', argIndex, sb.ToString());
            }

            name = hits[0];
            if (!spec.TryGetLong(name, out opt))
                return new ArgError(ArgErrorKind.ValidButUnsupported, "--" + name, '\0', argIndex);
        }

        string canonical = "--" + name;
        switch (opt.Kind)
        {
            case OptKind.Flag:
                if (attached is not null)
                    return new ArgError(ArgErrorKind.UnexpectedValue, canonical, '\0', argIndex);
                tokens.Add(new ArgToken(ArgTokKind.Option, argIndex, opt.Id, null, arg, false));
                return null;

            case OptKind.OptionalValue:
                tokens.Add(new ArgToken(ArgTokKind.Option, argIndex, opt.Id, attached, arg, false));
                return null;

            default: // Value
                if (attached is not null)
                {
                    tokens.Add(new ArgToken(ArgTokKind.Option, argIndex, opt.Id, attached, arg, false));
                    return null;
                }
                if (i + 1 >= argv.Length)
                    return new ArgError(ArgErrorKind.MissingValue, canonical, '\0', argIndex);
                i++;
                tokens.Add(new ArgToken(ArgTokKind.Option, argIndex, opt.Id, argv[i], arg, false));
                return null;
        }
    }

    private static bool SameOption(OptSpecSet spec, List<string> names)
    {
        if (!spec.TryGetLong(names[0], out var first)) return false;
        for (int k = 1; k < names.Count; k++)
        {
            if (!spec.TryGetLong(names[k], out var o) || o.Id != first.Id || o.Kind != first.Kind) return false;
        }
        return true;
    }

    private static ArgError? ParseShortBundle(
        ReadOnlySpan<string> argv, ref int i, string arg, OptSpecSet spec, List<ArgToken> tokens)
    {
        int argIndex = i;

        // -NUM shorthand (head -5): a dash then ONLY digits.
        if (spec.NumericShorthandId is { } numId && AllDigits(arg, 1))
        {
            tokens.Add(new ArgToken(ArgTokKind.Option, argIndex, numId, arg.Substring(1), arg, false));
            return null;
        }

        for (int j = 1; j < arg.Length; j++)
        {
            char c = arg[j];

            if (spec.BundleDigitsId is { } digitsId && c is >= '0' and <= '9')
            {
                int end = j;
                while (end < arg.Length && arg[end] is >= '0' and <= '9') end++;
                tokens.Add(new ArgToken(ArgTokKind.Option, argIndex, digitsId, arg.Substring(j, end - j), arg, false));
                j = end - 1;
                continue;
            }

            if (!spec.TryGetShort(c, out var opt))
            {
                if (spec.DigitOptionWording is { } wording && c is >= '0' and <= '9')
                    return new ArgError(ArgErrorKind.MisplacedDigit, arg, c, argIndex, wording);

                string single = "-" + c;
                if (spec.IsUnsupported(single))
                    return new ArgError(ArgErrorKind.ValidButUnsupported, single, c, argIndex);
                return new ArgError(ArgErrorKind.Unrecognized, arg, c, argIndex);
            }

            if (opt.Kind == OptKind.Flag)
            {
                tokens.Add(new ArgToken(ArgTokKind.Option, argIndex, opt.Id, null, arg, false));
                continue;
            }

            string rest = arg.Substring(j + 1);

            if (opt.Kind == OptKind.OptionalValue)
            {
                tokens.Add(new ArgToken(ArgTokKind.Option, argIndex, opt.Id, rest.Length > 0 ? rest : null, arg, false));
                return null; // value (or its absence) ends the bundle
            }

            // Value option: rest of the bundle, else the next element.
            if (rest.Length > 0)
            {
                tokens.Add(new ArgToken(ArgTokKind.Option, argIndex, opt.Id, rest, arg, false));
                return null;
            }
            if (i + 1 >= argv.Length)
                return new ArgError(ArgErrorKind.MissingValue, arg, c, argIndex);
            i++;
            tokens.Add(new ArgToken(ArgTokKind.Option, argIndex, opt.Id, argv[i], arg, false));
            return null;
        }

        return null;
    }

    private static bool AllDigits(string s, int start)
    {
        if (s.Length <= start) return false;
        for (int k = start; k < s.Length; k++)
        {
            if (s[k] < '0' || s[k] > '9') return false;
        }
        return true;
    }
}
