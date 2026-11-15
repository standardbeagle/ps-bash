## Imported from the Oils spec tests (command-sub-ksh.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### ${ echo hi;}

x=${ echo hi;}
echo "[$x]"
echo

# trailing space allowed
x=${ echo one; echo two; }
echo "[$x]"
echo

myfunc() {
  echo ' 3 '
  echo ' 4 5 '
}

x=${ myfunc;}
echo "[$x]"
echo

# SYNTAX ERROR
x=${myfunc;}
echo "[$x]"
## STDOUT:
[]

[]

[]

[]
## END
## status: 0
## timeout: 10

#### ${|REPLY=hi}

x=${|y=" reply var "; REPLY=$y}
echo "[$x]"
echo

echo '  from file  ' > tmp.txt

x=${|read -r < tmp.txt}
echo "[$x]"
echo

# SYNTAX ERROR
x=${ |REPLY=zz}
echo "[$x]"
## STDOUT:
[]

[]

[]
## END
## status: 0
## timeout: 10

#### for loop / case

x=${ for i in a b; do echo -$i-; done; }
echo "$x"

y=${|for i in a b; do REPLY+="-$i-"; done; }
echo "$y"

echo

x2=${ case foo in foo) echo sh-case ;; esac; }
echo "$x2"

y2=${|case foo in foo) REPLY=sh-case ;; esac; }
echo "$y2"
## STDOUT:





## END
## status: 0
## timeout: 10
