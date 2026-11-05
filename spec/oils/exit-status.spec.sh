## Imported from the Oils spec tests (exit-status.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Truncating 'return' status
f() { return 255; }; f
echo status=$?

f() { return 256; }; f
echo status=$?

f() { return 257; }; f
echo status=$?

echo ===

f() { return -1; }; f
echo status=$?

f() { return -2; }; f
echo status=$?


# dash aborts on bad exit code
## STDOUT:
status=255
status=0
status=1
===
status=255
status=254
## END
## status: 0
## XFAIL ps-bash gap-oils-exit-status exit-status (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### If empty command
if ''; then echo TRUE; else echo FALSE; fi
## stdout: FALSE
## status: 0
## XFAIL ps-bash gap-oils-exit-status exit-status (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### If subshell true
if `true`; then echo TRUE; else echo FALSE; fi
## stdout: TRUE
## status: 0
## timeout: 10

#### If subshell true WITH OUTPUT is different
if `sh -c 'echo X; true'`; then echo TRUE; else echo FALSE; fi
## stdout: FALSE
## status: 0
## timeout: 10

#### If subshell true WITH ARGUMENT
if `true` X; then echo TRUE; else echo FALSE; fi
## stdout: FALSE
## status: 0
## XFAIL ps-bash gap-oils-exit-status exit-status (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### If subshell false -- exit code is propagated in a weird way (strict_argv prevents)
if `false`; then echo TRUE; else echo FALSE; fi
## stdout: FALSE
## status: 0
## timeout: 10

#### Exit code when command sub evaluates to empty str, e.g. `false` (#2416)

# OSH had a bug here
`true`; echo $?
`false`; echo $?
$(true); echo $?
$(false); echo $?
echo ---

# OSH and others agree on these
eval true; echo $?
eval false; echo $?
`echo true`; echo $?
`echo false`; echo $?
## STDOUT:
0
1
0
1
---
0
1
0
1
## END
## status: 0
## XFAIL ps-bash gap-oils-exit-status exit-status (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### More test cases with empty argv

true $(false)
echo status=$?

$(exit 42)
echo status=$?

$(exit 42) $(exit 43)
echo status=$?
## STDOUT:
status=0
status=42
status=43
## END
## status: 0
## XFAIL ps-bash gap-oils-exit-status exit-status (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Truncating 'exit' status

$SH -c 'exit 255'
echo status=$?

$SH -c 'exit 256'
echo status=$?

$SH -c 'exit 257'
echo status=$?

echo ===

$SH -c 'exit -1'
echo status=$?

$SH -c 'exit -2'
echo status=$?
## STDOUT:
status=255
status=0
status=1
===
status=255
status=254
## END
## status: 0
## XFAIL ps-bash gap-oils-exit-status exit-status (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### subshell OverflowError https://github.com/oilshell/oil/issues/996

# We have to capture stderr here 

filter_err() {
  # check for bash/dash/mksh messages, and unwanted Python OverflowError
  egrep -o 'Illegal number|bad number|return: can only|expected a small integer|OverflowError'
  return 0
}

# true; disables subshell optimization!

# exit status too big, but integer isn't
$SH -c 'true; ( return 2147483647; )' 2>err.txt
echo status=$?
cat err.txt | filter_err

# now integer is too big
$SH -c 'true; ( return 2147483648; )' 2> err.txt
echo status=$?
cat err.txt | filter_err

# even bigger
$SH -c 'true; ( return 2147483649; )' 2> err.txt
echo status=$?
cat err.txt | filter_err

echo
echo '--- negative ---'

# negative vlaues
$SH -c 'true; ( return -2147483648; )' 2>err.txt
echo status=$?
cat err.txt | filter_err

# negative vlaues
$SH -c 'true; ( return -2147483649; )' 2>err.txt
echo status=$?
cat err.txt | filter_err


# osh-cpp checks overflow, but osh-py doesn't


# mksh behaves similarly, uses '1' as its "bad status" status!


# dash is similar, but seems to reject negative numbers


# bash disallows return at top level
## STDOUT:
status=2
return: can only
status=2
return: can only
status=2
return: can only

--- negative ---
status=2
return: can only
status=2
return: can only
## END
## status: 0
## XFAIL ps-bash gap-oils-exit-status exit-status (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher

#### func subshell OverflowError https://github.com/oilshell/oil/issues/996

# We have to capture stderr here 

filter_err() {
  # check for bash/dash/mksh messages, and unwanted Python OverflowError
  egrep -o 'Illegal number|bad number|return: can only|expected a small integer|OverflowError'
  return 0
}

# exit status too big, but integer isn't
$SH -c 'f() ( return 2147483647; ); f' 2>err.txt
echo status=$?
cat err.txt | filter_err

# now integer is too big
$SH -c 'f() ( return 2147483648; ); f' 2> err.txt
echo status=$?
cat err.txt | filter_err

# even bigger
$SH -c 'f() ( return 2147483649; ); f' 2> err.txt
echo status=$?
cat err.txt | filter_err



# bash truncates it to 0 here, I guess it's using 64 bit integers



# Weird case from bash-help mailing list.
#
# "Evaluations of backticks in if statements".  It doesn't relate to if
# statements but to $?, since && and || behave the same way.

# POSIX has a special rule for this.  In OSH strict_argv is preferred so it
# becomes a moot point.  I think this is an artifact of the
# "stateful"/imperative nature of $? -- it can be "left over" from a prior
# command, and sometimes the prior argv is [].  OSH has a more "functional"
# implementation so it doesn't have this weirdness.
## STDOUT:
status=255
status=0
status=1
## END
## status: 0
## XFAIL ps-bash gap-oils-exit-status exit-status (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
