## Imported from the Oils spec tests (osh-bugs.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### var x = $(echo bad; false) in OSH

#shopt -s verbose_errexit

# This turns on command_sub_errexit and fails
var x = $(echo bad; false)
echo 'unreachable'

pp test_ (x)
## stdout: unreachable
## status: 2
## timeout: 10

#### var x = $(echo one; false; echo two) in OSH

#shopt -s verbose_errexit

# I don't understand why this doesn't fail
var x = $(echo one; false; echo two)
echo 'unreachable'

pp test_ (x)
## stdout: unreachable
## status: 2
## timeout: 10
