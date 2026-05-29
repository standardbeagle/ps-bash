namespace PsBash.Core.Runtime;

/// <summary>
/// Shared parsing of boolean-style environment variables (e.g. PSBASH_COMPACT_OUTPUT).
/// One implementation so the launcher (PsBash.Shell) and the IPC layer (PsBash.Core)
/// agree on what counts as "on" — previously each kept its own copy.
/// </summary>
public static class EnvFlags
{
    /// <summary>
    /// True when the named environment variable is set to a truthy token
    /// (<c>1</c>, <c>true</c>, <c>yes</c>, or <c>on</c>, case-insensitive).
    /// </summary>
    public static bool IsTruthy(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
