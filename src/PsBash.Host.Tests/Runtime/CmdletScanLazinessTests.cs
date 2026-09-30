using System.Reflection;
using System.Runtime.Loader;
using PsBash.Core.Runtime;
using PsBash.Host.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace PsBash.Host.Tests.Runtime;

/// <summary>
/// Host startup must not load the Strata assemblies. They back only the styled-output cmdlets
/// (Format-Styled / Show-Styled / *Tui) and used to load into EVERY runspace because cmdlet
/// registration called <c>Assembly.GetTypes()</c> on PsBash.Cmdlets, which resolves the base
/// types of Strata-derived helper types. <see cref="CmdletTypeScanner"/> reads metadata first.
///
/// Oracle note (Directive 1): host-internal, no bash equivalent; hand-written asserts.
/// Strata is optional (src/Strata.props): with it off there is nothing to load, so these SKIP.
/// The scan runs in an isolated AssemblyLoadContext so the result cannot be polluted by other
/// tests that legitimately run Format-Styled in the shared xunit process.
/// </summary>
[Collection("SdkHost")]
public class CmdletScanLazinessTests
{
    private readonly ITestOutputHelper _out;

    public CmdletScanLazinessTests(ITestOutputHelper output) => _out = output;

    private static string CmdletsDir()
    {
        ModuleExtractor.ExtractEmbedded();
        return Path.GetDirectoryName(ModuleExtractor.GetCmdletsDllPath())!;
    }

    private static bool StrataBuiltIn(string dir) =>
        Directory.GetFiles(dir, "Strata.*.dll").Length > 0;

    /// <summary>Loads PsBash.Cmdlets and its private deps in a throwaway context; SMA/BCL stay shared.</summary>
    private sealed class IsolatedContext : AssemblyLoadContext
    {
        private readonly string _dir;
        public IsolatedContext(string dir) : base("cmdlet-scan-probe", isCollectible: true) => _dir = dir;

        protected override Assembly? Load(AssemblyName name)
        {
            var n = name.Name ?? "";
            if (n.StartsWith("System.", StringComparison.Ordinal) || n.StartsWith("Microsoft.", StringComparison.Ordinal)
                || n == "netstandard" || n == "mscorlib")
                return null; // shared default context (SMA must be the same Cmdlet base type)
            var path = Path.Combine(_dir, n + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private static string[] StrataLoaded(AssemblyLoadContext ctx) =>
        ctx.Assemblies.Select(a => a.GetName().Name ?? "")
            .Where(n => n.StartsWith("Strata.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [SkippableFact]
    public void EagerGetTypesScan_LoadsStrata_ProvingTheProbeSeesTheRegression()
    {
        var dir = CmdletsDir();
        Skip.IfNot(StrataBuiltIn(dir), "Strata is not built in (UseStrata=false): nothing to load.");

        var ctx = new IsolatedContext(dir);
        try
        {
            var asm = ctx.LoadFromAssemblyPath(Path.Combine(dir, "PsBash.Cmdlets.dll"));
            CmdletTypeScanner.FromAllTypes(asm);

            var loaded = StrataLoaded(ctx);
            _out.WriteLine("eager GetTypes() loaded: " + string.Join(", ", loaded));
            Assert.NotEmpty(loaded); // if this ever goes empty the lazy test below proves nothing
        }
        finally { ctx.Unload(); }
    }

    [SkippableFact]
    public void MetadataScan_FindsEveryCmdlet_WithoutLoadingStrata()
    {
        var dir = CmdletsDir();
        Skip.IfNot(StrataBuiltIn(dir), "Strata is not built in (UseStrata=false): nothing to load.");

        var ctx = new IsolatedContext(dir);
        try
        {
            var asm = ctx.LoadFromAssemblyPath(Path.Combine(dir, "PsBash.Cmdlets.dll"));
            var lazy = CmdletTypeScanner.FindCmdletTypes(asm);

            var loaded = StrataLoaded(ctx);
            _out.WriteLine("metadata scan loaded: [" + string.Join(", ", loaded) + "]");
            Assert.Empty(loaded);

            // The Strata-backed cmdlets are still registered (they just have not loaded Strata yet).
            var names = lazy.Select(t => $"{t.Attribute.VerbName}-{t.Attribute.NounName}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("Format-Styled", names);
            Assert.Contains("Show-Styled", names);
        }
        finally { ctx.Unload(); }
    }

    [SkippableFact]
    public void MetadataScan_RegistersTheSameCmdletsAsTheEagerScan()
    {
        // Runs whether or not Strata is built in: the lazy scan must never lose or invent a cmdlet.
        var dir = CmdletsDir();
        var ctx = new IsolatedContext(dir);
        try
        {
            var asm = ctx.LoadFromAssemblyPath(Path.Combine(dir, "PsBash.Cmdlets.dll"));
            static string[] Names(IEnumerable<(Type Type, System.Management.Automation.CmdletAttribute Attribute)> s) =>
                s.Select(t => t.Type.FullName + "=" + t.Attribute.VerbName + "-" + t.Attribute.NounName)
                 .OrderBy(x => x, StringComparer.Ordinal).ToArray();

            var eager = Names(CmdletTypeScanner.FromAllTypes(asm));
            var lazy = Names(CmdletTypeScanner.FindCmdletTypes(asm));

            Assert.True(eager.Length > 50, $"sanity: expected ~100 cmdlets, eager scan found {eager.Length}");
            Assert.Equal(eager, lazy);
        }
        finally { ctx.Unload(); }
    }

    [SkippableFact]
    public void PlainRunspaceInit_LoadsNoStrataAssembly()
    {
        var dir = CmdletsDir();
        Skip.IfNot(StrataBuiltIn(dir), "Strata is not built in (UseStrata=false): nothing to load.");

        // In-process: watch loads DURING Create only. If another test already loaded Strata into the
        // shared default context, no event fires (cannot false-fail); the isolated tests above are the
        // deterministic guard, this one proves the real startup path end to end.
        var loadedDuringInit = new List<string>();
        void OnLoad(object? s, AssemblyLoadEventArgs e)
        {
            var n = e.LoadedAssembly.GetName().Name ?? "";
            if (n.StartsWith("Strata.", StringComparison.Ordinal)) lock (loadedDuringInit) loadedDuringInit.Add(n);
        }

        AppDomain.CurrentDomain.AssemblyLoad += OnLoad;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rs = SdkRunspace.Create();
            sw.Stop();
            try
            {
                var total = AppDomain.CurrentDomain.GetAssemblies().Length;
                _out.WriteLine($"SdkRunspace.Create (cold): {sw.ElapsedMilliseconds} ms; assemblies in AppDomain after init: {total}");
                lock (loadedDuringInit) Assert.Empty(loadedDuringInit);
            }
            finally { rs.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

            // Warm creations (what a pooled daemon pays per runspace), for before/after comparison.
            var warm = new List<long>();
            for (int i = 0; i < 4; i++)
            {
                sw.Restart();
                var r = SdkRunspace.Create();
                warm.Add(sw.ElapsedMilliseconds);
                r.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            _out.WriteLine("SdkRunspace.Create (warm) ms: " + string.Join(", ", warm));
        }
        finally { AppDomain.CurrentDomain.AssemblyLoad -= OnLoad; }
    }
}
