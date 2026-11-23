using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Provider;
using System.Management.Automation.Runspaces;

namespace PsBash.Host.Runtime;

/// <summary>
/// What every pooled runspace starts from, built ONCE per host process: the session state (SDK and PsBash cmdlets,
/// plus the psm1's own functions and aliases as session-state entries) and the residual psm1 script that must still
/// run in each runspace.
/// <para>
/// The daemon discards a runspace after every command (<see cref="WorkerPool{TWorker}"/>), so whatever a runspace
/// costs to build is paid per command. Running the whole 200 KB psm1 as a script in every new runspace cost
/// ~150 ms of CPU on Linux and ~0.6 s on Windows (AMSI scans every compile). That bounded back-to-back
/// commands: with two warm spares, every third command waited on a rebuild. Most of the psm1 consists of
/// definitions (top-level <c>function</c> and constant <c>Set-Alias</c> statements). Those carry no
/// per-runspace state, so they go into the session state once. Opening a runspace from it costs ~10 ms.
/// </para>
/// <para>
/// <b>Equivalence:</b> a runspace opened from the template and given <see cref="ResidualScript"/> must end
/// with the same functions, aliases, and global variables as one that ran the whole psm1. That is guarded
/// by <c>RunspaceTemplateEquivalenceTests</c>. <see cref="Split"/> is conservative: a statement it can't
/// prove to be a pure definition stays in the residual and runs as before.
/// </para>
/// </summary>
internal sealed class RunspaceTemplate
{
    private RunspaceTemplate(InitialSessionState iss, string residualScript, bool split, bool cmdletsPreRegistered)
    {
        _iss = iss;
        ResidualScript = residualScript;
        IsSplit = split;
        CmdletsPreRegistered = cmdletsPreRegistered;
    }

    private readonly InitialSessionState _iss;

    /// <summary>The psm1 statements every runspace still runs (all of it when <see cref="IsSplit"/> is false).</summary>
    public string ResidualScript { get; }

    /// <summary>True when the psm1's definitions are in the session state rather than in <see cref="ResidualScript"/>.</summary>
    public bool IsSplit { get; }

    /// <summary>True when the PsBash cmdlets are registered in the session state.</summary>
    public bool CmdletsPreRegistered { get; }

    /// <summary>A private copy of the session state for one new runspace.</summary>
    public InitialSessionState NewSessionState() => _iss.Clone();

    // Test seam: how many templates this process built (one per distinct psm1 / cmdlets build).
    internal static int BuildCount;

    private static readonly object s_gate = new();
    private static RunspaceTemplate? s_cached;
    private static string? s_cachedKey;

    /// <summary>
    /// The template for this psm1 + cmdlets build. Built on first use and then shared. A different file, or the
    /// same path rewritten (new length or timestamp), builds a fresh one.
    /// </summary>
    public static RunspaceTemplate For(string? psm1Path, string cmdletsDllPath, Func<InitialSessionState> createBase)
    {
        string key = KeyOf(psm1Path) + "|" + KeyOf(cmdletsDllPath);
        lock (s_gate)
        {
            if (s_cached is not null && s_cachedKey == key) return s_cached;
            var built = Build(psm1Path, cmdletsDllPath, createBase);
            s_cached = built;
            s_cachedKey = key;
            return built;
        }
    }

    private static string KeyOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.FullName}:{info.Length}:{info.LastWriteTimeUtc.Ticks}" : info.FullName + ":absent";
        }
        catch (Exception) { return path; }
    }

    private static RunspaceTemplate Build(string? psm1Path, string cmdletsDllPath, Func<InitialSessionState> createBase)
    {
        Interlocked.Increment(ref BuildCount);
        var iss = createBase();
        bool preRegistered = RegisterPsBashCmdlets(iss, cmdletsDllPath);

        if (psm1Path is null || !File.Exists(psm1Path))
            return new RunspaceTemplate(iss, "", split: false, preRegistered);

        var text = SdkRunspace.ReadStartupModule(psm1Path);
        var parts = Split(text);
        if (parts is null) return new RunspaceTemplate(iss, text, split: false, preRegistered);

        foreach (var fn in parts.Functions)
        {
            // The psm1's grep/sed literal-argument proxies serve DIRECT PowerShell callers only; transpiled bash must
            // resolve straight to the cmdlet (see SdkRunspace). Leave them out when the cmdlet is registered.
            if (preRegistered && LiteralArgumentProxies.Contains(fn.Name)) continue;
            iss.Commands.Add(new SessionStateFunctionEntry(fn.Name, fn.Definition));
        }
        foreach (var alias in parts.Aliases)
        {
            // `Set-Alias -Force` over a built-in (echo, ls, cat, …) replaces it.
            iss.Commands.Remove(alias.Name, typeof(SessionStateAliasEntry));
            iss.Commands.Add(new SessionStateAliasEntry(alias.Name, alias.Value, alias.Description, alias.Options));
        }
        return new RunspaceTemplate(iss, parts.Residual, split: true, preRegistered);
    }

    /// <summary>The psm1 functions SdkRunspace drops once the same-named cmdlet exists.</summary>
    internal static readonly HashSet<string> LiteralArgumentProxies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Invoke-BashGrep", "Invoke-BashSed",
    };

    /// <summary>
    /// Pre-register the PsBash cmdlets via the session state, skipping the Import-Module path. Import-Module of a
    /// binary DLL ran 1.8-5.7 s under parallel load (host startup #9). Pre-registering is essentially free because
    /// the assembly is already loaded, or LoadFrom is a single file open. Returns true when any cmdlet was registered.
    /// </summary>
    private static bool RegisterPsBashCmdlets(InitialSessionState iss, string cmdletsDllPath)
    {
        if (!File.Exists(cmdletsDllPath)) return false;
        try
        {
            var asm = System.Reflection.Assembly.LoadFrom(cmdletsDllPath);
            // Metadata-first scan: NOT asm.GetTypes(), which loads every type (and so the
            // Strata assemblies its helper types derive from) into every runspace.
            int registered = 0;
            foreach (var (type, attr) in CmdletTypeScanner.FindCmdletTypes(asm))
            {
                iss.Commands.Add(new SessionStateCmdletEntry($"{attr.VerbName}-{attr.NounName}", type, null));
                registered++;
            }
            if (registered > 0) InstallShellEnvironmentDrive(iss, asm);
            return registered > 0;
        }
        catch
        {
            return false; // the setup script's Import-Module probe is the fallback
        }
    }

    /// <summary>
    /// Where the process environment cannot hold every value (<see cref="PsBash.Core.ProcessScalars.EnvironmentHoldsEveryString"/>:
    /// on Linux it turns an invalid byte's marker into U+FFFD), the shell's <c>Env:</c> drive is the ps-bash provider that
    /// reads and writes scalars through <c>BashVariableStore</c> (<c>PsBash.Cmdlets.JobEnvironmentProvider</c>, a job
    /// runspace's drive), so emitted <c>$env:NAME</c> gives back the exact value. REGRESSION: <c>s=$'\xff'; printf %s "$s"</c>
    /// wrote <c>ef bf bd</c>. Elsewhere PowerShell's own provider stays: it loses nothing there.
    /// </summary>
    internal static void InstallShellEnvironmentDrive(InitialSessionState iss, System.Reflection.Assembly cmdlets)
    {
        if (PsBash.Core.ProcessScalars.EnvironmentHoldsEveryString) return;
        var provider = cmdlets.GetType("PsBash.Cmdlets.JobEnvironmentProvider");
        var name = provider?.GetCustomAttributes(typeof(CmdletProviderAttribute), false) is [CmdletProviderAttribute attr, ..]
            ? attr.ProviderName
            : null;
        if (provider is null || name is null) return;
        // By index: CreateDefault2's entries come from the core snap-in, and Remove(name, type) leaves them in place.
        for (int i = iss.Providers.Count - 1; i >= 0; i--)
            if (iss.Providers[i].Name == "Environment") iss.Providers.RemoveItem(i);
        iss.Providers.Add(new SessionStateProviderEntry(name, provider, null));
    }

    internal sealed record FunctionDef(string Name, string Definition);

    internal sealed record AliasDef(string Name, string Value, string Description, ScopedItemOptions Options);

    internal sealed record Parts(IReadOnlyList<FunctionDef> Functions, IReadOnlyList<AliasDef> Aliases, string Residual);

    /// <summary>
    /// Split a module script into session-state definitions and the residual script, or null when it can't be
    /// split safely (parse errors, a param/begin/process block, <c>using</c> statements).
    /// A top-level statement moves to the session state only when it is one of these:
    /// <list type="bullet">
    /// <item>A <c>function</c>. Not a filter or workflow, not scope-qualified (<c>function global:f</c>), and
    /// defined once.</item>
    /// <item>A <c>Set-Alias</c> whose every argument is a constant: <c>-Name</c>, <c>-Value</c>,
    /// <c>-Description</c>, <c>-Option</c>, <c>-Force</c>, and <c>-Scope Global|Script</c>. At the top level
    /// of a script run in the global scope, Global and Script are the same scope.</item>
    /// </list>
    /// Everything else stays in the residual, in its original order.
    /// </summary>
    internal static Parts? Split(string script)
    {
        var ast = Parser.ParseInput(script, out _, out var errors);
        if (errors.Length > 0 || ast.ParamBlock is not null || ast.BeginBlock is not null
            || ast.ProcessBlock is not null || ast.DynamicParamBlock is not null || ast.UsingStatements.Count > 0
            || ast.EndBlock is null || ast.EndBlock.Traps is { Count: > 0 })
            return null;

        var statements = ast.EndBlock.Statements;
        var definedCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in statements)
            if (s is FunctionDefinitionAst f) definedCount[f.Name] = definedCount.GetValueOrDefault(f.Name) + 1;

        var functions = new List<FunctionDef>();
        var aliases = new List<AliasDef>();
        var residual = new System.Text.StringBuilder();
        foreach (var s in statements)
        {
            if (s is FunctionDefinitionAst fn && TryFunction(fn, definedCount, out var def)) { functions.Add(def); continue; }
            if (TryConstantSetAlias(s, out var alias)) { aliases.Add(alias); continue; }
            residual.Append(s.Extent.Text).Append('\n');
        }
        return new Parts(functions, aliases, residual.ToString());
    }

    private static bool TryFunction(FunctionDefinitionAst fn, Dictionary<string, int> definedCount, out FunctionDef def)
    {
        def = null!;
        if (fn.IsFilter || fn.IsWorkflow || fn.Name.Contains(':') || definedCount[fn.Name] != 1) return false;
        var body = fn.Body.Extent.Text;
        if (body.Length < 2 || body[0] != '{' || body[^1] != '}') return false;
        var inner = body[1..^1];
        // `function f($a, [int]$b) { … }`: the parameters sit outside the body; a definition string carries them as a
        // param block (the body cannot also have one: that is a parse error).
        if (fn.Parameters is { Count: > 0 } ps)
            inner = "param(" + string.Join(", ", ps.Select(p => p.Extent.Text)) + ")\n" + inner;
        def = new FunctionDef(fn.Name, inner);
        return true;
    }

    private static bool TryConstantSetAlias(StatementAst s, out AliasDef alias)
    {
        alias = null!;
        if (s is not PipelineAst { PipelineElements: [CommandAst cmd] } || cmd.InvocationOperator != TokenKind.Unknown
            || cmd.Redirections.Count > 0
            || !string.Equals(cmd.GetCommandName(), "Set-Alias", StringComparison.OrdinalIgnoreCase))
            return false;

        string? name = null, value = null, description = "", scope = null, option = null;
        var positional = new List<string>();
        var els = cmd.CommandElements;
        for (int i = 1; i < els.Count; i++)
        {
            if (els[i] is CommandParameterAst p)
            {
                if (p.Argument is not null) return false; // -Name:x form; not used, not worth parsing
                string pname = p.ParameterName;
                if (pname.Equals("Force", StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 >= els.Count || els[i + 1] is not StringConstantExpressionAst arg) return false;
                i++;
                switch (pname.ToLowerInvariant())
                {
                    case "name": name = arg.Value; break;
                    case "value": value = arg.Value; break;
                    case "description": description = arg.Value; break;
                    case "scope": scope = arg.Value; break;
                    case "option": option = arg.Value; break;
                    default: return false;
                }
            }
            else if (els[i] is StringConstantExpressionAst c) positional.Add(c.Value);
            else return false;
        }
        int next = 0;
        name ??= next < positional.Count ? positional[next++] : null;
        value ??= next < positional.Count ? positional[next++] : null;
        if (next != positional.Count || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value)) return false;
        if (scope is not null && !scope.Equals("Global", StringComparison.OrdinalIgnoreCase)
            && !scope.Equals("Script", StringComparison.OrdinalIgnoreCase))
            return false;
        var options = ScopedItemOptions.None;
        if (option is not null && !Enum.TryParse(option, ignoreCase: true, out options)) return false;
        // Private/ReadOnly/Constant behave differently as a session-state entry than as a Set-Alias result; keep
        // those in the residual.
        if ((options & ~ScopedItemOptions.AllScope) != 0) return false;
        alias = new AliasDef(name, value, description ?? "", options);
        return true;
    }
}
