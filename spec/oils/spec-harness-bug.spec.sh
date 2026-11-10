## Imported from the Oils spec tests (spec-harness-bug.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### osh-native duplicates stdin - is this a test harness issue?

echo $0 | grep -o sh
## STDOUT:
sh
sh
## END
## status: 0
## real-launcher
## timeout: 10
