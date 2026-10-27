## Imported from the Oils spec tests (blog1.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### ${##}
set -- $(seq 25)
echo ${##}
## stdout: 2
## status: 0
## XFAIL ps-bash gap-oils-blog1 blog1 (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ${###}
set -- $(seq 25)
echo ${###}
## stdout: 25
## status: 0
## XFAIL ps-bash gap-oils-blog1 blog1 (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ${####}
set -- $(seq 25)
echo ${####}
## stdout: 25
## status: 0
## XFAIL ps-bash gap-oils-blog1 blog1 (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ${##2}
set -- $(seq 25)
echo ${##2}
## stdout: 5
## status: 0
## XFAIL ps-bash gap-oils-blog1 blog1 (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ${###2}
set -- $(seq 25)
echo ${###2}
## stdout: 5
## status: 0
## XFAIL ps-bash gap-oils-blog1 blog1 (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ${1####}
set -- '####'
echo ${1####}
## stdout: ##
## status: 0
## timeout: 10

#### ${1#'###'}
set -- '####'
echo ${1#'###'}
## stdout: #
## status: 0
## timeout: 10

#### ${#1#'###'}
set -- '####'
echo ${#1#'###'}
# dash and zsh accept; mksh/bash/osh don't.
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-blog1 blog1 (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Julia example from spec/oil-user-feedback

case $SH in dash|mksh|zsh) exit ;; esac

git-branch-merged() {
  cat <<EOF
  foo
* bar
  baz
  master
EOF
}

shopt -s lastpipe  # required for bash, not OSH

branches=()  # dangerous when set -e is on
git-branch-merged | while read -r line; do
  line=${line# *}  # strip leading spaces
  if [[ $line != 'master' && ! ${line:0:1} == '*' ]]; then
    branches+=("$line")
  fi
done

if [[ ${#branches[@]} -eq 0 ]]; then
  echo "No merged branches"
else
  echo git branch -D "${branches[@]}"
fi
## stdout: git branch -D foo baz
## status: 0
## XFAIL ps-bash gap-oils-blog1 blog1 (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
