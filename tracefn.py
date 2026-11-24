#!/usr/bin/env python3
"""Trace the lifted build and show the source lines executed in one function (last call).
Usage: tracefn.py <FUNC_NAME> [seconds]   (uses out/cmp_lift/run.resc)"""
import sys, os, subprocess
HERE = os.path.dirname(os.path.abspath(__file__)); ELF = os.path.join(HERE, "..", "build", "d890uv.elf")
fn, secs = sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else "1"
out = os.path.join(HERE, "out", "cmp_lift")
s = open(os.path.join(out, "run.resc")).read().splitlines()
s = [l for l in s if not l.startswith(("emulation RunFor", "dump_state", "lcd Screenshot", "pc_sample")) and l != "quit"]
tr = os.path.join(out, "trace.txt")
s += ['cpu CreateExecutionTracing "tr" @%s PC' % tr, 'emulation RunFor "%s"' % secs, "cpu DisableExecutionTracing", "quit"]
open(os.path.join(out, "tf.resc"), "w").write("\n".join(s) + "\n")
subprocess.call([os.path.expanduser("~/.local/opt/renode_1.17.0-portable/renode"), "--disable-gui", "--plain", "-P", "-1",
                 os.path.join(out, "tf.resc")], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
nm = subprocess.check_output(["arm-none-eabi-nm", "-S", ELF], text=True).split("\n")
lo, sz = next((int(l.split()[0], 16), int(l.split()[1], 16)) for l in nm if l.endswith(" " + fn))
pcs = [int(l, 16) for l in open(tr) if l.startswith("0x")]
idx = [i for i, p in enumerate(pcs) if lo <= p < lo + sz]
if not idx:
    sys.exit("not executed")
# last contiguous call
start = idx[-1]
while start > 0 and (lo <= pcs[start - 1] < lo + sz or start - 1 in idx[-400:]):
    start -= 1
seq = [p for p in pcs[start:idx[-1] + 1] if lo <= p < lo + sz]
lines = subprocess.check_output(["arm-none-eabi-addr2line", "-e", ELF] + ["0x%x" % p for p in seq], text=True).split()
res = []
for l in lines:
    l = l.rsplit(":", 1)[-1]
    if not res or res[-1] != l:
        res.append(l)
print("executions:", len(idx), "lines (last call):", " ".join(res[-80:]))
