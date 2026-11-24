#!/usr/bin/env python3
"""Find what the lifted build ran just before it entered HardFault_Handler (or another symbol).

Usage: faulttrace.py [--start 4.6] [--len 0.3] [--target HardFault_Handler] [--n 12]
Uses out/cmp_lift/run.resc (run emu/compare.py first). Prints the last N distinct functions
before the first hit of the target, plus the last raw PCs.
"""
import argparse, os, subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
ELF = os.path.join(HERE, "..", "build", "d890uv.elf")
RENODE = os.path.expanduser("~/.local/opt/renode_1.17.0-portable/renode")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--start", type=float, default=4.6)
    ap.add_argument("--len", type=float, default=0.3)
    ap.add_argument("--target", default="HardFault_Handler")
    ap.add_argument("--n", type=int, default=12)
    a = ap.parse_args()
    out = os.path.join(HERE, "out", "cmp_lift")
    tr = os.path.join(out, "fault_trace.txt")
    s = [l for l in open(os.path.join(out, "run.resc")).read().splitlines()
         if not l.startswith(("emulation RunFor", "dump_state", "lcd Screenshot", "pc_sample")) and l != "quit"]
    s += ['emulation RunFor "%s"' % a.start, 'cpu CreateExecutionTracing "tr" @%s PC' % tr,
          'emulation RunFor "%s"' % a.len, "cpu DisableExecutionTracing", "quit"]
    open(os.path.join(out, "faulttrace.resc"), "w").write("\n".join(s) + "\n")
    if os.path.exists(tr):
        os.remove(tr)
    subprocess.call([RENODE, "--disable-gui", "--plain", "-P", "-1", os.path.join(out, "faulttrace.resc")],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    nm = subprocess.check_output(["arm-none-eabi-nm", ELF], text=True)
    tgt = next(int(l.split()[0], 16) for l in nm.splitlines() if l.endswith(" " + a.target)) & ~1
    pcs = [int(l, 16) for l in open(tr) if l.startswith("0x")]
    try:
        hit = pcs.index(tgt)
    except ValueError:
        print("target not reached in window (%d PCs traced)" % len(pcs))
        return
    window = pcs[max(0, hit - 400):hit]
    names = subprocess.check_output(["arm-none-eabi-addr2line", "-f", "-e", ELF] + ["0x%x" % p for p in window],
                                    text=True).splitlines()[0::2]
    seq = []
    for n in names:
        if not seq or seq[-1] != n:
            seq.append(n)
    print("last functions:", " ".join(seq[-a.n:]))
    print("last PCs:", " ".join("0x%x" % p for p in window[-6:]))


if __name__ == "__main__":
    main()
