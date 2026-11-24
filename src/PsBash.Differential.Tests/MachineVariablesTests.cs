using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>UID, OSTYPE, HOSTNAME, BASHPID, EPOCHSECONDS... were unset. Values differ by host, so each case asserts the shape.</summary>
public class MachineVariablesTests
{
    [SkippableFact] public Task Uid_IsNumeric() => AssertOracle.EqualAsync("[ \"$UID\" -ge 0 ] && [ \"$EUID\" -ge 0 ] && echo ok; case \"$UID\" in *[!0-9]*|'') echo bad;; *) echo digits;; esac");
    [SkippableFact] public Task Ostype_IsKnown() => AssertOracle.EqualAsync("case \"$OSTYPE\" in msys*|linux*|darwin*) echo known;; *) echo \"unknown $OSTYPE\";; esac");
    [SkippableFact] public Task Hosttype_IsArch() => AssertOracle.EqualAsync("case \"$HOSTTYPE\" in x86_64|aarch64) echo arch;; *) echo bad;; esac; case \"$MACHTYPE\" in \"$HOSTTYPE\"-*) echo machtype;; *) echo bad;; esac");
    [SkippableFact] public Task Hostname_NonEmpty() => AssertOracle.EqualAsync("[ -n \"$HOSTNAME\" ] && echo set; echo \"${HOSTNAME:-none}\" | grep -q . && echo default");
    // SHELL: bash sets it at startup (unexported) when the environment lacks it; `unset SHELL` then empties it.
    [SkippableFact] public Task Shell_SetAtStartupNotExported_UnsetEmpties() => AssertOracle.EqualAsync("unset SHELL; echo \"[$SHELL] ${SHELL-unset}\"; bash -c 'test -n \"$SHELL\" && echo set; env | grep -c \"^SHELL=\"'");
    [SkippableFact] public Task Bashpid_IsPositive() => AssertOracle.EqualAsync("[ \"$BASHPID\" -gt 0 ] && echo pid");
    // REGRESSION: $SECONDS counted from the runtime's load, not the shell's start, and ignored assignment.
    [SkippableFact] public Task Seconds_StartsAtZero() => AssertOracle.EqualAsync("echo \"start=$SECONDS\"; [ $SECONDS -lt 2 ] && echo fresh");
    [SkippableFact] public Task Seconds_AssignmentSetsTheCount() => AssertOracle.EqualAsync("SECONDS=100; echo \"$SECONDS\"; export SECONDS=7; echo \"$SECONDS\"; declare SECONDS=40; echo \"$SECONDS\"");
    [SkippableFact] public Task Seconds_SubshellAssignmentStaysInTheSubshell() => AssertOracle.EqualAsync("( SECONDS=500; echo \"in=$SECONDS\" ); [ $SECONDS -lt 5 ] && echo parent-untouched");
    [SkippableFact] public Task Epoch_Shapes() => AssertOracle.EqualAsync("[ \"$EPOCHSECONDS\" -gt 1700000000 ] && echo secs; case \"$EPOCHREALTIME\" in [0-9]*.[0-9][0-9][0-9][0-9][0-9][0-9]) echo real;; *) echo \"bad $EPOCHREALTIME\";; esac");
}
