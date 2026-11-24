#!/usr/bin/env python3
"""Convert an LCD PPM screenshot to PNG (stdlib only). Usage: ppm2png.py in.ppm out.png [--rot 0|90|180|270] [--scale N]"""
import sys, zlib, struct, argparse
ap = argparse.ArgumentParser(); ap.add_argument("inp"); ap.add_argument("out")
ap.add_argument("--rot", type=int, default=0)
ap.add_argument("--raw", action="store_true", help="controller memory order (default: transpose to the D890UV panel view)"); ap.add_argument("--scale", type=int, default=3)
a = ap.parse_args()
d = open(a.inp, "rb").read()
parts = d.split(b"\n", 3); w, h = map(int, parts[1].split()); px = parts[3]
img = [[px[(r * w + c) * 3:(r * w + c) * 3 + 3] for c in range(w)] for r in range(h)]
if not a.raw:  # D890UV: controller column axis = panel Y, row axis = panel X (MADCTL MV)
    img = [list(col) for col in zip(*img)]
for _ in range((a.rot // 90) % 4):
    img = [list(row) for row in zip(*img[::-1])]
h, w = len(img), len(img[0])
raw = b"".join(b"\0" + b"".join(p * a.scale for p in row) * 1 for row in img for _ in range(a.scale))
def chunk(t, b): return struct.pack(">I", len(b)) + t + b + struct.pack(">I", zlib.crc32(t + b) & 0xffffffff)
open(a.out, "wb").write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w * a.scale, h * a.scale, 8, 2, 0, 0, 0))
                        + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))
