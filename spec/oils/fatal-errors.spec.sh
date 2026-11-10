## Imported from the Oils spec tests (fatal-errors.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases needing the rest of the Oils harness ($REPO_ROOT, ysh, ...) are omitted; `$SH` cases run on the real launcher.
#### Unrecoverable: divide by zero in redirect word

$SH -c '
echo hi > file$(( 42 / 0 )) in
echo inside=$?
'
echo outside=$?



# bash makes the command fail


# bash makes the command fail
## STDOUT:
inside=1
outside=0
## END
## status: 0
## timeout: 10
## real-launcher

#### Unrecoverable: divide by zero in case

$SH -c '
case $(( 42 / 0 )) in
  (*) echo hi ;;
esac
echo inside=$?
'
echo outside=$?

echo ---

$SH -c '
case foo in
  ( $(( 42 / 0 )) )
    echo hi
    ;;
esac
echo inside=$?
'
echo outside=$?
## STDOUT:
inside=1
outside=0
---
inside=1
outside=0
## END
## status: 0
## XFAIL ps-bash gap-oils-fatal-errors fatal-errors (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### Unrecoverable: ${undef?message}

$SH -c '
echo ${undef?message}
echo inside=$?
'
echo outside=$?

$SH -c '
case ${undef?message} in 
  (*) echo hi ;;
esac
echo inside=$?
'
echo outside=$?
## STDOUT:
outside=127
outside=127
## END
## status: 0
## timeout: 10
## real-launcher

#### ${undef} with nounset

$SH -c '
set -o nounset
case ${undef} in 
  (*) echo hi ;;
esac
echo inside=$?
'
echo outside=$?
## stdout: outside=127
## status: 0
## timeout: 10
## real-launcher
