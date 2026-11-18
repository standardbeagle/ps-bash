using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PsBash.Core.Platform;
using Xunit;

namespace PsBash.Core.Tests.Platform;

/// <summary>
/// The one signal table (Platform/OsSignals). Oracle note (qa-rubric Directive 1): OS numbering facts from the Linux
/// (asm/signal.h) and XNU (sys/signal.h) headers; bash on Linux is the numbering a script sees.
/// </summary>
public class OsSignalsTests
{
    [Theory]
    // bash/Linux, macOS
    [InlineData(OsSignals.SIGHUP, 1)]
    [InlineData(OsSignals.SIGINT, 2)]
    [InlineData(OsSignals.SIGBUS, 10)]
    [InlineData(OsSignals.SIGKILL, 9)]
    [InlineData(OsSignals.SIGUSR1, 30)]
    [InlineData(OsSignals.SIGUSR2, 31)]
    [InlineData(OsSignals.SIGTERM, 15)]
    [InlineData(OsSignals.SIGCHLD, 20)]
    [InlineData(OsSignals.SIGCONT, 19)]
    [InlineData(OsSignals.SIGSTOP, 17)]
    [InlineData(OsSignals.SIGTSTP, 18)]
    [InlineData(OsSignals.SIGURG, 16)]
    [InlineData(OsSignals.SIGWINCH, 28)]
    [InlineData(OsSignals.SIGSYS, 12)]
    [InlineData(16, 0)]   // SIGSTKFLT: Linux only
    [InlineData(30, 0)]   // SIGPWR: Linux only
    [InlineData(34, 0)]   // real-time: none on macOS
    public void ToNative_MapsBashNumbersToTheMacOSTable(int bash, int macOS)
    {
        Assert.Equal(macOS, OsSignals.ToNative(bash, bsd: true));
        Assert.Equal(bash, OsSignals.ToNative(bash, bsd: false));
        if (macOS != 0) Assert.Equal(bash, OsSignals.FromNative(macOS, bsd: true));
    }

    [Fact]
    public void FromNative_KeepsASignalBashHasNoNumberFor()
    {
        Assert.Equal(7, OsSignals.FromNative(7, bsd: true));    // macOS SIGEMT
        Assert.Equal(29, OsSignals.FromNative(29, bsd: true));  // macOS SIGINFO
    }

    [Theory]
    [InlineData(0x0000, false, 0)]
    [InlineData(0x0300, false, 3)]       // exit 3
    [InlineData(0x0300, true, 3)]
    [InlineData(10, false, 138)]         // Linux child killed by SIGUSR1
    [InlineData(30, true, 138)]          // macOS child killed by SIGUSR1: 138, as on Linux (not 158)
    [InlineData(10, true, 135)]          // macOS SIGBUS is bash's 7
    [InlineData(13, true, 141)]          // SIGPIPE: the same everywhere
    [InlineData(0x80 | 11, false, 139)]  // core dumped flag ignored
    public void ExitStatusOf_ReportsTheBashNumberOfTheSignal(int waitStatus, bool bsd, int expected)
        => Assert.Equal(expected, OsSignals.ExitStatusOf(waitStatus, bsd));

    [Fact]
    public void RuntimeOwned_IsSigusr1OnMacOSOnly()
    {
        // REGRESSION (publish v0.15.0, macOS only): the host registered SIGUSR1, which the .NET runtime sends to its
        // own threads on every GC suspension there; scripts died with 138 and no output.
        Assert.True(OsSignals.RuntimeOwned(OsSignals.SIGUSR1, bsd: true));
        Assert.False(OsSignals.RuntimeOwned(OsSignals.SIGUSR1, bsd: false));
        Assert.False(OsSignals.RuntimeOwned(OsSignals.SIGUSR2, bsd: true));
        Assert.Equal(new[] { OsSignals.SIGUSR2 }, OsSignals.ScriptSignals(bsd: true));
        Assert.Equal(new[] { OsSignals.SIGUSR1, OsSignals.SIGUSR2 }, OsSignals.ScriptSignals(bsd: false));
    }

    [Fact]
    public void AsPosixSignal_RefusesARuntimeOwnedSignal()
    {
        if (OsSignals.BsdNumbering)
            Assert.Throws<InvalidOperationException>(() => OsSignals.AsPosixSignal(OsSignals.SIGUSR1));
        else
            Assert.Equal(OsSignals.SIGUSR1, (int)OsSignals.AsPosixSignal(OsSignals.SIGUSR1));
    }
}

/// <summary>
/// No signal number is written down outside Platform/OsSignals: a `SIGX = N` constant, a `(PosixSignal)N` cast, or a
/// kill/killpg/raise/signal call with a literal number other than one XSI fixes on every system (0 HUP INT QUIT ABRT
/// KILL ALRM TERM). Each was a macOS bug (SIGTSTP = 20 is SIGCHLD there). Oracle note: a source invariant, no bash
/// equivalent.
/// </summary>
public class OsSignalsGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root (the dir with CLAUDE.md) not found.");
    }

    private static readonly Regex SignalConstant = new(@"\bSIG[A-Z0-9]{2,}\s*=\s*\d", RegexOptions.Compiled);
    private static readonly Regex PosixSignalCast = new(@"\((System\.Runtime\.InteropServices\.)?PosixSignal\)\s*[\w(]", RegexOptions.Compiled);
    private static readonly Regex LiteralSignalCall = new(@"\b(kill|killpg|raise|signal|Raise|InstallRawHandler)\s*\(\s*[^,()]*,\s*(\d+)\s*[,)/]|\b(raise|Raise)\s*\(\s*(\d+)\s*\)", RegexOptions.Compiled);
    private static readonly HashSet<int> XsiFixed = [0, 1, 2, 3, 6, 9, 14, 15];

    internal static List<string> Offenders(string src)
    {
        var offenders = new List<string>();
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
            if (file.EndsWith($"Platform{sep}OsSignals.cs", StringComparison.Ordinal)) continue;
            if (file.EndsWith(nameof(OsSignalsGuardTests) + ".cs", StringComparison.Ordinal) || file.EndsWith("OsSignalsTests.cs", StringComparison.Ordinal)) continue;
            var lines = File.ReadAllLines(file);
            for (int n = 0; n < lines.Length; n++)
            {
                var line = lines[n];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                bool bad = SignalConstant.IsMatch(line) || PosixSignalCast.IsMatch(line);
                foreach (Match m in LiteralSignalCall.Matches(line))
                {
                    var num = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value;
                    if (int.TryParse(num, out var sig) && !XsiFixed.Contains(sig)) bad = true;
                }
                if (bad) offenders.Add($"{Path.GetRelativePath(src, file)}:{n + 1}: {trimmed}");
            }
        }
        return offenders;
    }

    [Fact]
    public void SignalNumbers_LiveOnlyInOsSignals()
    {
        var offenders = Offenders(Path.Combine(RepoRoot(), "src"));
        Assert.True(offenders.Count == 0,
            "a signal number outside src/PsBash.Transpiler/Platform/OsSignals.cs (use OsSignals.SIGx with ToNative/AsPosixSignal; "
            + "macOS numbers differ):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Guard_CatchesEachForm()
    {
        var dir = Directory.CreateTempSubdirectory("ps-bash-sigguard-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "A.cs"),
                "const int SIGTSTP = 20;\nkillpg(pid, 20);\nRaise(30);\nPosixSignalRegistration.Create((PosixSignal)30, h);\nkill(pid, 9);\nkill(pid, OsSignals.ToNative(x));\n");
            var found = Offenders(dir.FullName);
            Assert.Equal(4, found.Count);
            Assert.DoesNotContain(found, f => f.Contains("kill(pid, 9)", StringComparison.Ordinal));
        }
        finally { dir.Delete(recursive: true); }
    }
}
