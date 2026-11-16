using System.Text.RegularExpressions;

namespace PsBash.Spec;

/// <summary>
/// What a case shares with the other cases of a run. Every case gets its own working directory (<see cref="CaseDir"/>)
/// and a host put back to its baseline shell state, so relative files, options, variables, aliases and functions are
/// its own. Two locations cannot be given per case: the temp directory (<c>/tmp</c> is the machine's on Linux and the
/// user's <c>%TEMP%</c> on Windows, which the hosts themselves use for sockets and the extracted module) and the home
/// directory (rc files, the history file of an interactive shell). A case that names one of them therefore runs with
/// no other case beside it (<see cref="SpecRunner.RunAsync"/>).
/// </summary>
public static partial class CaseIsolation
{
    // /tmp, ~ at the start of a word, HOME / TMPDIR / TMP / HISTFILE by name, and an interactive shell (`$SH -i`,
    // `bash --rcfile f -i`), which reads and writes the home directory's history.
    [GeneratedRegex(@"(?<![A-Za-z0-9_.$/-])/tmp(?![A-Za-z0-9_.-])|(?<![A-Za-z0-9_/=.])~|\bHOME\b|\bTMPDIR\b|\$\{?TMP\b|\bHISTFILE\b|(?<=\s)-i(?=\s|$)")]
    private static partial Regex SharedLocation();

    /// <summary>True when <paramref name="script"/> names a location all cases share: it must not overlap another case.</summary>
    public static bool UsesSharedLocation(string script) => SharedLocation().IsMatch(script);
}
