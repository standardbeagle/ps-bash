# Plain export fixture for Invoke-BashEval integration tests
# 20 exports + 5 unsets

export FOO=bar
export BAR=baz
export BAZ=qux
export NUM=42
export FLOAT=3.14
export BOOL=true
export STR="hello world"
export EMPTY=""
export PATH_ADD=/usr/local/bin
export HOME=/tmp/testhome
export USER=testuser
export SHELL=/bin/bash
export LANG=en_US.UTF-8
export TERM=xterm-256color
export EDITOR=vim
export PAGER=less
export HISTSIZE=1000
export PS1='\$ '
export PS2='> '

unset REMOVE_A
unset REMOVE_B
unset REMOVE_C
unset REMOVE_D
unset REMOVE_E
