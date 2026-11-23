using System.Reflection;
using System.Text.RegularExpressions;

namespace PsBash.Testing;

/// <summary>
/// Finds test classes that write the PROCESS environment unsafely. The environment is one per process and xunit runs
/// test classes in parallel, so a class that sets <c>TERM</c> or <c>GREP_COLORS</c> changes what every class running
/// beside it reads (native <c>tput</c> printed the wrong bytes; grep's <c>--color</c> lost its <c>\e[K</c>).
/// <para>
/// A test class (one with a <c>[Fact]</c>/<c>[Theory]</c>, inherited or derived attributes included) is walked from
/// every method of it, its bases in the assembly and its nested non-test types (lambdas, local functions, state machines),
/// following calls transitively inside the assembly. A violation is:
/// <list type="bullet">
/// <item>a call to <c>Environment.SetEnvironmentVariable</c> (in C#, or as <c>[Environment]::SetEnvironmentVariable</c>
/// in a script string): the write must go through <see cref="ProcessEnv"/>, which restores the value from before the
/// test instead of deleting it;</item>
/// <item>a class that uses <see cref="ProcessEnv"/>, or (<c>scanScripts</c>) runs a script with <c>$env:NAME = ...</c>
/// / <c>Remove-Item env:NAME</c>, outside a collection with <c>DisableParallelization = true</c> (or an assembly with
/// test parallelization off);</item>
/// <item>(<c>scanScripts</c>) a script-written NAME that is not also a string literal of the class while it uses
/// <see cref="ProcessEnv"/>, i.e. not wrapped in <c>ProcessEnv.Preserve("NAME")</c>; a class that calls
/// <see cref="ProcessEnv.PreserveAll"/> covers every name, and is the only cover for a name built at run time.</item>
/// </list>
/// Writes the product makes on the test's behalf (a cmdlet's <c>read x</c> through the variable store) are product
/// semantics and are not seen (<see cref="ProcessEnv.PreserveAll"/> restores them too). <c>notWalked</c> types are
/// skipped: ones that write the environment before any test or as every class's identical baseline (fixtures, module
/// initializers), and ones whose <c>$env:</c> scripts run in a CHILD process (its own environment).
/// </para>
/// Dependency-light: xunit attributes are matched by full name.
/// </summary>
public static class ProcessEnvironmentGuard
{
    private static readonly Regex ScriptEnvAssign = new(
        @"\$(?:\{env:([A-Za-z_][A-Za-z0-9_]*)\}|env:([A-Za-z_][A-Za-z0-9_]*))\s*=(?!=)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ScriptEnvItem = new(
        @"\b(?:Remove|Set|Clear|New)-Item\b[^;|}\r\n]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ScriptEnvItemName = new(
        @"\benv:\\*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // A name built at run time ($"$env:{name} = 1", $"Remove-Item Env:{name}"): the literal ends where the name goes,
    // and for an assignment the method's next literal starts with the "=". Only PreserveAll covers it.
    private static readonly Regex ScriptEnvDynamicTail = new(
        @"\$\{?env:$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ScriptAssignHead = new(@"^\}?\s*=(?!=)", RegexOptions.CultureInvariant);

    private static readonly Regex ScriptEnvItemDynamic = new(
        @"\b(?:Remove|Set|Clear|New)-Item\b[^;|}\r\n]*\benv:\\*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const string DynamicName = "{name built at run time}";

    private static readonly Regex ScriptDirectWrite = new(
        @"\[(?:System\.)?Environment\]::SetEnvironmentVariable", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>One line per violation, naming the class and what to change. Empty when the assembly is clean.</summary>
    public static IReadOnlyList<string> Violations(Assembly assembly, bool scanScripts, params Type[] notWalked)
        => Violations(assembly, LoadableTypes(assembly).Where(t => !t.IsAbstract), scanScripts, notWalked);

    /// <summary>
    /// <see cref="Violations(Assembly, bool, Type[])"/> restricted to <paramref name="types"/> (abstract ones included),
    /// for the guard's own self-test.
    /// </summary>
    public static IReadOnlyList<string> Violations(Assembly assembly, IEnumerable<Type> types, bool scanScripts,
        params Type[] notWalked)
    {
        var runAlone = RunAloneCollections(assembly, out var assemblyRunsAlone);
        var exempt = new HashSet<Type>(notWalked);
        var facts = new Dictionary<(Module, int), MethodFacts>();
        var violations = new List<string>();

        foreach (var type in types)
        {
            if (IsCompilerGenerated(type) || IsWithin(type, exempt) || !IsTestClass(type)) continue;

            var direct = new SortedSet<string>(StringComparer.Ordinal);
            var scriptNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var literals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usesScope = false;
            var restoresAll = false;

            var queue = new Queue<MethodBase>(Roots(type));
            var seen = new HashSet<(Module, int)>();
            while (queue.Count > 0)
            {
                var method = queue.Dequeue();
                if (!seen.Add((method.Module, method.MetadataToken))) continue;
                if (method.DeclaringType is { } dt && IsWithin(dt, exempt)) continue;

                var key = (method.Module, method.MetadataToken);
                if (!facts.TryGetValue(key, out var f)) facts[key] = f = Inspect(method, assembly, scanScripts);
                foreach (var d in f.DirectWrites) direct.Add(d);
                scriptNames.UnionWith(f.ScriptNames);
                literals.UnionWith(f.Literals);
                usesScope |= f.UsesScope;
                restoresAll |= f.RestoresAll;
                foreach (var callee in f.LocalCallees) queue.Enqueue(callee);
            }

            var name = type.FullName ?? type.Name;
            if (direct.Count > 0)
                violations.Add($"{name}: writes the process environment directly ({string.Join(", ", direct)}) - "
                    + "use PsBash.Testing.ProcessEnv.Set/Preserve, which restores the value from before the test");

            var writes = usesScope || scriptNames.Count > 0 || direct.Count > 0;
            if (writes && !assemblyRunsAlone && !(CollectionOf(type) is { } c && runAlone.Contains(c)))
                violations.Add($"{name}: writes the process environment but runs beside other classes - put it in a "
                    + "collection with DisableParallelization = true (Cmdlets.Tests: [Collection(ProcessEnvironmentCollection.Name)])");

            var uncovered = restoresAll ? new List<string>()
                : scriptNames.Where(n => n == DynamicName || !usesScope || !literals.Contains(n)).ToList();
            if (uncovered.Count > 0)
                violations.Add($"{name}: a script it runs writes $env:{string.Join(", $env:", uncovered)} - wrap the run in "
                    + (uncovered.Contains(DynamicName)
                        ? "using var _ = ProcessEnv.PreserveAll()"
                        : $"using var _ = ProcessEnv.Preserve(\"{string.Join("\", \"", uncovered)}\") (or ProcessEnv.PreserveAll())")
                    + " so the prior value comes back");
        }

        violations.Sort(StringComparer.Ordinal);
        return violations;
    }

    private sealed record MethodFacts(
        List<string> DirectWrites, List<string> ScriptNames, List<string> Literals, bool UsesScope, bool RestoresAll,
        List<MethodBase> LocalCallees);

    private static MethodFacts Inspect(MethodBase method, Assembly assembly, bool scanScripts)
    {
        var direct = new List<string>();
        var scriptNames = new List<string>();
        var literals = new List<string>();
        var usesScope = false;
        var restoresAll = false;
        var local = new List<MethodBase>();
        var where = $"{method.DeclaringType?.Name}.{method.Name}";

        foreach (var callee in IlScan.Callees(method))
        {
            var declaring = callee.DeclaringType;
            if (declaring == typeof(ProcessEnv))
            {
                usesScope = true;
                restoresAll |= callee.Name == nameof(ProcessEnv.PreserveAll);
            }
            else if (declaring?.FullName == "System.Environment" && callee.Name == "SetEnvironmentVariable") direct.Add(where);
            else if (callee.Module.Assembly == assembly) local.Add(callee);
        }

        string? previous = null;
        foreach (var s in IlScan.Strings(method))
        {
            literals.Add(s);
            if (!scanScripts) continue;
            if (ScriptEnvItemDynamic.IsMatch(s)
                || (previous is not null && ScriptEnvDynamicTail.IsMatch(previous) && ScriptAssignHead.IsMatch(s)))
                scriptNames.Add(DynamicName);
            previous = s;
            if (ScriptDirectWrite.IsMatch(s)) direct.Add(where + " (script)");
            foreach (Match m in ScriptEnvAssign.Matches(s))
                scriptNames.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            foreach (Match item in ScriptEnvItem.Matches(s))
                foreach (Match n in ScriptEnvItemName.Matches(item.Value))
                    scriptNames.Add(n.Groups[1].Value);
        }

        return new MethodFacts(direct, scriptNames, literals, usesScope, restoresAll, local);
    }

    // Every method of the class, its bases inside the assembly, and the types nested in them (lambda closures,
    // local functions, iterator/async state machines, helper records).
    private static IEnumerable<MethodBase> Roots(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                 BindingFlags.Static | BindingFlags.DeclaredOnly;
        var pending = new Stack<Type>();
        for (var t = type; t is not null && t.Assembly == type.Assembly; t = t.BaseType) pending.Push(t);
        while (pending.Count > 0)
        {
            var t = pending.Pop();
            foreach (var m in t.GetMethods(all)) yield return m;
            foreach (var c in t.GetConstructors(all)) yield return c;
            foreach (var n in t.GetNestedTypes(all))
                if (!IsTestClass(n)) pending.Push(n); // a nested test class is checked on its own
        }
    }

    private static bool IsTestClass(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (var m in type.GetMethods(all))
            foreach (var a in m.CustomAttributes)
                for (var t = a.AttributeType; t is not null; t = t.BaseType)
                    if (t.FullName is "Xunit.FactAttribute") return true;
        return false;
    }

    private static string? CollectionOf(Type type)
    {
        foreach (var a in type.GetCustomAttributesData())
            if (a.AttributeType.FullName == "Xunit.CollectionAttribute" && a.ConstructorArguments.Count == 1
                && a.ConstructorArguments[0].Value is string name)
                return name;
        return null;
    }

    private static HashSet<string> RunAloneCollections(Assembly assembly, out bool assemblyRunsAlone)
    {
        assemblyRunsAlone = assembly.GetCustomAttributesData().Any(a =>
            a.AttributeType.FullName == "Xunit.CollectionBehaviorAttribute"
            && a.NamedArguments.Any(n => n.MemberName == "DisableTestParallelization" && n.TypedValue.Value is true));

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in LoadableTypes(assembly))
            foreach (var a in type.GetCustomAttributesData())
                if (a.AttributeType.FullName == "Xunit.CollectionDefinitionAttribute"
                    && a.ConstructorArguments.Count >= 1 && a.ConstructorArguments[0].Value is string name
                    && a.NamedArguments.Any(n => n.MemberName == "DisableParallelization" && n.TypedValue.Value is true))
                    names.Add(name);
        return names;
    }

    private static bool IsWithin(Type type, HashSet<Type> set)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
            if (set.Contains(t)) return true;
        return false;
    }

    private static bool IsCompilerGenerated(Type type) => type.Name.Contains('<');

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null)!; }
    }
}
