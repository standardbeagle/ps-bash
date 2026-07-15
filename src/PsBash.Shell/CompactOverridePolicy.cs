using PsBash.Core.Runtime.Compaction;

namespace PsBash.Shell;

internal static class CompactOverridePolicy
{
    internal static string Rewrite(
        string command,
        IReadOnlyList<FilterSpec>? filters,
        out string? skipReason)
    {
        skipReason = null;
        if (command.IndexOfAny([';', '|', '&', '<', '>', '\r', '\n']) >= 0)
        {
            skipReason = "command contains shell operators";
            return command;
        }

        var configured = FilterEngine.SelectOverride(command, filters);
        if (configured is null) return command;

        var eligible = FilterEngine.SelectLaunchOverride(command, filters);
        if (eligible is null)
        {
            skipReason = "command has explicit options or operands";
            return command;
        }

        return string.Join(' ', eligible.Select(QuoteBashArg));
    }

    private static string QuoteBashArg(string value)
        => "'" + value.Replace("'", "'\"'\"'") + "'";
}
