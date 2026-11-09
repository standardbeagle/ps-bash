## Imported from the Oils spec tests (hay.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### hay builtin usage

hay define
echo status=$?

hay define -- package user
echo status=$?

hay pp | wc -l | read n
echo read $?
test $n -gt 0
echo greater $?
## STDOUT:
status=127
status=127
read 0
greater 2
## END
## status: 0
## timeout: 10

#### hay reset
shopt --set parse_brace

hay define package

hay eval :a {
  package foo
  echo "package $?"
}

hay reset  # no more names

echo "reset $?"

hay eval :b {
  package foo
  echo "package $?"
}
## stdout: package 127
## status: 2
## XFAIL ps-bash gap-oils-hay hay (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### hay eval can't be nested
shopt --set parse_brace

hay eval :foo {
  echo foo
  hay eval :bar {
    echo bar
  }
}
## STDOUT:
foo
bar
## END
## status: 2
## XFAIL ps-bash gap-oils-hay hay (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### Parsing Nested Attributes nodes (bug fix)

shopt --set parse_brace parse_equals

hay define Package/License

Package glibc {
  version = '1.0'

  License {
    path = 'LICENSE.txt'
  }

  other = 'foo'
}

json write (_hay()) | jq '.children[0].children[0].attrs' > actual.txt

diff -u - actual.txt <<EOF
{
  "path": "LICENSE.txt"
}
EOF

invalid = 'syntax'  # parse error
## stdout:
## status: 2
## timeout: 10

#### hay eval Attr node, and JSON
shopt --set parse_brace parse_equals

hay define Package User

hay eval :result {
  Package foo {
    # not doing floats now
    int = 42
    bool = true
    mynull = null
    mystr = $'spam\n'

    mylist = [5, 'foo', {}]
    # TODO: Dict literals need to be in insertion order!
    #mydict = {alice: 10, bob: 20}
  }

  User alice
}

# Note: using jq to normalize
json write (result) | jq . > out.txt

diff -u - out.txt <<EOF
{
  "source": null,
  "children": [
    {
      "type": "Package",
      "args": [
        "foo"
      ],
      "children": [],
      "attrs": {
        "int": 42,
        "bool": true,
        "mynull": null,
        "mystr": "spam\n",
        "mylist": [
          5,
          "foo",
          {}
        ]
      }
    },
    {
      "type": "User",
      "args": [
        "alice"
      ]
    }
  ]
}
EOF

echo "diff $?"
## stdout:
## status: 2
## timeout: 10

#### hay eval shell node, and JSON
shopt --set parse_brace parse_equals

hay define TASK

hay eval :result {
  TASK { echo hi }

  TASK {
    echo one
    echo two
  }
}

#= result
json write (result) | jq . > out.txt

diff -u - out.txt <<'EOF'
{
  "source": null,
  "children": [
    {
      "type": "TASK",
      "args": [],
      "location_str": "[ stdin ]",
      "location_start_line": 6,
      "code_str": "         echo hi "
    },
    {
      "type": "TASK",
      "args": [],
      "location_str": "[ stdin ]",
      "location_start_line": 8,
      "code_str": "        \n    echo one\n    echo two\n  "
    }
  ]
}
EOF
## STDOUT:
one
two
## END
## status: 2
## XFAIL ps-bash gap-oils-hay hay (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### hay eval with shopt -s ysh:all
shopt --set parse_brace parse_equals parse_proc

hay define Package

const x = 'foo bar'

hay eval :result {
  Package foo {
    # set -e should be active!
    #false

    version = '1.0'

    # simple_word_eval should be active!
    write -- $x
  }
}
## stdout:
## status: 2
## timeout: 10

#### hay define and then an error
shopt --set parse_brace parse_equals parse_proc

hay define Package/License User TASK

hay pp defs > /dev/null

hay eval :result {
  User bob
  echo "user $?"

  Package cppunit
  echo "package $?"

  TASK build {
    configure
  }
  echo "TASK $?"

  Package unzip {
    version = '1.0'

    License FOO {
      echo 'inside'
    }
    echo "license $?"

    License BAR
    echo "license $?"

    zz foo
    echo 'should not get here'
  }
}

echo 'ditto'
## STDOUT:
user 127
package 127
## END
## status: 2
## XFAIL ps-bash gap-oils-hay hay (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
