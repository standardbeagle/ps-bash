namespace PsBash.Cmdlets.Args;

/// <summary>
/// A GNU long option that takes a value, for a cmdlet whose argv is still hand-scanned (awk, xargs) rather than
/// parsed by <see cref="ArgParser"/>. <paramref name="Long"/> includes the leading <c>--</c>. A
/// <paramref name="Required"/> value is <c>--name=V</c> or <c>--name V</c> (getopt_long); an optional one only follows
/// <c>=</c>, and a bare <c>--name</c> stands for <c>-X</c> + <paramref name="BareValue"/>.
/// </summary>
public readonly record struct LongOptionAlias(string Long, char Short, bool Required = true, string BareValue = "");

/// <summary>
/// The one place a hand-scanned cmdlet maps a long value option to its JOINED short form (<c>--file=f</c> →
/// <c>-ff</c>, <c>--max-procs 4</c> → <c>-P4</c>), so its existing short-option code handles both spellings. Without
/// it each scan matched only the separate-word spelling (<c>--file F</c>) or neither, and <c>--file=F</c> became an
/// operand (the awk program text). Migrating the command to <see cref="ArgParser"/> retires its table.
/// </summary>
public static class LongOptionAliases
{
    /// <summary>
    /// The joined short form of <c>args[i]</c> when it names one of <paramref name="table"/>, else null (also for a
    /// required value with no word left: the caller treats the option as given without a value). When the value is
    /// the NEXT word, <paramref name="i"/> is advanced onto it, so the caller's own step moves past both. Pure.
    /// </summary>
    public static string? Join(string[] args, ref int i, ReadOnlySpan<LongOptionAlias> table)
    {
        string arg = args[i];
        if (arg.Length < 3 || arg[0] != '-' || arg[1] != '-') return null;
        int eq = arg.IndexOf('=');
        ReadOnlySpan<char> name = eq < 0 ? arg : arg.AsSpan(0, eq);
        foreach (var opt in table)
        {
            if (!name.SequenceEqual(opt.Long)) continue;
            if (eq >= 0) return "-" + opt.Short + arg.Substring(eq + 1);
            if (!opt.Required) return "-" + opt.Short + opt.BareValue;
            if (i + 1 >= args.Length) return null;
            i++;
            return "-" + opt.Short + args[i];
        }
        return null;
    }
}
