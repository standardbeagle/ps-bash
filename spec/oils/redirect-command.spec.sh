## Imported from the Oils spec tests (redirect-command.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### >$file touches a file
rm -f myfile
test -f myfile
echo status=$?

>myfile
test -f myfile
echo status=$?



# regression for OSH
## STDOUT:
status=1
status=0
## END
## status: 0
## timeout: 10

#### $(< $file) yields the contents of the file

seq 2 3 > myfile
foo=$(< myfile)
argv.py "$foo"
## stdout:
## status: 127
## timeout: 10

#### `< $file` behaves like $(< file)

seq 7 8 > myfile

x=`< myfile`

echo "[$x]"
## STDOUT:
[7
8]
## END
## status: 0
## timeout: 10

#### $(< file; end) is not a special case

seq 5 6 > myfile

# zsh prints the file each time!
# other shells do nothing?

foo=$(echo begin; < myfile)
echo $foo
echo ---

foo=$(< myfile; echo end)
echo $foo
echo ---

foo=$(< myfile; <myfile)
echo $foo
echo ---
## STDOUT:
begin
---
end
---

---
## END
## status: 0
## timeout: 10

#### < file in pipeline and subshell doesn't work
echo FOO > file2

# This only happens in command subs, which is weird
< file2 | tr A-Z a-z
( < file2 )
echo end
## stdout: end
## status: 0
## timeout: 10

#### Redirect in command sub
FOO=$(echo foo 1>&2)
echo $FOO
## STDOUT:

## END
## status: 0
## timeout: 10

#### Redirect in function body
fun() { echo hi; } 1>&2
fun
## stdout:
## status: 0
## timeout: 10

#### Redirect in function body AND function call
fun() { echo hi; } 1>&2
fun 2>&1
## stdout: hi
## status: 0
## timeout: 10

#### redirect bash extensions:   [[  ((  for ((

case $SH in dash|mksh) exit ;; esac

rm -f dbracket dparen for-expr

[[ x = x ]] > dbracket

(( 42 )) > dparen

for ((x = 0; x < 1; ++x)); do
  echo for-expr
done > for-expr

wc -l dbracket dparen for-expr
## STDOUT:
0 dbracket
0 dparen
1 for-expr
1 total
## END
## status: 0
## timeout: 10
## real-launcher

#### redirect if
if true; then
  echo if-body
fi >out

cat out
## stdout: if-body
## status: 0
## timeout: 10

#### redirect case
case foo in
  foo)
    echo case-body
    ;;
esac > out

cat out
## stdout: case-body
## status: 0
## timeout: 10

#### redirect while
while true; do
  echo while-body
  break
done > out

cat out
## stdout: while-body
## status: 0
## timeout: 10

#### redirect subshell
( echo foo ) 1>&2
## stdout:
## status: 0
## timeout: 10
