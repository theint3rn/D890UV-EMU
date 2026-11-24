#!/usr/bin/env python3
"""Interactive front panel for the emulated D890UV in a web browser.

  python3 emu/webui.py --launch orig            # boot original V1.05 in a new Renode, UI on http://localhost:8890
  python3 emu/webui.py --launch elf             # same with the lifted build (build/d890uv.elf)
  python3 emu/webui.py --launch cfw             # custom firmware (make -C cfw, then cfw/build/cfw.elf)
  python3 emu/webui.py --launch cfw-emu         # cfw emulator test build (make EMU=1: symbol FIFO + DMR rx glue)
  python3 emu/webui.py --attach 1235            # use a Renode already started with run.py --serve 1235

The emulator runs in short slices of virtual time; between slices the UI applies key presses/releases
(mouse or PC keyboard), takes an LCD screenshot and reads the strings the firmware drew (hook on the
string renderer: stock FUN_0808d894, UTF-16 string at [sp+4]; cfw gfx_text, C string in r2; --text-fn). Keys are held while the button is held, so long presses work.
PC keyboard: 0-9 * #, Enter = Menu, Esc/Backspace = Exit, arrows = Up/Down, F1/F2 = P1/P2.
"""
import argparse, io, json, os, queue, struct, subprocess, sys, threading, time, zlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ctl import talk

HERE = os.path.dirname(os.path.abspath(__file__))
TEXT_FN = 0x0808D894          # stock string renderer: r0 = x, r1 = y, [sp+4] = UTF-16 string
CFW_DIR = os.path.join(os.path.dirname(HERE), "cfw")
CFW_ELF = os.path.join(CFW_DIR, "build", "cfw.elf")
SIDE_PINS = ["A8", "A10", "A14", "E2"]   # side keys / PTT inputs (active low); names not mapped yet


def ppm_to_png(data):
    parts = data.split(b"\n", 3)
    w, h = map(int, parts[1].split())
    px = parts[3]
    # controller memory is the transpose of the panel view (MADCTL MV)
    rows = []
    for c in range(w):
        rows.append(b"\0" + b"".join(px[(r * w + c) * 3:(r * w + c) * 3 + 3] for r in range(h)))
    def chunk(t, b):
        return struct.pack(">I", len(b)) + t + b + struct.pack(">I", zlib.crc32(t + b) & 0xffffffff)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", h, w, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(b"".join(rows), 1)) + chunk(b"IEND", b""))


class Emu:
    def __init__(self, port, out, slice_s, text_fn=TEXT_FN, text_abi="utf16sp"):
        self.port, self.out, self.slice = port, out, slice_s
        self.q = queue.Queue()
        self.png = b""
        self.frame = 0
        self.vtime = 0.0
        self.speed = 0.0
        self.running = True
        self.power = "on"          # on | stopping | off
        self.texts = []
        self.lock = threading.Lock()
        self.ppm = os.path.join(out, "ui_lcd.ppm")
        self.txt = os.path.join(out, "ui_text.log")
        open(self.txt, "w").close()
        self.txtpos = 0
        self.down_at = {}          # key/pin -> virtual time pressed
        self.pending = []          # (release virtual time, command)
        if text_abi == "cstr":      # cfw gfx_text(x, y, const char *s, ...): ASCII string in r2
            get = ("p = self.GetRegister(2).RawValue; "
                   "s = ''.join([chr(self.Bus.ReadByte(p + i)) for i in range(48)]).split(chr(0))[0]; ")
        else:                       # stock FUN_0808d894: UTF-16 string pointer at [sp+4]
            get = ("p = self.Bus.ReadDoubleWord(self.GetRegister(13).RawValue + 4); "
                   "s = u''.join([unichr(self.Bus.ReadWord(p + 2*i)) for i in range(48)]).split(unichr(0))[0]; ")
        if text_fn:
            hook = ("cpu AddHook 0x%08X \"import System; " + get +
                    "System.IO.File.AppendAllText('%s', str(self.GetRegister(0).RawValue) + ',' + "
                    "str(self.GetRegister(1).RawValue) + ' ' + s.encode('utf-8') + chr(10))\"") % (text_fn, self.txt)
            talk(port, ["cpu RemoveHooksAt 0x%08X" % text_fn, hook])

    def loop(self):
        while True:
            try:
                self.step()
            except Exception as e:          # keep the panel alive whatever happens
                print("step error:", repr(e), flush=True)
                time.sleep(0.5)

    def step(self):
        if True:
            cmds = []
            try:
                while True:
                    cmds.append(self.q.get_nowait())
            except queue.Empty:
                pass
            if not self.running and not cmds:
                time.sleep(0.05)
                return
            with self.lock:
                due = [c for t, c in self.pending if t <= self.vtime + 1e-9]
                self.pending = [(t, c) for t, c in self.pending if t > self.vtime + 1e-9]
            cmds += due
            if self.running:
                cmds.append('emulation RunFor "00:00:%09.6f"' % self.slice)
            cmds.append("lcd Screenshot @%s" % self.ppm)
            t0 = time.time()
            try:
                talk(self.port, cmds)
            except Exception as e:      # emulator gone
                print("emulator error:", e)
                time.sleep(1)
                return
            if self.power == "stopping":
                # PC3 = power latch; the firmware drops it after saving, then resets (real radio is dead by then)
                odr = talk(self.port, ["sysbus ReadDoubleWord 0x42020814"])[0].split()[0]
                if not int(odr, 16) & 8:
                    self.running = False
                    self.power = "off"
            png = ppm_to_png(open(self.ppm, "rb").read())
            if self.power == "off":
                png = ppm_to_png(b"P6\n128 160\n255\n" + b"\0" * (128 * 160 * 3))
            left = self.slice - (time.time() - t0)
            if self.running and left > 0:                          # never faster than real time
                time.sleep(left)
            dt = time.time() - t0
            self.read_texts()
            with self.lock:
                self.png = png
                self.frame += 1
                if self.running:
                    self.vtime += self.slice
                    self.speed = 0.8 * self.speed + 0.2 * (self.slice / max(dt, 1e-3))

    def press(self, name, down, cmd_down, cmd_up, min_s=0.15):
        """Key/pin edge from the UI; a short click still holds the key min_s of virtual time."""
        with self.lock:
            if down:
                self.down_at[name] = self.vtime
                self.pending = [(t, c) for t, c in self.pending if c != cmd_up]
                self.q.put(cmd_down)
            else:
                t0 = self.down_at.pop(name, self.vtime - min_s)
                if self.vtime - t0 >= min_s:
                    self.q.put(cmd_up)
                else:
                    self.pending.append((t0 + min_s, cmd_up))

    def read_texts(self):
        with open(self.txt, "rb") as f:
            f.seek(self.txtpos)
            data = f.read()
            self.txtpos += len(data)
        new = []
        for l in data.decode("utf-8", "replace").splitlines():
            xy, _, s = l.partition(" ")
            if s.strip():
                new.append(s.strip())
        if new:
            with self.lock:
                t = self.texts
                for x in new:               # most recent last; a redrawn string moves to the end
                    if x in t[-12:]:
                        t.remove(x)
                    t.append(x)
                self.texts = t[-40:]


PAGE = os.path.join(HERE, "webui.html")      # read on every request: edit it and reload the browser


def make_handler(emu):
    class H(BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def send(self, code, body, ctype):
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            path = self.path.split("?")[0]
            if path == "/":
                self.send(200, open(PAGE, "rb").read(), "text/html; charset=utf-8")
            elif path == "/lcd.png":
                with emu.lock:
                    self.send(200, emu.png, "image/png")
            elif path == "/state":
                with emu.lock:
                    s = {"power": emu.power, "frame": emu.frame, "vtime": emu.vtime, "speed": emu.speed, "running": emu.running,
                         "texts": emu.texts}
                self.send(200, json.dumps(s).encode(), "application/json")
            else:
                self.send(404, b"not found", "text/plain")

        def do_POST(self):
            n = int(self.headers.get("Content-Length", 0))
            req = json.loads(self.rfile.read(n) or b"{}")
            res = {"ok": True}
            if self.path == "/key":
                c = int(req["code"])
                emu.press("k%d" % c, req["down"], "lcd Hold %d true" % c, "lcd Hold %d false" % c)
            elif self.path == "/pin":
                pin = str(req["pin"])
                if pin in SIDE_PINS:
                    emu.press(pin, req["down"], 'board Set "%s" false' % pin, 'board Set "%s" true' % pin)
            elif self.path == "/knob":
                emu.q.put("knob Turn %d" % int(req["steps"]))
            elif self.path == "/power":
                if req["on"] and emu.power != "on":
                    emu.q.put("machine Reset"); emu.q.put("board PowerOn")
                    emu.power = "on"; emu.running = True
                elif not req["on"] and emu.power == "on":
                    emu.q.put("board PowerOff")
                    emu.power = "stopping"; emu.running = True
            elif self.path == "/pause":
                emu.running = not emu.running
            elif self.path == "/save":
                p = os.path.join(emu.out, "ui_shot_%d.png" % int(time.time()))
                with emu.lock:
                    open(p, "wb").write(emu.png)
                res["path"] = p
            elif self.path == "/cmd":
                # run between slices: wait for the worker by queueing a marker is complex; send directly (broker serialises)
                try:
                    res["out"] = "\n".join(talk(emu.port, [str(req["cmd"])]))
                except Exception as e:
                    res["out"] = "error: %s" % e
            else:
                return self.send(404, b"{}", "application/json")
            self.send(200, json.dumps(res).encode(), "application/json")
    return H


def resolve_text_fn(spec, elf):
    """'none' -> None; hex/decimal address; else an ELF symbol (arm-none-eabi-nm), None if not found."""
    if spec.lower() == "none":
        return None
    try:
        return int(spec, 0) & ~1
    except ValueError:
        pass
    try:
        for l in subprocess.check_output(["arm-none-eabi-nm", elf], text=True).splitlines():
            f = l.split()
            if len(f) == 3 and f[2] == spec:
                return int(f[0], 16) & ~1
    except Exception as e:
        print("text pane: cannot read symbols: %s" % e)
    print("text pane: %s not found, text pane off" % spec)
    return None


def wait_broker(port, secs=600):
    t0 = time.time()
    while time.time() - t0 < secs:
        try:
            talk(port, ["version"], timeout=600)
            return True
        except OSError:
            time.sleep(1)
    return False


def main():
    ap = argparse.ArgumentParser()
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--launch", choices=["orig", "elf", "cfw", "cfw-emu"],
                   help="start a new emulator (original V1.05, lifted ELF, or the custom firmware cfw/build/cfw.elf)")
    g.add_argument("--attach", type=int, help="monitor port of a running run.py --serve")
    ap.add_argument("--monitor", type=int, default=1245, help="monitor port for --launch")
    ap.add_argument("--http", type=int, default=8890)
    ap.add_argument("--slice", type=float, default=0.05, help="virtual seconds per step")
    ap.add_argument("--uart-socket", action="append", default=[], metavar="UART:PORT",
                    help="with --launch: expose a UART as TCP on localhost (usart1:3470 = programming cable)")
    ap.add_argument("--text-fn", help="string renderer to hook for the text pane: address, ELF symbol, or 'none' "
                                      "(default: FUN_0808d894; with --launch cfw: gfx_text)")
    ap.add_argument("--text-abi", choices=["utf16sp", "cstr"],
                    help="utf16sp = stock (UTF-16 at [sp+4]), cstr = C string in r2 (default by --launch)")
    a = ap.parse_args()
    port = a.attach or a.monitor
    run_name = "ui" if a.launch == "orig" else "ui_%s" % (a.launch or "").replace("-", "_")     # separate out dirs: panels can run side by side
    out = os.path.join(HERE, "out", run_name if a.launch else "serve")
    elf = None
    if a.launch == "cfw":
        subprocess.check_call(["make", "-C", CFW_DIR, "-s"])       # incremental: builds only if sources changed
        elf = CFW_ELF
    elif a.launch == "cfw-emu":                                    # test build: symbol FIFO backend, DMR rx glue
        subprocess.check_call(["make", "-C", CFW_DIR, "-s", "EMU=1"])
        elf = os.path.join(CFW_DIR, "build_emu", "cfw.elf")
    elif a.launch == "elf":
        elf = os.path.join(HERE, "..", "build", "d890uv.elf")
    cfw = a.launch in ("cfw", "cfw-emu")
    text_abi = a.text_abi or ("cstr" if cfw else "utf16sp")
    text_fn = resolve_text_fn(a.text_fn or ("gfx_text" if cfw else "0x%08X" % TEXT_FN), elf)
    if a.launch:
        src = ["--orig"] if a.launch == "orig" else ["--elf", elf]
        subprocess.check_call([sys.executable, os.path.join(HERE, "run.py")] + src +
                              ["--seconds", "4", "--name", run_name, "--serve", str(port)] +
                              sum((["--uart-socket", u] for u in a.uart_socket), []))
    print("waiting for emulator monitor on %d ..." % port, flush=True)
    if not wait_broker(port):
        raise SystemExit("emulator monitor not reachable")
    os.makedirs(out, exist_ok=True)
    emu = Emu(port, out, a.slice, text_fn, text_abi)
    threading.Thread(target=emu.loop, daemon=True).start()
    srv = ThreadingHTTPServer(("127.0.0.1", a.http), make_handler(emu))
    print("D890UV front panel: http://localhost:%d" % a.http, flush=True)
    import signal
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))     # stop -> quit the emulator we launched
    try:
        srv.serve_forever()
    finally:
        if a.launch:
            try:
                talk(port, ["quit"], timeout=10)
            except Exception:
                pass


if __name__ == "__main__":
    main()
