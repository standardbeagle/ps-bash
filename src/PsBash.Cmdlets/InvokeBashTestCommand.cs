using System.Globalization;
using System.IO;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet for the bash <c>test</c> / <c>[</c> builtin. <b>There are no options</b>: every
/// word is part of the expression, dash-leading or not, so the expression is evaluated by
/// <see cref="BashTestExpr"/> (a port of bash's argument-count driven <c>test.c</c>), never by the
/// getopt-style scanner the other migrated commands share. <c>test</c> is on
/// <c>PsEmitter.OrderedArgCommands</c>, so the transpiler single-quotes every dash word and the
/// expression reaches <see cref="Arguments"/> verbatim and in order.
///
/// <para>Exit status: 0 = true, 1 = false, 2 = syntax error (<c>test: x: integer expression
/// expected</c>, <c>too many arguments</c>, <c>argument expected</c>, ...). Nothing is written to
/// stdout. There is no <c>--help</c> / <c>--version</c>: <c>test --help</c> is the one-word
/// expression "--help" (true).</para>
///
/// <para><b>Direct PowerShell calls:</b> the single-letter operators <c>-e -d -w -a -o</c>
/// prefix-collide with common parameters / <c>-Arguments</c>, so they are declared as decoy
/// <see cref="SwitchParameter"/>s and re-injected at the head of the expression. Decoys lose their
/// position (a transpiled <c>test</c> never binds them), so quote them
/// (<c>Invoke-BashTest '-e' $p '-a' ...</c>) when order matters.</para>
///
/// <para><b>Bracket form:</b> invoked as <c>[</c> (alias) the final word must be <c>]</c> and is
/// removed; its absence is <c>[: missing `]'</c>, exit 2. As <c>test</c> a trailing <c>]</c> is an
/// ordinary word.</para>
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashTest")]
[OutputType(typeof(bool))]
public sealed class InvokeBashTestCommand : PSCmdlet
{
    // Decoy switches: catch the bare bash operators before the binder maps
    // them to a common parameter. The IsPresent flag is what we re-inject.

    /// <summary>Catches bare <c>-e</c> (file-exists). Prefix-collides with
    /// <c>-ErrorAction</c> / <c>-ErrorVariable</c>.</summary>
    [Parameter]
    public SwitchParameter E { get; set; }

    /// <summary>Catches bare <c>-d</c> (is-directory). Prefix-collides with
    /// <c>-Debug</c>.</summary>
    [Parameter]
    public SwitchParameter D { get; set; }

    /// <summary>Catches bare <c>-w</c> (is-writable). Prefix-collides with
    /// <c>-WarningAction</c> / <c>-WarningVariable</c>.</summary>
    [Parameter]
    public SwitchParameter W { get; set; }

    /// <summary>Catches bare <c>-a</c> (logical AND between predicates).
    /// Prefix-matches the cmdlet's own <c>-Arguments</c> parameter.</summary>
    [Parameter]
    public SwitchParameter A { get; set; }

    /// <summary>Catches bare <c>-o</c> (logical OR between predicates).
    /// Prefix-collides with <c>-OutVariable</c> / <c>-OutBuffer</c>.</summary>
    [Parameter]
    public SwitchParameter O { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    protected override void ProcessRecord()
    {
        var raw = Arguments ?? Array.Empty<string>();
        FileSystemHelpers.SetLastExitCode(this, 0);

        // Re-inject decoy switches the binder consumed (direct PowerShell calls only).
        var operands = RebuildWithDecoys(raw);

        string name = IsBracketInvocation() ? "[" : "test";
        if (name == "[")
        {
            if (operands.Count == 0 || operands[^1] != "]")
            {
                FileSystemHelpers.WriteBashError(this, "bash: [: missing `]'");
                FileSystemHelpers.SetLastExitCode(this, 2);
                return;
            }
            operands.RemoveAt(operands.Count - 1);
        }

        bool result;
        try
        {
            result = BashTestExpr.Eval(operands.ToArray(), Unary, FileBinary);
        }
        catch (TestSyntaxException ex)
        {
            FileSystemHelpers.WriteBashError(this, $"bash: {name}: {ex.Message}");
            FileSystemHelpers.SetLastExitCode(this, 2);
            return;
        }

        // bash `test` / `[` is SILENT — the result is the EXIT CODE only, never stdout.
        FileSystemHelpers.SetLastExitCode(this, result ? 0 : 1);
    }

    /// <summary>
    /// A decoy switch swallowed its token, so the bound parameters no longer say WHERE it stood;
    /// <c>test</c>'s grammar is positional (<c>-z '' -a -n x</c>), so re-read the command's own
    /// AST from the invocation line and put each decoy back in place. Falls back to the head of
    /// the expression when the line cannot be mapped one-to-one onto <paramref name="raw"/>.
    /// </summary>
    private List<string> RebuildWithDecoys(string[] raw)
    {
        var decoys = new List<string>(5);
        if (E.IsPresent) decoys.Add("-e");
        if (D.IsPresent) decoys.Add("-d");
        if (W.IsPresent) decoys.Add("-w");
        if (A.IsPresent) decoys.Add("-a");
        if (O.IsPresent) decoys.Add("-o");

        var result = new List<string>(raw.Length + decoys.Count);
        if (decoys.Count == 0) { result.AddRange(raw); return result; }

        try
        {
            var line = MyInvocation?.Line ?? string.Empty;
            var ast = System.Management.Automation.Language.Parser.ParseInput(line, out _, out _);
            int off = MyInvocation?.OffsetInLine ?? 0;
            var cmd = ast.Find(n => n is System.Management.Automation.Language.CommandAst c
                                    && c.Extent.StartColumnNumber - 1 == off, true) as System.Management.Automation.Language.CommandAst
                      ?? ast.Find(n => n is System.Management.Automation.Language.CommandAst, true) as System.Management.Automation.Language.CommandAst;
            if (cmd is not null)
            {
                int next = 0, placed = 0;
                var rebuilt = new List<string>(raw.Length + decoys.Count);
                for (int i = 1; i < cmd.CommandElements.Count; i++)
                {
                    var el = cmd.CommandElements[i];
                    if (el is System.Management.Automation.Language.CommandParameterAst p && p.Argument is null
                        && p.ParameterName.Length == 1 && "edwao".IndexOf(char.ToLowerInvariant(p.ParameterName[0])) >= 0
                        && decoys.Contains("-" + char.ToLowerInvariant(p.ParameterName[0])))
                    {
                        rebuilt.Add("-" + char.ToLowerInvariant(p.ParameterName[0]));
                        placed++;
                    }
                    else if (next < raw.Length) rebuilt.Add(raw[next++]);
                    else { rebuilt = null!; break; }
                }
                if (rebuilt is not null && next == raw.Length && placed == decoys.Count) return rebuilt;
            }
        }
        catch { /* fall through to the head-injection fallback */ }

        result.AddRange(decoys);
        result.AddRange(raw);
        return result;
    }

    /// <summary>
    /// True when invoked as <c>[</c>. InvocationName is the alias as typed, but the call operator
    /// (<c>&amp; '[' ...</c>) can surface the resolved cmdlet name instead, so the text at the
    /// command's own offset in the invocation line is the second signal.
    /// </summary>
    private bool IsBracketInvocation()
    {
        if (MyInvocation?.InvocationName == "[") return true;
        var line = MyInvocation?.Line ?? string.Empty;
        int off = Math.Clamp(MyInvocation?.OffsetInLine ?? 0, 0, line.Length);
        var rest = line.AsSpan(off).TrimStart();
        if (rest.StartsWith("&")) rest = rest.Slice(1).TrimStart();
        if (rest.Length > 0 && (rest[0] == '\'' || rest[0] == '"')) rest = rest.Slice(1);
        return rest.Length > 0 && rest[0] == '[' && (rest.Length == 1 || rest[1] is ' ' or '\'' or '"' or '\t');
    }

    // ---- predicates (everything BashTestExpr does not decide itself) ------------------------

    private string Resolve(string raw)
    {
        try { return FileSystemHelpers.ProviderPath(this, FileSystemHelpers.NormalizeOperandPath(raw)); }
        catch { return raw; }
    }

    private static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo fi = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return fi.Exists && (fi.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch { return false; }
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    private bool Unary(string op, string operand)
    {
        char c = op[1];
        if (c == 'v') return BashVariableStore.Get(operand) is not null;
        if (c == 'o' || c == 'R') return false; // shell options / namerefs are not modelled
        if (c == 't')
        {
            return int.TryParse(operand, out int fd) && fd switch
            {
                0 => !Console.IsInputRedirected,
                1 => !Console.IsOutputRedirected,
                2 => !Console.IsErrorRedirected,
                _ => false,
            };
        }

        if (FileSystemHelpers.IsNullDevice(operand))
            return c is 'a' or 'e' or 'c' or 'r' or 'w';

        string p = Resolve(operand);
        switch (c)
        {
            case 'a':
            case 'e': return Exists(p);
            case 'f': return File.Exists(p);
            case 'd': return Directory.Exists(p);
            case 'r':
                if (Directory.Exists(p)) return true;
                try { using var s = BashFileSystem.OpenRead(p); return true; }
                catch { return false; }
            case 'w':
                // Never OpenWrite: that CREATES a missing file. Writable = exists and not read-only.
                if (Directory.Exists(p)) return true;
                try { return File.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReadOnly) == 0; }
                catch { return false; }
            case 'x':
                // Windows has no exec bit: an existing file is taken as executable (historic parity).
                if (Directory.Exists(p)) return true;
                if (!File.Exists(p)) return false;
                if (OperatingSystem.IsWindows()) return true;
                try { return (File.GetUnixFileMode(p) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0; }
                catch { return false; }
            case 's':
                if (Directory.Exists(p)) return true;
                try { return File.Exists(p) && new FileInfo(p).Length > 0; }
                catch { return false; }
            case 'h':
            case 'L': return IsLink(p);
            case 'O':
            case 'G': return Exists(p);
            case 'g':
            case 'u':
            case 'k':
                if (OperatingSystem.IsWindows() || !Exists(p)) return false;
                try
                {
                    var m = File.GetUnixFileMode(p);
                    return c == 'g' ? (m & UnixFileMode.SetGroup) != 0
                         : c == 'u' ? (m & UnixFileMode.SetUser) != 0
                         : (m & UnixFileMode.StickyBit) != 0;
                }
                catch { return false; }
            default: return false; // -b -c -p -S -N: no block/char/fifo/socket files here
        }
    }

    private bool FileBinary(string op, string l, string r)
    {
        string a = Resolve(l), b = Resolve(r);
        switch (op)
        {
            case "-ef":
                try { return Exists(a) && Exists(b) && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            default:
                bool ea = Exists(a), eb = Exists(b);
                if (op == "-nt") return ea && (!eb || Mtime(a) > Mtime(b));
                return eb && (!ea || Mtime(a) < Mtime(b)); // -ot
        }
    }

    private static DateTime Mtime(string p)
        => Directory.Exists(p) ? Directory.GetLastWriteTimeUtc(p) : File.GetLastWriteTimeUtc(p);
}