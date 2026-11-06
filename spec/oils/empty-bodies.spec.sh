## Imported from the Oils spec tests (empty-bodies.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### Empty do/done
while false; do
done
echo empty
## stdout:
## status: 2
## timeout: 10

#### Empty case/esac
case foo in
esac
echo empty
## stdout: empty
## status: 0
## timeout: 10

#### Empty then/fi
if foo; then
fi
echo empty
## stdout:
## status: 2
## timeout: 10
