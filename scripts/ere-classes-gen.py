import sys
src, dst = sys.argv[1], sys.argv[2]
cls = {}
pairs = {}
for line in open(src):
    name, *items = line.split()
    if name in ("toupper", "tolower"):
        pairs[name] = [tuple(int(x, 16) for x in it.split(":")) for it in items]
        continue
    s = set()
    rs = []
    for it in items:
        a, _, b = it.partition("-")
        lo = int(a, 16); hi = int(b, 16) if b else lo
        rs.append((lo, hi))
    cls[name] = rs

def toset(rs):
    s = set()
    for lo, hi in rs: s.update(range(lo, hi + 1))
    return s

S = {k: toset(v) for k, v in cls.items()}
print("graph == print - space:", S["graph"] == S["print"] - S["space"], file=sys.stderr)
print("graph - print:", sorted(S["graph"] - S["print"])[:10], file=sys.stderr)
print("punct == graph - alnum:", S["punct"] == S["graph"] - S["alnum"], file=sys.stderr)
print("punct diff:", [hex(c) for c in sorted(S["punct"] ^ (S["graph"] - S["alnum"]))][:20], file=sys.stderr)
print("alnum == alpha|digit:", S["alnum"] == S["alpha"] | S["digit"], file=sys.stderr)
for k in ("space", "blank", "cntrl", "digit", "xdigit"):
    print(k, cls[k], file=sys.stderr)

# Runs of the case maps: (first, last, step, delta) where cp = first, first+step, ... maps to cp+delta.
def runs(p):
    p = sorted(p)
    out = []
    i = 0
    while i < len(p):
        cp, m = p[i]
        d = m - cp
        best = (1, 1)
        for step in (1, 2):
            j = i
            while j + 1 < len(p) and p[j + 1][0] == p[j][0] + step and p[j + 1][1] - p[j + 1][0] == d:
                j += 1
            if j - i + 1 > best[0]: best = (j - i + 1, step)
        n, step = best
        out.append((cp, cp + step * (n - 1), step, d))
        i += n
    return out

def b36(n):
    if n < 0: return "-" + b36(-n)
    digits = "0123456789abcdefghijklmnopqrstuvwxyz"
    s = ""
    while True:
        s = digits[n % 36] + s; n //= 36
        if n == 0: return s

def enc_ranges(rs):
    # delta-encoded: gap-from-previous-end, length-1
    parts = []
    prev = -1
    for lo, hi in rs:
        parts.append(b36(lo - prev - 1) + "." + b36(hi - lo))
        prev = hi
    return " ".join(parts)

def enc_runs(rr):
    parts = []
    prev = -1
    for first, last, step, d in rr:
        parts.append(b36(first - prev - 1) + "." + b36((last - first) // step) + "." + str(step) + "." + b36(d))
        prev = last
    return " ".join(parts)

up = runs(pairs["toupper"]); lo = runs(pairs["tolower"])
print("runs", len(up), len(lo), file=sys.stderr)

def chunk(s, w=150):
    out = []
    while s:
        cut = s.rfind(" ", 0, w) if len(s) > w else len(s)
        if cut <= 0: cut = len(s)
        out.append(s[:cut]); s = s[cut:].lstrip(" ")
    return out

def lit(name, s):
    lines = chunk(s)
    body = "\n".join('        + "' + l + ' "' for l in lines)
    return "    internal const string " + name + " = \"\"\n" + body + ";\n"

with open(dst, "w", newline="\n") as f:
    f.write("namespace PsBash.Core.Parser;\n\n")
    f.write("// GENERATED from glibc 2.39 (Ubuntu 24.04, the bash 5.2.21 oracle) in the C.UTF-8 locale: iswctype for every\n")
    f.write("// code point and the towupper map. Do not edit by hand; regenerate: scripts/ere-classes-dump.py on the oracle,\n")
    f.write("// then scripts/ere-classes-gen.py. Ranges: base-36 \"gap.length-1\" pairs, gap counted from the previous range's end.\n")
    f.write("// Case map (towupper): base-36 \"gap.count-1.step.delta\" runs (cp, cp+step, ... maps to cp+delta).\n")
    f.write("internal static partial class BashEreClasses\n{\n")
    for k in ("alpha", "upper", "lower", "punct", "print"):
        f.write(lit(k.capitalize() + "Ranges", enc_ranges(cls[k])))
    f.write(lit("ToUpperRuns", enc_runs(up)))

    f.write("}\n")
