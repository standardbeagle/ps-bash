using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// An array element a cmdlet names at RUN time, from an operand or a value rather than the script text:
/// <c>printf -v 'm[$k]'</c>, <c>unset 'm[$k]'</c>, <c>x='a[i+1]'; $(( x = 1 ))</c>. The one place that turns such a
/// subscript into an associative key or an indexed position, and writes the element.
/// <para>
/// bash's rules (oracle in <c>BashArrayElementTests</c> and the <c>printf -v and assoc array</c> spec case):
/// a command substitution in the subscript runs (<see cref="BashSubscriptExpansion"/>). An associative key drops its
/// quotes (<c>m['q']</c>, <c>m["K"]</c>), and <c>$k</c> / <c>${k}</c> expand outside single quotes. An indexed subscript
/// is an arithmetic expression (<c>a[$i]</c>, <c>a[i+1]</c>); a negative one counts from the end. Writing past the end
/// leaves a gap, and the array becomes sparse.
/// </para>
/// <para>
/// The bug class: each cmdlet re-derived these rules. <c>printf -v</c> never expanded <c>$k</c>, so
/// <c>printf -v 'm[$k]'</c> added a key named <c>$k</c>, and an indexed <c>a[$i]</c> overwrote the whole array as a
/// scalar. Guard: <c>Core.Tests/ArrayElementGuardTests</c> (no other cmdlet expands a subscript's variables itself).
/// </para>
/// </summary>
public static class BashArrayElement
{
    /// <summary>How the subscript reads and writes variables (an arithmetic subscript may assign: <c>a[i++]</c>).</summary>
    public readonly record struct Vars(Func<string, long> Read, Action<string, long> Write, Func<string, string?> ReadRaw);

    /// <summary>
    /// Variables seen through <paramref name="state"/>, with no side effects allowed. A value that is not a plain number
    /// is itself an expression, as in bash (<c>i='0+1'; unset 'a[i]'</c> is element 1; <c>i=1/0</c> fails the subscript).
    /// </summary>
    public static Vars ReadOnly(SessionState state)
    {
        string? Raw(string name) => BashVariableLookup.Scalar(state, name);
        long Number(string name, int depth)
        {
            string? value = Raw(name)?.Trim();
            if (string.IsNullOrEmpty(value)) return 0;
            // A leading 0 is octal (010 is 8, 08 an error), so only a plain decimal is read directly.
            if (value[0] != '0' && long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var direct))
                return direct;
            if (depth >= MaxRecursionDepth)
                throw new BashArith.BashArithException("expression recursion level exceeded") { Expression = name, Token = name };
            return BashArith.Evaluate(value, n => Number(n, depth + 1), (_, _) => { }, Raw);
        }
        return new Vars(n => Number(n, 0), (_, _) => { }, Raw);
    }

    /// <summary>bash's EXPR_NEST_MAX: how deep a value naming another value is followed.</summary>
    private const int MaxRecursionDepth = 1024;

    /// <summary>
    /// The subscript after a command substitution in it has run, and whether that happened (the result is then used as
    /// is, never expanded again). Null: the text is not a valid word, bash's "bad substitution".
    /// </summary>
    public static string? ExpandSubscript(CommandInvocationIntrinsics invoke, string subscript, out bool expanded)
    {
        expanded = BashSubscriptExpansion.NeedsWordExpansion(subscript);
        return expanded ? BashSubscriptExpansion.Expand(invoke, subscript) : subscript;
    }

    /// <summary>
    /// An associative array's key: a single-quoted key is literal; otherwise outer double quotes go and <c>$k</c> /
    /// <c>${k}</c> are replaced by the variable's value.
    /// </summary>
    public static string AssocKey(string subscript, bool expanded, Func<string, string?> readRaw)
    {
        if (expanded) return subscript;
        string key = subscript.Trim();
        if (key.Length >= 2 && key[0] == '\'' && key[^1] == '\'' && key.IndexOf('\'', 1) == key.Length - 1) return key[1..^1];
        if (key.Length >= 2 && key[0] == '"' && key[^1] == '"' && key.IndexOf('"', 1) == key.Length - 1) key = key[1..^1];
        if (key.IndexOf('$') < 0) return key;
        return System.Text.RegularExpressions.Regex.Replace(key, @"\$\{?(\w+)\}?", m => readRaw(m.Groups[1].Value) ?? "");
    }

    /// <summary>
    /// An indexed array's position: the subscript as an arithmetic expression, a negative one counted back from
    /// <paramref name="count"/>. Throws <see cref="BashArith.BashArithException"/> when it is not an expression.
    /// </summary>
    public static long Index(string subscript, long count, Vars vars)
    {
        long index = BashArith.Evaluate(subscript, vars.Read, vars.Write, vars.ReadRaw);
        return index < 0 ? index + count : index;
    }

    /// <summary>
    /// Set <c>baseName[subscript]</c> to <paramref name="value"/>: a key of an associative array, else a position of
    /// the indexed array (a scalar is its element 0; no variable is a new array). False when a negative subscript
    /// reaches before the start, or an associative key is empty: bash's "bad array subscript", nothing set.
    /// </summary>
    public static bool Write(SessionState state, string baseName, string subscript, bool expanded, string value, Vars vars)
    {
        object? holder = BashVariableLookup.ArrayHolder(state, baseName);
        if (holder is BashSparseArray sparse)
        {
            long at = Index(subscript, sparse.Count == 0 ? 0 : sparse.NextIndex, vars);
            if (at < 0) return false;
            sparse[at] = value;
            return true;
        }
        if (holder is System.Collections.IDictionary dict)
        {
            string key = AssocKey(subscript, expanded, vars.ReadRaw);
            if (key.Length == 0) return false;   // bash: an empty key is a bad subscript
            dict[key] = value;
            return true;
        }
        var items = new List<object?>();
        if (holder is System.Collections.IEnumerable seq && holder is not string)
            foreach (var item in seq) items.Add(item);
        bool wasScalar = false;
        if (holder is null && BashVariableLookup.Scalar(state, baseName) is { } scalar) { items.Add(scalar); wasScalar = true; }
        long index = Index(subscript, items.Count, vars);
        if (index < 0) return false;
        object array;
        if (index > items.Count)
        {
            // A gap: a dense array can't hold it (bash keeps the holes unset), so it becomes a sparse one.
            var sp = BashSparseArray.FromList(items);
            sp[index] = value;
            array = sp;
        }
        else
        {
            if (index == items.Count) items.Add(value); else items[(int)index] = value;
            array = items.ToArray();
        }
        if (holder is not null)
        {
            BashVariableLookup.ReplaceArray(state, baseName, array);
            return true;
        }
        // A new array (or a scalar turned into one) is bash's global, and a scalar lives only in the store, an array
        // only as a PowerShell variable (environment-model.md section 8).
        BashVariableLookup.SetArray(state, baseName, array);
        if (wasScalar) BashVariableStore.Set(baseName, null);
        return true;
    }

    /// <summary>
    /// The value of <c>baseName[subscript]</c>, by the rules <see cref="Write"/> writes it (a scalar is its element 0);
    /// null when unset or when a negative subscript reaches before the start.
    /// </summary>
    public static string? Read(SessionState state, string baseName, string subscript, Vars vars)
    {
        object? holder = BashVariableLookup.ArrayHolder(state, baseName);
        if (holder is BashSparseArray sparse)
        {
            long at = Index(subscript, sparse.Count == 0 ? 0 : sparse.NextIndex, vars);
            return at >= 0 && sparse.Contains(at) ? sparse[at]?.ToString() ?? "" : null;
        }
        if (holder is System.Collections.IDictionary dict)
        {
            string key = AssocKey(subscript, false, vars.ReadRaw);
            return dict.Contains(key) ? dict[key]?.ToString() ?? "" : null;
        }
        if (holder is System.Collections.IList list && holder is not string)
        {
            long index = Index(subscript, list.Count, vars);
            return index >= 0 && index < list.Count ? list[(int)index]?.ToString() ?? "" : null;
        }
        return Index(subscript, 1, vars) == 0 ? BashVariableLookup.Scalar(state, baseName) : null;
    }

    // NAME or NAME[subscript]: a run-time assignment target (`read NAME`, `printf -v NAME`).
    private static readonly System.Text.RegularExpressions.Regex s_target =
        new(@"^[A-Za-z_][A-Za-z0-9_]*(\[[^\]]+\])?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>True for <c>NAME</c> or <c>NAME[subscript]</c>, what <c>read</c> and <c>printf -v</c> accept as a target.</summary>
    public static bool IsTarget(string s) => s_target.IsMatch(s);

    /// <summary>
    /// Set a run-time target (<see cref="IsTarget"/>) to <paramref name="value"/>: a scalar (element 0 of the array the
    /// name holds), or one element by <see cref="Write"/>. False when nothing was set: a negative or empty subscript is
    /// bash's "bad array subscript" (status 1), and one that is not an expression is <see cref="FailSubscript"/>.
    /// </summary>
    public static bool Assign(PSCmdlet cmdlet, string target, string value, string builtin)
    {
        // A nameref writes its target; one with no target yet takes the value as its target (BashShellState.WriteTarget).
        // REGRESSION: `declare -n r=x; read r` set r, and `declare -n r; printf -v r %s hi` stored hi in r.
        if (target.IndexOf('[') < 0)
        {
            if (BashShellState.WriteTarget(target, value, out bool invalid) is not { } resolved)
            {
                if (!invalid) return true;
                FileSystemHelpers.WriteBashError(cmdlet, ShellDiagnostic.Prefix(cmdlet.SessionState) + $"{builtin}: `{value}': not a valid identifier");
                return false;
            }
            target = resolved;
        }
        int br = target.IndexOf('[');
        if (br < 0)
        {
            try { BashVariableLookup.SetScalar(cmdlet.SessionState, target, value); } catch { /* a restricted name: not fatal */ }
            return true;
        }
        string name = target[..br], subscript = target[(br + 1)..^1];
        string? sub = ExpandSubscript(cmdlet.InvokeCommand, subscript, out bool expanded);
        bool written;
        try { written = sub is not null && Write(cmdlet.SessionState, name, sub, expanded, value, ReadOnly(cmdlet.SessionState)); }
        catch (BashArith.BashArithException ex) { FailSubscript(cmdlet, subscript, ex); return false; }
        if (!written)
        {
            FileSystemHelpers.WriteBashError(cmdlet, ShellDiagnostic.Prefix(cmdlet.SessionState) + target + ": bad array subscript");
        }
        return written;
    }

    /// <summary>
    /// A subscript a builtin was handed that is not a valid arithmetic expression (<c>unset 'a[1/0]'</c>,
    /// <c>read 'a[1/0]'</c>, <c>printf -v 'a[1/0]'</c>, <c>test -v 'a[1/0]'</c>). bash reports it as it does
    /// <c>${a[1/0]}</c> and treats it as that expansion error, not a failed command: a script drops the rest of the
    /// input line and <c>bash -c</c> ends the shell (<see cref="FileSystemHelpers.MarkExpansionFailed(PSCmdlet, bool)"/>).
    /// Returns only when neither applies (no line scope armed), with status 1.
    /// <para>The bug class: each builtin caught the error as a plain failure (status 1, the line went on). Guard:
    /// <c>Cmdlets.Tests/SubscriptErrorGuardTests</c> (a builtin's subscript arithmetic error reaches this one path).</para>
    /// </summary>
    public static void FailSubscript(PSCmdlet cmdlet, string subscript, BashArith.BashArithException ex)
    {
        FileSystemHelpers.WriteBashError(cmdlet, ShellDiagnostic.Prefix(cmdlet.SessionState) + ex.Describe(subscript));
        FileSystemHelpers.MarkExpansionFailed(cmdlet, inSubscript: true);
    }
}
