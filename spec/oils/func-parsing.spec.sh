## Imported from the Oils spec tests (func-parsing.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Incomplete Function

## stdout:
## status: 0
## timeout: 10

#### Incomplete Function 2

## stdout:
## status: 0
## timeout: 10

#### Bad function

## stdout:
## status: 0
## timeout: 10

#### Unbraced function body.
# dash allows this, but bash does not.  The POSIX grammar might not allow
# this?  Because a function body needs a compound command.
# function_body    : compound_command
#                  | compound_command redirect_list  /* Apply rule 9 */
## stdout:
## status: 0
## timeout: 10

#### Function with spaces, to see if ( and ) are separate tokens.
# NOTE: Newline after ( is not OK.
fun ( ) { echo in-func; }; fun
## stdout: in-func
## status: 0
## timeout: 10

#### subshell function
# bash allows this.
i=0
j=0
inc() { i=$((i+5)); }
inc_subshell() ( j=$((j+5)); )
inc
inc_subshell
echo $i $j
## stdout: 5 0
## status: 0
## timeout: 10

#### Hard case, function with } token in it
rbrace() { echo }; }; rbrace
## stdout: }
## status: 0
## XFAIL ps-bash gap-oils-func-parsing func-parsing (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### . in function name
# bash accepts; dash doesn't
func-name.ext ( ) { echo func-name.ext; }
func-name.ext
## stdout: func-name.ext
## status: 0
## timeout: 10

#### = in function name
# WOW, bash is so lenient. foo=bar is a command, I suppose.  I  think I'm doing
# to disallow this one.
func-name=ext ( ) { echo func-name=ext; }
func-name=ext
## stdout: func-name=ext
## status: 0
## timeout: 10

#### Function name with $
$foo-bar() { ls ; }
## stdout:
## status: 1
## timeout: 10

#### Function name with command sub
foo-$(echo hi)() { ls ; }
## stdout:
## status: 1
## timeout: 10

#### Function name with !
# bash allows this; dash doesn't.
foo!bar() { ls ; }
## stdout:
## status: 0
## timeout: 10

#### Function name with -
# bash allows this; dash doesn't.
foo-bar() { ls ; }
## stdout:
## status: 0
## timeout: 10

#### Break after ) is OK.
# newline is always a token in "normal" state.
echo hi; fun ( )
{ echo in-func; }
fun
## STDOUT:
hi
in-func
## END
## status: 0
## timeout: 10

#### Nested definition
# A function definition is a command, so it can be nested
fun() {
  nested_func() { echo nested; }
  nested_func
}
fun
## stdout: nested
## status: 0
## timeout: 10
