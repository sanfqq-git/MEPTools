# -*- coding: utf-8 -*-
"""放大检查某几个图标（32px 放大 4 倍、16px 放大 8 倍），不改动正式产物"""
import sys

from PIL import Image, ImageDraw, ImageFont
import make_icons as M

TARGET = sys.argv[1:] or list(M.ICONS.keys())

f = ImageFont.truetype(M.FONT_CN, 13)
zoomed32 = [M.render(M.ICONS[t], False).resize((128, 128), Image.NEAREST) for t in TARGET]
zoomed16 = [M.render(M.ICONS[t], True).resize((128, 128), Image.NEAREST) for t in TARGET]

cols = 5
rows = (len(TARGET) + cols - 1) // cols
cw, ch = 148, 300
sheet = Image.new("RGBA", (cols * cw, rows * ch), (240, 241, 243, 255))
d = ImageDraw.Draw(sheet)
for i, t in enumerate(TARGET):
    r, c = divmod(i, cols)
    x, y = c * cw + 8, r * ch + 6
    d.text((x, y), t, font=f, fill=(30, 34, 40, 255))
    sheet.alpha_composite(zoomed32[i], (x, y + 18))
    sheet.alpha_composite(zoomed16[i], (x, y + 156))
    d.text((x, y + 140), "32px", font=f, fill=(90, 96, 104, 255))
    d.text((x, y + 288), "16px", font=f, fill=(90, 96, 104, 255))
sheet.save(M.os.path.join(M.HERE, "zoom.png"))
print("zoom.png ->", len(TARGET))
