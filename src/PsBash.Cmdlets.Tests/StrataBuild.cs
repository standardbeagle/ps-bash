namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Whether this build of PsBash.Cmdlets includes the Strata-backed cmdlets (Format-Styled /
/// Show-Styled / the *Tui cmdlets). Strata is optional (see src/Strata.props): CI has no local feed,
/// so those cmdlets are compiled out and tests that need them must SKIP with a reason, not fail.
/// Detected at runtime by type presence, so it can never disagree with what was actually built.
/// </summary>
internal static class StrataBuild
{
    public const string SkipReason =
        "Strata is not built in (UseStrata=false; no ../strata/local-feed): Show-Styled/Format-Styled/*Tui cmdlets are compiled out.";

    public static bool Enabled { get; } =
        typeof(BashRuntime).Assembly.GetType("PsBash.Cmdlets.ShowStyledCommand") is not null;

    /// <summary>Cmdlet names that exist only when Strata is built in.</summary>
    public static readonly string[] GatedCmdletTargets =
        { "Invoke-BashGitTui", "Invoke-BashFfmpegTui" };
}
