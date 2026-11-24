#!/usr/bin/env python3
"""Crawl every main-menu item, each in a FRESH emulator (the crawler presses Select on option lists, which
changes settings and even the main menu order, so one long crawl drifts).

  python3 emu/crawl_all.py [--items 0-12] [--jobs 3] [--depth 4] [--out emu/out/crawl2]
Per item: run.py --serve on its own port -> crawl.py --only N --out OUT/item_NN -> quit.
"""
import argparse, os, subprocess, sys, time, socket
from concurrent.futures import ThreadPoolExecutor
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from ctl import talk


def broker_up(port):
    # a bare connect would crash the broker (it reads one JSON request per connection)
    try:
        talk(port, ["version"], timeout=900)
        return True
    except (OSError, ValueError):
        return False


def one(i, port, a):
    out = os.path.join(a.out, "item_%02d" % i)
    os.makedirs(out, exist_ok=True)
    log = open(os.path.join(out, "driver.txt"), "w")
    subprocess.call([sys.executable, os.path.join(HERE, "run.py"), "--orig", "--seconds", "25",
                     "--name", "crawl_item%02d" % i, "--serve", str(port)], stdout=log, stderr=subprocess.STDOUT)
    t0 = time.time()
    while not broker_up(port):
        if time.time() - t0 > 900:
            log.write("broker never came up\n"); return i, "no emulator"
        time.sleep(3)
    rc = subprocess.call([sys.executable, os.path.join(HERE, "crawl.py"), "--port", str(port), "--only", str(i),
                          "--depth", str(a.depth), "--out", out], stdout=log, stderr=subprocess.STDOUT)
    try:
        talk(port, ["quit"], timeout=20)
    except Exception:
        pass
    return i, "ok" if rc == 0 else "crawl rc=%d" % rc


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--items", default="0-12")
    ap.add_argument("--jobs", type=int, default=3)
    ap.add_argument("--depth", type=int, default=4)
    ap.add_argument("--out", default=os.path.join(HERE, "out", "crawl2"))
    a = ap.parse_args()
    lo, _, hi = a.items.partition("-")
    items = list(range(int(lo), int(hi or lo) + 1))
    import queue
    ports = queue.Queue()
    for k in range(a.jobs):
        ports.put(1300 + 10 * k)
    def job(i):
        p = ports.get()                     # a port is reused only after its emulator has quit
        try:
            return one(i, p, a)
        finally:
            time.sleep(3); ports.put(p)
    with ThreadPoolExecutor(a.jobs) as ex:
        futs = [ex.submit(job, i) for i in items]
        for f in futs:
            i, r = f.result()
            print("item %d: %s" % (i, r), flush=True)


if __name__ == "__main__":
    main()
