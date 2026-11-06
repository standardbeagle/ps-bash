## Imported from the Oils spec tests (bool-parse.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### test builtin - Unexpected trailing word '--' (#2409)

# Minimal repro of sqsh build error
set -- -o; test $# -ne 0 -a "$1" != "--"
echo status=$?

# Now hardcode $1
test $# -ne 0 -a "-o" != "--"
echo status=$?

# Remove quotes around -o
test $# -ne 0 -a -o != "--"
echo status=$?

# How about a different flag?
set -- -z; test $# -ne 0 -a "$1" != "--"
echo status=$?

# A non-flag?
set -- z; test $# -ne 0 -a "$1" != "--"
echo status=$?
## STDOUT:
status=0
status=0
status=0
status=0
status=0
## END
## status: 0
## timeout: 10

#### test builtin: ( = ) is confusing: equality test or non-empty string test

# here it's equality
test '(' = ')'
echo status=$?

# here it's like -n =
test 0 -eq 0 -a '(' = ')'
echo status=$?
## STDOUT:
status=1
status=0
## END
## status: 0
## timeout: 10

#### test builtin: ( == ) is confusing: equality test or non-empty string test

# here it's equality
test '(' == ')'
echo status=$?

# here it's like -n ==
test 0 -eq 0 -a '(' == ')'
echo status=$?
## STDOUT:
status=1
status=0
## END
## status: 0
## timeout: 10

#### Allowed: [[ = ]] and [[ == ]]

[[ = ]]
echo status=$?
[[ == ]]
echo status=$?
## STDOUT:
status=0
status=0
## END
## status: 0
## timeout: 10

#### Not allowed: [[ ) ]] and [[ ( ]]

[[ ) ]]
echo status=$?
[[ ( ]]
echo status=$?
## stdout:
## status: 2
## timeout: 10

#### test builtin: ( x ) behavior is the same in both cases

test '(' x ')'
echo status=$?

test 0 -eq 0 -a '(' x ')'
echo status=$?
## STDOUT:
status=0
status=0
## END
## status: 0
## timeout: 10

#### [ -f = ] and [ -f == ]

[ -f = ]
echo status=$?
[ -f == ]
echo status=$?
## STDOUT:
status=1
status=1
## END
## status: 0
## timeout: 10

#### [[ -f -f ]] and [[ -f == ]]
[[ -f -f ]]
echo status=$?

[[ -f == ]]
echo status=$?
## STDOUT:
status=1
status=1
## END
## status: 0
## timeout: 10
