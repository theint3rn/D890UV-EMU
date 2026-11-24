#!/usr/bin/env python3
"""Summarise pc_samples.txt by function (uses decomp/functions.csv)."""
import sys, csv, bisect, collections, os
here = os.path.dirname(os.path.abspath(__file__))
rows = sorted((int(r["address"], 16), r["name"]) for r in csv.DictReader(open(os.path.join(here, "..", "decomp", "functions.csv"))))
st = [r[0] for r in rows]
def fn(a):
    i = bisect.bisect_right(st, a & ~1) - 1
    return "%s+%x" % (rows[i][1], (a & ~1) - rows[i][0]) if i >= 0 else hex(a)
def fname(a):
    i = bisect.bisect_right(st, a & ~1) - 1
    return rows[i][1] if i >= 0 else hex(a)
samples = [l.split() for l in open(sys.argv[1])]
c = collections.Counter((fname(int(s[1], 16)), fname(int(s[2], 16))) for s in samples)
for (f, caller), n in c.most_common(int(sys.argv[2]) if len(sys.argv) > 2 else 15):
    print("%4d  %-22s <- %s" % (n, f, caller))
