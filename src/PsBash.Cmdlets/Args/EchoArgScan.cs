namespace PsBash.Cmdlets.Args;

/// <summary>
/// The option scanner of bash's <c>echo</c> BUILTIN (not GNU getopt): a leading run of words that
/// each match <c>-[neE]+</c> are options; the first word that does not (<c>-x</c>, <c>--</c>,
/// <c>-n-</c>, <c>--help</c>, <c>-</c>, <c>-e-n</c>) ends option parsing and is printed literally
/// together with everything after it. There is no <c>--</c> handling, no abbreviation and no error.
/// Within the run <c>-e</c> / <c>-E</c> are last-wins; <c>-n</c> is sticky. xpg_echo is off.
/// </summary>
internal readonly record struct EchoArgScan(bool NoNewline, bool Escapes, int FirstOperand)
{
    public static EchoArgScan Scan(IReadOnlyList<string> args)
    {
        bool n = false, e = false;
        int i = 0;
        for (; i < args.Count; i++)
        {
            var a = args[i];
            if (a.Length < 2 || a[0] != '-') break;
            bool ok = true;
            for (int k = 1; k < a.Length; k++)
            {
                if (a[k] is not ('n' or 'e' or 'E')) { ok = false; break; }
            }
            if (!ok) break;
            for (int k = 1; k < a.Length; k++)
            {
                switch (a[k])
                {
                    case 'n': n = true; break;
                    case 'e': e = true; break;
                    case 'E': e = false; break;
                }
            }
        }
        return new EchoArgScan(n, e, i);
    }
}
