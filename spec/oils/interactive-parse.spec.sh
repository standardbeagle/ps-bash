## Imported from the Oils spec tests (interactive-parse.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases needing the rest of the Oils harness ($REPO_ROOT, ysh, ...) are omitted; `$SH` cases run on the real launcher.
#### parse if

case $SH in zsh) exit ;; esac

export PS1='[PS1]'

echo 'if true
then
  echo hi
fi' | $SH -i

if test -z "$OILS_VERSION"; then
  echo '^D'  # fudge
fi



# hm somehow bash prints it more nicely; code is echo'd to stderr
## STDOUT:
hi
^D
## END
## status: 0
## timeout: 10
## real-launcher
