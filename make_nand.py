#!/usr/bin/env python3
"""Synthetic D890UV NAND image for the emulator: images/synth.pages, made from scratch (no AnyTone files).

The published emulator cannot ship images/extflash.pages (stock first-boot format + AnyTone ICON fonts/logo + a real
codeplug). This writes a stand-in with only our own content:

  * block 0: empty bad-block table (slot 0 used, 0 bad blocks) + format marker 0x1FF00 "CHECK 4G BLOCK FOR VALID."
    (without it the stock firmware erases and test-writes every block on boot);
  * 0x7B000: the 10-byte check value the stock firmware compares at boot (0x55..0x5E, "UVWXYZ[\\]^"; else
    "UPDATE MODE");
  * 0x04EC0000 "CHECKING DATA FOR D890UV": data-area format marker (stock re-initialises the data areas without it;
    cfw shows it on the boot screen);
  * default RF calibration block 0x04F00000 (copy A + valid marker; tables from cfw/drivers/cal_default.c);
  * a small made-up channel set in the stock CPS channel layout at 0x01000000 (bank 0, 128-byte records, as
    cfw/app/channels.c reads it): FM and DMR test channels "TEST 1".., + block valid marker 55 55 AA AA at +0x3FFFC.

No ICON data (fonts, logo): the stock firmware boots on it but draws no text. The cfw has its own font.
Radio identity used in the made-up data: callsign N0CALL, DMR ID 9990001 (not real).

Pages file format (as emu/build_extflash.py): sequence of (u32 page address LE, 4096 bytes) for non-blank pages.

  python3 emu/make_nand.py [--out images/synth.pages]
"""
import argparse
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PAGE = 0x1000
VALID = b"\x55\x55\xaa\xaa"
CP_BASE = 0x01000000              # channel bank 0 (cfw/app/channels.c CP_BASE)
CP_REC = 128

# made-up channels: (name, RX MHz, TX offset MHz (signed), digital, colour code, slot, 25 kHz)
CHANNELS = [
    ("TEST 1", 146.520, 0.0, False, 0, 1, True),
    ("TEST 2", 446.000, 0.0, False, 0, 1, False),
    ("TEST 3 RPT", 442.000, 5.0, False, 0, 1, True),
    ("TEST 4 RPT", 145.210, -0.6, False, 0, 1, True),
    ("TEST 5 DMR", 441.000, 0.0, True, 1, 1, False),
    ("TEST 6 DMR", 446.500, 0.0, True, 1, 2, False),
    ("TEST 7 DMR RPT", 446.025, -5.0, True, 1, 1, False),
    ("TEST 8 DMR RPT", 446.075, -5.0, True, 2, 2, False),
]


class Flash:
    def __init__(self):
        self.pages = {}

    def write(self, addr, data):
        data = bytes(data)
        while data:
            pa, off = addr & ~(PAGE - 1), addr & (PAGE - 1)
            n = min(len(data), PAGE - off)
            self.pages.setdefault(pa, bytearray(b"\xff" * PAGE))[off:off + n] = data[:n]
            addr, data = addr + n, data[n:]

    def load_pages(self, path):
        d = open(path, "rb").read()
        for o in range(0, len(d), 4 + PAGE):
            self.write(struct.unpack_from("<I", d, o)[0], d[o + 4:o + 4 + PAGE])

    def save(self, path):
        n = 0
        with open(path, "wb") as f:
            for pa in sorted(self.pages):
                p = bytes(self.pages[pa])
                if p != b"\xff" * PAGE:
                    f.write(struct.pack("<I", pa) + p)
                    n += 1
        return n


def bcd(v):
    """integer -> 4 BCD bytes big endian (8 digits)"""
    s = "%08d" % v
    return bytes(int(s[i]) << 4 | int(s[i + 1]) for i in range(0, 8, 2))


def channel_record(name, rx_mhz, off_mhz, digital, cc, slot, wide):
    r = bytearray(CP_REC)
    r[0:4] = bcd(int(round(rx_mhz * 1e5)))            # 10 Hz units
    r[4:8] = bcd(int(round(abs(off_mhz) * 1e5)))
    d = 0 if off_mhz == 0 else (1 if off_mhz > 0 else 2)
    r[8] = (d << 6) | (int(wide) << 4) | (2 << 2) | (1 if digital else 0)   # power level 2
    r[0x14:0x16] = struct.pack("<H", 0)                # contact index 0
    r[0x20] = cc & 0x0F
    r[0x21] = slot - 1
    n = name.encode("ascii")[:16]
    for i, c in enumerate(n):
        r[0x44 + 2 * i] = c
    return bytes(r)


def build():
    fl = Flash()
    table = bytearray(0x1F8)
    table[0x1F4] = 0x55                                # slot 0 used, index 0, 0 bad blocks
    fl.write(0x00000, table)
    fl.write(0x1FF00, b"CHECK 4G BLOCK FOR VALID.")
    fl.write(0x7B000, bytes(range(0x55, 0x5F)))
    fl.write(0x04EC0000, b"CHECKING DATA FOR D890UV")
    for i, ch in enumerate(CHANNELS):
        fl.write(CP_BASE + i * CP_REC, channel_record(*ch))
    fl.write(CP_BASE + 0x3FFFC, VALID)
    # default RF calibration (cfw/drivers/cal_default.c, via make_rfcal.py)
    sys.path.insert(0, HERE)
    import make_rfcal
    cal = make_rfcal.cal_default()
    if cal is None:
        print("note: %s missing, no RF calibration block (cfw refuses TX)" % make_rfcal.CAL_C)
    else:
        for pa, p in make_rfcal.pages(cal).items():
            fl.write(pa, p)
    return fl


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--out", default=os.path.join(HERE, "images", "synth.pages"))
    a = ap.parse_args()
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    n = build().save(a.out)
    print("%s: %d pages, %d channels" % (a.out, n, len(CHANNELS)))


if __name__ == "__main__":
    main()
