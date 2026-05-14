namespace PsBash.Shell.Pty;

/// <summary>
/// Pure decision logic (PTY-12) for whether the interactive launcher should
/// allocate a pseudo-terminal and spawn the host under it, versus falling
/// through to the legacy inherited-stdio path.
/// </summary>
/// <remarks>
/// <para>The PTY path (<see cref="Program.RunHostUnderPtyAsync"/>) only makes
/// sense when the launcher's own stdin is a real terminal. If the launcher was
/// started with redirected stdin — CI log capture, a GUI process spawning
/// ps-bash with a pipe, <c>ps-bash &lt; /dev/null</c> — there are no keystrokes
/// to pump and no terminal signals to forward. Allocating a PTY in that case
/// just adds a pseudo-terminal layer the user never asked for and can wedge a
/// non-interactive parent that expects plain pipe semantics.</para>
/// <para>This is separate from the pre-Win10-1809 fallback (handled by the
/// <see cref="System.PlatformNotSupportedException"/> catch around
/// <see cref="Program.RunHostUnderPtyAsync"/>): that path is reached, attempts
/// allocation, and falls back on a thrown exception. This policy decides
/// <em>before</em> any allocation, purely from stdin-redirection state.</para>
/// </remarks>
internal static class PtyLaunchPolicy
{
    /// <summary>
    /// Decide whether to run the host under a launcher-allocated PTY.
    /// </summary>
    /// <param name="ptyOptIn">True when <c>PSBASH_PTY=1</c> (or <c>true</c>) is set.</param>
    /// <param name="launcherStdinRedirected">
    /// <see cref="System.Console.IsInputRedirected"/> for the launcher process.
    /// </param>
    /// <returns>
    /// True only when the user opted in <em>and</em> the launcher's stdin is a
    /// real terminal. False otherwise — the caller falls through to the legacy
    /// inherited-stdio path (pipe-based interactive harness).
    /// </returns>
    public static bool ShouldUsePty(bool ptyOptIn, bool launcherStdinRedirected)
        => ptyOptIn && !launcherStdinRedirected;
}
