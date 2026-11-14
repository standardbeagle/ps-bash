## Imported from the Oils spec tests (unicode.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### $'' does NOT check that 0x110000 is too big at parse time

py-repr() {
  python2 -c 'import sys; print repr(sys.argv[1])'  "$@"
}

py-repr $'\U00110000'
## stdout:
## status: 127
## timeout: 10

#### $'' does not check for surrogate range at parse time

py-repr() {
  python2 -c 'import sys; print repr(sys.argv[1])'  "$@"
}

py-repr $'\udc00'

py-repr $'\U0000dc00' 
## stdout:
## status: 127
## timeout: 10

#### OSH source code doesn't have to be valid Unicode (like other shells)

# Should YSH be different?  It would be nice.
# We would have to validate all Lit_Chars tokens, and the like.
#
# The logical place to put that would be in osh/word_parse.py where we read
# single and double quoted strings.  Although there might be a global lexer
# hack for Id.Lit_Chars tokens.  Would that catch here docs though?

# Test all the lexing contexts
cat >unicode.sh << 'EOF'
echo μ 'μ' "μ" $'μ'
EOF

# Show that all lexer modes recognize unicode sequences
#
# Oh I guess we need to check here docs too?

#$SH -n unicode.sh

$SH unicode.sh

# Trim off the first byte of mu
sed 's/\xce//g' unicode.sh > not-unicode.sh

echo --
$SH not-unicode.sh | od -A n -t x1



# dash and ash don't support $''
## STDOUT:
μ μ μ μ
--
 bc 20 bc 20 bc 20 bc 0a
## END
## status: 0
## XFAIL ps-bash gap-sed-byte-escape sed \xce does not match one byte of a UTF-8 character: sed works on decoded text
## timeout: 10
## real-launcher

#### Unicode escapes \u03bc \U000003bc in $'', echo -e, printf

case $SH in dash|ash) exit ;; esac

echo $'\u03bc \U000003bc'

echo -e '\u03bc \U000003bc'

printf '\u03bc \U000003bc\n'
## STDOUT:
μ μ
μ μ
μ μ
## END
## status: 0
## timeout: 10
## real-launcher

#### Max code point U+10ffff can escaped with $''  printf  echo -e

case $SH in dash|ash) exit ;; esac

py-repr() {
  python2 -c 'import sys; print repr(sys.argv[1])'  "$@"
}

py-repr $'\U0010ffff'
py-repr $(echo -e '\U0010ffff')
py-repr $(printf '\U0010ffff')



# Unicode replacement char 
## stdout:
## status: 127
## timeout: 10
## real-launcher

#### printf / echo -e do NOT check max code point at runtime
case $SH in mksh) exit ;; esac

py-repr() {
  python2 -c 'import sys; print repr(sys.argv[1])'  "$@"
}

e="$(echo -e '\U00110000')"
echo status=$?
py-repr "$e"

p="$(printf '\U00110000')"
echo status=$?
py-repr "$p"
## STDOUT:
status=0
status=0
## END
## status: 127
## timeout: 10
## real-launcher

#### printf / echo -e do NOT check surrogates at runtime
case $SH in mksh) exit ;; esac

py-repr() {
  python2 -c 'import sys; print repr(sys.argv[1])'  "$@"
}

e="$(echo -e '\udc00')"
echo status=$?
py-repr "$e"

e="$(echo -e '\U0000dc00')"
echo status=$?
py-repr "$e"

p="$(printf '\udc00')"
echo status=$?
py-repr "$p"

p="$(printf '\U0000dc00')"
echo status=$?
py-repr "$p"
## STDOUT:
status=0
status=0
status=0
status=0
## END
## status: 127
## timeout: 10
## real-launcher
