using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace PsBash.Core.Parser;

/// <summary>
/// Bounds the recursion of the hand-written recursive-descent scanners and parsers so NO input can
/// overflow the stack.
/// <para>
/// A .NET StackOverflow cannot be caught: it kills the process. The transpiler runs inside the
/// shared host for <c>eval</c>/<c>source</c>/<c>bash -c</c> and in the arithmetic runtime, so
/// <c>eval "$(cat deep.sh)"</c> with ~5000 levels of <c>$(</c>, <c>((</c>, <c>${x:-…}</c> or
/// <c>if</c> killed the daemon and every session on it. Each recursion cycle enters this guard
/// once; past <see cref="MaxDepth"/> levels, or when the current thread is near the end of its
/// stack (<see cref="RuntimeHelpers.TryEnsureSufficientExecutionStack"/> — whatever its size), it
/// throws an ordinary parse error instead. The depth cap makes the limit deterministic and keeps
/// the AST shallow enough for the (unguarded) emitter and evaluator that walk it afterwards; the
/// stack probe is the backstop for threads with small stacks.
/// </para>
/// <see cref="WithStackFallback{T}"/> runs a transpile inline and, only if the caller's stack ran out
/// first, again on a large-stack worker (<see cref="OnLargeStack{T}"/>), so any input under the cap
/// works on any thread without paying a thread hop on the common path.
/// </summary>
public static class NestingGuard
{
    /// <summary>Nesting levels accepted per thread. Real scripts nest tens of levels.</summary>
    public const int MaxDepth = 1000;

    /// <summary>Stack for <see cref="OnLargeStack{T}"/>: reserved, not committed, so it costs address
    /// space only. Comfortably holds <see cref="MaxDepth"/> levels through lexer + parser + emitter.</summary>
    internal const int LargeStackBytes = 256 * 1024 * 1024;

    [ThreadStatic] private static int t_depth;
    [ThreadStatic] private static bool t_onLargeStack;

    internal const string TooDeepMessage = "nesting too deep";

    /// <summary>Enter one nesting level of the bash lexer/parser; dispose to leave it.</summary>
    public static Scope Enter() => EnterCore(arithmetic: false);

    /// <summary>Enter one nesting level of the arithmetic parser (its own exception type, which every
    /// arithmetic consumer already converts).</summary>
    public static Scope EnterArithmetic() => EnterCore(arithmetic: true);

    [ThreadStatic] private static bool t_stackExhausted;

    /// <summary>
    /// The stack probe alone, for recursion that is already depth-bounded by the parser (the emitter's
    /// walk of an accepted AST): never overflow, even on a small stack.
    /// </summary>
    public static void EnsureStack()
    {
        if (RuntimeHelpers.TryEnsureSufficientExecutionStack()) return;
        t_stackExhausted = true;
        throw new ParseException($"{TooDeepMessage} (out of stack)", 1, 1, "nesting");
    }

    /// <summary>
    /// Run <paramref name="work"/> inline; if it failed because THIS thread's stack ran out (not the
    /// depth cap), run it again on a large-stack worker. Inline is the fast path — a cross-thread hop
    /// costs milliseconds on an STA caller (every PowerShell pipeline thread on Windows) — and the
    /// retry keeps any input within <see cref="MaxDepth"/> working on any thread.
    /// </summary>
    public static T WithStackFallback<T>(Func<T> work)
    {
        if (t_onLargeStack) return work();
        t_stackExhausted = false;
        try
        {
            return work();
        }
        catch (Exception ex) when (t_stackExhausted && ex is ParseException or BashArithmeticParseException)
        {
            t_stackExhausted = false;
            return OnLargeStack(work);
        }
    }

    private static Scope EnterCore(bool arithmetic)
    {
        bool stackOk = RuntimeHelpers.TryEnsureSufficientExecutionStack();
        if (!stackOk) t_stackExhausted = true;
        if (t_depth >= MaxDepth || !stackOk)
        {
            string message = $"{TooDeepMessage} (more than {MaxDepth} levels, or out of stack)";
            throw arithmetic
                ? new BashArithmeticParseException(message + " in arithmetic expression")
                : new ParseException(message, 1, 1, "nesting");
        }
        t_depth++;
        return default;
    }

    /// <summary>
    /// Operators allowed in one flat chain: <c>&amp;&amp;</c>/<c>||</c> lists, <c>test</c>/<c>[[ ]]</c>
    /// operators, an arithmetic operator run. Breadth matters as much as depth: the emitter lowers a
    /// flat chain to a PowerShell binary-operator chain, which PowerShell parses LEFT-DEEP and whose
    /// compiler (<c>VariableAnalysis</c>/<c>Compiler.VisitBinaryExpression</c>) recurses per operand —
    /// <c>[ a -o a … ]</c> with ~48k operands overflowed PowerShell's own 10 MB pipeline thread and
    /// killed the host. Real scripts use tens.
    /// </summary>
    public const int MaxChainLength = 4096;

    /// <summary>Stages allowed in one pipeline (a 2000-stage <c>cat | cat | …</c> killed the host the
    /// same way). Real pipelines use a handful.</summary>
    public const int MaxPipelineStages = 256;

    internal const string TooLongMessage = "too long";

    /// <summary>Reject a flat construct past <paramref name="limit"/> elements (see <see cref="MaxChainLength"/>).</summary>
    public static void CheckBreadth(int count, int limit, string what)
    {
        if (count > limit)
            throw new ParseException($"{what} {TooLongMessage} (more than {limit})", 1, 1, "breadth");
    }

    /// <summary><see cref="CheckBreadth"/> for the arithmetic parser (its own exception type).</summary>
    public static void CheckArithmeticBreadth(int count, int limit, string what)
    {
        if (count > limit)
            throw new BashArithmeticParseException($"{what} {TooLongMessage} (more than {limit}) in arithmetic expression");
    }

    /// <summary>Leaves the level entered by <see cref="Enter"/>. A struct: no allocation per level.</summary>
    public readonly struct Scope : IDisposable
    {
        public void Dispose() => t_depth--;
    }

    /// <summary>
    /// Run <paramref name="work"/> on a dedicated thread with a <see cref="LargeStackBytes"/> stack
    /// (inline when already on one), rethrowing its exception with the original stack trace. Callers
    /// arrive on threads of every size — the AOT launcher's 1 MB main thread, a PowerShell pipeline
    /// thread, a thread-pool thread — and the emitter's recursion is bounded only by the AST depth,
    /// so the work gets one known, ample stack regardless.
    /// </summary>
    public static T OnLargeStack<T>(Func<T> work)
    {
        if (t_onLargeStack) return work();
        // Reused workers: creating a thread per transpile cost 7-60 ms each (the interactive shell
        // transpiles every line). A pool keeps the steady state to one handoff, and a slow transpile in
        // one session never blocks another's (a busy pool grows instead of queueing).
        var worker = s_idle.TryTake(out var idle) ? idle : new LargeStackWorker();
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var culture = Thread.CurrentThread.CurrentCulture;
        var uiCulture = Thread.CurrentThread.CurrentUICulture;
        using var done = new ManualResetEventSlim(false);
        worker.Post(() =>
        {
            // The caller's culture flows (number/format behaviour must not depend on which thread ran).
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = uiCulture;
            try { result = work(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { done.Set(); }
        });
        done.Wait();
        if (s_idle.Count < MaxIdleWorkers) s_idle.Add(worker); else worker.Post(null);
        failure?.Throw();
        return result;
    }

    private const int MaxIdleWorkers = 4;
    private static readonly System.Collections.Concurrent.ConcurrentBag<LargeStackWorker> s_idle = new();

    /// <summary>A long-lived background thread with a <see cref="LargeStackBytes"/> stack that runs one
    /// posted item at a time; posting <c>null</c> ends it.</summary>
    private sealed class LargeStackWorker
    {
        private readonly SemaphoreSlim _ready = new(0);
        private Action? _next;

        public LargeStackWorker()
        {
            new Thread(Loop, LargeStackBytes) { IsBackground = true, Name = "ps-bash transpile" }.Start();
        }

        public void Post(Action? item)
        {
            Volatile.Write(ref _next, item);
            _ready.Release();
        }

        private void Loop()
        {
            t_onLargeStack = true;
            while (true)
            {
                _ready.Wait();
                var item = Interlocked.Exchange(ref _next, null);
                if (item is null) return;
                item();
            }
        }
    }
}
