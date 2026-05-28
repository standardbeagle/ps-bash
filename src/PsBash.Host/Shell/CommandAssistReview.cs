using System.Text.RegularExpressions;

namespace PsBash.Host.Shell;

internal enum CommandAssistReviewAction
{
    Execute,
    Insert,
    Retry,
    SwitchProvider,
    Cancel,
}

internal sealed record CommandAssistSafetyFinding(string Pattern, string Reason);

internal sealed record CommandAssistReviewRequest(
    string ProviderName,
    string Command,
    string Explanation,
    string Cwd,
    IReadOnlyList<CommandAssistSafetyFinding> Warnings);

internal sealed record CommandAssistReviewDecision(CommandAssistReviewAction Action, bool DangerousConfirmed = false)
{
    public static CommandAssistReviewDecision Execute(bool dangerousConfirmed = false)
        => new(CommandAssistReviewAction.Execute, dangerousConfirmed);

    public static CommandAssistReviewDecision Insert()
        => new(CommandAssistReviewAction.Insert);

    public static CommandAssistReviewDecision Retry()
        => new(CommandAssistReviewAction.Retry);

    public static CommandAssistReviewDecision SwitchProvider()
        => new(CommandAssistReviewAction.SwitchProvider);

    public static CommandAssistReviewDecision Cancel()
        => new(CommandAssistReviewAction.Cancel);
}

internal static class CommandAssistSafety
{
    private static readonly (Regex Pattern, string Label, string Reason)[] DangerousPatterns =
    [
        (new Regex(@"\brm\s+(-[^\n;&|]*[rf][^\n;&|]*|[^\n;&|]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "rm", "removes files"),
        (new Regex(@"\b(git\s+reset\s+--hard|git\s+clean\s+-[^\n;&|]*f|git\s+push\s+--force)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "git force/reset", "can discard or overwrite work"),
        (new Regex(@"\b(del|erase|Remove-Item)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "delete", "removes files"),
        (new Regex(@">\|", RegexOptions.Compiled), "overwrite redirect", "forces overwrite"),
        (new Regex(@"\b(sudo|Start-Process\s+-Verb\s+RunAs)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "privilege escalation", "runs with elevated privileges"),
        (new Regex(@"\b(curl|wget|irm|iwr|Invoke-WebRequest|Invoke-RestMethod)\b.*\|\s*(sh|bash|pwsh|powershell)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "network install", "pipes network content into a shell"),
        (new Regex(@"\b(npm|pnpm|yarn|pip|pipx|gem|cargo|dotnet)\s+.*\b(install|add|global|tool)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "package install", "installs code or tools"),
    ];

    public static IReadOnlyList<CommandAssistSafetyFinding> Classify(string command)
    {
        var findings = new List<CommandAssistSafetyFinding>();
        foreach (var (pattern, label, reason) in DangerousPatterns)
        {
            if (pattern.IsMatch(command))
                findings.Add(new CommandAssistSafetyFinding(label, reason));
        }
        return findings;
    }
}

internal static class CommandAssistReview
{
    public static CommandAssistResponse ApplyDecision(
        CommandAssistReviewRequest request,
        CommandAssistReviewDecision decision)
    {
        return decision.Action switch
        {
            CommandAssistReviewAction.Insert => CommandAssistResponse.Insert(request.Command),
            CommandAssistReviewAction.Execute when request.Warnings.Count == 0 || decision.DangerousConfirmed
                => CommandAssistResponse.Execute(request.Command),
            _ => CommandAssistResponse.Cancelled,
        };
    }
}
