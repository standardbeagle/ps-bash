## Imported from the Oils spec tests (shell-bugs.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### ./configure idiom
set -o errexit

if command time -f '%e %M' true; then
  echo 'supports -f'
  # BUG: this was wrong
  #time -f '%e %M' true

  # Need 'command time'
  command time -f '%e %M' true
fi

if env time -f '%e %M' true; then
  echo 'env'
  env time -f '%e %M' true
fi
## STDOUT:
supports -f
env
## END
## status: 0
## timeout: 10
