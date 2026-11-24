"""Drive a Renode started with `run.py --serve PORT` through its telnet monitor.

  ctl.py [--port 1235] [--out DIR] cmd "lcd Press 13 150"       raw monitor command(s)
  ctl.py run 1.5                                                  run virtual seconds
  ctl.py key 13 [ms] [settle_s]                                   press a key, run settle (default 1.2 s)
  ctl.py keys 13,16,16,13 [settle_s]                              several presses
  ctl.py shot NAME                                                 screenshot -> DIR/NAME.png
  ctl.py state                                                     dump_state -> printed
  ctl.py quit
  ctl.py broker                (started by run.py --serve; keeps the one monitor connection, serves on PORT+1)
Monitor output is printed (prompt lines stripped).
"""
import argparse, os, re, socket, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
PROMPT = re.compile(rb"\((?:d890uv|monitor)\) ?$")


def monitor_session(port, timeout=1800):
    """One persistent telnet connection (Renode's monitor serves only its first client well)."""
    s = socket.create_connection(("127.0.0.1", port), timeout=timeout)
    buf = b""
    def until_prompt():
        nonlocal buf
        while True:
            tail = re.sub(rb"\x1b\[[0-9;?]*[A-Za-z]", b"", buf).rstrip(b"\r\n ")[-40:]
            if PROMPT.search(tail.split(b"\n")[-1] + b" "):
                out, buf = buf, b""
                return out
            d = s.recv(65536)
            if not d:
                raise SystemExit("monitor closed")
            # answer telnet negotiation with refusals
            while b"\xff" in d:
                i = d.index(b"\xff")
                if i + 2 < len(d) and d[i + 1] in (251, 252, 253, 254):
                    s.send(bytes([255, 254 if d[i + 1] in (251, 252) else 252, d[i + 2]]))
                    d = d[:i] + d[i + 3:]
                else:
                    d = d[:i] + d[i + 1:]
            buf += d
    time.sleep(0.3)
    s.send(b"\n")          # Renode shows no prompt on connect
    until_prompt()
    def do(l):
        s.send(l.encode() + b"\n")
        o = re.sub(rb"\x1b\[[0-9;?]*[A-Za-z]", b"", until_prompt()).decode(errors="replace")
        return "\n".join(x for x in o.replace("\r", "").split("\n")
                         if x.strip() and not PROMPT.search((x + " ").encode()) and x.strip() != l.strip())
    return do


def broker(port, bport):
    """Hold the monitor connection; accept JSON command lists on localhost:bport."""
    import json
    do = monitor_session(port)
    srv = socket.socket(); srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", bport)); srv.listen(1)
    while True:
        c, _ = srv.accept()
        f = c.makefile("rwb")
        try:
            req = json.loads(f.readline())
        except ValueError:                 # empty/garbage request (e.g. a port probe): ignore
            c.close()
            continue
        res = [do(l) for l in req]
        f.write((json.dumps(res) + "\n").encode()); f.flush(); c.close()
        if req and req[-1] == "quit":
            return


def talk(port, lines, timeout=1800):
    import json
    c = socket.create_connection(("127.0.0.1", port + 1), timeout=timeout)
    f = c.makefile("rwb")
    f.write((json.dumps(lines) + "\n").encode()); f.flush()
    return json.loads(f.readline())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=1235)
    ap.add_argument("--out", default=os.path.join(HERE, "out", "serve"))
    ap.add_argument("op")
    ap.add_argument("args", nargs="*")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    def run(secs):
        t = float(secs)
        return 'emulation RunFor "%02d:%02d:%09.6f"' % (t // 3600, t % 3600 // 60, t % 60)
    L, shots = [], []
    if a.op == "cmd":
        L = a.args
    elif a.op == "run":
        L = [run(a.args[0])]
    elif a.op in ("key", "keys"):
        codes = a.args[0].split(",")
        ms = a.args[1] if a.op == "key" and len(a.args) > 1 else "150"
        settle = a.args[2] if a.op == "key" and len(a.args) > 2 else (a.args[1] if a.op == "keys" and len(a.args) > 1 else "1.2")
        for c in codes:
            L += ["lcd Press %s %s" % (c, ms), run(settle)]
    elif a.op == "ks":             # ks KEYS NAME [settle]: press keys (comma list, may be empty) then screenshot
        settle = a.args[2] if len(a.args) > 2 else "1.2"
        keys = list(filter(None, a.args[0].split(",")))
        for c in keys:
            L += ["lcd Press %s 150" % c, run(settle)]
        if not keys and len(a.args) > 2:
            L.append(run(settle))
        p = os.path.join(a.out, a.args[1] + ".ppm")
        L.append("lcd Screenshot @%s" % p); shots.append(p)
        a.quiet = True
    elif a.op == "shot":
        p = os.path.join(a.out, a.args[0] + ".ppm")
        L = ["lcd Screenshot @%s" % p]; shots.append(p)
    elif a.op == "state":
        p = os.path.join(a.out, "state.txt")
        L = ['dump_state "%s"' % p]
    elif a.op == "broker":
        return broker(a.port, a.port + 1)
    elif a.op == "quit":
        L = ["quit"]
    for o in talk(a.port, L):
        if o.strip() and not getattr(a, "quiet", False):
            print(o)
    for p in shots:
        subprocess.call([sys.executable, "-I", os.path.join(HERE, "ppm2png.py"), p, p[:-4] + ".png"])
        os.remove(p)
    if a.op == "state":
        print(open(os.path.join(a.out, "state.txt")).read())


if __name__ == "__main__":
    main()
