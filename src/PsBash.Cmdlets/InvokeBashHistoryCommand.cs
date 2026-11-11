using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// The <c>history</c> builtin as a script (non-interactive shell) sees it: bash keeps no history there, so the list
/// holds only what <c>history -s</c> / <c>history -r</c> put in it. The interactive line editor handles a typed
/// <c>history</c> itself (<c>Host/Shell/HistoryBuiltin.cs</c>, the persistent store) before anything is transpiled.
/// <code>
/// history [n]                 list (the last n entries), "%5d  %s"
/// history -c | -d offset      clear | delete one entry (negative offset counts from the end)
/// history -s arg...           append the args as one entry
/// history -p arg...           print each arg (no history expansion in a script)
/// history -a|-n|-r|-w [file]  append / (nothing new) / read / write the list to file or $HISTFILE
/// </code>
/// Without this, PowerShell's <c>history</c> alias (<c>Get-History</c>) ran and leaked its binder errors.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashHistory")]
public sealed class InvokeBashHistoryCommand : PSCmdlet
{
    // Decoys for DIRECT calls only (the transpiler single-quotes dash words): bare -c / -d / -p / -w would bind
    // common parameters. Put back in front of the arguments.
    [Parameter] public SwitchParameter c { get; set; }
    [Parameter] public SwitchParameter d { get; set; }
    [Parameter] public SwitchParameter p { get; set; }
    [Parameter] public SwitchParameter w { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    internal const string StateVariable = "__BashHistoryList";
    internal const string Usage =
        "history: usage: history [-c] [-d offset] [n] or history -anrw [filename] or history -ps arg [arg...]";

    /// <summary>One run's result: lines for stdout, lines for stderr, the exit status.</summary>
    internal sealed record Result(List<string> Out, List<string> Err, int Status);

    /// <summary>Pure: applies <paramref name="args"/> to <paramref name="list"/> (mutated in place). File options go
    /// through <paramref name="readFile"/> / <paramref name="writeFile"/> (append flag) so tests need no disk.</summary>
    internal static Result Run(
        IReadOnlyList<string> args, List<string> list, string? histFile,
        Func<string, string[]?> readFile, Action<string, IEnumerable<string>, bool> writeFile)
    {
        var o = new List<string>(); var e = new List<string>();
        Result Done(int s) => new(o, e, s);
        Result Fail(int s, string msg) { e.Add("history: " + msg); return Done(s); }

        char? fileOp = null; bool clear = false, print = false, store = false; string? delete = null;
        int i = 0;
        for (; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--") { i++; break; }
            if (a.Length < 2 || a[0] != '-') break;
            for (int k = 1; k < a.Length; k++)
            {
                char ch = a[k];
                switch (ch)
                {
                    case 'c': clear = true; break;
                    case 'p': print = true; break;
                    case 's': store = true; break;
                    case 'a' or 'n' or 'r' or 'w': fileOp = ch; break;
                    case 'd':
                        var rest = a[(k + 1)..];
                        if (rest.Length == 0)
                        {
                            if (i + 1 >= args.Count) { e.Add("history: -d: option requires an argument"); e.Add(Usage); return Done(2); }
                            rest = args[++i];
                        }
                        delete = rest; k = a.Length; break;
                    default:
                        e.Add($"history: -{(char.IsAsciiDigit(ch) ? a[1..] : ch.ToString())}: invalid option");
                        e.Add(Usage);
                        return Done(2);
                }
            }
        }
        var operands = args.Skip(i).ToList();

        if (clear) { list.Clear(); if (delete is null && fileOp is null && !print && !store) return Done(0); }
        if (delete is not null)
        {
            if (!long.TryParse(delete, out var off)) return Fail(1, $"{delete}: history position out of range");
            long idx = off < 0 ? list.Count + off : off - 1;
            if (idx < 0 || idx >= list.Count) return Fail(1, $"{delete}: history position out of range");
            list.RemoveAt((int)idx);
            return Done(0);
        }
        if (print) { o.AddRange(operands); return Done(0); }
        if (store) { if (operands.Count > 0) list.Add(string.Join(' ', operands)); return Done(0); }
        if (fileOp is char op)
        {
            var file = operands.Count > 0 ? operands[0] : histFile;
            if (string.IsNullOrEmpty(file)) return Done(0);
            switch (op)
            {
                case 'w': writeFile(file, list, false); return Done(0);
                case 'a': writeFile(file, list, true); return Done(0);
                case 'n': return Done(0);
                default:
                    var lines = readFile(file);
                    if (lines is null) return Fail(1, $"{file}: cannot read history file");
                    list.AddRange(lines.Where(l => l.Length > 0));
                    return Done(0);
            }
        }

        int count = list.Count;
        if (operands.Count > 0)
        {
            if (!int.TryParse(operands[0], System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0)
                return Fail(1, $"{operands[0]}: numeric argument required");
            if (operands.Count > 1) return Fail(1, "too many arguments");
            count = Math.Min(n, list.Count);
        }
        for (int j = list.Count - count; j < list.Count; j++) o.Add($"{j + 1,5}  {list[j]}");
        return Done(0);
    }

    protected override void EndProcessing()
    {
        var args = new List<string>();
        if (c) args.Add("-c");
        if (d) args.Add("-d");
        if (p) args.Add("-p");
        if (w) args.Add("-w");
        args.AddRange(Arguments ?? Array.Empty<string>());
        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "history", args.ToArray())) return;

        // Global: `history -s` inside a function must outlive the function's scope.
        var list = SessionState.PSVariable.GetValue("global:" + StateVariable) as List<string>;
        if (list is null) { list = new List<string>(); SessionState.PSVariable.Set("global:" + StateVariable, list); }
        var histFile = BashVariableStore.Get("HISTFILE");

        string Resolve(string f) =>
            FileSystemHelpers.ResolveOperands(this, f, expandWildcards: false).Select(op => op.Path).FirstOrDefault() ?? f;
        var r = Run(args, list, histFile,
            f =>
            {
                var path = Resolve(f);
                return File.Exists(path) ? BashFileSystem.ReadLines(path).ToArray() : null;
            },
            (f, lines, append) =>
            {
                var path = Resolve(f);
                using var sw = new StreamWriter(path, append, PsBash.Core.RawBytes.Encoding);
                foreach (var l in lines) sw.Write(l + "\n");
            });

        foreach (var line in r.Out) WriteObject(line);
        foreach (var line in r.Err) FileSystemHelpers.WriteStderr(this, line);
        FileSystemHelpers.SetLastExitCode(this, r.Status);
    }
}
