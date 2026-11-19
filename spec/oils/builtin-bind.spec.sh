## Imported from the Oils spec tests (builtin-bind.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### bind -p -P to print function names and key bindings

# silly workaround for spec test format - change # comment to %
bind -p | grep vi-subst | sed 's/^#/%/'
echo

bind -P | grep vi-subst
## STDOUT:
% vi-subst (not bound)

vi-subst is not bound to any keys
## END
## status: 0
## XFAIL ps-bash wontfix-interactive-bind bind lists and edits GNU readline's keymaps; ps-bash's line editor is not readline (intentional-differences.md, Interactive shell)
## timeout: 10

#### bind -s -S accepted

# TODO: add non-trivial tests here

bind -s
bind -S
## stdout:
## status: 0
## timeout: 10

#### bind -v -V accepted

bind -v | grep blink-matching-paren
echo

# transform silly quote so we don't mess up syntax highlighting
bind -V | grep blink-matching-paren | sed "s/\`/'/g"
## STDOUT:
set blink-matching-paren off

blink-matching-paren is set to 'off'
## END
## status: 0
## XFAIL ps-bash wontfix-interactive-bind bind lists and edits GNU readline's keymaps; ps-bash's line editor is not readline (intentional-differences.md, Interactive shell)
## timeout: 10

#### bind -q

bind -q zz-bad
echo status=$?

# bash prints message to stdout

bind -q vi-subst
echo status=$?

bind -q yank
echo status=$?
## STDOUT:
status=1
vi-subst is not bound to any keys.
status=1
yank can be invoked via "\C-y".
status=0
## END
## status: 0
## XFAIL ps-bash wontfix-interactive-bind bind lists and edits GNU readline's keymaps; ps-bash's line editor is not readline (intentional-differences.md, Interactive shell)
## timeout: 10

#### bind -X
bind -X | grep -oF '\C-o\C-s\C-h'
echo status=$?

bind -x '"\C-o\C-s\C-h": echo foo'
bind -X | grep -oF '\C-o\C-s\C-h'
bind -X | grep -oF 'echo foo'
echo status=$?
## STDOUT:
status=1
\C-o\C-s\C-h
echo foo
status=0
## END
## status: 0
## XFAIL ps-bash wontfix-interactive-bind bind lists and edits GNU readline's keymaps; ps-bash's line editor is not readline (intentional-differences.md, Interactive shell)
## timeout: 10

#### bind -m with bind -x/-X
bind -X | grep -oF 'emacs|vi'
echo status=$?

bind -m emacs -x '"\C-o\C-s\C-h": echo emacs'
bind -m emacs -X | grep -oF 'emacs'
echo status=$?

bind -m vi -x '"\C-o\C-s\C-h": echo vi'
bind -m vi -X | grep -oF 'vi'
echo status=$?
## STDOUT:
status=1
emacs
status=0
vi
status=0
## END
## status: 0
## XFAIL ps-bash wontfix-interactive-bind bind lists and edits GNU readline's keymaps; ps-bash's line editor is not readline (intentional-differences.md, Interactive shell)
## timeout: 10

#### bind -r
bind -q yank | grep -oF '\C-o\C-s\C-h'
echo status=$?

bind '"\C-o\C-s\C-h": yank'
bind -q yank | grep -oF '\C-o\C-s\C-h'
echo status=$?

bind -r "\C-o\C-s\C-h"
bind -q yank | grep -oF '\C-o\C-s\C-h'
echo status=$?
## STDOUT:
status=1
\C-o\C-s\C-h
status=0
status=1
## END
## status: 0
## XFAIL ps-bash wontfix-interactive-bind bind lists and edits GNU readline's keymaps; ps-bash's line editor is not readline (intentional-differences.md, Interactive shell)
## timeout: 10

#### bind -r of bind -x commands
bind -X | grep -oF '\C-o\C-s\C-h'
echo status=$?

bind -x '"\C-o\C-s\C-h": echo foo'
bind -X | grep -oF '\C-o\C-s\C-h'
echo status=$?

bind -r "\C-o\C-s\C-h"
bind -X | grep -oF '\C-o\C-s\C-h'
echo status=$?
## STDOUT:
status=1
\C-o\C-s\C-h
status=0
status=1
## END
## status: 0
## XFAIL ps-bash wontfix-interactive-bind bind lists and edits GNU readline's keymaps; ps-bash's line editor is not readline (intentional-differences.md, Interactive shell)
## timeout: 10
