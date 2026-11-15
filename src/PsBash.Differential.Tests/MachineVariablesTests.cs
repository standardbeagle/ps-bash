using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>UID, OSTYPE, HOSTNAME, BASHPID, EPOCHSECONDS... were unset. Values differ by host, so each case asserts the shape.</summary>
public class MachineVariablesTests
{
    [Fact] public Task Uid_IsNumeric() => AssertOracle.EqualAsync("[ \"$UID\" -ge 0 ] && [ \"$EUID\" -ge 0 ] && echo ok; case \"$UID\" in *[!0-9]*|'') echo bad;; *) echo digits;; esac");
    [Fact] public Task Ostype_IsKnown() => AssertOracle.EqualAsync("case \"$OSTYPE\" in msys*|linux*|darwin*) echo known;; *) echo \"unknown $OSTYPE\";; esac");
    [Fact] public Task Hosttype_IsArch() => AssertOracle.EqualAsync("case \"$HOSTTYPE\" in x86_64|aarch64) echo arch;; *) echo bad;; esac; case \"$MACHTYPE\" in \"$HOSTTYPE\"-*) echo machtype;; *) echo bad;; esac");
    [Fact] public Task Hostname_NonEmpty() => AssertOracle.EqualAsync("[ -n \"$HOSTNAME\" ] && echo set; echo \"${HOSTNAME:-none}\" | grep -q . && echo default");
    // SHELL: bash sets it at startup (unexported) when the environment lacks it; `unset SHELL` then empties it.
    [Fact] public Task Shell_SetAtStartupNotExported_UnsetEmpties() => AssertOracle.EqualAsync("unset SHELL; echo \"[$SHELL] ${SHELL-unset}\"; bash -c 'test -n \"$SHELL\" && echo set; env | grep -c \"^SHELL=\"'");
    [Fact] public Task Bashpid_IsPositive() => AssertOracle.EqualAsync("[ \"$BASHPID\" -gt 0 ] && echo pid");
    [Fact] public Task Epoch_Shapes() => AssertOracle.EqualAsync("[ \"$EPOCHSECONDS\" -gt 1700000000 ] && echo secs; case \"$EPOCHREALTIME\" in [0-9]*.[0-9][0-9][0-9][0-9][0-9][0-9]) echo real;; *) echo \"bad $EPOCHREALTIME\";; esac");
}
