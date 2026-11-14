## Imported from the Oils spec tests (history-expand.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases needing the rest of the Oils harness ($REPO_ROOT, ysh, ...) are omitted; `$SH` cases run on the real launcher.
#### !! expansion

# file in this test case directory
HISTFILE=myhistory

i-shell() {
  $SH --rcfile /dev/null "$@"
}

# -i -c is not interactive
i-shell -i -c '
echo one
echo !!
'

# -i is interactive
echo '
echo two
echo !!
exit
' | i-shell -i
## STDOUT:
one
!!
two
echo two
## END
## status: 0
## timeout: 10
## real-launcher
