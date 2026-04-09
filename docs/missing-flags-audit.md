# ps-bash Missing Flags Audit

Generated: 2026-04-07
Updated: 2026-04-08

## Critical Issues — RESOLVED

1. ✅ **tar uses ZIP format** — FIXED: now delegates to Windows native tar.exe (POSIX format)
2. ✅ **tail missing `-f`** — FIXED: tail -f now follows file continuously with 100ms polling
3. ✅ **find/xargs missing `-print0`/`-0`** — FIXED: null-delimited I/O working end-to-end
4. **jq missing core operators** — `..`, `//`, if-then-else, reduce — STILL PENDING
5. ✅ **grep missing `-e`/`-F`/`-H`/`-h`/`-w`/`-o`/`-m`** — FIXED: all 7 flags implemented

## Priority Matrix

### Tier 1: Breaking Compatibility — ALL RESOLVED

| Command | Missing | Status |
|---------|---------|--------|
| tar | Real tar format (was ZIP) | ✅ Fixed: delegates to system tar.exe |
| find | `-print0` | ✅ Fixed: null-delimited output |
| xargs | `-0` | ✅ Fixed: null-delimited input |
| tail | `-f` (follow) | ✅ Fixed: polling follow mode |

### Tier 2: High-Value Gaps

| Command | Missing | Impact |
|---------|---------|--------|
| grep | `-F` `-H` `-h` `-e` `-w` `-o` `-m` | ✅ Fixed: all 7 flags implemented |
| sed | `-r` (ERE alias), `-f` (script file), `a`/`i`/`c` commands | Script portability |
| awk | `-f` (program file), `sprintf()`, `match()`, user functions | Intermediate awk broken |
| jq | `..` (recurse), `//` (alt), if-then-else, format strings | Real-world jq scripts fail |
| sort | `-b` (blanks), column-based `-k N.M,N.M` | Field sorting incomplete |
| tr | `-c` (complement) | Common pattern: `tr -cd` |
| uniq | `-u` (unique only), `-i` (case-insensitive), `-f`/`-s` (skip) | Filtering limited |
| diff | `-q` (brief), `-w` (whitespace), `-i` (case) | Comparison limited |

### Tier 3: Conveniences

| Command | Missing | Impact |
|---------|---------|--------|
| head/tail | `-c` (bytes), `-q`/`-v` (headers) | Minor gaps |
| wc | `-m` (characters), `-L` (longest line) | Rarely needed |
| cut | `-s` (suppress), `--output-delimiter` | Edge cases |
| ls | `-i` (inode), `-n` (numeric UID), `-F` (classify) | Display options |
| cat | `-b` (number non-blank) | Already has `-n` |
| date | `%w`, `%y`, `%F`, `%T` format codes, ISO 8601 | Format gaps |
| du | `-S` (exclude subdirs), `-L` (follow symlinks) | Sizing options |
| gzip | `-t` (test), `-N`/`-n` (name), `-q` (quiet) | Minor |
| ps | `--forest` (tree), `-w` (wide), `--no-headers` | Display options |
| xargs | `-P` (parallel), `-p`/`-t` (prompt/trace) | Advanced use |
| tee | `-i` (ignore interrupt) | Signal handling |

## Per-Command Detail

### grep
**Supported:** `-i` `-v` `-n` `-c` `-r` `-l` `-E` `-A` `-B` `-C`
**Missing high:** `-F` `-H` `-h` `-e` `-w` `-o` `-m`

### sed
**Supported:** `-n` `-i` `-E` `-e`, commands: `s` `d` `p` `y`, address ranges
**Missing high:** `-r` `-f`, commands: `a` `i` `c` `q` `N` `D` `P`, `I` flag on `s///`

### awk
**Supported:** `-F` `-v`, BEGIN/END, field refs, string/math funcs, arrays, control flow
**Missing high:** `-f`, `sprintf()` `match()` `strftime()`, user-defined functions, `getline`

### sort
**Supported:** `-r` `-n` `-u` `-f` `-h` `-V` `-M` `-c` `-k` `-t`
**Missing high:** `-b` column-based `-k N.M,N.M`, multiple `-k` keys

### find
**Supported:** `-name` `-type` `-maxdepth` `-size` `-mtime` `-empty` `-exec`
**Missing high:** `-print0` `-iname` `-path` `-newer` `-perm` `-mindepth`, boolean ops

### xargs
**Supported:** `-I` `-n`
**Missing high:** `-0` `-d` `-p` `-t` `-P`

### jq
**Supported:** `-r` `-c` `-S` `-s`, `.field`, `.[n]`, `.[]`, `|`, `,`, `[]`/`{}` construction, `select()` `map()` `keys` `values` `length` `type`
**Missing high:** `..` `//` if-then-else, `@base64`/`@csv`/`@tsv`, `to_entries`/`from_entries`, `group_by`/`sort_by`/`unique_by`, `reduce` `foreach`, `try-catch`, `as $var`

### tr
**Supported:** `-d` `-s`, char ranges
**Missing high:** `-c`/`-C` (complement), POSIX char classes `[:alpha:]`

### uniq
**Supported:** `-c` `-d`
**Missing high:** `-u` `-i` `-f` `-s` `-w`

### diff
**Supported:** normal format, `-u` (unified)
**Missing high:** `-q` `-w` `-b` `-B` `-i`

### tail
**Supported:** `-n` `-n +N`
**Missing high:** `-f` `-c`

### head
**Supported:** `-n`
**Missing high:** `-c`
