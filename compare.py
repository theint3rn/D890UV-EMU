#!/usr/bin/env python3
"""Run the original V1.05 image and the lifted build with the same inputs, then compare.

Usage: emu/compare.py [--seconds 15] [--then "SECS|cmd" ...]
Writes emu/out/cmp_orig, emu/out/cmp_lift and emu/out/compare.txt:
  - screen pixel difference (same LCD output = lifted firmware drew the same thing)
  - final state (faults, RTOS running, lift trap) for both
  - NAND activity counts (reads/programs/erases) for both
Exit code 0 when screens match and the lifted build has no fault or trap.
"""
import argparse, os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def run(name, target, a):
    args = [sys.executable, "-I", os.path.join(HERE, "run.py"), "--seconds", str(a.seconds), "--name", name,
            "--timeout", "3000"] + target
    for t in a.then:
        args += ["--then", t]
    subprocess.call(args, stdout=subprocess.DEVNULL)
    out = os.path.join(HERE, "out", name)
    state = open(os.path.join(out, "state.txt")).read() if os.path.exists(os.path.join(out, "state.txt")) else "(no state)"
    ppm = os.path.join(out, "lcd.ppm")
    px = open(ppm, "rb").read().split(b"\n", 3)[3] if os.path.exists(ppm) else b""
    return state, px


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=float, default=15)
    ap.add_argument("--then", action="append", default=[])
    a = ap.parse_args()
    s1, p1 = run("cmp_orig", ["--orig"], a)
    s2, p2 = run("cmp_lift", ["--elf", os.path.join(ROOT, "build", "d890uv.elf")], a)
    diff = sum(1 for i in range(0, min(len(p1), len(p2)), 3) if p1[i:i + 3] != p2[i:i + 3])
    total = len(p1) // 3
    lines = ["screen: %d of %d pixels differ%s" % (diff, total, " (identical)" if diff == 0 else ""),
             "", "== original ==", s1.strip(), "", "== lifted ==", s2.strip()]
    report = "\n".join(lines)
    open(os.path.join(HERE, "out", "compare.txt"), "w").write(report + "\n")
    print(report)
    bad = diff or "FAULT" in s2 or ("lift_trap=0x" in s2 and "lift_trap=0x00000000" not in s2)
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
