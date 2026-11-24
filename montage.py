"""Tile emulator screenshots with labels: montage.py out.png label=file.png ..."""
import sys
from PIL import Image, ImageDraw
out, items = sys.argv[1], [a.split("=", 1) for a in sys.argv[2:]]
ims = [(l, Image.open(f).convert("RGB").resize((240, 192))) for l, f in items]
w, h = ims[0][1].size
cols = min(4, len(ims)); rows = (len(ims) + cols - 1) // cols
M = Image.new("RGB", (cols * (w + 4), rows * (h + 16)), "white")
d = ImageDraw.Draw(M)
for i, (l, im) in enumerate(ims):
    x, y = (i % cols) * (w + 4), (i // cols) * (h + 16)
    d.text((x + 2, y + 2), l, fill="black"); M.paste(im, (x, y + 14))
M.save(out)
