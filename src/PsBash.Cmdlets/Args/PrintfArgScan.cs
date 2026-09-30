namespace PsBash.Cmdlets.Args;

/// <summary>
/// The option scan of bash's <c>printf</c> BUILTIN (bash's internal getopt, optstring <c>v:</c>):
/// <c>-v VAR</c> / <c>-vVAR</c> assigns instead of printing, <c>--</c> ends options, a lone <c>-</c>
/// is the format, and every other dash-led word is an error naming its FIRST option letter
/// (<c>-x</c>, <c>-n</c>, and <c>--</c> for <c>--help</c> / <c>---</c>). Scanning stops at the first
/// non-option word; the format and its arguments after that are verbatim.
/// </summary>
internal readonly record struct PrintfArgScan(
    string? VarName, int FirstOperand, char InvalidOption, bool MissingValue)
{
    public bool IsError => InvalidOption != '\0' || MissingValue;

    public static PrintfArgScan Scan(IReadOnlyList<string> args)
    {
        string? var = null;
        int i = 0;
        while (i < args.Count)
        {
            var a = args[i];
            if (a.Length < 2 || a[0] != '-') break;
            if (a == "--") { i++; break; }
            // Only the first letter after the dash matters: `v` takes the rest of the word (or
            // the next word) as NAME; anything else is an invalid option (`-xv` reports `x`).
            if (a[1] != 'v') return new PrintfArgScan(var, i, a[1], MissingValue: false);
            if (a.Length > 2) var = a.Substring(2);
            else if (i + 1 < args.Count) var = args[++i];
            else return new PrintfArgScan(var, i, '\0', MissingValue: true);            i++;
        }
        return new PrintfArgScan(var, i, '\0', false);
    }
}
