namespace PsBash.Host.Runtime;

/// <summary>
/// The process-wide default <see cref="System.Text.RegularExpressions.Regex"/> match timeout for the host.
/// <para>
/// GNU grep/sed/awk use automata that cannot blow up; .NET's engine backtracks, so a pattern such as
/// <c>(a+)+$</c> against a long run of <c>a</c> ending in a non-match takes exponential time. In the
/// host that was worse than slow: a cmdlet stuck inside <c>Regex.Match</c> ignores PowerShell's Stop,
/// so the command held the process-wide exec gate forever (even after its launcher was killed) and
/// every other command on the host queued behind it. The runtime reads <c>REGEX_DEFAULT_MATCH_TIMEOUT</c>
/// once, when the first Regex is constructed, so <see cref="Install"/> must run first thing in Main.
/// The timeout is per match call — generous enough for a linear match over a very large line.
/// Override (seconds) with <c>PSBASH_REGEX_TIMEOUT_SECS</c>; <c>0</c> disables.
/// </para>
/// </summary>
internal static class HostRegexTimeout
{
    internal static readonly TimeSpan Default = TimeSpan.FromSeconds(15);

    internal static TimeSpan? Resolve(string? env)
    {
        if (env is null) return Default;
        if (!double.TryParse(env, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var secs) || secs < 0)
            return Default;
        return secs == 0 ? null : TimeSpan.FromSeconds(Math.Min(secs, int.MaxValue / 1000.0));
    }

    public static void Install()
    {
        if (Resolve(Environment.GetEnvironmentVariable("PSBASH_REGEX_TIMEOUT_SECS")) is { } timeout)
            AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", timeout);
    }
}
