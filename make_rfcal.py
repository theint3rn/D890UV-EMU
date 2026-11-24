#!/usr/bin/env python3
"""Default RF calibration block for the emulated D890UV NAND (cfw runs).

Writes images/rfcal_synthetic.pages (sparse pages file, same format as extflash.pages): NAND 0x04F00000 (copy A) =
the default RF calibration tables of cfw/drivers/cal_default.c (made by cfw/tools/make_cal_default.py) + valid marker
55 55 AA AA at +0x3FFFC. Layout as cfw/drivers/dac.c reads it: +0x000 UHF struct (APC per level, tune points), +0x100
VHF struct, APC tables UHF +0xC00.., VHF +0x1000.. . A cfw refuses TX without a valid calibration block, so
emu/run.py loads this file on top of the NAND image whenever it runs a cfw ELF.

  python3 emu/make_rfcal.py [--cal cfw/drivers/cal_default.c]"""
import argparse
import os
import re
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PAGE = 0x1000
BASE = 0x04F00000
CAL_C = os.path.join(os.path.dirname(HERE), "cfw", "drivers", "cal_default.c")


def cal_default(path=CAL_C):
    """bytes of cal_default[] in cfw/drivers/cal_default.c (None when the file is missing)"""
    if not os.path.exists(path):
        return None
    s = open(path).read()
    s = re.sub(r"/\*.*?\*/", "", s[s.index("cal_default[CAL_DEFAULT_LEN]"):], flags=re.S)
    return bytes(int(x, 16) for x in re.findall(r"0x([0-9A-Fa-f]{2})\b", s))


def pages(cal):
    """{page address: bytearray} of the calibration block (copy A + valid marker)"""
    out = {}

    def w(addr, data):
        for i, b in enumerate(data):
            a = addr + i
            out.setdefault(a & ~(PAGE - 1), bytearray(b"\xff" * PAGE))[a & (PAGE - 1)] = b

    w(BASE, cal)
    w(BASE + 0x3FFFC, b"\x55\x55\xaa\xaa")
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--cal", default=CAL_C, help="cal_default.c (default cfw/drivers/cal_default.c)")
    a = ap.parse_args()
    cal = cal_default(a.cal)
    if cal is None:
        sys.exit("%s not found: make it with cfw/tools/make_cal_default.py" % a.cal)
    pg = pages(cal)
    os.makedirs(os.path.join(HERE, "images"), exist_ok=True)
    out = os.path.join(HERE, "images", "rfcal_synthetic.pages")
    with open(out, "wb") as f:
        for a_ in sorted(pg):
            f.write(struct.pack("<I", a_) + bytes(pg[a_]))
    print("%s: %d pages (%d bytes of default calibration)" % (out, len(pg), len(cal)))


if __name__ == "__main__":
    main()
