namespace PsBash.Cmdlets;

/// <summary>
/// Identifies the kind of ps-bash hook.
/// </summary>
public enum HookKind
{
    /// <summary>Fires when the current directory changes between prompt ticks.</summary>
    ChpwdHook,

    /// <summary>Fires on every prompt tick, unconditionally.</summary>
    PromptHook,
}
