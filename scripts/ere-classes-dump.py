import ctypes, sys
libc = ctypes.CDLL("libc.so.6")
libc.setlocale.restype = ctypes.c_char_p
print(libc.setlocale(6, b"C.UTF-8"), file=sys.stderr)
libc.wctype.restype = ctypes.c_ulong
libc.iswctype.argtypes = [ctypes.c_uint, ctypes.c_ulong]
libc.towupper.argtypes = [ctypes.c_uint]
libc.towupper.restype = ctypes.c_uint
libc.towlower.argtypes = [ctypes.c_uint]
libc.towlower.restype = ctypes.c_uint
names = ["alpha", "upper", "lower", "punct", "print", "graph", "space", "blank", "cntrl", "digit", "xdigit", "alnum"]
out = {}
for n in names:
    t = libc.wctype(n.encode())
    ranges = []
    start = None
    for cp in range(0, 0x110000):
        m = (0xD800 <= cp <= 0xDFFF) is False and libc.iswctype(cp, t) != 0
        if m and start is None:
            start = cp
        elif not m and start is not None:
            ranges.append((start, cp - 1)); start = None
    if start is not None:
        ranges.append((start, 0x10FFFF))
    out[n] = ranges
    print(n, len(ranges), file=sys.stderr)
with open(sys.argv[1], "w") as f:
    for n in names:
        f.write(n + " " + " ".join("%X-%X" % r if r[0] != r[1] else "%X" % r[0] for r in out[n]) + "\n")
    up = []
    for cp in range(0, 0x110000):
        if 0xD800 <= cp <= 0xDFFF: continue
        u = libc.towupper(cp)
        if u != cp: up.append("%X:%X" % (cp, u))
    f.write("toupper " + " ".join(up) + "\n")
    lo = []
    for cp in range(0, 0x110000):
        if 0xD800 <= cp <= 0xDFFF: continue
        u = libc.towlower(cp)
        if u != cp: lo.append("%X:%X" % (cp, u))
    f.write("tolower " + " ".join(lo) + "\n")
print("toupper", len(up), "tolower", len(lo), file=sys.stderr)
