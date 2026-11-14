## Imported from the Oils spec tests (if_.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### If
if true; then
  echo if
fi
## stdout: if
## status: 0
## timeout: 10

#### else
if false; then
  echo if
else
  echo else
fi
## stdout: else
## status: 0
## timeout: 10

#### elif
if (( 0 )); then
  echo if
elif true; then
  echo elif
else
  echo else
fi
## stdout: elif
## status: 0
## timeout: 10

#### Long style
if [[ 0 -eq 1 ]]
then
  echo if
  echo if
elif true
then
  echo elif
else
  echo else
  echo else
fi
## stdout: elif
## status: 0
## timeout: 10

#### if break corner case

# This is analogous to the 'while' case in spec/loop
f() {
  if break; then
    echo hi
  fi
}
f
## stdout: hi
## status: 0
## timeout: 10
