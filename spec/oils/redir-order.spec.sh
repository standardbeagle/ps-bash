## Imported from the Oils spec tests (redir-order.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### echo `cat OSCFLAGS` "world" > OSCFLAGS (from Alpine imap)

echo hello > OSCFLAGS
echo `cat OSCFLAGS` "world" > OSCFLAGS
#echo $(cat OSCFLAGS) "world" > OSCFLAGS
cat OSCFLAGS
## stdout: hello world
## status: 0
## timeout: 10

#### subshell + redirect order

echo hello > OSCFLAGS
(echo `cat OSCFLAGS` "world") > OSCFLAGS
cat OSCFLAGS
## stdout: world
## status: 0
## timeout: 10

#### for word + redirect order

echo hello > OSCFLAGS
for x in `cat OSCFLAGS` world; do
  echo $x
done > OSCFLAGS
cat OSCFLAGS
## stdout: world
## status: 0
## timeout: 10

#### case word + redirect order

echo hello > OSCFLAGS
case `cat OSCFLAGS` in
  hello)
    echo hello
    ;;
  *)
    echo other
    ;;
esac > OSCFLAGS
cat OSCFLAGS
## stdout: other
## status: 0
## XFAIL ps-bash gap-oils-redir-order redir-order (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### [[ + redirect order
case $SH in dash|ash) exit ;; esac

echo hello > OSCFLAGS

[[ `cat OSCFLAGS` = hello ]] > OSCFLAGS
echo status=$?

# it is the empty string!
[[ `cat OSCFLAGS` = '' ]] > OSCFLAGS
echo status=$?
## STDOUT:
status=1
status=0
## END
## status: 0
## timeout: 10
## real-launcher
