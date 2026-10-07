using System.Linq;
using System.Management.Automation;
using System.Text;
using PsBash.Cmdlets.Args;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet replacement for the psm1 <c>Invoke-BashPaste</c> function. Merges corresponding
/// lines from multiple files, separated by TABs (or the <c>-d</c> delimiter LIST, cycled per
/// column exactly like GNU coreutils paste).
///
/// Options (shared ordered parser, GNU coreutils 9.4): <c>-d LIST</c> / <c>-dLIST</c> /
/// <c>--delimiters=LIST</c> (a bundle such as <c>-sd,</c> works: <c>d</c> takes the rest of the
/// bundle), <c>-s</c> / <c>--serial</c>, unique long prefixes, <c>--</c>. <c>-z</c> /
/// <c>--zero-terminated</c> is refused (exit 2). A <c>-</c> operand reads stdin.
///
/// Delimiter list: each CHARACTER is one delimiter, used in turn between the columns of a row
/// (parallel) or between the lines of a file (serial, restarting per file). Escapes
/// <c>\n \t \r \b \f \v \\ \0</c> (<c>\0</c> = empty delimiter); any other <c>\x</c> is <c>x</c>;
/// a trailing lone backslash is an error (exit 1). An empty list means <c>\0</c>.
/// The pre-migration cmdlet used the whole list as ONE multi-character delimiter, so
/// <c>paste -d ',;' a b c</c> printed <c>a,;b,;c</c> instead of GNU's <c>a,b;c</c>.
///
/// Decoy: bare <c>-d</c> binds the <c>-Debug</c> common parameter for DIRECT PowerShell calls, so
/// <see cref="Delimiter"/> is declared and re-injected as <c>-d VALUE</c> (PsEmitter quotes every
/// dash word for the transpiler, which never binds it).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashPaste")]
[OutputType(typeof(string))]
public sealed class InvokeBashPasteCommand : PSCmdlet
{
    [Parameter]
    [Alias("d")]
    public string? Delimiter { get; set; }

    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    /// <summary>
    /// Valid GNU paste options ps-bash does not implement (NUL-terminated records).
    /// (A string[] on purpose: CommonParameterCollisionGuardTests enumerates static string sets.)
    /// </summary>
    private static readonly string[] PasteValidButUnsupported = { "-z", "--zero-terminated" };

    private const string OptDelimiters = "delim", OptSerial = "serial";

    private static readonly OptSpecSet PasteSpec = new(
        new[]
        {
            new OptSpec(OptDelimiters, 'd', "delimiters", OptKind.Value),
            new OptSpec(OptSerial, 's', "serial"),
        },
        validButUnsupported: PasteValidButUnsupported,
        allowAbbrev: true,
        gnuInfoOptions: true);

    /// <summary>Pure argv scan (unit-test seam).</summary>
    internal static ParsedArgs ScanArgs(string[] args) => ArgParser.Parse(args, PasteSpec);

    internal sealed class PasteArgs
    {
        public ParsedArgs Parsed = null!;
        /// <summary>The delimiter characters (as strings; <c>\0</c> = empty), cycled per column.</summary>
        public string[] Delimiters = { "\t" };
        public bool Serial;
        public List<string> Operands = new();
        public string? Error;
    }

    internal static PasteArgs Plan(string[] args)
    {
        var p = new PasteArgs { Parsed = ScanArgs(args) };
        p.Operands = p.Parsed.Operands();
        if (p.Parsed.HasError) return p;

        p.Serial = p.Parsed.Has(OptSerial);
        if (p.Parsed.Last(OptDelimiters) is { } d)
        {
            if (!TryParseDelimiterList(d.Value!, out var list, out var err))
            {
                p.Error = err;
                return p;
            }
            p.Delimiters = list;
        }
        return p;
    }

    /// <summary>Expand a GNU paste delimiter LIST into its per-column delimiters.</summary>
    internal static bool TryParseDelimiterList(string spec, out string[] list, out string? error)
    {
        error = null;
        var items = new List<string>();
        for (int i = 0; i < spec.Length; i++)
        {
            char c = spec[i];
            if (c != '\\')
            {
                items.Add(c.ToString());
                continue;
            }
            if (i + 1 >= spec.Length)
            {
                list = Array.Empty<string>();
                error = $"paste: delimiter list ends with an unescaped backslash: {spec}";
                return false;
            }
            char n = spec[++i];
            items.Add(n switch
            {
                'n' => "\n",
                't' => "\t",
                'r' => "\r",
                'b' => "\b",
                'f' => "\f",
                'v' => "\v",
                '0' => string.Empty,
                _ => n.ToString(),   // includes \\ ; any other \x is x
            });
        }
        if (items.Count == 0) items.Add(string.Empty);
        list = items.ToArray();
        return true;
    }

    // Buffered stdin. GNU paste reads standard input when it has no file operands (or for a `-`
    // operand), which is exactly the shape of the common `... | paste -sd,` idiom.
    private readonly List<string> _stdin = new();

    protected override void ProcessRecord()
    {
        if (InputObject == null) return;
        // A record may carry its trailing newline (printf/cat) or not (seq);
        // split so each line is one field either way.
        string text = BashRuntime.GetBashText(InputObject);
        if (text.EndsWith('\n')) text = text[..^1];
        foreach (var line in text.Split('\n'))
            _stdin.Add(line);
    }

    /// <summary>Arguments with the decoy-bound <c>-d VALUE</c> re-injected.</summary>
    private string[] ArgsWithDecoys()
    {
        var args = Arguments ?? Array.Empty<string>();
        if (Delimiter is null) return args;
        return new[] { "-d", Delimiter }.Concat(args).ToArray();
    }

    protected override void EndProcessing()
    {
        var args = ArgsWithDecoys();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "paste", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript(
                         "param($n) Show-BashHelp $n", "paste"))
            {
                WriteObject(line);
            }
            return;
        }

        var plan = Plan(args);
        if (FileSystemHelpers.TryWriteParseError(this, "paste", plan.Parsed)) return;
        if (FileSystemHelpers.TryHandleInfoOptions(this, "paste", plan.Parsed)) return;
        if (plan.Error is { } planError)
        {
            FileSystemHelpers.WriteBashError(this, planError);
            FileSystemHelpers.SetLastExitCode(this, 1);
            return;
        }

        var delimiters = plan.Delimiters;
        bool serial = plan.Serial;

        // Sources in operand order. A `-` operand (or no operand at all) is stdin.
        var sources = new List<(string Name, string? Path)>();
        foreach (var raw in plan.Operands)
        {
            if (raw == "-") { sources.Add(("-", null)); continue; }
            foreach (var filePath in FileSystemHelpers.ResolveOperandPaths(this, raw))
                sources.Add((filePath, filePath));
        }
        if (sources.Count == 0)
        {
            if (_stdin.Count == 0) return;
            sources.Add(("-", null));
        }

        if (serial)
        {
            // Serial mode: each source becomes one line, its fields joined by the cycling list.
            foreach (var (name, path) in sources)
            {
                string? line = ReadSerialLine(name, path, delimiters);
                if (line is null) return;
                WriteObject(BashRuntime.NewBashObject(line));
            }
            return;
        }

        // Normal mode: merge sources line by line, padding short ones with empty strings.
        EmitParallelPaste(sources, delimiters);
    }

    private IEnumerable<string> Lines(string? path) => path is null ? _stdin : BashFileSystem.ReadLines(path);

    private void EmitParallelPaste(IReadOnlyList<(string Name, string? Path)> sources, string[] delimiters)
    {
        var enumerators = new List<IEnumerator<string>>(sources.Count);
        // Every `-` operand shares ONE stdin cursor, so `paste - -` alternates lines (pairs them).
        IEnumerator<string>? stdinCursor = null;
        var current = new string?[sources.Count];
        string currentName = string.Empty;

        try
        {
            for (int i = 0; i < sources.Count; i++)
            {
                currentName = sources[i].Name;
                var e = sources[i].Path is null
                    ? (stdinCursor ??= ((IEnumerable<string>)_stdin).GetEnumerator())
                    : BashFileSystem.ReadLines(sources[i].Path!).GetEnumerator();
                enumerators.Add(e);
                current[i] = e.MoveNext() ? e.Current : null;
            }

            var sb = new StringBuilder();
            while (AnyNonNull(current))
            {
                sb.Clear();
                for (int i = 0; i < current.Length; i++)
                {
                    if (i > 0) sb.Append(delimiters[(i - 1) % delimiters.Length]);
                    sb.Append(current[i] ?? string.Empty);
                }
                WriteObject(BashRuntime.NewBashObject(sb.ToString()));

                for (int i = 0; i < enumerators.Count; i++)
                {
                    currentName = sources[i].Name;
                    current[i] = current[i] is not null && enumerators[i].MoveNext()
                        ? enumerators[i].Current
                        : null;
                }
            }
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            WriteReadError(currentName, ex);
        }
        finally
        {
            foreach (var e in enumerators)
            {
                e.Dispose();
            }
        }
    }

    // Allocation-free replacement for `current.Any(l => l is not null)`.
    private static bool AnyNonNull(string?[] items)
    {
        for (int i = 0; i < items.Length; i++)
            if (items[i] is not null) return true;
        return false;
    }

    private string? ReadSerialLine(string name, string? path, string[] delimiters)
    {
        try
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (var line in Lines(path))
            {
                if (n > 0) sb.Append(delimiters[(n - 1) % delimiters.Length]);
                sb.Append(line);
                n++;
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            WriteReadError(name, ex);
            return null;
        }
    }

    private void WriteReadError(string path, Exception ex)
    {
        string msg = FileSystemHelpers.ReadErrorMessage(ex);
        string normalized = path.Replace('\\', '/');
        FileSystemHelpers.WriteBashError(this, $"paste: {normalized}: {msg}");
    }
}
