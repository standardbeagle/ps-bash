using System.Reflection;

namespace PsBash.Testing;

/// <summary>
/// Finds tests that can skip but cannot be skipped.
/// <para>
/// Xunit.SkippableFact turns a thrown <c>Xunit.SkipException</c> (what <c>Skip.If</c> /
/// <c>Skip.IfNot</c> throw) into a skip only for a test marked <c>[SkippableFact]</c> or
/// <c>[SkippableTheory]</c>. Under a plain <c>[Fact]</c> / <c>[Theory]</c> the same throw is
/// a FAILURE, so the test passes on the machine that wrote it and fails wherever its
/// precondition is missing (a Linux host with no pwsh, a box with no WSL, ...).
/// </para>
/// <para>
/// The guard walks every method marked exactly <c>[Fact]</c>/<c>[Theory]</c>, plus the
/// constructor and <c>InitializeAsync</c> of its class (they run per test), and scans the IL
/// for a call to <c>Xunit.Skip.If</c>/<c>IfNot</c> or a <c>new Xunit.SkipException</c>. Calls,
/// delegates (<c>ldftn</c>) and async/iterator state machines inside the same assembly are
/// followed transitively, depth- and cycle-limited, so a skip buried in a shared helper is
/// found too. Dependency-light: attributes and xunit types are matched by full name, so this
/// assembly needs no xunit reference.
/// </para>
/// </summary>
public static class SkipUsageGuard
{
    public const int DefaultMaxDepth = 10;

    private static readonly HashSet<string> PlainTestAttributes = new(StringComparer.Ordinal)
    {
        "Xunit.FactAttribute",
        "Xunit.TheoryAttribute",
    };

    /// <summary>
    /// One line per plain <c>[Fact]</c>/<c>[Theory]</c> that can reach a skip, naming the
    /// test and the call chain to the skip. Empty when the assembly is clean.
    /// </summary>
    public static IReadOnlyList<string> Violations(Assembly assembly, int maxDepth = DefaultMaxDepth)
        // Abstract classes are never run by xunit; a [Fact] declared on one is checked through
        // each concrete subclass (GetMethods returns inherited methods), with that subclass's ctor.
        => Violations(assembly, LoadableTypes(assembly).Where(t => !t.IsAbstract), maxDepth);

    /// <summary>
    /// <see cref="Violations(Assembly, int)"/> restricted to <paramref name="types"/> (abstract
    /// ones included) — the guard's own self-test uses this to check fixture classes xunit never runs.
    /// </summary>
    public static IReadOnlyList<string> Violations(Assembly assembly, IEnumerable<Type> types, int maxDepth = DefaultMaxDepth)
    {
        var walker = new Walker(assembly, maxDepth);
        var violations = new List<string>();
        foreach (var type in types)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;
            List<MethodBase>? perTestSetup = null;
            foreach (var method in type.GetMethods(all))
            {
                var attr = PlainTestAttribute(method);
                if (attr is null) continue;

                perTestSetup ??= PerTestSetup(type);
                foreach (var root in perTestSetup.Prepend(method))
                {
                    var chain = walker.ChainToSkip(root);
                    if (chain is null) continue;
                    if (root != method) chain = [Walker.Describe(root) + " (runs per test)", .. chain];
                    violations.Add(
                        $"{type.FullName}.{method.Name}: [{attr}] reaches {string.Join(" -> ", chain)}" +
                        " — mark it [Skippable" + attr + "]");
                    break;
                }
            }
        }
        violations.Sort(StringComparer.Ordinal);
        return violations;
    }

    private static string? PlainTestAttribute(MethodInfo method)
    {
        foreach (var data in method.CustomAttributes)
        {
            var name = data.AttributeType.FullName;
            if (name is not null && PlainTestAttributes.Contains(name))
                return data.AttributeType.Name[..^"Attribute".Length];
        }
        return null;
    }

    // xunit constructs the class (and runs IAsyncLifetime.InitializeAsync) once per test, and a
    // SkipException thrown there fails a plain [Fact] just like one thrown in the body.
    private static List<MethodBase> PerTestSetup(Type type)
    {
        var setup = new List<MethodBase>();
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            if (t.Assembly != type.Assembly) break;
            if (t.IsAbstract && t.IsSealed) break; // static class: no per-test construction
            setup.AddRange(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
            var init = t.GetMethod("InitializeAsync",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                Type.EmptyTypes);
            if (init is not null) setup.Add(init);
        }
        return setup;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null)!; }
    }

    private sealed class Walker(Assembly assembly, int maxDepth)
    {
        // Memo: method -> chain to a skip (null = proven clean). Only complete answers are stored.
        private readonly Dictionary<(Module, int), List<string>?> _memo = new();
        private readonly HashSet<(Module, int)> _onStack = new();

        public List<string>? ChainToSkip(MethodBase root) => Walk(root, 0, out _);

        private List<string>? Walk(MethodBase method, int depth, out bool complete)
        {
            complete = true;
            var key = (method.Module, method.MetadataToken);
            if (_memo.TryGetValue(key, out var known)) return known;
            if (depth > maxDepth || !_onStack.Add(key)) { complete = false; return null; }
            try
            {
                List<string>? found = null;
                foreach (var callee in IlScan.Callees(method))
                {
                    if (IsSkipCall(callee))
                    {
                        found = [$"{callee.DeclaringType!.FullName}.{(callee is ConstructorInfo ? "ctor" : callee.Name)}"];
                        break;
                    }
                    if (callee.Module.Assembly != assembly) continue;
                    var sub = Walk(callee, depth + 1, out var subComplete);
                    complete &= subComplete;
                    if (sub is not null)
                    {
                        found = [Describe(callee), .. sub];
                        complete = true;
                        break;
                    }
                }
                if (found is not null || complete) _memo[key] = found;
                return found;
            }
            finally { _onStack.Remove(key); }
        }

        public static string Describe(MethodBase m) => $"{m.DeclaringType?.Name}.{m.Name}";

        private static bool IsSkipCall(MethodBase callee)
        {
            var declaring = callee.DeclaringType?.FullName;
            return callee switch
            {
                ConstructorInfo => declaring == "Xunit.SkipException",
                _ => declaring == "Xunit.Skip" && callee.Name is "If" or "IfNot",
            };
        }
    }
}
