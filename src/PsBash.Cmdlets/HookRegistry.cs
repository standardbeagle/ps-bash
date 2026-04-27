using System.Collections.Concurrent;
using System.Management.Automation;

namespace PsBash.Cmdlets;

/// <summary>
/// Singleton registry for ps-bash prompt and chpwd hooks.
///
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>. The firing
/// loop always works from a snapshot so hooks that self-unregister mid-iteration
/// do not cause <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class HookRegistry
{
    private readonly ConcurrentDictionary<(HookKind Kind, string Name), ScriptBlock> _hooks = new();

    /// <summary>Process-global singleton instance.</summary>
    public static HookRegistry Instance { get; } = new();

    private HookRegistry() { }

    /// <summary>Adds or replaces a hook (upsert).</summary>
    public void Register(HookKind kind, string name, ScriptBlock scriptBlock)
        => _hooks[(kind, name)] = scriptBlock;

    /// <summary>Removes a hook. Returns <c>true</c> if it existed; silent no-op otherwise.</summary>
    public bool Unregister(HookKind kind, string name)
        => _hooks.TryRemove((kind, name), out _);

    /// <summary>
    /// Returns a snapshot of all registered hooks sorted by name (ordinal).
    /// </summary>
    public BashHookInfo[] GetAll()
        => _hooks
            .OrderBy(kv => kv.Key.Name, StringComparer.Ordinal)
            .Select(kv => new BashHookInfo
            {
                Kind = kv.Key.Kind,
                Name = kv.Key.Name,
                ScriptBlock = kv.Value,
            })
            .ToArray();

    /// <summary>
    /// Returns a name-sorted snapshot of scriptblocks for a single kind.
    /// Safe for iteration while the dictionary is mutated by hooks.
    /// </summary>
    public ScriptBlock[] SnapshotByKind(HookKind kind)
        => _hooks
            .Where(kv => kv.Key.Kind == kind)
            .OrderBy(kv => kv.Key.Name, StringComparer.Ordinal)
            .Select(kv => kv.Value)
            .ToArray();

    /// <summary>
    /// Fires hooks for a single prompt tick.
    ///
    /// <list type="bullet">
    ///   <item>PromptHooks always fire (no args).</item>
    ///   <item>ChpwdHooks fire only when <paramref name="oldPath"/> differs from
    ///     <paramref name="newPath"/>; they receive both paths as positional args.</item>
    /// </list>
    ///
    /// Exceptions are caught per-hook and appended to
    /// <c>$global:BashHookErrors</c> in the caller's runspace. A failing hook
    /// does not prevent subsequent hooks from firing.
    /// </summary>
    public void FirePrompt(
        System.Management.Automation.SessionState callerState,
        string oldPath,
        string newPath)
    {
        bool pathChanged = !string.Equals(oldPath, newPath, StringComparison.Ordinal);

        if (pathChanged)
        {
            foreach (var sb in SnapshotByKind(HookKind.ChpwdHook))
            {
                try
                {
                    sb.InvokeWithContext(null, null, oldPath, newPath);
                }
                catch (Exception ex)
                {
                    AppendHookError(callerState, ex);
                }
            }
        }

        foreach (var sb in SnapshotByKind(HookKind.PromptHook))
        {
            try
            {
                sb.InvokeWithContext(null, null);
            }
            catch (Exception ex)
            {
                AppendHookError(callerState, ex);
            }
        }
    }

    private static void AppendHookError(System.Management.Automation.SessionState callerState, Exception ex)
    {
        try
        {
            var errors = callerState.PSVariable.GetValue("global:BashHookErrors") as System.Collections.IList;
            errors?.Add(ex);
        }
        catch
        {
            // Best-effort: if we can't append the error, swallow it silently.
        }
    }
}
