using PsBash.Shell.Pty;
using Xunit;

namespace PsBash.Shell.Tests.Pty;

/// <summary>
/// PTY-12: the non-tty fallback decision. When the launcher's own stdin is
/// redirected (CI logs, GUI invoke, <c>ps-bash &lt; /dev/null</c>), the launcher
/// must not allocate a PTY even with <c>PSBASH_PTY=1</c> — it falls through to
/// the legacy inherited-stdio path.
/// </summary>
public class PtyLaunchPolicyTests
{
    [Fact]
    public void OptIn_And_RealTty_UsesPty()
        => Assert.True(PtyLaunchPolicy.ShouldUsePty(ptyOptIn: true, launcherStdinRedirected: false));

    [Fact]
    public void OptIn_But_StdinRedirected_DoesNotUsePty()
        => Assert.False(PtyLaunchPolicy.ShouldUsePty(ptyOptIn: true, launcherStdinRedirected: true));

    [Fact]
    public void NoOptIn_RealTty_DoesNotUsePty()
        => Assert.False(PtyLaunchPolicy.ShouldUsePty(ptyOptIn: false, launcherStdinRedirected: false));

    [Fact]
    public void NoOptIn_StdinRedirected_DoesNotUsePty()
        => Assert.False(PtyLaunchPolicy.ShouldUsePty(ptyOptIn: false, launcherStdinRedirected: true));
}
