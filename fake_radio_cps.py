#!/usr/bin/env python3
"""Pretend to be a D890UV on a virtual serial port so the real CPS (under Wine) can
"Write to radio". Every 16-byte block CPS writes is recorded with its address.

Protocol (from the radio firmware's PC-link handler, FUN_080d4664 / FUN_0812efdc / FUN_0812f19c):
  "PROGRAM"                          -> "QX\\x06"
  0x02                               -> 'I' "D890UV\\0" <hw> "V100" 00 00 06   (16 bytes)
  'R' a3 a2 a1 a0 len                -> 'W' addr len data cs 06   (cs = sum of addr..data)
  'W' a3 a2 a1 a0 len data cs 06     -> 06  (stored)
  "END"                              -> 06
Addresses are big-endian and equal external-flash addresses (>= 0x01000000).

Usage: fake_radio_cps.py <wineprefix> <outdir> [--com com1] [--read-image extflash.bin]
Writes <outdir>/writes.bin (records: u32 addr, u16 len, data), traffic.log, summary.txt.
"""
import os, sys, pty, tty, struct, select, time, argparse, termios

ID = b"I" + b"D890UV\x00" + b"\x00" + b"V100" + b"\x00\x00" + b"\x06"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("wineprefix")
    ap.add_argument("outdir")
    ap.add_argument("--com", default="com1")
    ap.add_argument("--read-image", help="answer reads from this flash image (default 0xFF)")
    ap.add_argument("--idle-exit", type=float, default=0, help="exit after N s idle once END seen")
    a = ap.parse_args()
    os.makedirs(a.outdir, exist_ok=True)
    master, slave = pty.openpty()
    tty.setraw(master)
    tty.setraw(slave)
    sname = os.ttyname(slave)
    link = os.path.join(a.wineprefix, "dosdevices", a.com)
    if os.path.islink(link) or os.path.exists(link):
        os.remove(link)
    os.symlink(sname, link)
    img = open(a.read_image, "rb").read() if a.read_image else None
    log = open(os.path.join(a.outdir, "traffic.log"), "a")
    wr = open(os.path.join(a.outdir, "writes.bin"), "ab")
    stats = {"writes": 0, "reads": 0, "bad_cs": 0, "unknown": 0}
    lo, hi = None, None
    print("fake radio on %s -> %s" % (link, sname), flush=True)
    buf = b""
    ended_at = None

    def send(b, why):
        os.write(master, b)
        log.write("%.3f TX %-6s %s\n" % (time.time(), why, b.hex()))

    while True:
        r, _, _ = select.select([master], [], [], 0.5)
        if not r:
            if ended_at and a.idle_exit and time.time() - ended_at > a.idle_exit:
                break
            continue
        try:
            chunk = os.read(master, 4096)
        except OSError:
            time.sleep(0.2)
            continue
        log.write("%.3f RX %s\n" % (time.time(), chunk.hex()))
        buf += chunk
        while buf:
            if buf.startswith(b"PROGRAM"):
                buf = buf[7:]; send(b"QX\x06", "prog")
            elif buf[:1] == b"\x02":
                buf = buf[1:]; send(ID, "id")
            elif buf[:1] == b"R":
                if len(buf) < 6: break
                addr, n = struct.unpack(">IB", buf[1:6]); buf = buf[6:]
                data = img[addr:addr + n] if img and addr + n <= len(img) else b"\xff" * n
                body = struct.pack(">IB", addr, n) + data
                send(b"W" + body + bytes([sum(body) & 0xFF, 6]), "read")
                stats["reads"] += 1
            elif buf[:1] == b"W":
                if len(buf) < 6: break
                n = buf[5]
                if len(buf) < 8 + n: break
                pkt, buf = buf[:8 + n], buf[8 + n:]
                addr = struct.unpack(">I", pkt[1:5])[0]
                data = pkt[6:6 + n]
                if sum(pkt[1:6 + n]) & 0xFF != pkt[6 + n]:
                    stats["bad_cs"] += 1
                wr.write(struct.pack("<IH", addr, n) + data)
                stats["writes"] += 1
                lo = addr if lo is None else min(lo, addr)
                hi = addr + n if hi is None else max(hi, addr + n)
                send(b"\x06", "ack")
            elif buf.startswith(b"END"):
                buf = buf[3:]; send(b"\x06", "end"); ended_at = time.time()
                wr.flush()
            elif len(buf) < 7 and b"PROGRAM".startswith(buf) or buf in (b"E", b"EN"):
                break
            else:
                log.write("%.3f ?? %s\n" % (time.time(), buf[:16].hex()))
                stats["unknown"] += 1
                buf = buf[1:]
        log.flush()
        open(os.path.join(a.outdir, "summary.txt"), "w").write(
            "%s range 0x%s-0x%s\n" % (stats, "%08x" % lo if lo is not None else "-", "%08x" % hi if hi is not None else "-"))


if __name__ == "__main__":
    main()
