"""Walk the radio's menus in a running emulator (run.py --serve) and record every screen.

Depth-first: at each list, ENTER (Menu key 13) each item, record the screen it opens, recurse, go back
(Exit key 14), move DOWN (16) until the list wraps. Screens are identified by a hash of the LCD frame;
labels come from a CPU hook on the firmware's string renderer FUN_0808d894 (x, y, UTF-16 text).
If Exit does not return to the expected screen, the crawler goes home and replays the key path.

  python3 emu/crawl.py --depth 3 --out emu/out/crawl [--start "13"] [--only 2]
Output: tree.json, tree.md (indented menu tree with each screen's text), shots/<path>.png
Keys: 13 Menu/OK, 14 Exit/Back, 15 Up, 16 Down (see emu/README.md).
"""
import argparse, hashlib, json, os, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ctl import talk

HERE = os.path.dirname(os.path.abspath(__file__))
TEXT_FN = 0x0808D894
MENU, EXIT, UP, DOWN = 13, 14, 15, 16


class Radio:
    def __init__(self, port, out, settle):
        self.port, self.out, self.settle = port, out, settle
        self.txt = os.path.join(out, "text.log")
        self.ppm = os.path.join(out, "cur.ppm")
        self.steps = 0
        open(self.txt, "w").close()
        hook = ("cpu AddHook 0x%08X \"import System; p = self.Bus.ReadDoubleWord(self.GetRegister(13).RawValue + 4); "
                "s = u''.join([unichr(self.Bus.ReadWord(p + 2*i)) for i in range(48)]).split(unichr(0))[0]; "
                "System.IO.File.AppendAllText('%s', str(self.GetRegister(0).RawValue) + ',' + "
                "str(self.GetRegister(1).RawValue) + ' ' + s.encode('utf-8') + chr(10))\"") % (TEXT_FN, self.txt)
        talk(port, ["cpu RemoveHooksAt 0x%08X" % TEXT_FN, hook])

    def key(self, code, settle=None):
        """Press a key; return (frame hash, texts drawn meanwhile)."""
        pos = os.path.getsize(self.txt)
        self.steps += 1
        talk(self.port, ["lcd Press %d 120" % code, 'emulation RunFor "00:00:%09.6f"' % (settle or self.settle),
                         "lcd Screenshot @%s" % self.ppm])
        with open(self.txt, "rb") as f:
            f.seek(pos)
            lines = f.read().decode("utf-8", "replace").splitlines()
        texts = []
        for l in lines:
            xy, _, s = l.partition(" ")
            if s.strip():
                x, y = (int(v) for v in xy.split(","))
                texts.append((y, x, s.strip()))
        return self.sig(self.frame_hash(), texts), texts

    @staticmethod
    def sig(h, texts):
        """Screen identity. Live screens (a running hh:mm:ss clock, GPS info) change every frame, so for
        them the drawn text with digits masked is used instead of the frame hash."""
        import re
        if any(re.search(r"\d\d:\d\d:\d\d", t[2]) for t in texts):
            return "t" + hashlib.sha1("|".join(re.sub(r"\d", "#", t[2]) for t in sorted(texts)).encode()).hexdigest()[:11]
        return h

    def hold(self, code, secs):
        """Hold a key (e.g. Exit in a text editor clears the text), then release and settle."""
        self.steps += 1
        talk(self.port, ["lcd Hold %d true" % code, 'emulation RunFor "00:00:%09.6f"' % secs,
                         "lcd Hold %d false" % code, 'emulation RunFor "00:00:%09.6f"' % self.settle,
                         "lcd Screenshot @%s" % self.ppm])
        return self.frame_hash()

    def frame_hash(self):
        return hashlib.sha1(open(self.ppm, "rb").read()).hexdigest()[:12]

    def save_shot(self, name):
        dst = os.path.join(self.out, "shots", name + ".png")
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        os.system("%s -I %s %s %s" % (sys.executable, os.path.join(HERE, "ppm2png.py"), self.ppm, dst))
        return os.path.relpath(dst, self.out)


def screen_text(texts):
    """Unique texts in screen order (top-to-bottom, left-to-right)."""
    seen, out = set(), []
    for y, x, s in sorted(texts):
        if s not in seen:
            seen.add(s); out.append(s)
    return out


def item_label(texts):
    """Label of the newly highlighted list row: the last non-title text drawn at x < 60 (row text)."""
    rows = [s for y, x, s in texts if y > 12 and y < 105 and x < 60]
    return rows[-1] if rows else (texts[-1][2] if texts else "?")


class Crawler:
    def __init__(self, r, depth, log):
        self.r, self.depth, self.log = r, depth, log
        self.home = None

    def go_home(self):
        """Exit until the main screen (it draws the date line; its hash changes as the clock runs)."""
        import re
        for i in range(16):
            if i in (6, 11):
                self.r.hold(EXIT, 3.0)          # text editor: Exit deletes one char, long press clears
            h, texts = self.r.key(EXIT)
            if any(re.match(r"\d{4}-\d\d-\d\d", t[2]) for t in texts):
                return True
        return False

    def walk(self, path, here_hash, node, level):
        """Enumerate the list currently shown (hash here_hash, reached by `path` + ENTER)."""
        first = here_hash
        cur = here_hash
        seen = {here_hash}
        idx = 0
        import re
        # Data lists ("Talk Group 1/69", channels, messages): sample the first few entries only.
        limit = 3 if any(re.search(r"\d+/\d+$", t) for t in node.get("text", [])[:2]) else 60
        while idx < limit:
            # enter the item under the cursor
            h, texts = self.r.key(MENU)
            child = {"index": idx, "text": screen_text(texts), "hash": h, "items": []}
            name = "_".join(str(p) for p in path + [idx])
            if h != cur:
                child["shot"] = self.r.save_shot(name)
                self.log("%s%s -> %s" % ("  " * level, name, " | ".join(child["text"][:6])))
                editor = any(t in ("AB", "ab", "Ab", "123", "Text") for t in child["text"])
                if editor:
                    child["editor"] = True
                if level < self.depth and not editor:
                    self.walk(path + [idx], h, child, level + 1)
                # back (editors: clear the text first so Exit leaves instead of deleting a char)
                if child.get("editor"):
                    self.r.hold(EXIT, 3.0)
                hb, _ = self.r.key(EXIT)
                if hb != cur:
                    hb = self.replay_to(path, idx)
                    if hb != cur:
                        self.log("%s!! lost at %s (got %s want %s)" % ("  " * level, name, hb, cur))
                        cur = hb
            else:
                child["leaf"] = "no screen change"
            node["items"].append(child)
            # Data list without an "n/m" title (zones, channels): every entry opens the same option screen.
            sig = [c.get("text", [])[:6] for c in node["items"][-3:]]
            if len(sig) == 3 and sig[0] == sig[1] == sig[2] and sig[0]:
                node["sampled"] = True
                break
            # next item
            h, texts = self.r.key(DOWN)
            if h in seen or h == first:
                break
            child["label_next"] = item_label(texts)
            seen.add(h)
            cur = h
            idx += 1
        node["count"] = idx + 1

    def replay_to(self, path, idx):
        """Go home, open the main menu, follow `path` (downs then ENTER per level), cursor down idx times."""
        if not self.go_home():
            raise RuntimeError("cannot get home")
        h, _ = self.r.key(MENU)
        for downs in path:
            for _ in range(downs):
                h, _ = self.r.key(DOWN)
            h, _ = self.r.key(MENU)
        for _ in range(idx):
            h, _ = self.r.key(DOWN)
        return h


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=1235)
    ap.add_argument("--out", default=os.path.join(HERE, "out", "crawl"))
    ap.add_argument("--depth", type=int, default=3)
    ap.add_argument("--settle", type=float, default=0.6)
    ap.add_argument("--only", type=int, default=-1, help="main-menu item index to crawl (default all)")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    logf = open(os.path.join(a.out, "crawl.log"), "a")
    def log(s):
        print(s); logf.write(s + "\n"); logf.flush()
    r = Radio(a.port, a.out, a.settle)
    c = Crawler(r, a.depth, log)
    # home = main screen (Exit a few times, take the stable frame)
    # Exit on the main screen opens the List (TG list), so look for the screen that draws the date line.
    import re
    for _ in range(8):
        h, texts = r.key(EXIT)
        if any(re.match(r"\d{4}-\d\d-\d\d", t[2]) for t in texts):
            break
    else:
        raise SystemExit("main screen not found")
    c.home = h
    r.save_shot("home")
    t0 = time.time()
    # main menu
    h, texts = r.key(MENU)
    root = {"text": screen_text(texts), "hash": h, "items": [], "shot": r.save_shot("menu")}
    log("menu -> " + " | ".join(root["text"]))
    if a.only >= 0:
        for _ in range(a.only):
            h, _ = r.key(DOWN)
        hh, texts = r.key(MENU)
        child = {"index": a.only, "text": screen_text(texts), "hash": hh, "items": [],
                 "shot": r.save_shot(str(a.only))}
        log("%d -> %s" % (a.only, " | ".join(child["text"][:6])))
        c.walk([a.only], hh, child, 2)
        root["items"].append(child)
    else:
        c.walk([], h, root, 1)
    json.dump(root, open(os.path.join(a.out, "tree%s.json" % ("" if a.only < 0 else "_%d" % a.only)), "w"), indent=1)
    log("done: %d key presses, %.0f s" % (r.steps, time.time() - t0))


if __name__ == "__main__":
    main()
