## Imported from the Oils spec tests (print-source-code.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases needing the rest of the Oils harness ($REPO_ROOT, ysh, ...) are omitted; `$SH` cases run on the real launcher.
#### typeset -f prints function source code
: prefix; myfunc() { echo serialized; }

code=$(typeset -f myfunc)

$SH -c "$code; myfunc"
## stdout: serialized
## status: 0
## timeout: 10
## real-launcher

#### typeset -f with function keyword (ksh style)
: prefix; function myfunc {
	echo serialized
}

code=$(typeset -f myfunc)

$SH -c "$code; myfunc"
## stdout: serialized
## status: 0
## timeout: 10
## real-launcher

#### typeset -f prints function source code - nested functions
outer() {
  echo outer
  : prefix; inner() {
    echo inner
  }
}

code=$(typeset -f outer)

if false; then
  echo ---
  echo $code
  echo ---
fi

$SH -c "$code; outer; inner"
## STDOUT:
outer
inner
## END
## status: 0
## XFAIL ps-bash gap-oils-print-source-code print-source-code (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### non-{ } function bodies can be serialized (rare)

# TODO: we can add more of these

f() ( echo 'subshell body' )

code=$(typeset -f f)

$SH -c "$code; f"
## stdout: subshell body
## status: 0
## XFAIL ps-bash gap-oils-print-source-code print-source-code (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
