## Imported from the Oils spec tests (vars-bash.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases needing the rest of the Oils harness ($REPO_ROOT, ysh, ...) are omitted; `$SH` cases run on the real launcher.
#### $SHELL is set to what is in /etc/passwd

sh=$(which $SH)

unset SHELL

prog='
if test -n "$SHELL"; then
  # the exact value is different on CI, so do not assert
  echo SHELL is set
  echo SHELL=$SHELL >&2
fi
'

$SH -c "$prog"

$SH -i -c "$prog"

# make it a login shell
$SH -l -c "$prog"
## STDOUT:
SHELL is set
SHELL is set
SHELL is set
## END
## status: 0
## XFAIL ps-bash gap-shell-var-default SHELL is not set at startup when the environment lacks it (bash sets it from the passwd entry)
## timeout: 10
## real-launcher
