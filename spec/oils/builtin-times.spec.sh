## Imported from the Oils spec tests (builtin-times.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### times shows two formatted lines
output=$(times)
echo "$output" | while read line
do
	echo "$line" | egrep -q '[0-9]+m[0-9]+.[0-9]+s [0-9]+m[0-9]+.[0-9]+s' && echo "pass"
done

echo "$output" | wc -l
## STDOUT:
pass
pass
2
## END
## status: 0
## timeout: 10
