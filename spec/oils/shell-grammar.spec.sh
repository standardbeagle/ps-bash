## Imported from the Oils spec tests (shell-grammar.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Simple command
echo
## STDOUT:

## END
## status: 0
## timeout: 10

#### Command with args
echo word word
## stdout: word word
## status: 0
## timeout: 10

#### Command with trailer
echo word word &
## stdout: word word
## status: 0
## XFAIL ps-bash gap-oils-shell-grammar shell-grammar (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### a & b
echo word_a & echo word_b
## STDOUT:
word_b
word_a
## END
## status: 0
## XFAIL ps-bash gap-oils-shell-grammar shell-grammar (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### a & b &
echo word_a & echo word_b &
## STDOUT:
word_a
word_b
## END
## status: 0
## XFAIL ps-bash gap-oils-shell-grammar shell-grammar (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### a && b
echo word_a && echo word_b 
## STDOUT:
word_a
word_b
## END
## status: 0
## timeout: 10

#### a || b
echo word_a || echo word_b 
## stdout: word_a
## status: 0
## timeout: 10

#### a && b || c
echo word_a && echo word_b || echo
## STDOUT:
word_a
word_b
## END
## status: 0
## timeout: 10

#### Invalid token
invalid
# TODO: change these to qualifiers!
## stdout:
## status: 127
## timeout: 10

#### Filename Redirect
echo 2>filename
## STDOUT:

## END
## status: 0
## timeout: 10

#### Append redirect
echo >>filename
## stdout:
## status: 0
## timeout: 10

#### Prefix redirect
<filename echo 
## stdout:
## status: 1
## XFAIL ps-bash gap-oils-shell-grammar shell-grammar (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Var assignment
NAME=foo echo >>filename
## stdout:
## status: 0
## timeout: 10

#### Brace group
{ echo
  echo
}
## STDOUT:


## END
## status: 0
## timeout: 10

#### Brace group on oneline
{ echo word_a; echo word_b; }
## STDOUT:
word_a
word_b
## END
## status: 0
## timeout: 10

#### Subshell
(echo word_a; echo word_b;)
## STDOUT:
word_a
word_b
## END
## status: 0
## timeout: 10

#### Case with last dsemi
case word_a in
  word_b) echo
    ;;
esac
## stdout:
## status: 0
## timeout: 10

#### Case with empty clauses
case word_a in
  word_b)
    ;;
  word_c)
esac
## stdout:
## status: 0
## timeout: 10

#### case item without ;; is not allowed
case word_a in
  word_a)
  word_b)
    echo
    ;;
esac
## stdout:
## status: 2
## timeout: 10

#### Case with last dsemi on same line
case word_a in
  word_b) echo ;;
esac
## stdout:
## status: 0
## timeout: 10

#### Case with 2 options
case word_a in
  word_b|word_c)
    echo word_d
    echo word_e
    ;;
  word_e)
    echo word_f
    ;;
esac
## stdout:
## status: 0
## timeout: 10

#### Case all on one line
case word_a in word_b) echo ;; word_c) echo ;; esac
## stdout:
## status: 0
## timeout: 10

#### Case all on one line without trailing ;;
case word_a in word_b) echo word_b;; word_c) echo word_c ;; esac
## stdout:
## status: 0
## timeout: 10

#### Case all on one line without trailing ;; or ;
# My ANTLR parsers don't fail here and they should.
case word_a in word_b) echo word_b;; word_c) echo word_c esac
## stdout:
## status: 2
## timeout: 10

#### case: Using ; instead of ;;
case word_a in
  word_a)
    ;
  word_b)
    echo
    ;;
esac
## stdout:
## status: 2
## timeout: 10

#### Function def
name_a() {
  echo word_a
  echo word_b
}
## stdout:
## status: 0
## timeout: 10

#### Command sub
#echo $(echo word_a; echo word_b;)
echo
## STDOUT:

## END
## status: 0
## timeout: 10

#### Subshell on multiple lines
(echo
echo
echo
)
## STDOUT:



## END
## status: 0
## timeout: 10

#### For loop
# TODO: need to add variables
for name in word_a word_b word_c
do
  echo word_x
  echo word_y
done
## STDOUT:
word_x
word_y
word_x
word_y
word_x
word_y
## END
## status: 0
## timeout: 10

#### While loop with empty lines
while ! echo word_a
do

  echo word_b

  echo word_c

done
## stdout: word_a
## status: 0
## timeout: 10

#### Until loop
until echo word_a
do
  echo word_b
  echo word_c
done
## stdout: word_a
## status: 0
## timeout: 10

#### If
if echo
then
  echo
else
  echo
fi
## STDOUT:


## END
## status: 0
## timeout: 10

#### If with then on same line
if echo; then
  echo
else
  echo
fi
## STDOUT:


## END
## status: 0
## timeout: 10

#### If with then on same line missing semicolon
# My ANTLR parsers fail to flag this.  The 'else' keyword should be unexpected.
if echo then
  echo
else
  echo
fi
## stdout:
## status: 2
## timeout: 10

#### If on one line
if echo; then echo; else echo; fi
## STDOUT:


## END
## status: 0
## timeout: 10

#### If pipe
if echo | echo word_b; then
  echo
else
  echo
fi
## STDOUT:
word_b

## END
## status: 0
## timeout: 10

#### Empty case
case word_a in
esac
## stdout:
## status: 0
## timeout: 10

#### Case without last dsemi
case word_a in
  word_b) echo
esac
## stdout:
## status: 0
## timeout: 10
