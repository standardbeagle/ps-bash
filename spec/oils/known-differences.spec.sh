## Imported from the Oils spec tests (known-differences.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### `set` output format - ifupdown-ng
export FOO=bar
set | grep bar | head -n 1
## stdout: FOO=bar
## status: 0
## timeout: 10

#### nested function declaration - xcb-util-renderutil
f() g() { echo 'hi'; }
## stdout:
## status: 2
## timeout: 10
