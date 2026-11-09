using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The bash <c>getopts optstring name [arg ...]</c> builtin: parse one option per call.
///
/// State between calls lives where bash keeps it: <c>OPTIND</c> (the next argument to look at, 1-based)
/// is an ordinary shell variable the script may reset or read (<c>shift $((OPTIND-1))</c>), and the
/// position inside a clustered argument (<c>-abc</c>) is a hidden companion <c>__PSBASH_OPTPOS</c> that is
/// only trusted while it was recorded for the current <c>OPTIND</c> — assigning <c>OPTIND=1</c> restarts.
///
/// Sets <c>name</c> to the option letter (<c>?</c> for an unknown one, <c>:</c> for a missing argument in
/// silent mode), <c>OPTARG</c> to its argument, and returns 0 while an option was found, 1 at the end.
/// A leading <c>:</c> in <c>optstring</c> selects silent mode (no diagnostics, the letter goes to OPTARG).
/// Without explicit arguments it walks the positional parameters.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashGetopts")]
public sealed class InvokeBashGetoptsCommand : PSCmdlet
{
    private const string PosVar = "__PSBASH_OPTPOS";

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void EndProcessing()
    {
        var a = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (a.Length < 2)
        {
            FileSystemHelpers.WriteStderr(this, "getopts: usage: getopts optstring name [arg ...]");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        string optstring = a[0];
        string name = a[1];
        bool silent = optstring.StartsWith(':');
        string spec = silent ? optstring.Substring(1) : optstring;
        string[] args = a.Length > 2 ? a[2..] : PositionalParameters();

        int optind = int.TryParse(BashVariableStore.Get("OPTIND"), out var oi) && oi >= 1 ? oi : 1;
        // An OPTIND past the end of the (new) argument list is pulled back to just past it (bash).
        if (optind > args.Length + 1) optind = args.Length + 1;
        int charPos = ReadCharPos(optind);

        var result = Next(spec, silent, args, ref optind, ref charPos);

        Assign("OPTIND", optind.ToString());
        BashVariableStore.Set(PosVar, charPos == 0 ? null : optind + ":" + charPos);
        bool validName = System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$");
        if (validName) Assign(name, result.Name);
        else FileSystemHelpers.WriteStderr(this, $"getopts: `{name}': not a valid identifier");
        if (result.OptArg is null) Unassign("OPTARG");
        else Assign("OPTARG", result.OptArg);
        if (result.Error is not null) FileSystemHelpers.WriteStderr(this, result.Error);
        FileSystemHelpers.SetLastExitCode(this, result.Found && validName ? 0 : 1);
    }

    /// <summary>One step of the scan; pure, so the option grammar is testable without a runspace.</summary>
    internal static GetoptsResult Next(string spec, bool silent, string[] args, ref int optind, ref int charPos)
    {
        int idx = optind - 1;
        if (idx >= args.Length) { charPos = 0; return new GetoptsResult(false, "?", null, null); }

        string arg = args[idx];
        if (charPos == 0)
        {
            if (arg == "--") { optind++; return new GetoptsResult(false, "?", null, null); }
            if (arg.Length < 2 || arg[0] != '-') return new GetoptsResult(false, "?", null, null);
            charPos = 1;
        }

        char c = arg[charPos++];
        int at = c == ':' ? -1 : spec.IndexOf(c);
        bool last = charPos >= arg.Length;

        if (at < 0)
        {
            if (last) { optind++; charPos = 0; }
            return silent
                ? new GetoptsResult(true, "?", c.ToString(), null)
                : new GetoptsResult(true, "?", null, $"illegal option -- {c}");
        }

        bool takesArg = at + 1 < spec.Length && spec[at + 1] == ':';
        if (!takesArg)
        {
            if (last) { optind++; charPos = 0; }
            return new GetoptsResult(true, c.ToString(), null, null);
        }

        if (!last)
        {
            string rest = arg.Substring(charPos);
            optind++; charPos = 0;
            return new GetoptsResult(true, c.ToString(), rest, null);
        }
        if (idx + 1 < args.Length)
        {
            string next = args[idx + 1];
            optind += 2; charPos = 0;
            return new GetoptsResult(true, c.ToString(), next, null);
        }

        optind++; charPos = 0;
        return silent
            ? new GetoptsResult(true, ":", c.ToString(), null)
            : new GetoptsResult(true, "?", null, $"option requires an argument -- {c}");
    }

    internal readonly record struct GetoptsResult(bool Found, string Name, string? OptArg, string? Error);

    /// <summary>The hidden in-cluster position, valid only for the OPTIND it was stored with.</summary>
    private static int ReadCharPos(int optind)
    {
        var s = BashVariableStore.Get(PosVar);
        if (s is null) return 0;
        int colon = s.IndexOf(':');
        return colon > 0 && int.TryParse(s.AsSpan(0, colon), out var o) && o == optind
               && int.TryParse(s.AsSpan(colon + 1), out var p) ? p : 0;
    }

    private string[] PositionalParameters()
    {
        var v = SessionState.PSVariable.GetValue("global:BashPositional");
        if (v is null || v is string) return Array.Empty<string>();
        var list = new List<string>();
        if (v is System.Collections.IEnumerable e)
            foreach (var item in e) list.Add(item?.ToString() ?? "");
        return list.ToArray();
    }

    /// <summary>Visible to both <c>$name</c> and <c>$env:name</c> reads, like <c>read</c>.</summary>
    private void Assign(string name, string value)
    {
        SessionState.PSVariable.Set(name, value);
        try { BashVariableStore.Set(name, value); } catch { /* restricted name: non-fatal */ }
    }

    private void Unassign(string name)
    {
        SessionState.PSVariable.Remove(name);
        try { BashVariableStore.Set(name, null); } catch { /* restricted name: non-fatal */ }
    }
}
