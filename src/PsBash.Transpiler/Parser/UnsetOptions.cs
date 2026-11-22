namespace PsBash.Core.Parser;

/// <summary>
/// bash's <c>unset</c> options, read ONE way by the emitter (which rewrites <c>unset ref</c> to the nameref's target when
/// it knows it) and by the cmdlet (<c>InvokeBashUnsetCommand</c>). bash 5.2 reads them with <c>internal_getopt "fnv"</c>:
/// the LEADING words only, letters may cluster (<c>-vn</c>, <c>-nf</c>), <c>--</c> ends them, and so does the first
/// operand (<c>unset x -f g</c> unsets the variables x and g; <c>-f</c> there is a name). No order matters among them:
/// <c>-f</c> with <c>-v</c> is an error whichever comes last (<see cref="ConflictMessage"/>, status 1, nothing unset), and
/// <c>-f</c> overrides <c>-n</c> (<c>unset -nf r</c> unsets the function r and keeps the reference r).
/// REGRESSION: both sides matched only the words <c>-f</c>/<c>-v</c>/<c>-n</c>, so <c>unset -vn r</c> unset r's target and
/// left r a nameref. Guard: <c>Core.Tests/Parser/UnsetOptionsTests</c> (every cluster permutation).
/// </summary>
public readonly record struct UnsetOptions(bool Functions, bool Variables, bool Reference, int FirstOperand, bool Known)
{
    /// <summary>The option letters <c>unset</c> has (<c>FileSystemHelpers.TryRejectBuiltinOptions</c> rejects any other).</summary>
    public const string Letters = "fnv";

    /// <summary>bash's message for <c>-f</c> with <c>-v</c> (after the shell's diagnostic prefix).</summary>
    public const string ConflictMessage = "unset: cannot simultaneously unset a function and a variable";

    /// <summary><c>-f</c> and <c>-v</c> together: nothing is unset, status 1.</summary>
    public bool Conflict => Functions && Variables;

    /// <summary><c>-n</c> without <c>-f</c>: each operand names a nameref, which goes; the variable it names stays.</summary>
    public bool ByReference => Reference && !Functions;

    /// <summary>
    /// The options among <paramref name="args"/> from <paramref name="start"/> on (pure). <see cref="FirstOperand"/> is the
    /// index of the first name. A word known only at run time (null, the emitter's view) stops the scan with
    /// <see cref="Known"/> false: which words are options is then the runtime's to say. A word with a letter
    /// <c>unset</c> does not have (or a <c>--NAME</c>) also stops it unknown: it is the usage error, status 2.
    /// </summary>
    public static UnsetOptions Parse(IReadOnlyList<string?> args, int start = 0)
    {
        bool functions = false, variables = false, reference = false;
        int i = start;
        for (; i < args.Count; i++)
        {
            string? a = args[i];
            if (a is null) return new(functions, variables, reference, i, Known: false);
            if (a == "--") { i++; break; }
            if (a.Length < 2 || a[0] != '-') break;
            if (a[1] == '-') return new(functions, variables, reference, i, Known: false);
            for (int k = 1; k < a.Length; k++)
            {
                switch (a[k])
                {
                    case 'f': functions = true; break;
                    case 'v': variables = true; break;
                    case 'n': reference = true; break;
                    default: return new(functions, variables, reference, i, Known: false);
                }
            }
        }
        return new(functions, variables, reference, i, Known: true);
    }
}
