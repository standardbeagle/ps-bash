using System.Runtime.CompilerServices;
using PsBash.Core.Parser.Ast;
using PsBash.Core.Transpiler;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    // The stdin probe (docs/specs/emitter-strategy.md "Stage stdin", rule 4). A construct that must decide whether its
    // body can consume the stdin it inherits -- a read loop (stream per record, or read the shared cursor), a function
    // body (take the pipe it is called with), the whole script (ask the launcher for its stdin) -- emits the body inside
    // a stdin scope through ProbeStdinScope and asks ONE question: "is this body PROVEN not to read?". Everything the
    // emitter cannot see into counts as a reader: a function of another unit, a program, eval / source text, a run-time
    // command name. The old question was the opposite ("can I see a reader?"), asked in three places with three marker
    // lists, so a reader behind a function call, a sourced file, a background job or a program read nothing.

    /// <summary>
    /// Raised while a stdin scope is emitted by a command that may consume the scope's stdin although its emitted text
    /// shows no reader (<see cref="PsBuild.ReadsSharedStdin"/>): see <see cref="NoteStdinUse"/>.
    /// </summary>
    [ThreadStatic]
    private static bool _stdinOpaque;

    /// <summary>
    /// Emit <paramref name="emit"/> inside a stdin scope and say whether what it emitted may read that stdin. False only
    /// when every command in it is known not to: the caller may then leave the stdin alone (stream a read loop's
    /// records, keep a function a plain one). The ONLY way the emitter may answer this question
    /// (StageStdinMatrixTests pins every reader kind to it).
    /// </summary>
    private static (string Text, bool Reads) ProbeStdinScope(Func<string> emit)
    {
        bool outer = _stdinOpaque;
        var outerSealed = _stdinSealed;
        _stdinOpaque = false;
        _stdinSealed = null;
        _stdinProbeDepth++;
        try
        {
            string scoped = WithStdinScope(true, emit);
            return (scoped, _stdinOpaque || PsBuild.ReadsSharedStdin(WithoutSealed(scoped)));
        }
        finally
        {
            _stdinOpaque |= outer;
            var inner = _stdinSealed;
            _stdinSealed = outerSealed;
            _stdinProbeDepth--;
            // The enclosing probe's text holds these fragments too.
            if (inner is not null && _stdinProbeDepth > 0) (_stdinSealed ??= new List<string>()).AddRange(inner);
        }
    }

    /// <summary>Above 0 while a probe is collecting.</summary>
    [ThreadStatic]
    private static int _stdinProbeDepth;

    /// <summary>
    /// <see cref="TranspileWithLauncherStdin(string, TranspileContext)"/> with the probe's verdict for the whole script:
    /// <paramref name="reads"/> is false only when no command of it may read stdin, so the launcher need forward none.
    /// </summary>
    public static string? TranspileProbingLauncherStdin(string bash, TranspileContext context, out bool reads)
    {
        bool outer = _stdinOpaque;
        var outerSealed = _stdinSealed;
        _stdinOpaque = false;
        _stdinSealed = null;
        _stdinProbeDepth++;
        try
        {
            string? text = TranspileWithLauncherStdin(bash, context);
            reads = text is not null && (_stdinOpaque || PsBuild.ReadsSharedStdin(WithoutSealed(text)));
            return text;
        }
        finally { _stdinOpaque = outer; _stdinSealed = outerSealed; _stdinProbeDepth--; }
    }

    /// <summary>Text emitted by constructs that have a stdin of their OWN (<see cref="SealedFromStdinProbe"/>) since the innermost probe began.</summary>
    [ThreadStatic]
    private static List<string>? _stdinSealed;

    /// <summary>
    /// Emit a construct whose readers read a stdin of its OWN, never the enclosing scope's (<c>cmd &lt; /dev/null</c>): what
    /// it emits raises nothing in the enclosing probe, and its text -- which names the cursor it installs -- is left out
    /// of the probe's text scan.
    /// </summary>
    private static string SealedFromStdinProbe(Func<string> emit)
    {
        bool outer = _stdinOpaque;
        string text;
        try { text = emit(); }
        finally { _stdinOpaque = outer; }
        if (_stdinProbeDepth > 0) (_stdinSealed ??= new List<string>()).Add(text);
        return text;
    }

    private static string WithoutSealed(string text)
    {
        if (_stdinSealed is null) return text;
        foreach (var fragment in _stdinSealed)
            if (fragment.Length > 0) text = text.Replace(fragment, "", StringComparison.Ordinal);
        return text;
    }

    /// <summary>
    /// Called for every simple command emitted inside a stdin scope: raises <see cref="_stdinOpaque"/> unless the command
    /// is proven not to read the scope's stdin -- it has an input of its own (<c>&lt; f</c>, a here-doc), it is an
    /// assignment, or <see cref="StdinReaders.MayReadStdin"/> clears its name and operands. An unknown name is a
    /// reader, except a function of this unit whose one definition was itself proven not to read.
    /// </summary>
    private static void NoteStdinUse(Command.Simple simple)
    {
        if (_stdinOpaque || simple.Words.IsDefaultOrEmpty) return;
        if (!simple.HereDocs.IsDefaultOrEmpty || HasInputRedirect(simple.Redirects)) return;
        if (GetLiteralValue(simple.Words[0]) is not { } name) { _stdinOpaque = true; return; }
        if (StdinReaders.Classify(name) == StdinReaders.Kind.Native && IsStdinFreeFunction(name)) return;
        var args = new List<string?>(simple.Words.Length - 1);
        for (int i = 1; i < simple.Words.Length; i++)
            args.Add(GetLiteralValue(simple.Words[i]));
        if (StdinReaders.MayReadStdin(name, args)) _stdinOpaque = true;
    }

    /// <summary>What one unit knows about its functions and stdin; keyed by the unit's <see cref="_unitFunctions"/> set.</summary>
    private sealed class UnitStdinFacts
    {
        /// <summary>Function name to "its body was proven not to read stdin".</summary>
        public readonly Dictionary<string, bool> Free = new(StringComparer.Ordinal);
        /// <summary>How many definitions of each function name the unit holds (a name defined twice is never trusted).</summary>
        public Dictionary<string, int>? Definitions;
        public Command? DefinitionsRoot;
    }

    private static readonly ConditionalWeakTable<HashSet<string>, UnitStdinFacts> s_unitStdinFacts = new();

    /// <summary>Record the probe's verdict for a function definition of this unit.</summary>
    private static void NoteFunctionStdin(string name, bool reads)
    {
        if (_unitFunctions is null) return;
        s_unitStdinFacts.GetOrCreateValue(_unitFunctions).Free[name] = !reads;
    }

    /// <summary>
    /// True when <paramref name="name"/> is a function this unit has already defined, defines exactly once, and whose body
    /// was proven not to read stdin: calling it cannot consume the caller's stdin. A function of another unit (a sourced
    /// file, an earlier command line), one defined later, and one with two definitions are all unknown, so readers.
    /// </summary>
    private static bool IsStdinFreeFunction(string name)
    {
        if (_unitFunctions is null || !_unitFunctions.Contains(name)) return false;
        if (!s_unitStdinFacts.TryGetValue(_unitFunctions, out var facts)) return false;
        if (!facts.Free.TryGetValue(name, out bool free) || !free) return false;
        if (_rootCommand is null) return false;
        if (facts.Definitions is null || !ReferenceEquals(facts.DefinitionsRoot, _rootCommand))
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            VisitCommands(_rootCommand, c =>
            {
                if (c is Command.ShFunction f) counts[f.Name] = counts.GetValueOrDefault(f.Name) + 1;
            });
            facts.Definitions = counts;
            facts.DefinitionsRoot = _rootCommand;
        }
        return facts.Definitions.GetValueOrDefault(name) == 1;
    }
}
