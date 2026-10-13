#!/usr/bin/env python3
"""Draws the Remotely app icon (same visual family as Portico's: night tile, amber) and writes every size the Windows and macOS apps need.
   python3 tools/make-icons.py      (needs Pillow)
"""
from PIL import Image, ImageDraw
import os

S = 2048  # draw large, then reduce: smooth edges at every size

def grad(size, top, bottom):
    img = Image.new("RGB", size)
    px = img.load()
    for y in range(size[1]):
        t = y / max(1, size[1] - 1)
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
        for x in range(size[0]):
            px[x, y] = c
    return img

def rounded_mask(size, box, r):
    m = Image.new("L", size, 0)
    ImageDraw.Draw(m).rounded_rectangle(box, r, fill=255)
    return m

def k(v):  # coordinates were designed on a 1024 grid
    return int(v * S / 1024)

base = Image.new("RGBA", (S, S), (0, 0, 0, 0))
base.paste(Image.new("RGBA", (S, S), (0x1E, 0x23, 0x28, 255)), (0, 0), rounded_mask((S, S), (0, 0, S - 1, S - 1), k(228)))
shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(shadow).rounded_rectangle((k(190), k(250), k(846), k(690)), k(56), fill=(0x11, 0x15, 0x1A, 120))
base = Image.alpha_composite(base, shadow)
base.paste(grad((S, S), (0xFF, 0xD2, 0x77), (0xD9, 0x8A, 0x0E)).convert("RGBA"), (0, 0), rounded_mask((S, S), (k(172), k(228), k(852), k(668)), k(56)))
base.paste(Image.new("RGBA", (S, S), (0x16, 0x1A, 0x1E, 255)), (0, 0), rounded_mask((S, S), (k(212), k(268), k(812), k(628)), k(28)))
d = ImageDraw.Draw(base)
d.polygon([(k(452), k(668)), (k(572), k(668)), (k(596), k(764)), (k(428), k(764))], fill=(0xE0, 0x9A, 0x1C, 255))
d.rounded_rectangle((k(372), k(756), k(652), k(808)), k(26), fill=(0xF2, 0xB1, 0x34, 255))
pointer = [(k(430), k(336)), (k(430), k(566)), (k(496), k(506)), (k(546), k(610)), (k(600), k(586)), (k(550), k(484)), (k(636), k(484))]
cream = grad((S, S), (0xFF, 0xFB, 0xF0), (0xE8, 0xD6, 0xAE)).convert("RGBA")
pm = Image.new("L", (S, S), 0)
ImageDraw.Draw(pm).polygon(pointer, fill=255)
base.paste(cream, (0, 0), pm)
ImageDraw.Draw(base).line(pointer + [pointer[0]], fill=(0xC9, 0xB5, 0x8E, 255), width=k(10), joint="curve")

root = os.path.join(os.path.dirname(__file__), "..")
win = os.path.join(root, "src", "Remotely.Viewer", "Assets")
os.makedirs(win, exist_ok=True)
for n in (16, 24, 32, 48, 64, 128, 256, 512, 1024):
    base.resize((n, n), Image.LANCZOS).save(os.path.join(win, f"remotely-{n}.png"))
base.resize((256, 256), Image.LANCZOS).save(os.path.join(win, "remotely.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
base.resize((1024, 1024), Image.LANCZOS).save(os.path.join(root, "mac", "Remotely.icns"), format="ICNS")
print("icons written")
