## Imported from the Oils spec tests (zsh-idioms.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### git-completion snippet

# copied directly from git completion - 2024-04

if false; then
  unset ${(M)${(k)parameters[@]}:#__gitcomp_builtin_*} 2>/dev/null
fi
echo status=$?
## stdout: status=0
## status: 0
## timeout: 10

#### asdf snippet

# copied directly from asdf - 2024-04

if false; then
  ASDF_DIR=${(%):-%x}
fi
## stdout:
## status: 0
## timeout: 10

#### zsh var sub is rejected at runtime

eval 'echo z ${(m)foo} z'
echo status=$?

eval 'echo ${x:-${(m)foo}}'
echo status=$?

# double quoted
eval 'echo "${(m)foo}"'
echo status=$?
## STDOUT:
status=1
status=1
status=1
## END
## status: 0
## XFAIL ps-bash gap-bad-subst-ends-script a bad substitution ends the whole script; bash abandons only the current top-level command (status 1) and continues
## timeout: 10
