## Imported from the Oils spec tests (var-sub.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Bad var sub
echo ${a&}
## stdout:
## status: 1
## timeout: 10

#### Braced block inside ${}
# NOTE: This bug was in bash 4.3 but fixed in bash 4.4.
echo ${foo:-$({ ls /bin/ls; })}
## stdout: /bin/ls
## status: 0
## XFAIL ps-bash gap-oils-var-sub var-sub (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Nested ${}
bar=ZZ
echo ${foo:-${bar}}
## stdout: ZZ
## status: 0
## timeout: 10

#### Filename redirect with "$@"
# bash - ambiguous redirect -- yeah I want this error
#   - But I want it at PARSE time?  So is there a special DollarAtPart?
#     MultipleArgsPart?
# mksh - tries to create '_tmp/var-sub1 _tmp/var-sub2'
# dash - tries to create '_tmp/var-sub1 _tmp/var-sub2'
fun() {
  echo hi > "$@"
}
fun _tmp/var-sub1 _tmp/var-sub2
## stdout:
## status: 1
## timeout: 10

#### Descriptor redirect to bad "$@"
# All of them give errors:
# dash - bad fd number, parse error?
# bash - ambiguous redirect
# mksh - illegal file descriptor name
set -- '2 3' 'c d'
echo hi 1>& "$@"
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-var-sub var-sub (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Here doc with bad "$@" delimiter
# bash - syntax error
# dash - syntax error: end of file unexpected
# mksh - runtime error: here document unclosed
#
# What I want is syntax error: bad delimiter!
#
# This means that "$@" should be part of the parse tree then?  Anything that
# involves more than one token.
fun() {
  cat << "$@"
hi
1 2
}
fun 1 2
## stdout:
## status: 2
## timeout: 10
