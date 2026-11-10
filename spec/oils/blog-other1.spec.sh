## Imported from the Oils spec tests (blog-other1.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Blog Post Example
paths=`tr '\n' ':' | sed -e 's/:$//'`<<EOPATHS
/foo
/bar
/baz
EOPATHS
echo "$paths"
## STDOUT:

## END
## status: 0
## timeout: 10

#### Blog Post Example Fix
paths=`tr '\n' ':' | sed -e 's/:$//'<<EOPATHS
/foo
/bar
/baz
EOPATHS`
echo "$paths"
## STDOUT:
/foo
/bar
/baz
## END
## status: 0
## timeout: 10

#### Rewrite of Blog Post Example
paths=$(tr '\n' ':' | sed -e 's/:$//' <<EOPATHS
/foo
/bar
/baz
EOPATHS
)
echo "$paths"
## STDOUT:
/foo
/bar
/baz
## END
## status: 0
## timeout: 10

#### Simpler example
foo=`cat`<<EOM
hello world
EOM
echo "$foo"
## STDOUT:

## END
## status: 0
## timeout: 10

#### ` after here doc delimiter
foo=`cat <<EOM
hello world
EOM`
echo "$foo"
## stdout: hello world
## status: 0
## timeout: 10

#### ` on its own line
foo=`cat <<EOM
hello world
EOM
`
echo "$foo"
## stdout: hello world
## status: 0
## timeout: 10
