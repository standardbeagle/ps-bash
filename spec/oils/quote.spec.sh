## Imported from the Oils spec tests (quote.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Unquoted words
echo unquoted    words
## stdout: unquoted words
## status: 0
## timeout: 10

#### Single-quoted
echo 'single   quoted'
## stdout: single   quoted
## status: 0
## timeout: 10

#### Two single-quoted parts
echo 'two single-quoted pa''rts in one token'
## stdout: two single-quoted parts in one token
## status: 0
## timeout: 10

#### Unquoted and single quoted
echo unquoted' and single-quoted'
## stdout: unquoted and single-quoted
## status: 0
## timeout: 10

#### newline inside single-quoted string
echo 'newline
inside single-quoted string'
## STDOUT:
newline
inside single-quoted string
## END
## status: 0
## timeout: 10

#### Double-quoted
echo "double   quoted"
## stdout: double   quoted
## status: 0
## timeout: 10

#### Mix of quotes in one word
echo unquoted'  single-quoted'"  double-quoted  "unquoted
## stdout: unquoted  single-quoted  double-quoted  unquoted
## status: 0
## timeout: 10

#### Var substitution
FOO=bar
echo "==$FOO=="
## stdout: ==bar==
## status: 0
## timeout: 10

#### Var substitution with braces
FOO=bar
echo foo${FOO}
## stdout: foobar
## status: 0
## timeout: 10

#### Var substitution with braces, quoted
FOO=bar
echo "foo${FOO}"
## stdout: foobar
## status: 0
## timeout: 10

#### Var length
FOO=bar
echo "foo${#FOO}"
## stdout: foo3
## status: 0
## timeout: 10

#### Storing backslashes and then echoing them
# This is a bug fix; it used to cause problems with unescaping.
one='\'
two='\\'
echo $one $two
echo "$one" "$two"
## STDOUT:
\ \\
\ \\
## END
## status: 0
## timeout: 10

#### Backslash escapes
echo \$ \| \a \b \c \d \\
## stdout: $ | a b c d \
## status: 0
## timeout: 10

#### Backslash escapes inside double quoted string
echo "\$ \\ \\ \p \q"
## stdout: $ \ \ \p \q
## status: 0
## timeout: 10

#### C-style backslash escapes inside double quoted string
# mksh and dash implement POSIX incompatible extensions.  $ ` " \ <newline>
# are the only special ones
echo "\a \b"

# BUG
## stdout: \a \b
## status: 0
## timeout: 10

#### Literal $
echo $
## stdout: $
## status: 0
## timeout: 10

#### Quoted Literal $
echo $ "$" $
## stdout: $ $ $
## status: 0
## timeout: 10

#### Line continuation
echo foo\
$
## stdout: foo$
## status: 0
## timeout: 10

#### Line continuation inside double quotes
echo "foo\
$"
## stdout: foo$
## status: 0
## timeout: 10

#### $'' octal escapes with fewer than 3 chars
echo $'\1 \11 \11 \111' | od -A n -c | sed 's/ \+/ /g'
## STDOUT:
 001 \t \t I \n
## END
## status: 0
## timeout: 10

#### $"" is a synonym for ""
echo $"foo"
x=x
echo $"foo $x"
## STDOUT:
foo
foo x
## END
## status: 0
## timeout: 10

#### printf supports tabs
# This accepts \t by itself, hm.
printf "c1\tc2\nc3\tc4\n"
## STDOUT:
c1	c2
c3	c4
## END
## status: 0
## timeout: 10

#### $? split over multiple lines
# Same with $$, etc.  OSH won't do this because $? is a single token.
echo $\
?

#
# Bad quotes
#

# TODO: Also test unterminated quotes inside ${} and $()
## stdout: 0
## status: 0
## XFAIL ps-bash gap-oils-quote quote (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Unterminated single quote

## stdout:
## status: 0
## timeout: 10

#### Unterminated double quote


#
# TODO: Might be another section?
#
## stdout:
## status: 0
## timeout: 10

#### Semicolon
echo separated; echo by semi-colon

#
# TODO: Variable substitution operators.
#
## STDOUT:
separated
by semi-colon
## END
## status: 0
## timeout: 10

#### No tab escapes within single quotes
# dash and mksh allow this, which is a BUG.
# POSIX says: "Enclosing characters in single-quotes ( '' ) shall preserve the
# literal value of each character within the single-quotes. A single-quote
# cannot occur within single-quotes"
echo 'a\tb'

# See if it supports ANSI C escapes.  Bash supports this, but dash does NOT.  I
# guess dash you would do IFS=$(printf '\n\t')
## stdout: a\tb
## status: 0
## timeout: 10

#### $''
echo $'foo'
## stdout: foo
## status: 0
## timeout: 10

#### $'' with quotes
echo $'single \' double \"'
## stdout: single ' double "
## status: 0
## timeout: 10

#### $'' with newlines
echo $'col1\ncol2\ncol3'
# In dash, \n is special within single quotes
## STDOUT:
col1
col2
col3
## END
## status: 0
## timeout: 10

#### $'' octal escapes don't have leading 0
# echo -e syntax is echo -e \0377
echo -n $'\001' $'\377' | od -A n -c | sed 's/ \+/ /g'
## STDOUT:
 001 377
## END
## status: 0
## timeout: 10

#### OSH allows invalid backslashes
case $SH in dash|mksh) exit ;; esac

w=$'\uZ'
x=$'\u{03bc'
y=$'\z'
echo $w $x $y
## stdout: \uZ \u{03bc \z
## status: 0
## timeout: 10
## real-launcher

#### $'' supports \cA escape for Ctrl-A - mask with 0x1f

# note: AT&T ksh supports this too

case $SH in dash|ash) exit ;; esac

show_bytes() {
  # -A n - no file offset
  od -A n -t c -t x1
}

# this isn't special
# mksh doesn't like it
#echo -n $'\c' | show_bytes

echo -n $'\c0\c9-' | show_bytes
echo

# control chars are case insensitive
echo -n $'\ca\cz' | show_bytes
echo

echo -n $'\cA\cZ' | show_bytes
echo

echo -n $'\c-\c+\c"' | show_bytes
## STDOUT:
 020 031   -
  10  19  2d

 001 032
  01  1a

 001 032
  01  1a

  \r  \v 002
  0d  0b  02
## END
## status: 0
## timeout: 10
## real-launcher

#### \c' is an escape, unlike bash

# mksh and ksh agree this is an esacpe

case $SH in dash|ash) exit ;; esac

show_bytes() {
  # -A n - no file offset
  od -A n -t c -t x1
}

# this isn't special
# mksh doesn't like it
echo -n $'\c'' | show_bytes
## stdout:
## status: 2
## XFAIL ps-bash gap-oils-quote quote (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
