#!/usr/bin/env python3
"""Build the emulated external SPI flash contents for the D890UV.

Sources:
  1. ICON package (.CDI file table + .CDD blob): logo, icons, fonts at 0x00040000-0x00452800.
     .CDI = N entries of 0x116 bytes: name (GBK), u32 addr, u32 size, u32 offset in .CDD, ...
  2. Optional codeplug captured from the real CPS by emu/fake_radio_cps.py (writes.bin:
     records of u32 addr, u16 len, data). CPS addresses are external-flash addresses.

Codeplug blocks use the firmware's A/B scheme (FUN_08023ea8): for every 256 KB block the
copy at `base` is used when base+0x3FFFC holds 55 55 AA AA and base+0x3FDF8 does not hold
AA AA 55 55. Captured data goes in copy A and gets that marker.

Output: images/extflash.pages = sequence of (u32 page_addr, 4096 bytes) for non-blank pages.
Everything else reads as 0xFF (erased). Also images/extflash_low16M.bin (dense first 16 MB).

Usage: build_extflash.py [--codeplug out/codeplug_capture/writes.bin]
"""
import os, struct, sys, hashlib, argparse

HERE = os.path.dirname(os.path.abspath(__file__))
PKG = os.path.join(os.path.dirname(os.path.dirname(HERE)), "peripheral_fw", "D890UV_ICON_V102")
ENTRY = 0x116
PAGE = 0x1000
VALID = b"\x55\x55\xaa\xaa"


class Flash:
    def __init__(self):
        self.pages = {}

    def write(self, addr, data):
        while data:
            pa, off = addr & ~(PAGE - 1), addr & (PAGE - 1)
            n = min(len(data), PAGE - off)
            p = self.pages.setdefault(pa, bytearray(b"\xff" * PAGE))
            p[off:off + n] = data[:n]
            addr, data = addr + n, data[n:]

    def read(self, addr, n):
        out = bytearray()
        while n:
            pa, off = addr & ~(PAGE - 1), addr & (PAGE - 1)
            k = min(n, PAGE - off)
            p = self.pages.get(pa)
            out += p[off:off + k] if p else b"\xff" * k
            addr, n = addr + k, n - k
        return bytes(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--codeplug", help="writes.bin from fake_radio_cps.py")
    ap.add_argument("--out", default=os.path.join(HERE, "images"))
    ap.add_argument("--base", help="start from a factory-formatted NAND (pages file saved by run.py --save-nand)")
    ap.add_argument("--name", default="extflash.pages")
    a = ap.parse_args()
    fl = Flash()
    if a.base:
        d = open(a.base, "rb").read()
        for o in range(0, len(d), 4 + PAGE):
            fl.pages[struct.unpack_from("<I", d, o)[0]] = bytearray(d[o + 4:o + 4 + PAGE])
        print("base: %d pages from %s" % (len(d) // (4 + PAGE), a.base))

    cdi = open(os.path.join(PKG, "D890UV_ICON_V102_20260324.CDI"), "rb").read()
    cdd = open(os.path.join(PKG, "D890UV_ICON_V102_20260324.CDD"), "rb").read()
    assert len(cdi) % ENTRY == 0, "unexpected .CDI size"
    for i in range(len(cdi) // ENTRY):
        e = cdi[i * ENTRY:(i + 1) * ENTRY]
        addr, n, off = struct.unpack_from("<III", e, 0x100)
        fl.write(addr, cdd[off:off + n])
    print("ICON: %d files, %d bytes" % (len(cdi) // ENTRY, len(cdd)))

    # Factory format of NAND block 0 (FUN_081318e8 / FUN_080e0a34 / FUN_0813a4a0). Without it the
    # firmware runs its first-boot scan that erases and test-writes every 256 KB block.
    #   0x00000: bad-block table slot 0, 0x1F8 bytes: u16 bad[..], u16 spare[..] @0xFA,
    #            0x1F4 = 0x55 (slot used), 0x1F5 = slot index, 0x1F6 = bad-block count, 0x1F7 = 0
    #   0x1FF00: "CHECK 4G BLOCK FOR VALID." (25 bytes)
    table = bytearray(0x1F8)
    table[0x1F4] = 0x55
    fl.write(0x00000, bytes(table))
    fl.write(0x1FF00, b"CHECK 4G BLOCK FOR VALID.")
    print("NAND block 0: format marker + empty bad-block table")
    # Data-area format marker (FUN_08030d24 / FUN_0805cdc8). Missing = firmware re-initialises every
    # data area, codeplug included, on boot.
    fl.write(0x04EC0000, b"CHECKING DATA FOR D890UV")
    print("0x04EC0000: data format marker")

    if a.codeplug:
        d = open(a.codeplug, "rb").read()
        recs, o = [], 0
        while o < len(d):
            addr, n = struct.unpack_from("<IH", d, o)
            recs.append((addr, d[o + 6:o + 6 + n]))
            o += 6 + n
        blocks = {addr & 0xFFFC0000 for addr, _ in recs}
        # The radio erases a block before writing CPS data into it, and the pair's other copy is
        # retired: start every touched pair blank (drops factory defaults), A = CPS data + valid marker.
        for b in blocks:
            for pa in range(b, b + 0x80000, PAGE):
                fl.pages.pop(pa, None)
        total = 0
        for addr, data in recs:
            fl.write(addr, data)
            total += len(data)
        for b in blocks:
            fl.write(b + 0x3FFFC, VALID)
        print("codeplug: %d bytes in %d blocks (A copy + valid marker)" % (total, len(blocks)))

    os.makedirs(a.out, exist_ok=True)
    h = hashlib.sha256()
    with open(os.path.join(a.out, a.name), "wb") as f:
        for pa in sorted(fl.pages):
            p = bytes(fl.pages[pa])
            if p != b"\xff" * PAGE:
                f.write(struct.pack("<I", pa) + p)
                h.update(struct.pack("<I", pa) + p)
    open(os.path.join(a.out, "extflash_low16M.bin"), "wb").write(fl.read(0, 16 << 20))
    n = sum(1 for p in fl.pages.values() if bytes(p) != b"\xff" * PAGE)
    print("extflash.pages: %d pages (%.1f MB), top 0x%08x, sha256 %s"
          % (n, n * PAGE / 1e6, max(fl.pages) + PAGE, h.hexdigest()[:16]))


if __name__ == "__main__":
    main()
