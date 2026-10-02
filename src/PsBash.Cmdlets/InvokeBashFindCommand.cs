using PsBash.Core;
using System.Globalization;
using System.Linq;
using System.Management.Automation;
using System.Text;
using System.Text.RegularExpressions;

namespace PsBash.Cmdlets;

/// <summary>
/// Binary cmdlet <c>Invoke-BashFind</c>: walks directory trees and evaluates GNU find 4.9's expression language,
/// emitting typed <c>PsBash.FindEntry</c> PSObjects (<c>BashText</c> = the printed path) for the implicit/explicit
/// <c>-print</c> and exact-bytes text records for <c>-printf</c>/<c>-print0</c>.
///
/// <para><b>Expression model.</b> Tests AND actions are both members of one boolean expression (GNU precedence
/// <c>! &gt; AND &gt; OR &gt; ,</c>), compiled to per-item delegates over <see cref="FindCtx"/>. Actions
/// (<c>-print -print0 -printf -fprint* -ls -fls -exec -execdir -ok -okdir -delete -quit -prune</c>) perform their side effect
/// when the evaluator reaches them and return their truth value, so <c>-name a -o -name b -print</c> prints only what the
/// <c>-print</c> is reached for. If the expression contains no action other than <c>-prune</c>/<c>-quit</c>, GNU's implicit
/// <c>( expr ) -print</c> is added. <c>-maxdepth -mindepth -depth -daystart -follow -xdev -mount -noleaf -ignore_readdir_race
/// -warn -nowarn</c> are options (always true, applied globally).</para>
///
/// <para><b>Walk.</b> Lazy depth-first: a directory is listed when the walk reaches it and every action runs as it is found,
/// so <c>find / -name x | head -1</c> stops at the first hit (<see cref="WalkItem"/>). <c>-P</c> (default) never follows a
/// link, <c>-H</c> follows command-line links only, <c>-L</c>/<c>-follow</c> follows all (with GNU's file-system-loop
/// detection). Unix stat facts and the Windows mapping are documented on <see cref="FindStat"/>.</para>
///
/// <para>The <c>-regextype</c> syntaxes are translated by <see cref="FindRegexDialect"/>; <c>-printf</c> is
/// <see cref="FindPrintf"/>; <c>-perm</c> is <see cref="FindPerm"/>.</para>
///
/// Common-parameter audit: find's words are long names that do not prefix-collide with a common parameter, and the cmdlet is on
/// <c>PsEmitter.OrderedArgCommands</c> (every dash word reaches <see cref="Arguments"/> verbatim and in order). It reads
/// pipeline input only as the answers of <c>-ok</c>/<c>-okdir</c>.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "BashFind")]
[OutputType(typeof(PSObject))]
public sealed class InvokeBashFindCommand : PSCmdlet
{
    [Parameter(ValueFromRemainingArguments = true)]
    public string[]? Arguments { get; set; }

    /// <summary>Standard input, read only to answer <c>-ok</c>/<c>-okdir</c> prompts.</summary>
    [Parameter(ValueFromPipeline = true)]
    public PSObject? InputObject { get; set; }

    private readonly List<PSObject> _stdinRecords = new();

    protected override void ProcessRecord()
    {
        if (InputObject != null) _stdinRecords.Add(InputObject);
    }

    // ───────────── per-run state ─────────────

    private int _exit;
    private int _followMode;                 // 0 = -P, 1 = -H, 2 = -L
    private int _maxDepth = int.MaxValue, _minDepth;
    private bool _depthFirst, _quit;
    private DateTime _now, _origin;
    private Func<FindCtx, bool> _expr = static _ => true;
    private readonly List<Action> _finalizers = new();
    private readonly Dictionary<string, FindSink> _sinks = new(StringComparer.Ordinal);
    private readonly List<(string Real, string Display)> _ancestors = new();
    private StdinLineSource? _answers;

    protected override void EndProcessing()
    {
        try { Run(); }
        finally
        {
            foreach (var sink in _sinks.Values) { try { sink.Dispose(); } catch { /* best-effort */ } }
            try { FileSystemHelpers.SetLastExitCode(this, _exit); } catch (Exception ex) when (FileSystemHelpers.IsPipelineStop(ex)) { /* stopping */ }
        }
    }

    /// <summary>The per-entry evaluation context shared by every predicate leaf and action.</summary>
    private sealed class FindCtx
    {
        public FileSystemInfo Item = null!;     // the entry itself (lstat view)
        public FileSystemInfo Eff = null!;      // the entry find sees: the link target under -L/-H, else Item
        public bool Resolved;                   // Eff is a followed link target
        public bool IsDir;                      // a directory as find sees it
        public bool LinkLeaf;                   // a link that is not followed (-type l)
        public string Display = "";
        public string Start = "";
        public int Depth;
        public bool Prune;
        public Func<FindStat> StatFn = null!;
        private FindStat? _stat;
        public FindStat Stat => _stat ??= StatFn();
    }

    private enum FTk { Leaf, And, Or, Not, LParen, RParen, Comma }

    /// <summary>A parsed expression token. <see cref="Text"/> is the word as typed (for GNU's "expected an expression after" errors).</summary>
    private readonly record struct FindTok(FTk Kind, Func<FindCtx, bool>? Pred, string Text = "");

    private sealed class FindSyntaxException : Exception
    {
        public FindSyntaxException(string message) : base(message) { }
    }

    private static readonly HashSet<string> UnsupportedValuePredicates = new(StringComparer.Ordinal)
    {
        "-used", "-context",
    };

    private static readonly HashSet<string> NoOpOptions = new(StringComparer.Ordinal)
    {
        "-xdev", "-mount", "-noleaf", "-ignore_readdir_race", "-noignore_readdir_race", "-warn", "-nowarn",
    };

    private void Run()
    {
        var args = Arguments ?? Array.Empty<string>();

        FileSystemHelpers.SetLastExitCode(this, 0);
        if (FileSystemHelpers.TryHandleVersion(this, "find", args)) return;
        if (Array.IndexOf(args, "--help") >= 0)
        {
            foreach (var line in InvokeCommand.InvokeScript("param($n) Show-BashHelp $n", "find"))
                WriteObject(line);
            return;
        }

        _now = DateTime.Now;
        _origin = _now;
        var operands = new List<string>();
        var exprTokens = new List<FindTok>();
        bool hasAction = false, sawPrune = false, sawDelete = false, sawExplicitDepth = false;
        var rxDialect = FindRegexDialect.Dialect.Emacs;
        _answers = new StdinLineSource(this, _stdinRecords);

        // GNU find is NOT getopt: `find [-H|-L|-P] [-D opts] [-Olevel] [PATH...] [EXPRESSION]`.
        int i = 0;
        while (i < args.Length)
        {
            string o = args[i];
            if (o == "-P") { _followMode = 0; i++; continue; }
            if (o == "-H") { _followMode = 1; i++; continue; }
            if (o == "-L") { _followMode = 2; i++; continue; }
            if (o == "-D") { i += 2; continue; }                      // debug options: accepted, ignored
            if (o.Length >= 2 && o[0] == '-' && o[1] == 'O' && o.Skip(2).All(char.IsDigit)) { i++; continue; }
            break;
        }
        while (i < args.Length && !StartsExpression(args[i]))
        {
            operands.Add(args[i]);
            i++;
        }

        bool TryArg(string predicate, out string value)
        {
            if (++i < args.Length) { value = args[i]; return true; }
            EmitError($"find: missing argument to `{predicate}'");
            value = string.Empty;
            return false;
        }

        void Add(Func<FindCtx, bool> pred, string text = "") => exprTokens.Add(new FindTok(FTk.Leaf, pred, text));
        void AddAction(Func<FindCtx, bool> pred) { hasAction = true; Add(pred); }

        bool NumCmp(string predicate, string text, out char op, out double n)
        {
            op = '='; n = 0;
            var m = Regex.Match(text, @"^([+-]?)(\d+(?:\.\d+)?)$");
            if (!m.Success) { EmitError($"find: invalid argument `{text}' to `{predicate}'"); return false; }
            n = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (m.Groups[1].Length > 0) op = m.Groups[1].Value[0];
            return true;
        }

        FindSink? SinkFor(string path)
        {
            if (_sinks.TryGetValue(path, out var existing)) return existing;
            FindSink? sink = null;
            if (path == "/dev/stdout") sink = new StdoutSink(this);
            else if (path == "/dev/stderr") sink = new StderrSink(this);
            else
            {
                try
                {
                    var full = SessionState.Path.GetUnresolvedProviderPathFromPSPath(FileSystemHelpers.NormalizeOperandPath(path));
                    sink = new FileSink(new StreamWriter(new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), RawBytes.Encoding, 65536));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    string why = ex is UnauthorizedAccessException ? "Permission denied"
                        : ex is DirectoryNotFoundException or FileNotFoundException ? "No such file or directory" : ex.Message;
                    EmitError($"find: ‘{path}’: {why}");
                    return null;
                }
            }
            _sinks[path] = sink;
            return sink;
        }

        string Typed(string s) => s;

        while (i < args.Length)
        {
            string arg = args[i];
            switch (arg)
            {
                // ── options (global, always true) ──
                case "-maxdepth":
                case "-mindepth":
                {
                    if (!TryArg(arg, out var depthText)) return;
                    if (depthText.Length == 0 || !depthText.All(char.IsDigit) || !int.TryParse(depthText, out var depthValue))
                    {
                        EmitError($"find: Expected a positive decimal integer argument to {arg}, but got ‘{depthText}’");
                        return;
                    }
                    if (arg == "-maxdepth") _maxDepth = depthValue; else _minDepth = depthValue;
                    i++;
                    continue;
                }
                case "-depth":
                case "-d":
                    _depthFirst = true; sawExplicitDepth = true;
                    Add(static _ => true);
                    i++;
                    continue;
                case "-daystart":
                    _origin = DateTime.Today.AddDays(1);          // GNU: the midnight that ENDS today
                    Add(static _ => true);
                    i++;
                    continue;
                case "-follow":
                    _followMode = 2;
                    Add(static _ => true);
                    i++;
                    continue;
                // ── boolean operators ──
                case "-a":
                case "-and":
                    exprTokens.Add(new FindTok(FTk.And, null, arg)); i++; continue;
                case "-o":
                case "-or":
                    exprTokens.Add(new FindTok(FTk.Or, null, arg)); i++; continue;
                case "!":
                case "-not":
                    exprTokens.Add(new FindTok(FTk.Not, null, arg)); i++; continue;
                case "(":
                    exprTokens.Add(new FindTok(FTk.LParen, null, arg)); i++; continue;
                case ")":
                    exprTokens.Add(new FindTok(FTk.RParen, null, arg)); i++; continue;
                case ",":
                    exprTokens.Add(new FindTok(FTk.Comma, null, arg)); i++; continue;

                // ── actions ──
                case "-print":
                    AddAction(c => { EmitEntry(c); return true; });
                    i++;
                    continue;
                case "-print0":
                case "--print0":
                    AddAction(c => { WriteExact(c.Display + "\0"); return true; });
                    i++;
                    continue;
                case "-printf":
                {
                    if (!TryArg(arg, out var fmt)) return;
                    var f = FindPrintf.TryCompile(fmt, out var perr);
                    if (f is null) { EmitError("find: " + perr); return; }
                    foreach (var w in f.Warnings) FileSystemHelpers.WriteStderr(this, "find: " + w);
                    AddAction(c => { WriteExact(f.Render(PrintItem(c), out _)); return true; });
                    i++;
                    continue;
                }
                case "-fprint":
                case "-fprint0":
                {
                    if (!TryArg(arg, out var file)) return;
                    var sink = SinkFor(file);
                    if (sink is null) return;
                    string term = arg == "-fprint0" ? "\0" : "\n";
                    AddAction(c => { sink.Write(c.Display + term); return true; });
                    i++;
                    continue;
                }
                case "-fprintf":
                {
                    if (!TryArg(arg, out var file)) return;
                    if (!TryArg(arg, out var fmt)) return;
                    var f = FindPrintf.TryCompile(fmt, out var perr);
                    if (f is null) { EmitError("find: " + perr); return; }
                    foreach (var w in f.Warnings) FileSystemHelpers.WriteStderr(this, "find: " + w);
                    var sink = SinkFor(file);
                    if (sink is null) return;
                    AddAction(c => { sink.Write(f.Render(PrintItem(c), out _)); return true; });
                    i++;
                    continue;
                }
                case "-ls":
                    AddAction(c => { WriteLine(LsLine(c)); return true; });
                    i++;
                    continue;
                case "-fls":
                {
                    if (!TryArg(arg, out var file)) return;
                    var sink = SinkFor(file);
                    if (sink is null) return;
                    AddAction(c => { sink.Write(LsLine(c) + "\n"); return true; });
                    i++;
                    continue;
                }
                case "-delete":
                    _depthFirst = true; sawDelete = true;
                    AddAction(DeleteAction);
                    i++;
                    continue;
                case "-prune":
                    sawPrune = true;
                    Add(c => { c.Prune = true; return true; });
                    i++;
                    continue;
                case "-quit":
                    Add(c => { _quit = true; return true; });
                    i++;
                    continue;
                case "-exec":
                case "-execdir":
                case "-ok":
                case "-okdir":
                {
                    bool plusAllowed = arg is "-exec" or "-execdir";
                    var cmd = new List<string>();
                    string? terminator = null;
                    i++;
                    while (i < args.Length)
                    {
                        string ea = args[i];
                        if (ea == ";") { terminator = ";"; i++; break; }
                        if (ea == "+" && plusAllowed && cmd.Count > 0 && cmd[^1] == "{}") { terminator = "+"; i++; break; }
                        cmd.Add(ea);
                        i++;
                    }
                    if (terminator == null || cmd.Count == 0)
                    {
                        EmitError($"find: missing argument to `{arg}'");
                        return;
                    }
                    if (terminator == "+")
                    {
                        for (int k = 0; k < cmd.Count - 1; k++)
                            if (cmd[k].Contains("{}", StringComparison.Ordinal))
                            {
                                EmitError($"find: Only one instance of {{}} is supported with {arg} ... +");
                                return;
                            }
                    }
                    AddAction(MakeExecAction(arg, cmd, terminator));
                    continue;
                }

                // ── tests ──
                case "-true": Add(static _ => true); i++; continue;
                case "-false": Add(static _ => false); i++; continue;
                case "-name":
                case "-iname":
                {
                    if (!TryArg(arg, out var p)) return;
                    bool ci = arg == "-iname";
                    Add(c => GlobMatch(c.Item.Name, p, ci));
                    i++;
                    continue;
                }
                case "-path":
                case "-wholename":
                case "-ipath":
                case "-iwholename":
                {
                    if (!TryArg(arg, out var p)) return;
                    bool ci = arg is "-ipath" or "-iwholename";
                    Add(c => PathGlobMatch(c.Display, p, ci));
                    i++;
                    continue;
                }
                case "-lname":
                case "-ilname":
                {
                    if (!TryArg(arg, out var p)) return;
                    bool ci = arg == "-ilname";
                    Add(c =>
                    {
                        if (!c.LinkLeaf && !(c.Resolved)) { if (!FindStat.IsLinkEntry(c.Item)) return false; }
                        string? t;
                        try { t = c.Item.LinkTarget; } catch { return false; }
                        return t is not null && PathGlobMatch(t.Replace('\\', '/'), p, ci);
                    });
                    i++;
                    continue;
                }
                case "-regextype":
                {
                    if (!TryArg(arg, out var name)) return;
                    if (!FindRegexDialect.TryDialect(name, out rxDialect))
                    {
                        EmitError("find: " + FindRegexDialect.UnknownTypeMessage(name));
                        return;
                    }
                    i++;
                    continue;
                }
                case "-regex":
                case "-iregex":
                {
                    if (!TryArg(arg, out var p)) return;
                    if (!FindRegexDialect.TryCompile(p, rxDialect, arg == "-iregex", out var rx, out var rerr))
                    {
                        EmitError($"find: failed to compile regular expression '{p}': {rerr}");
                        return;
                    }
                    Add(c => rx!.IsMatch(c.Display));
                    i++;
                    continue;
                }
                case "-type":
                case "-xtype":
                {
                    if (!TryArg(arg, out var tf)) return;
                    var letters = tf.Split(',');
                    foreach (var tc in letters)
                    {
                        if (tc.Length != 1 || "bcdflpsD".IndexOf(tc[0]) < 0)
                        {
                            EmitError($"find: Unknown argument to {arg}: {tc}");
                            return;
                        }
                    }
                    bool x = arg == "-xtype";
                    Add(c =>
                    {
                        // -type: the entry as found (a link that is not followed is `l`); -xtype: the other view
                        bool link = c.LinkLeaf, dir = c.IsDir;
                        if (x)
                        {
                            if (c.LinkLeaf) { var t = FindStat.TryResolveLink(c.Item); link = t is null; dir = t is DirectoryInfo; if (t is null) { link = true; } else link = false; }
                            else if (FindStat.IsLinkEntry(c.Item) && c.Resolved) { link = true; dir = false; }
                        }
                        foreach (var letter in letters)
                        {
                            bool hit = letter switch
                            {
                                "f" => !link && !dir,
                                "d" => !link && dir,
                                "l" => link,
                                _ => false,   // b c p s D: no Windows analogue (Unix: from stat below)
                            };
                            if (!hit && !OperatingSystem.IsWindows() && letter is "b" or "c" or "p" or "s")
                                hit = c.Stat.TypeChar == letter[0];
                            if (hit) return true;
                        }
                        return false;
                    });
                    i++;
                    continue;
                }
                case "-size":
                {
                    if (!TryArg(arg, out var e)) return;
                    if (!TryParseSize(e, out var op, out var units, out var unitSize, out var serr)) { EmitError("find: " + serr); return; }
                    Add(c => SizeMatch(c, op, units, unitSize));
                    i++;
                    continue;
                }
                case "-empty":
                    Add(EmptyMatch); i++; continue;
                case "-perm":
                {
                    if (!TryArg(arg, out var m)) return;
                    if (!FindPerm.TryParse(m, out var perm, out var warn)) { EmitError($"find: invalid mode ‘{m}’"); return; }
                    if (warn is not null) FileSystemHelpers.WriteStderr(this, "find: " + warn);
                    Add(c => perm.Matches(c.Stat.Mode));
                    i++;
                    continue;
                }
                case "-user":
                case "-group":
                {
                    if (!TryArg(arg, out var name)) return;
                    bool isUser = arg == "-user";
                    if (!FindIdentity.TryResolve(name, isUser, out var id))
                    {
                        EmitError($"find: ‘{name}’ is not the name of a known {(isUser ? "user" : "group")}");
                        return;
                    }
                    Add(c => isUser
                        ? (id.Sid is not null && c.Stat.OwnerSid == id.Sid) || (id.Id is { } u && c.Stat.Uid == u && (id.Sid is null || c.Stat.OwnerSid.Length == 0))
                        : (id.Sid is not null && c.Stat.GroupSid == id.Sid) || (id.Id is { } g && c.Stat.Gid == g && (id.Sid is null || c.Stat.GroupSid.Length == 0)));
                    i++;
                    continue;
                }
                case "-uid":
                case "-gid":
                case "-links":
                case "-inum":
                {
                    if (!TryArg(arg, out var text)) return;
                    if (!NumCmp(arg, text, out var op, out var n)) return;
                    string which = arg;
                    Add(c =>
                    {
                        double v = which switch
                        {
                            "-uid" => c.Stat.Uid, "-gid" => c.Stat.Gid, "-links" => c.Stat.Nlink, _ => c.Stat.Inode,
                        };
                        return op switch { '+' => v > n, '-' => v < n, _ => v == n };
                    });
                    i++;
                    continue;
                }
                case "-nouser": Add(c => !c.Stat.UserKnown); i++; continue;
                case "-nogroup": Add(c => !c.Stat.GroupKnown); i++; continue;
                case "-samefile":
                {
                    if (!TryArg(arg, out var f)) return;
                    string refPath;
                    try
                    {
                        refPath = SessionState.Path.GetUnresolvedProviderPathFromPSPath(FileSystemHelpers.NormalizeOperandPath(f));
                        if (!File.Exists(refPath) && !Directory.Exists(refPath) && !FindStat.IsLinkEntry(new FileInfo(refPath))) throw new FileNotFoundException();
                    }
                    catch { EmitError($"find: ‘{f}’: No such file or directory"); return; }
                    Add(c => _followMode == 2
                        ? FileIdentity.SameFileFollowingLinks(refPath, c.Eff.FullName)
                        : FileIdentity.SameEntryNotFollowing(refPath, c.Item.FullName));
                    i++;
                    continue;
                }
                case "-executable":
                case "-readable":
                case "-writable":
                {
                    string kind = arg;
                    Add(c => FindAccess.Check(c.Eff, kind));
                    i++;
                    continue;
                }
                case "-fstype":
                {
                    if (!TryArg(arg, out var ft)) return;
                    Add(c => string.Equals(FsTypeOf(c.Eff.FullName), ft, StringComparison.OrdinalIgnoreCase));
                    i++;
                    continue;
                }
                case "-amin": case "-cmin": case "-mmin":
                case "-atime": case "-ctime": case "-mtime":
                {
                    if (!TryArg(arg, out var text)) return;
                    if (!NumCmp(arg, text, out var op, out var n)) return;
                    bool days = arg.EndsWith("time", StringComparison.Ordinal);
                    char which = arg[1];
                    double w = days ? 86400 : 60;
                    Add(c =>
                    {
                        DateTime t = which switch { 'a' => c.Eff.LastAccessTime, 'c' => c.Stat.Ctime, _ => c.Eff.LastWriteTime };
                        double age = (_origin - t).TotalSeconds;
                        return days
                            ? op switch { '-' => age < n * w, '+' => age > (n + 1) * w, _ => age >= n * w && age < (n + 1) * w }
                            : op switch { '-' => age < n * w, '+' => age > n * w, _ => age > (n - 1) * w && age <= n * w };
                    });
                    i++;
                    continue;
                }
                default:
                    if (arg is "-newer" or "-anewer" or "-cnewer" || (arg.StartsWith("-newer", StringComparison.Ordinal) && arg.Length == 8))
                    {
                        char x = arg switch { "-newer" => 'm', "-anewer" => 'a', "-cnewer" => 'c', _ => arg[6] };
                        char y = arg is "-newer" or "-anewer" or "-cnewer" ? 'm' : arg[7];
                        if ("aBcm".IndexOf(x) < 0 || "aBcmt".IndexOf(y) < 0)
                        {
                            EmitError($"find: invalid predicate `{arg}'");
                            return;
                        }
                        if (!TryArg(arg, out var refArg)) return;
                        DateTime refTime;
                        if (y == 't')
                        {
                            if (!GnuDateParser.TryParse(refArg, DateTimeOffset.Now, TimeZoneInfo.Local, out var parsed))
                            {
                                EmitError($"find: I cannot figure out how to interpret ‘{refArg}’ as a date or time");
                                return;
                            }
                            refTime = parsed.LocalDateTime;
                        }
                        else
                        {
                            try
                            {
                                var full = SessionState.Path.GetUnresolvedProviderPathFromPSPath(FileSystemHelpers.NormalizeOperandPath(refArg));
                                FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
                                if (!info.Exists && !FindStat.IsLinkEntry(info)) throw new FileNotFoundException();
                                refTime = y switch { 'a' => info.LastAccessTime, 'B' => info.CreationTime, 'c' => FindStat.ChangeTimeOf(info), _ => info.LastWriteTime };
                            }
                            catch { EmitError($"find: ‘{refArg}’: No such file or directory"); return; }
                        }
                        char xx = x;
                        Add(c =>
                        {
                            DateTime t = xx switch { 'a' => c.Eff.LastAccessTime, 'B' => c.Eff.CreationTime, 'c' => c.Stat.Ctime, _ => c.Eff.LastWriteTime };
                            return t > refTime;
                        });
                        i++;
                        continue;
                    }
                    if (NoOpOptions.Contains(arg)) { Add(static _ => true); i++; continue; }
                    if (UnsupportedValuePredicates.Contains(arg))
                    {
                        EmitError($"find: unsupported predicate '{arg}'");
                        return;
                    }
                    // Any other dash-led token is a predicate find does not know at all: GNU rejects it
                    // ("unknown predicate"); a non-dash token after the expression began is a misplaced path.
                    if (FileSystemHelpers.IsOptionLike(arg))
                    {
                        EmitError($"find: unknown predicate `{arg}'");
                        return;
                    }
                    EmitError($"find: paths must precede expression: `{arg}'");
                    return;
            }
        }

        if (sawDelete && sawPrune && !sawExplicitDepth)
        {
            EmitError("find: The -delete action automatically turns on -depth, but -prune does nothing when -depth is in effect.  If you want to carry on anyway, just explicitly use the -depth option.");
            return;
        }

        try { _expr = ParseFindExpr(exprTokens); }
        catch (FindSyntaxException ex)
        {
            EmitError($"find: {ex.Message}");
            return;
        }
        if (!hasAction)
        {
            // GNU: no action other than -prune/-quit => `( expr ) -print`
            var inner = _expr;
            _expr = c => inner(c) && !_quit && Emit(c);
        }

        // GNU find walks every listed search-path operand as its own root, in order. A missing root prints the
        // "No such file or directory" error, sets exit 1, and CONTINUES with the remaining roots.
        var searchPaths = operands.Count > 0 ? operands : new List<string> { "." };
        foreach (var rootSearchPath in searchPaths)
        {
            if (_quit) break;
            var rootResult = InvokeCommand.InvokeScript(
                "param($p) Get-BashItem -Path $p -Command 'find' 2>&1", rootSearchPath);
            FileSystemInfo? rootItem = null;
            foreach (var r in rootResult)
            {
                if (r?.BaseObject is FileSystemInfo fsi) { rootItem = fsi; break; }
                if (r?.BaseObject is ErrorRecord innerEr) FileSystemHelpers.WriteBashError(this, innerEr.ToString());
            }
            if (rootItem == null)
            {
                _exit = 1;
                continue;
            }
            string display = rootSearchPath.Replace('\\', '/');
            _ancestors.Clear();
            WalkItem(rootItem, display, display, 0);
        }

        foreach (var fin in _finalizers) fin();
    }

    private bool Emit(FindCtx c) { EmitEntry(c); return true; }

    // ───────────── the walk ─────────────

    /// <summary>
    /// Visit one entry and (when it is a directory) its children. Returns false once <c>-quit</c> was executed.
    /// The expression runs once per entry: before the children by default, after them under <c>-depth</c>/<c>-delete</c>;
    /// a directory whose evaluation set <c>-prune</c> is not entered (ignored under <c>-depth</c>). A link is a leaf
    /// unless the policy follows it (<c>-L</c> everywhere, <c>-H</c> for the command-line roots).
    /// </summary>
    private bool WalkItem(FileSystemInfo item, string display, string start, int depth)
    {
        bool follow = _followMode == 2 || (_followMode == 1 && depth == 0);
        bool linkEntry = FindStat.IsLinkEntry(item);
        FileSystemInfo eff = item;
        bool resolved = false;
        if (follow && linkEntry)
        {
            var t = FindStat.TryResolveLink(item);
            if (t is not null) { eff = t; resolved = true; }
        }
        bool linkLeaf = linkEntry && !resolved;
        bool isDir = eff is DirectoryInfo && !linkLeaf;

        string? real = null;
        if (isDir && follow)
        {
            real = eff.FullName.TrimEnd('\\', '/');
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var (anc, ancDisplay) in _ancestors)
            {
                if (string.Equals(anc, real, cmp))
                {
                    _exit = 1;
                    FileSystemHelpers.WriteStderr(this, $"find: File system loop detected; ‘{display}’ is part of the same file system loop as ‘{ancDisplay}’.");
                    return true;
                }
            }
        }

        var ctx = new FindCtx
        {
            Item = item, Eff = eff, Resolved = resolved, IsDir = isDir, LinkLeaf = linkLeaf, Display = display, Start = start, Depth = depth,
        };
        ctx.StatFn = () => FindStat.Read(item, eff, resolved);

        bool evaluate = depth >= _minDepth;
        if (evaluate && !_depthFirst)
        {
            _expr(ctx);
            if (_quit) return false;
        }

        if (isDir && depth < _maxDepth && !(ctx.Prune && !_depthFirst))
        {
            if (follow) _ancestors.Add((real!, display));
            try
            {
                foreach (var child in FileSystemHelpers.ListChildrenNoFollow((DirectoryInfo)eff))
                {
                    string childDisplay = display.EndsWith('/') ? display + child.Name : display + "/" + child.Name;
                    if (!WalkItem(child, childDisplay, start, depth + 1)) return false;
                }
            }
            finally
            {
                if (follow) _ancestors.RemoveAt(_ancestors.Count - 1);
            }
        }

        if (evaluate && _depthFirst)
        {
            _expr(ctx);
            if (_quit) return false;
        }
        return true;
    }

    // ───────────── actions ─────────────

    private FindPrintItem PrintItem(FindCtx c) => new() { Path = c.Display, Start = c.Start, Depth = c.Depth, StatFn = () => c.Stat };

    /// <summary>The default action: a typed <c>PsBash.FindEntry</c> per match.</summary>
    private void EmitEntry(FindCtx c)
    {
        var fileInfo = BuildFileInfo(c.Item);
        var obj = new PSObject();
        obj.TypeNames.Insert(0, "PsBash.FindEntry");
        obj.Properties.Add(new PSNoteProperty("Path", c.Display));
        obj.Properties.Add(new PSNoteProperty("Name", c.Item.Name));
        obj.Properties.Add(new PSNoteProperty("FullPath", c.Item.FullName));
        obj.Properties.Add(new PSNoteProperty("IsDirectory", c.IsDir));
        obj.Properties.Add(new PSNoteProperty("SizeBytes", fileInfo.SizeBytes));
        obj.Properties.Add(new PSNoteProperty("Permissions", fileInfo.Permissions));
        obj.Properties.Add(new PSNoteProperty("LinkCount", fileInfo.LinkCount));
        obj.Properties.Add(new PSNoteProperty("Owner", fileInfo.Owner));
        obj.Properties.Add(new PSNoteProperty("Group", fileInfo.Group));
        obj.Properties.Add(new PSNoteProperty("LastModified", c.Item.LastWriteTime));
        obj.Properties.Add(new PSNoteProperty("BashText", c.Display));
        WriteObject(obj);
    }

    /// <summary>Exact bytes (no terminator is added): <c>-print0</c>, <c>-printf</c>.</summary>
    private void WriteExact(string text)
    {
        if (text.Length == 0) return;
        var rec = new PSObject();
        rec.TypeNames.Insert(0, "PsBash.TextOutput");
        rec.Properties.Add(new PSNoteProperty("BashText", text));
        rec.Properties.Add(new PSNoteProperty("NoTrailingNewline", true));
        WriteObject(rec);
    }

    private void WriteLine(string line)
    {
        var rec = new PSObject();
        rec.TypeNames.Insert(0, "PsBash.TextOutput");
        rec.Properties.Add(new PSNoteProperty("BashText", line));
        WriteObject(rec);
    }

    /// <summary>The <c>-ls</c> line: inode(9) blocks(6) mode links(3) owner(-8) group(-8) size(8) date name [-&gt; target].</summary>
    private string LsLine(FindCtx c)
    {
        var s = c.Stat;
        var m = s.Mtime;
        double age = (_now - m).TotalSeconds;
        bool recent = age < 15778476 && age > -3600;
        string date = m.ToString("MMM", CultureInfo.InvariantCulture) + " " + m.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2) + " "
            + (recent ? m.ToString("HH:mm", CultureInfo.InvariantCulture) : " " + m.Year.ToString(CultureInfo.InvariantCulture));
        var sb = new StringBuilder();
        sb.Append(s.Inode.ToString(CultureInfo.InvariantCulture).PadLeft(9)).Append(' ')
          .Append(s.BlocksK.ToString(CultureInfo.InvariantCulture).PadLeft(6)).Append(' ')
          .Append(FindStat.SymbolicMode(s.TypeChar, s.Mode)).Append(' ')
          .Append(s.Nlink.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append(' ')
          .Append(s.User.PadRight(8)).Append(' ').Append(s.Group.PadRight(8)).Append(' ')
          .Append(s.Size.ToString(CultureInfo.InvariantCulture).PadLeft(8)).Append(' ')
          .Append(date).Append(' ').Append(c.Display);
        if (s.IsLink && s.LinkTarget is not null) sb.Append(" -> ").Append(s.LinkTarget.Replace('\\', '/'));
        return sb.ToString();
    }

    private bool DeleteAction(FindCtx c)
    {
        try
        {
            if (c.Item is DirectoryInfo)
            {
                // Non-recursive on purpose: GNU `find -delete` fails on a non-empty directory (matched contents are
                // deleted first, depth-first). Clear the read-only bit so an empty read-only dir still deletes on Windows.
                FileSystemHelpers.ClearReadOnly(c.Item.FullName);
                Directory.Delete(c.Item.FullName, recursive: false);
            }
            else FileSystemHelpers.DeleteFileForce(c.Item.FullName);
            return true;
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            EmitError($"find: cannot delete ‘{c.Display}’: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// <c>-exec</c>/<c>-execdir</c>/<c>-ok</c>/<c>-okdir</c> with <c>;</c> (run now, the test is the exit status) or <c>+</c>
    /// (collect: one run at the end, or per directory for <c>-execdir</c>; true). <c>{}</c> is replaced inside every word of a
    /// <c>;</c> command. <c>-execdir</c> runs in the directory of the entry with <c>./name</c>.
    /// </summary>
    private Func<FindCtx, bool> MakeExecAction(string kind, List<string> cmd, string terminator)
    {
        bool inDir = kind is "-execdir" or "-okdir";
        bool ask = kind is "-ok" or "-okdir";

        string Subst(string token, string path) => token.Replace("{}", path, StringComparison.Ordinal);

        if (terminator == ";")
        {
            return c =>
            {
                string path = c.Display, dir = "";
                if (inDir) { var (d, name) = FindPrintf.ExecDirParts(c.Display); dir = d; path = "./" + name; }
                var argv = cmd.Select(t => Subst(t, path)).ToList();
                if (ask)
                {
                    FileSystemHelpers.WriteStderr(this, $"< {argv[0]} ... {path} > ? ");
                    if (!StdinLineSource.IsYes(_answers!.ReadLine())) return false;
                }
                return RunCommand(argv, inDir ? dir : null);
            };
        }

        // '+': the last word is the single {}.
        var batch = new List<string>();
        string? batchDir = null;
        void Flush()
        {
            if (batch.Count == 0) return;
            var argv = new List<string>();
            foreach (var t in cmd) { if (t == "{}") argv.AddRange(batch); else argv.Add(t); }
            string? dir = batchDir;
            batch.Clear(); batchDir = null;
            if (!RunCommand(argv, inDir ? dir : null)) _exit = 1;
        }
        _finalizers.Add(Flush);
        return c =>
        {
            if (inDir)
            {
                var (d, name) = FindPrintf.ExecDirParts(c.Display);
                if (batchDir is not null && !string.Equals(batchDir, d, StringComparison.Ordinal)) Flush();
                batchDir = d;
                batch.Add("./" + name);
            }
            else batch.Add(c.Display);
            return true;
        };
    }

    /// <summary>
    /// Run an external command (or ps-bash cmdlet) whose name and arguments come from user input. Name and each argument are
    /// passed as positional <c>$args</c> through a fixed script body — never concatenated into it — so a path or token
    /// containing <c>;</c>, <c>$(...)</c>, scriptblock chars or backticks cannot be re-parsed as PowerShell
    /// (qa-rubric Directive 12). Returns true when the command exited 0.
    /// </summary>
    private bool RunCommand(IReadOnlyList<string> argv, string? workDir)
    {
        if (argv.Count == 0) return true;
        const string body =
            "$rest = @(); if ($args.Count -gt 1) { $rest = $args[1..($args.Count-1)] }; " +
            "& $args[0] @rest; if (-not $?) { if (-not $global:LASTEXITCODE) { $global:LASTEXITCODE = 1 } }";
        var boxed = new object[argv.Count];
        for (int k = 0; k < argv.Count; k++) boxed[k] = argv[k];

        string? prevCwd = null;
        string? prevLoc = null;
        try
        {
            if (workDir is not null)
            {
                prevLoc = SessionState.Path.CurrentFileSystemLocation.Path;
                prevCwd = Environment.CurrentDirectory;
                string target = Path.GetFullPath(workDir.Length == 0 ? "/" : workDir, prevLoc);
                SessionState.Path.SetLocation(target);
                Environment.CurrentDirectory = target;
            }
            SessionState.PSVariable.Set("global:LASTEXITCODE", 0);
            foreach (var o in InvokeCommand.InvokeScript(body, boxed)) WriteObject(o);
            var code = SessionState.PSVariable.GetValue("LASTEXITCODE");
            return code is null || (code is int ci && ci == 0) || (code is long cl && cl == 0);
        }
        catch (Exception ex)
        {
            if (FileSystemHelpers.IsPipelineStop(ex)) throw;
            if (ex is CommandNotFoundException || ex.InnerException is CommandNotFoundException)
                FileSystemHelpers.WriteStderr(this, $"find: ‘{argv[0]}’: No such file or directory");
            else
                FileSystemHelpers.WriteStderr(this, $"find: -exec failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (prevLoc is not null)
            {
                try { SessionState.Path.SetLocation(prevLoc); } catch { /* best-effort */ }
                try { Environment.CurrentDirectory = prevCwd!; } catch { /* best-effort */ }
            }
        }
    }

    // ───────────── output sinks (-fprint family) ─────────────

    private abstract class FindSink : IDisposable
    {
        public abstract void Write(string text);
        public virtual void Dispose() { }
    }

    private sealed class FileSink : FindSink
    {
        private readonly StreamWriter _w;
        public FileSink(StreamWriter w) { _w = w; }
        public override void Write(string text) => _w.Write(text);
        public override void Dispose() => _w.Dispose();
    }

    /// <summary>/dev/stdout: complete lines are ordinary records, a trailing partial line is an exact-bytes record.</summary>
    private sealed class StdoutSink : FindSink
    {
        private readonly InvokeBashFindCommand _cmd;
        public StdoutSink(InvokeBashFindCommand cmd) { _cmd = cmd; }
        public override void Write(string text)
        {
            int start = 0;
            while (true)
            {
                int nl = text.IndexOf('\n', start);
                if (nl < 0) break;
                _cmd.WriteLine(text.Substring(start, nl - start));
                start = nl + 1;
            }
            if (start < text.Length) _cmd.WriteExact(text.Substring(start));
        }
    }

    private sealed class StderrSink : FindSink
    {
        private readonly InvokeBashFindCommand _cmd;
        private readonly StringBuilder _pending = new();
        public StderrSink(InvokeBashFindCommand cmd) { _cmd = cmd; }
        public override void Write(string text)
        {
            _pending.Append(text);
            while (true)
            {
                var s = _pending.ToString();
                int nl = s.IndexOf('\n');
                if (nl < 0) break;
                FileSystemHelpers.WriteStderr(_cmd, s.Substring(0, nl));
                _pending.Remove(0, nl + 1);
            }
        }
        public override void Dispose() { if (_pending.Length > 0) FileSystemHelpers.WriteStderr(_cmd, _pending.ToString()); }
    }

    // ───────────── predicate expression parser ─────────────
    // GNU precedence: ! > AND (explicit -a or juxtaposition) > OR > , (list: the value of the last member).

    private static Func<FindCtx, bool> ParseFindExpr(IReadOnlyList<FindTok> toks)
    {
        if (toks.Count == 0) return static _ => true;
        int pos = 0;
        var pred = ParseList(toks, ref pos);
        if (pos != toks.Count)
            throw new FindSyntaxException("invalid expression; you have too many ')'");
        return pred;
    }

    private static void RequireTerm(IReadOnlyList<FindTok> t, int p, string after)
    {
        if (p >= t.Count || !IsTermStart(t[p].Kind))
            throw new FindSyntaxException($"expected an expression after '{after}'");
    }

    private static Func<FindCtx, bool> ParseList(IReadOnlyList<FindTok> t, ref int p)
    {
        var left = ParseOr(t, ref p);
        while (p < t.Count && t[p].Kind == FTk.Comma)
        {
            string text = t[p].Text;
            p++;
            RequireTerm(t, p, text);
            var right = ParseOr(t, ref p);
            var l = left;
            left = c => { l(c); return right(c); };
        }
        return left;
    }

    private static Func<FindCtx, bool> ParseOr(IReadOnlyList<FindTok> t, ref int p)
    {
        var left = p < t.Count && t[p].Kind == FTk.Or ? (static _ => true) : ParseAnd(t, ref p);
        while (p < t.Count && t[p].Kind == FTk.Or)
        {
            string text = t[p].Text;
            p++;
            RequireTerm(t, p, text);
            var right = ParseAnd(t, ref p);
            var l = left;
            left = c => l(c) || right(c);
        }
        return left;
    }

    private static Func<FindCtx, bool> ParseAnd(IReadOnlyList<FindTok> t, ref int p)
    {
        if (p < t.Count && t[p].Kind == FTk.And)
        {
            string lead = t[p].Text;
            p++;
            RequireTerm(t, p, lead);
        }
        var left = ParseNot(t, ref p);
        while (p < t.Count && (t[p].Kind == FTk.And || IsTermStart(t[p].Kind)))
        {
            if (t[p].Kind == FTk.And)
            {
                string text = t[p].Text;
                p++;
                RequireTerm(t, p, text);
            }
            var right = ParseNot(t, ref p);
            var l = left;
            left = c => l(c) && right(c);
        }
        return left;
    }

    private static Func<FindCtx, bool> ParseNot(IReadOnlyList<FindTok> t, ref int p)
    {
        if (p < t.Count && t[p].Kind == FTk.Not)
        {
            string text = t[p].Text;
            p++;
            RequireTerm(t, p, text);
            var inner = ParseNot(t, ref p);
            return c => !inner(c);
        }
        return ParseTerm(t, ref p);
    }

    private static Func<FindCtx, bool> ParseTerm(IReadOnlyList<FindTok> t, ref int p)
    {
        if (p >= t.Count) throw new FindSyntaxException("invalid expression");
        var tok = t[p];
        if (tok.Kind == FTk.Comma) throw new FindSyntaxException("expected an expression after ','");
        if (tok.Kind == FTk.LParen)
        {
            p++;
            if (p < t.Count && t[p].Kind == FTk.RParen)
                throw new FindSyntaxException("invalid expression; empty parentheses are not allowed.");
            RequireTerm(t, p, "(");
            var inner = ParseList(t, ref p);
            if (p >= t.Count || t[p].Kind != FTk.RParen)
                throw new FindSyntaxException("invalid expression; I was expecting to find a ')' somewhere but did not see one.");
            p++;
            return inner;
        }
        if (tok.Kind == FTk.Leaf)
        {
            p++;
            return tok.Pred!;
        }
        throw new FindSyntaxException("invalid expression");
    }

    private static bool IsTermStart(FTk k) => k is FTk.Leaf or FTk.Not or FTk.LParen;

    // ───────────── leaf helpers ─────────────

    /// <summary>
    /// <c>-size [+-]N[bcwkMG]</c>: no suffix = 512-byte blocks; the size is rounded UP to the unit for every comparison.
    /// GNU errors: <c>invalid -size type `B'</c> for an unknown/extra suffix character, <c>invalid argument</c> for no digits.
    /// </summary>
    internal static bool TryParseSize(string expr, out char op, out long units, out long unitSize, out string? error)
    {
        op = '='; units = 0; unitSize = 512; error = null;
        int i = 0;
        if (i < expr.Length && (expr[i] == '+' || expr[i] == '-')) { op = expr[i]; i++; }
        int digitsStart = i;
        while (i < expr.Length && char.IsAsciiDigit(expr[i])) i++;
        if (i == digitsStart) { error = $"invalid argument `{expr}' to `-size'"; return false; }
        // No real file is larger than long.MaxValue, so an overflowing count simply saturates.
        if (!long.TryParse(expr.AsSpan(digitsStart, i - digitsStart), NumberStyles.None, CultureInfo.InvariantCulture, out units)) units = long.MaxValue;
        if (i < expr.Length && "bcwkMG".IndexOf(expr[i]) >= 0)
        {
            unitSize = expr[i] switch { 'c' => 1L, 'w' => 2L, 'k' => 1024L, 'M' => 1048576L, 'G' => 1073741824L, _ => 512L };
            i++;
        }
        if (i < expr.Length) { error = $"invalid -size type `{expr[i]}'"; return false; }
        return true;
    }

    private static bool SizeMatch(FindCtx c, char op, long units, long unitSize)
    {
        long fileSize = c.IsDir ? 0L : c.Eff is FileInfo fi ? SafeLength(fi) : 0L;
        if (c.LinkLeaf) fileSize = c.Stat.Size;
        long fileUnits = fileSize == 0 ? 0 : 1 + ((fileSize - 1) / unitSize);
        return op switch
        {
            '+' => fileUnits > units,
            '-' => fileUnits < units,
            _ => fileUnits == units,
        };
    }

    private static long SafeLength(FileInfo fi) { try { return fi.Length; } catch { return 0; } }

    private static bool EmptyMatch(FindCtx c)
    {
        if (c.IsDir)
        {
            try { return !((DirectoryInfo)c.Eff).EnumerateFileSystemInfos().Any(); }
            catch { return false; }
        }
        return c.Eff is FileInfo fi && SafeLength(fi) == 0;
    }

    private static string FsTypeOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? "" : new DriveInfo(root).DriveFormat;
        }
        catch { return ""; }
    }

    // ── glob match (fnmatch semantics) ──

    private static bool GlobMatch(string name, string pattern, bool ci) =>
        WildcardPattern.Get(pattern, ci ? WildcardOptions.IgnoreCase : WildcardOptions.None).IsMatch(name);

    // -path / -ipath / -lname: matched against the whole text; unlike -name a '*' may span '/'.
    private static bool PathGlobMatch(string path, string pattern, bool ci) =>
        WildcardPattern.Get(pattern, ci ? WildcardOptions.IgnoreCase : WildcardOptions.None).IsMatch(path);

    // ── FindEntry metadata (the typed object's Permissions/Owner/Group) ──

    private sealed class FindFileInfo
    {
        public long SizeBytes;
        public string Permissions = string.Empty;
        public int LinkCount = 1;
        public string Owner = string.Empty;
        public string Group = string.Empty;
    }

    private static FindFileInfo BuildFileInfo(FileSystemInfo item)
    {
        var info = new FindFileInfo();
        var attrs = item.Attributes;
        bool isDir = item is DirectoryInfo;
        bool isLink = (attrs & FileAttributes.ReparsePoint) != 0;
        char typeChar = isDir ? 'd' : (isLink ? 'l' : '-');

        if (OperatingSystem.IsWindows())
        {
            bool readOnly = (attrs & FileAttributes.ReadOnly) != 0;
            bool isExec = isDir || IsExecExtension(item.Extension);
            string r = "r";
            string w = readOnly ? "-" : "w";
            string x = isExec ? "x" : "-";
            info.Permissions = $"{typeChar}{r}{w}{x}{r}-{x}{r}-{x}";
            info.Owner = Environment.GetEnvironmentVariable("USERNAME") ?? string.Empty;
            info.Group = info.Owner;
        }
        else
        {
            int mode = (int)item.UnixFileMode;
            info.Permissions = $"{typeChar}{ConvertToPermissionString(mode)}";
            try
            {
                bool isMac = OperatingSystem.IsMacOS();
                var statArgs = isMac
                    ? new[] { "-f", "%Su %Sg", item.FullName }
                    : new[] { "-c", "%U %G", item.FullName };
                var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/stat")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var a in statArgs) psi.ArgumentList.Add(a);
                // Bounded spawn + concurrent drain + kill-tree on timeout so a hung /usr/bin/stat cannot wedge the host.
                string statOut = BashRuntime.RunChildProcess(psi).Stdout.Trim();
                if (statOut.Length > 0)
                {
                    var parts = statOut.Split(new[] { ' ' }, 2);
                    info.Owner = parts[0];
                    info.Group = parts.Length > 1 ? parts[1] : string.Empty;
                }
            }
            catch
            {
                // /usr/bin/stat unavailable — match the oracle's 2>$null swallow.
            }
        }

        info.SizeBytes = isDir ? 4096L : ((FileInfo)item).Length;
        info.LinkCount = 1;
        return info;
    }

    private static bool IsExecExtension(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return false;
        return ext.ToLowerInvariant() switch
        {
            ".exe" or ".bat" or ".cmd" or ".ps1" or ".sh" or ".com" => true,
            _ => false,
        };
    }

    private static string ConvertToPermissionString(int mode)
    {
        var sb = new StringBuilder(9);
        int[] bits = { 256, 128, 64, 32, 16, 8, 4, 2, 1 };
        char[] chars = { 'r', 'w', 'x', 'r', 'w', 'x', 'r', 'w', 'x' };
        for (int k = 0; k < 9; k++) sb.Append((mode & bits[k]) != 0 ? chars[k] : '-');
        return sb.ToString();
    }

    // ── error sink ──

    /// <summary>
    /// GNU find: the path list ends at the first word that starts an expression — anything beginning with
    /// a dash, or one of <c>( ) ! ,</c>.
    /// </summary>
    internal static bool StartsExpression(string word) =>
        word.Length > 0 && (word[0] == '-' || word is "(" or ")" or "!" or ",");

    private void EmitError(string message)
    {
        _exit = 1;
        FileSystemHelpers.WriteBashError(this, message);
    }
}
