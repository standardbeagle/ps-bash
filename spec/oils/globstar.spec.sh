## Imported from the Oils spec tests (globstar.test.sh, Apache-2.0, https://github.com/oils-for-unix/oils).
## Names and scripts only; expectations are recorded from bash 5.2, not taken from Oils.
## Cases using the Oils harness ($SH, $REPO_ROOT, ...) are omitted.
#### each occurrence of ** recurses through all depths
shopt -s globstar

mkdir -p c/subdir
touch {leaf.md,c/leaf.md,c/subdir/leaf.md}

echo **/*.* | tr ' ' '\n' | sort
echo
echo **/**/*.* | tr ' ' '\n' | sort
## STDOUT:
c/leaf.md
c/subdir/leaf.md
leaf.md

c/leaf.md
c/subdir/leaf.md
leaf.md
## END
## status: 0
## timeout: 10

#### within braces, globstar works when there is a comma
shopt -s globstar

mkdir -p c/subdir
touch c/subdir/leaf.md

echo {**/*.*,} | sort | sed 's/[[:space:]]*$//'
## stdout: c/subdir/leaf.md
## status: 0
## XFAIL ps-bash gap-oils-globstar globstar (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10

#### ** behaves like * if adjacent to anything other than /
shopt -s globstar

mkdir directory
touch leaf.md
touch directory/leaf.md

echo **/*.* | sort
echo directory/**/*.md | sort
echo d**/*.md | sort
echo **y/*.md | sort
echo d**y/*.md | sort
## STDOUT:
directory/leaf.md leaf.md
directory/leaf.md
directory/leaf.md
directory/leaf.md
directory/leaf.md
## END
## status: 0
## timeout: 10

#### globstar is off -> ** is treated like *
case $SH in zsh) exit ;; esac

shopt -u globstar

mkdir -p c/subdir
touch {leaf.md,c/leaf.md,c/subdir/leaf.md}

echo **/*.* | sort
## stdout: c/leaf.md
## status: 0
## timeout: 10
## real-launcher

#### in zsh, ***/ follows symlinked directories, while **/ does not
case $SH in bash) exit ;; esac

mkdir directory-1
mkdir directory-2
touch directory-2/leaf-2.md
ln -s -T ../directory-2 directory-1/symlink

echo **/*.* | sort
echo ***/*.* | sort
## stdout:
## status: 0
## XFAIL ps-bash gap-oils-globstar globstar (oils): differs from bash 5.2; see docs/specs/spec-gaps.md
## timeout: 10
## real-launcher
