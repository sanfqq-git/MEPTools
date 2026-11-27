# -*- coding: utf-8 -*-
"""
MEPTools Ribbon 图标生成器

规则（与 App.cs 的 ApplyIcon 完全对应，不改任何功能代码）：
    LargeImage = {Text}_32.png
    Image      = {Text}_16.png
    路径       = pack://application:,,,/MEPTools;component/Resources/{fileName}
所以文件名必须严格等于 RibbonConfig.cs 里的 Text 字段。
本脚本从 RibbonConfig.cs 读取全部 Text，逐个生成 32 / 16 两个尺寸的 PNG 到 ..\  (即 Resources)。

用法：
    python make_icons.py            # 生成全部图标
    python make_icons.py --preview  # 生成 + 输出两张对照预览图
"""

import math
import os
import re
import sys

from PIL import Image, ImageDraw, ImageFont

SCALE = 8          # 超采样倍数（256 -> 32）
SIZE = 32
HERE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.abspath(os.path.join(HERE, os.pardir))
CFG = os.path.abspath(os.path.join(OUT_DIR, os.pardir, "RibbonConfig.cs"))
FONT_BOLD = r"C:\Windows\Fonts\arialbd.ttf"
FONT_CN = r"C:\Windows\Fonts\msyh.ttc"

# ---------------- 调色板 ----------------
BLUE = (34, 113, 180)
BLUE_D = (20, 62, 102)
TEAL = (18, 137, 112)
TEAL_D = (10, 94, 78)
ORANGE = (214, 122, 35)
RED = (190, 52, 42)
SLATE = (96, 112, 128)
GRAY = (152, 165, 178)
DARK = (28, 41, 55)
LIGHT = (232, 242, 251)
WHITE = (255, 255, 255)
CHIP = (247, 250, 252)
CHIP_BD = (158, 176, 192)


def _c(c, a=255):
    return (int(c[0]), int(c[1]), int(c[2]), a)


def _p(pts):
    return [(x * SCALE, y * SCALE) for (x, y) in pts]


def mix(c, t):
    """向白色混合，t=0 原色，t=1 全白"""
    return (int(c[0] + (255 - c[0]) * t), int(c[1] + (255 - c[1]) * t), int(c[2] + (255 - c[2]) * t))


# ---------------- 基础图元 ----------------
def rrect(d, box, r=1.5, fill=None, outline=None, w=1.0):
    d.rounded_rectangle(
        [box[0] * SCALE, box[1] * SCALE, box[2] * SCALE, box[3] * SCALE],
        radius=r * SCALE,
        fill=fill,
        outline=outline,
        width=max(1, round(w * SCALE)),
    )


def ln(d, pts, c=SLATE, w=1.0):
    d.line(_p(pts), fill=_c(c), width=max(1, round(w * SCALE)), joint="curve")


def dot(d, x, y, r, c=DARK):
    d.ellipse([(x - r) * SCALE, (y - r) * SCALE, (x + r) * SCALE, (y + r) * SCALE], fill=_c(c))


def arrow_head(d, tip, ang_deg, c=SLATE, size=2.4):
    a = math.radians(ang_deg)
    p1 = (tip[0] - size * math.cos(a - math.radians(26)), tip[1] - size * math.sin(a - math.radians(26)))
    p2 = (tip[0] - size * math.cos(a + math.radians(26)), tip[1] - size * math.sin(a + math.radians(26)))
    d.polygon(_p([tip, p1, p2]), fill=_c(c))


def arrow(d, p1, p2, c=BLUE, w=1.2, size=2.4):
    d.line(_p([p1, p2]), fill=_c(c), width=max(1, round(w * SCALE)), joint="curve")
    ang = math.degrees(math.atan2(p2[1] - p1[1], p2[0] - p1[0]))
    arrow_head(d, p2, ang, c, size)


def dash(d, p1, p2, c=SLATE, w=0.9, dl=1.5, gp=1.2):
    x1, y1 = p1
    x2, y2 = p2
    dx, dy = x2 - x1, y2 - y1
    L = math.hypot(dx, dy)
    if L == 0:
        return
    ux, uy = dx / L, dy / L
    t = 0.0
    while t < L:
        e = min(t + dl, L)
        d.line(
            _p([(x1 + ux * t, y1 + uy * t), (x1 + ux * e, y1 + uy * e)]),
            fill=_c(c),
            width=max(1, round(w * SCALE)),
        )
        t = e + gp


def dash_rect(d, box, c=SLATE, w=0.9, r=0.0):
    x1, y1, x2, y2 = box
    dash(d, (x1, y1), (x2, y1), c, w)
    dash(d, (x2, y1), (x2, y2), c, w)
    dash(d, (x2, y2), (x1, y2), c, w)
    dash(d, (x1, y2), (x1, y1), c, w)


def dim_h(d, y, x1, x2, c=SLATE, w=0.9):
    ln(d, [(x1, y), (x2, y)], c, w)
    arrow_head(d, (x2, y), 0, c, 2.0)
    arrow_head(d, (x1, y), 180, c, 2.0)
    ln(d, [(x1, y - 1), (x1, y + 1)], c, w)
    ln(d, [(x2, y - 1), (x2, y + 1)], c, w)


def dim_v(d, x, y1, y2, c=SLATE, w=0.9):
    ln(d, [(x, y1), (x, y2)], c, w)
    arrow_head(d, (x, y2), 90, c, 2.0)
    arrow_head(d, (x, y1), 270, c, 2.0)
    ln(d, [(x - 1, y1), (x + 1, y1)], c, w)
    ln(d, [(x - 1, y2), (x + 1, y2)], c, w)


def pipe(d, pts, w=4.0, c=BLUE, sm=False):
    """带描边的管道：深色轮廓 + 色带 + 高光"""
    p = _p(pts)
    d.line(p, fill=_c(DARK), width=max(1, round((w + 1.4) * SCALE)), joint="curve")
    d.line(p, fill=_c(c), width=max(1, round(w * SCALE)), joint="curve")
    if not sm:
        d.line(p, fill=_c(mix(c, 0.66)), width=max(1, round(w * 0.30 * SCALE)), joint="curve")


def flange(d, x, y, axis_ang, w, c=DARK, t=1.1):
    """垂直于管道轴线的小法兰（表现三通/接头）"""
    a = math.radians(axis_ang + 90)
    l = (w + 2.2) / 2.0
    d.line(
        _p([(x - math.cos(a) * l, y - math.sin(a) * l), (x + math.cos(a) * l, y + math.sin(a) * l)]),
        fill=_c(c),
        width=max(1, round(t * SCALE)),
    )


SMALL = False   # 渲染 16px 版本时置 True，用于减少细节


def adjustable(d, vx, vy, alpha, ghost, r=5.0, c=BLUE, length=12.0):
    """在弯点画一条虚线备用射线 + 角度弧 + 顶点圆点，表示该角度可调（自定义角度）"""
    a = math.radians(ghost)
    dash(d, (vx, vy), (vx + length * math.cos(a), vy - length * math.sin(a)), mix(c, 0.30), 0.9, 1.2, 1.0)
    pts = []
    for i in range(11):
        t = math.radians(ghost + (alpha - ghost) * i / 10.0)
        pts.append((vx + r * math.cos(t), vy - r * math.sin(t)))
    ln(d, pts, c, 0.9)
    dot(d, vx, vy, 1.2, c)


def angle_glyph(d, cx, cy, r=3.6, sweep=55, c=BLUE, ghost=None):
    """自定义角度标记：顶点 + 两条实线射线 + 弧；ghost 为一条虚线射线（表示角度可调）"""
    vx, vy = cx - r * 0.85, cy + r * 0.85
    a = math.radians(sweep)
    ln(d, [(vx, vy), (vx + r * 1.95, vy)], c, 1.1)
    ln(d, [(vx, vy), (vx + r * 1.75 * math.cos(a), vy - r * 1.75 * math.sin(a))], c, 1.1)
    arc_pts = []
    for i in range(13):
        t = math.radians(sweep * i / 12.0)
        arc_pts.append((vx + r * 0.95 * math.cos(t), vy - r * 0.95 * math.sin(t)))
    ln(d, arc_pts, c, 0.9)
    if ghost is not None:
        ag = math.radians(ghost)
        dash(d, (vx, vy), (vx + r * 2.2 * math.cos(ag), vy - r * 2.2 * math.sin(ag)), mix(c, 0.35), 0.8, 1.1, 0.9)
    dot(d, vx, vy, 0.85, c)


def tag(d, box, c=SLATE, fill=WHITE, bands=2):
    x, y, x2, y2 = box
    w, h = x2 - x, y2 - y
    rrect(d, box, 1.3, fill=_c(fill, 250), outline=_c(c), w=1.0)
    if SMALL:
        bands = 1
    for i in range(bands):
        yy = y + h * (i + 1.0) / (bands + 1.0)
        ln(d, [(x + 0.9, yy), (x2 - 0.9, yy)], c, 0.8)


def gear(d, cx, cy, r, c=SLATE):
    for i in range(8):
        a = math.radians(i * 45 + 22.5)
        ln(
            d,
            [(cx + math.cos(a) * r * 0.72, cy + math.sin(a) * r * 0.72),
             (cx + math.cos(a) * r * 1.02, cy + math.sin(a) * r * 1.02)],
            c, 0.9,
        )
    d.ellipse(
        [(cx - r * 0.74) * SCALE, (cy - r * 0.74) * SCALE, (cx + r * 0.74) * SCALE, (cy + r * 0.74) * SCALE],
        outline=_c(c), width=max(1, round(1.0 * SCALE)),
    )
    dot(d, cx, cy, r * 0.28, c)


def eye(d, cx, cy, w=2.7, h=1.8, c=SLATE, open_=True, slash=False):
    if open_:
        top = [(cx - w + 2 * w * i / 12.0, cy - h * math.sin(math.pi * i / 12.0)) for i in range(13)]
        bot = [(cx + w - 2 * w * i / 12.0, cy + h * math.sin(math.pi * i / 12.0)) for i in range(13)]
        ln(d, top + bot + [top[0]], c, 0.9)
        dot(d, cx, cy, h * 0.5, c)
    else:
        arc = [(cx - w + 2 * w * i / 12.0, cy + h * 0.75 * math.sin(math.pi * i / 12.0)) for i in range(13)]
        ln(d, arc, c, 0.9)
    if slash:
        ln(d, [(cx - w, cy - h * 1.5), (cx + w, cy + h * 1.5)], RED, 1.0)


def lock(d, cx, cy, s=6.0, c=SLATE, open_=False):
    rrect(d, (cx - s * 0.42, cy - s * 0.06, cx + s * 0.42, cy + s * 0.62), 0.8,
          fill=_c(WHITE, 240), outline=_c(c), w=1.0)
    dot(d, cx, cy + s * 0.20, s * 0.09, c)
    ln(d, [(cx, cy + s * 0.24), (cx, cy + s * 0.38)], c, 0.8)
    if open_:
        d.arc(
            [(cx - s * 0.26 + s * 0.16) * SCALE, (cy - s * 0.52) * SCALE,
             (cx + s * 0.34 + s * 0.16) * SCALE, (cy + s * 0.10) * SCALE],
            start=185, end=330, fill=_c(c), width=max(1, round(0.9 * SCALE)),
        )
    else:
        d.arc(
            [(cx - s * 0.30) * SCALE, (cy - s * 0.50) * SCALE,
             (cx + s * 0.30) * SCALE, (cy + s * 0.10) * SCALE],
            start=180, end=360, fill=_c(c), width=max(1, round(0.9 * SCALE)),
        )


def cloud(d, cx, cy, w=19.0, h=9.0, c=BLUE, edge=None):
    """云：edge=None 时实心浅色；edge 给颜色时先铺一层放大版做描边"""
    def shape(grow, colr):
        d.ellipse([(cx - w * 0.50 * grow) * SCALE, (cy - h * 0.10 * grow) * SCALE,
                   (cx - w * 0.10 * grow) * SCALE, (cy + h * 0.70 * grow) * SCALE], fill=colr)
        d.ellipse([(cx - w * 0.28 * grow) * SCALE, (cy - h * 0.55 * grow) * SCALE,
                   (cx + w * 0.14 * grow) * SCALE, (cy + h * 0.55 * grow) * SCALE], fill=colr)
        d.ellipse([(cx + w * 0.02 * grow) * SCALE, (cy - h * 0.26 * grow) * SCALE,
                   (cx + w * 0.46 * grow) * SCALE, (cy + h * 0.62 * grow) * SCALE], fill=colr)
        rrect(d, (cx - w * 0.46 * grow, cy + h * 0.18 * grow, cx + w * 0.42 * grow, cy + h * 0.62 * grow),
              h * 0.24, fill=colr)

    if edge is not None:
        shape(1.10, _c(edge, 255))
    shape(1.0, _c(mix(c, 0.80) if edge is not None else mix(c, 0.80), 255))


def doc(d, x, y, w, h, c=BLUE, lines=2, title=True):
    rrect(d, (x, y, x + w, y + h), 1.0, fill=_c(WHITE, 250), outline=_c(c), w=1.0)
    if title:
        rrect(d, (x + w * 0.42, y + h * 0.68, x + w - 0.9, y + h - 0.9), 0.5, outline=_c(GRAY), w=0.7)
        for i in range(2):
            yy = y + h * 0.68 + 0.9 + i * 1.2
            ln(d, [(x + w * 0.46, yy), (x + w - 1.4, yy)], GRAY, 0.6)
    for i in range(lines):
        yy = y + 2.4 + i * 2.0
        ln(d, [(x + 1.6, yy), (x + w * 0.36, yy)], mix(c, 0.2), 0.7)


def table(d, x, y, w, h, cols=3, rows=3, c=BLUE, head=True):
    rrect(d, (x, y, x + w, y + h), 0.9, fill=_c(WHITE, 250), outline=_c(c), w=1.0)
    cw, ch = w / cols, h / rows
    for i in range(1, cols):
        ln(d, [(x + cw * i, y), (x + cw * i, y + h)], mix(c, 0.35), 0.7)
    for j in range(1, rows):
        ln(d, [(x, y + ch * j), (x + w, y + ch * j)], mix(c, 0.35), 0.7)
    if head:
        d.rectangle([x * SCALE, y * SCALE, (x + w) * SCALE, (y + ch) * SCALE], fill=_c(mix(c, 0.72)))


def cube(d, cx, cy, s=8.5, c=BLUE):
    top = (cx, cy - s * 0.95)
    ur = (cx + s * 0.82, cy - s * 0.48)
    lr = (cx + s * 0.82, cy + s * 0.50)
    bot = (cx, cy + s * 0.95)
    ll = (cx - s * 0.82, cy + s * 0.50)
    ul = (cx - s * 0.82, cy - s * 0.48)
    d.polygon(_p([ul, top, ur, ur, lr, bot, ll, ul]), outline=_c(c), width=max(1, round(0.9 * SCALE)), fill=_c(mix(c, 0.85)))
    ln(d, [top, (cx, cy)], c, 0.85)
    ln(d, [(cx, cy), lr], c, 0.85)
    ln(d, [(cx, cy), ll], c, 0.85)


def magnifier(d, cx, cy, r=4.6, c=SLATE, handle=True):
    d.ellipse([(cx - r) * SCALE, (cy - r) * SCALE, (cx + r) * SCALE, (cy + r) * SCALE],
              outline=_c(c), width=max(1, round(1.0 * SCALE)))
    if handle:
        ln(d, [(cx + r * 0.68, cy + r * 0.68), (cx + r * 1.55, cy + r * 1.55)], c, 1.1)


def person(d, cx, cy, s=6.0, c=SLATE):
    d.ellipse([(cx - s * 0.26) * SCALE, (cy - s * 0.62) * SCALE, (cx + s * 0.26) * SCALE, (cy - s * 0.10) * SCALE],
              outline=_c(c), width=max(1, round(0.9 * SCALE)))
    arc = [(cx - s * 0.46 + s * 0.92 * i / 12.0, cy + s * 0.55 - s * 0.42 * math.sin(math.pi * i / 12.0)) for i in range(13)]
    ln(d, arc, c, 1.0)


def refresh(d, cx, cy, r=4.6, c=RED):
    for a0, a1 in ((200, 340), (20, 160)):
        d.arc([(cx - r) * SCALE, (cy - r) * SCALE, (cx + r) * SCALE, (cy + r) * SCALE],
              start=a0, end=a1, fill=_c(c), width=max(1, round(1.0 * SCALE)))
    a = math.radians(340)
    arrow_head(d, (cx + r * math.cos(a), cy + r * math.sin(a)), 340 + 90, c, 2.2)
    a = math.radians(160)
    arrow_head(d, (cx + r * math.cos(a), cy + r * math.sin(a)), 160 + 90, c, 2.2)


def funnel(d, cx, cy, w=6.0, h=7.0, c=SLATE):
    d.polygon(
        _p([(cx - w, cy - h * 0.5), (cx + w, cy - h * 0.5), (cx + w * 0.22, cy + h * 0.1),
            (cx + w * 0.22, cy + h * 0.55), (cx - w * 0.22, cy + h * 0.5), (cx - w * 0.22, cy + h * 0.1)]),
        outline=_c(c), width=max(1, round(0.9 * SCALE)),
    )


def check(d, x, y, s=2.4, c=TEAL):
    ln(d, [(x, y), (x + s * 0.4, y + s * 0.55)], c, 0.9)
    ln(d, [(x + s * 0.4, y + s * 0.55), (x + s * 1.05, y - s * 0.55)], c, 0.9)


def xmark(d, cx, cy, s=2.6, c=RED):
    ln(d, [(cx - s, cy - s), (cx + s, cy + s)], c, 1.1)
    ln(d, [(cx - s, cy + s), (cx + s, cy - s)], c, 1.1)


def pencil(d, x, y, s=7.0, ang=45, c=SLATE):
    a = math.radians(ang)
    ux, uy = math.cos(a), -math.sin(a)
    nx, ny = -uy * 0.9, ux * 0.9
    tipx, tipy = x, y
    body = [
        (tipx, tipy),
        (tipx + ux * s * 0.22 + nx, tipy + uy * s * 0.22 + ny),
        (tipx + ux * s + nx, tipy + uy * s + ny),
        (tipx + ux * s - nx, tipy + uy * s - ny),
        (tipx + ux * s * 0.22 - nx, tipy + uy * s * 0.22 - ny),
    ]
    d.polygon(_p(body), fill=_c(mix(c, 0.25)), outline=_c(c), width=max(1, round(0.7 * SCALE)))
    d.polygon(_p([(tipx, tipy), (tipx + ux * s * 0.24 + nx * 0.9, tipy + uy * s * 0.24 + ny * 0.9),
                  (tipx + ux * s * 0.24 - nx * 0.9, tipy + uy * s * 0.24 - ny * 0.9)]), fill=_c(DARK))


def card(d, box, c=BLUE, fill=True, r=1.6):
    rrect(d, box, r, fill=_c(mix(c, 0.86) if fill else WHITE, 250), outline=_c(c), w=0.9)


def bez(d, p0, p1, p2, c=TEAL, w=1.0, n=16):
    pts = []
    for i in range(n + 1):
        t = i / float(n)
        x = (1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t ** 2 * p2[0]
        y = (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t ** 2 * p2[1]
        pts.append((x, y))
    ln(d, pts, c, w)
    ang = math.degrees(math.atan2(p2[1] - pts[-2][1], p2[0] - pts[-2][0]))
    arrow_head(d, p2, ang, c, 2.6)


def dial(d, cx, cy, r=4.0, a0=18, a1=162, c=BLUE):
    """可调角度：半圆刻度盘 + 指针"""
    pts = []
    for i in range(17):
        t = math.radians(a0 + (a1 - a0) * i / 16.0)
        pts.append((cx + r * math.cos(t), cy - r * math.sin(t)))
    ln(d, pts, c, 1.1)
    for i in range(3):
        t = math.radians(a0 + (a1 - a0) * (i + 0.5) / 3.0)
        ln(d, [(cx + (r - 0.2) * math.cos(t), cy - (r - 0.2) * math.sin(t)),
               (cx + (r - 1.2) * math.cos(t), cy - (r - 1.2) * math.sin(t))], c, 0.9)
    t = math.radians(62)
    ln(d, [(cx, cy), (cx + (r - 0.4) * math.cos(t), cy - (r - 0.4) * math.sin(t))], c, 1.2)
    ln(d, [(cx - r * 0.9, cy), (cx + r * 0.9, cy)], c, 0.9)
    dot(d, cx, cy, 0.8, c)


def duct(d, x, top, bottom, w=4.2, c=TEAL):
    """风管断面"""
    rrect(d, (x, top, x + w, bottom), 0.6, fill=_c(mix(c, 0.55), 250), outline=_c(c), w=1.0)


def ex_arrow(d, y=16.0, to_right=True, x1=18.4, x2=29.4):
    """导出/导入大箭头：出=向右，入=向左"""
    if to_right:
        arrow(d, (x1, y), (x2, y), TEAL, 1.5, 3.0)
    else:
        arrow(d, (x2, y), (x1, y), TEAL, 1.5, 3.0)


# ---------------- 各功能图标 ----------------
def chip(d, sm):
    if sm:
        rrect(d, (0.5, 0.5, 31.5, 31.5), 3.0, fill=_c(CHIP, 250))
    else:
        rrect(d, (1.1, 1.1, 30.9, 30.9), 4.4, fill=_c(CHIP, 248), outline=_c(CHIP_BD), w=1.0)


# ===== 连接与翻弯 / 连接工具 =====
def i_tee3(d, sm):
    pipe(d, [(4.5, 11.3), (27.5, 11.3)], 4.0, TEAL, sm)
    pipe(d, [(4.5, 21.6), (27.5, 21.6)], 4.0, BLUE, sm)
    pipe(d, [(20.4, 11.3), (20.4, 21.6)], 4.0, BLUE, sm)
    for y in (11.3, 21.6):
        flange(d, 20.4, y, 90, 4.0)


def i_tee_cross(d, sm):
    pipe(d, [(4.5, 10.6), (27.5, 10.6)], 3.7, TEAL, sm)
    pipe(d, [(4.5, 22.3), (27.5, 22.3)], 3.7, BLUE, sm)
    for x in (11.6, 22.6):
        pipe(d, [(x, 10.6), (x, 22.3)], 3.7, BLUE, sm)
        flange(d, x, 10.6, 90, 3.7)
        flange(d, x, 22.3, 90, 3.7)


def i_hl45(d, sm):
    pipe(d, [(4.5, 25.0), (12.6, 25.0)], 4.0, SLATE, sm)
    pipe(d, [(12.6, 25.0), (18.6, 19.0)], 4.0, BLUE, sm)
    pipe(d, [(18.6, 19.0), (27.5, 19.0)], 4.0, BLUE, sm)
    flange(d, 12.6, 25.0, 45, 4.0)
    flange(d, 18.6, 19.0, 45, 4.0)


def i_hl90(d, sm):
    pipe(d, [(4.5, 26.0), (16.0, 26.0)], 4.0, SLATE, sm)
    pipe(d, [(16.0, 26.0), (16.0, 15.0)], 4.0, BLUE, sm)
    pipe(d, [(16.0, 15.0), (27.5, 15.0)], 4.0, BLUE, sm)
    flange(d, 16.0, 26.0, 0, 4.0)
    flange(d, 16.0, 15.0, 0, 4.0)


def i_hl_custom(d, sm):
    pipe(d, [(4.5, 26.0), (11.4, 26.0)], 4.0, SLATE, sm)
    pipe(d, [(11.4, 26.0), (18.6, 15.4)], 4.0, BLUE, sm)
    pipe(d, [(18.6, 15.4), (27.5, 15.4)], 4.0, BLUE, sm)
    flange(d, 18.6, 15.4, 56, 4.0)
    dial(d, 8.4, 10.6, 4.0, 18, 162, BLUE)


def i_collinear(d, sm):
    dash(d, (3.6, 16.5), (28.4, 16.5), mix(SLATE, 0.45), 0.8)
    pipe(d, [(4.5, 16.5), (11.6, 16.5)], 4.2, BLUE, sm)
    pipe(d, [(20.4, 16.5), (27.5, 16.5)], 4.2, BLUE, sm)
    flange(d, 11.6, 16.5, 0, 4.2)
    flange(d, 20.4, 16.5, 0, 4.2)
    col = mix(BLUE, 0.10)
    ln(d, [(14.2, 10.6), (16.0, 12.9)], col, 1.1)
    ln(d, [(17.8, 10.6), (16.0, 12.9)], col, 1.1)
    ln(d, [(14.2, 22.4), (16.0, 20.1)], col, 1.1)
    ln(d, [(17.8, 22.4), (16.0, 20.1)], col, 1.1)


def i_corner(d, sm):
    pipe(d, [(4.5, 25.0), (19.6, 25.0)], 4.0, SLATE, sm)
    pipe(d, [(19.6, 25.0), (19.6, 6.6)], 4.0, BLUE, sm)
    d.ellipse([(19.6 - 3.0) * SCALE, (25.0 - 3.0) * SCALE, (19.6 + 3.0) * SCALE, (25.0 + 3.0) * SCALE],
              fill=_c(mix(TEAL, 0.35)), outline=_c(DARK), width=max(1, round(0.9 * SCALE)))
    dot(d, 19.6, 25.0, 1.0, mix(TEAL, 0.75))


def i_align_prep(d, sm):
    dash(d, (3.6, 13.2), (28.4, 13.2), mix(SLATE, 0.3), 0.8)
    pipe(d, [(4.5, 13.2), (12.6, 13.2)], 4.0, BLUE, sm)
    pipe(d, [(10.4, 22.2), (27.5, 22.2)], 3.6, SLATE, sm)
    arrow(d, (19.8, 20.2), (19.8, 15.4), BLUE, 1.1, 2.6)


# ===== 选择工具 =====
def i_select_conn(d, sm):
    dash_rect(d, (4.4, 5.6, 27.6, 26.4), mix(BLUE, 0.45), 0.8)
    pipe(d, [(9.4, 8.4), (9.4, 23.4)], 3.4, BLUE, sm)
    pipe(d, [(9.4, 15.0), (23.4, 15.0)], 3.4, BLUE, sm)
    pipe(d, [(9.4, 8.4), (18.4, 8.4)], 3.4, BLUE, sm)
    for p in ((9.4, 8.4), (9.4, 15.0), (9.4, 23.4), (18.4, 8.4), (23.4, 15.0)):
        dot(d, p[0], p[1], 1.5, DARK)


def i_select_same(d, sm):
    dash_rect(d, (3.8, 5.6, 21.4, 26.4), mix(BLUE, 0.45), 0.8)
    pipe(d, [(7.4, 9.4), (7.4, 22.6)], 3.4, BLUE, sm)
    pipe(d, [(7.4, 16.0), (17.6, 16.0)], 3.4, BLUE, sm)
    dot(d, 7.4, 16.0, 1.5, DARK)
    dot(d, 17.6, 16.0, 1.5, DARK)
    pipe(d, [(26.4, 8.0), (26.4, 24.0)], 3.4, ORANGE, sm)


def i_probe(d, sm):
    pipe(d, [(7.6, 7.6), (7.6, 22.4)], 3.6, BLUE, sm)
    pipe(d, [(7.6, 14.4), (15.4, 14.4)], 3.6, BLUE, sm)
    for p in ((7.6, 7.6), (7.6, 14.4), (7.6, 22.4), (15.4, 14.4)):
        dot(d, p[0], p[1], 1.8, DARK)
    magnifier(d, 21.4, 20.4, 4.2, SLATE)


# ===== 翻弯工具 =====
def i_saddle_hl(d, sm):
    pipe(d, [(4.5, 24.0), (9.0, 24.0), (13.6, 17.0), (19.4, 17.0), (24.0, 24.0), (27.5, 24.0)], 3.7, BLUE, sm)
    flange(d, 9.0, 24.0, 0, 3.7)
    flange(d, 24.0, 24.0, 0, 3.7)


def i_plan_saddle(d, sm):
    pipe(d, [(4.5, 23.0), (8.2, 23.0), (12.8, 16.4), (19.6, 16.4), (24.2, 23.0), (27.5, 23.0)], 3.7, TEAL, sm)
    flange(d, 8.2, 23.0, 0, 3.7)
    flange(d, 24.2, 23.0, 0, 3.7)


def i_lat45(d, sm):
    pipe(d, [(4.5, 20.6), (13.6, 20.6), (21.0, 13.2), (27.5, 13.2)], 3.7, TEAL, sm)
    flange(d, 13.6, 20.6, 0, 3.7)


def i_lat90(d, sm):
    pipe(d, [(4.5, 21.0), (14.2, 21.0), (14.2, 13.0), (27.5, 13.0)], 3.7, TEAL, sm)
    flange(d, 14.2, 21.0, 0, 3.7)


def i_lat_free(d, sm):
    pipe(d, [(4.5, 21.0), (13.0, 21.0), (21.4, 11.6), (27.5, 11.6)], 3.7, TEAL, sm)
    dial(d, 9.4, 9.0, 3.8, 18, 162, TEAL)


def i_saddle_plan(d, sm):
    pipe(d, [(4.5, 22.6), (8.4, 22.6), (12.6, 16.6), (19.8, 16.6), (24.0, 22.6), (27.5, 22.6)], 3.7, TEAL, sm)
    flange(d, 8.4, 22.6, 0, 3.7)
    flange(d, 24.0, 22.6, 0, 3.7)


def i_up45(d, sm):
    pipe(d, [(4.5, 25.0), (11.4, 25.0), (18.8, 17.6), (27.5, 17.6)], 3.8, BLUE, sm)
    flange(d, 11.4, 25.0, 0, 3.8)


def i_up90(d, sm):
    pipe(d, [(4.5, 25.0), (16.4, 25.0), (16.4, 12.8), (27.5, 12.8)], 3.8, BLUE, sm)
    flange(d, 16.4, 25.0, 0, 3.8)


def i_up_free(d, sm):
    pipe(d, [(4.5, 25.0), (11.2, 25.0), (19.6, 11.8), (27.5, 11.8)], 3.8, BLUE, sm)
    dial(d, 8.6, 10.4, 3.8, 18, 162, BLUE)


# ===== 对齐与排序 =====
def i_bottom_align(d, sm):
    dash(d, (3.4, 24.9), (28.6, 24.9), SLATE, 0.9, 1.4, 1.1)
    for x, y, c in ((4.4, 10.6, TEAL), (10.4, 15.4, BLUE), (16.4, 7.4, BLUE), (22.4, 17.2, TEAL)):
        duct(d, x, y, 24.6, 4.2, c)


def i_group_align(d, sm):
    for x in (5.6, 12.2, 18.8, 25.4):
        duct(d, x, 11.4, 20.6, 3.8, BLUE)
    dim_h(d, 24.4, 9.4, 12.2, SLATE)
    dim_h(d, 24.4, 16.0, 18.8, SLATE)
    dash(d, (4.8, 10.2), (28.6, 10.2), mix(SLATE, 0.40), 0.7)


def i_anchor_align(d, sm):
    for x in (4.4, 10.6, 16.8, 23.0):
        duct(d, x, 12.8, 22.0, 4.0, BLUE if x == 10.6 else SLATE)
    dash(d, (10.6, 10.8), (10.6, 24.0), mix(BLUE, 0.25), 0.9)
    # 基准钉
    d.ellipse([10.6 * SCALE, 4.4 * SCALE, 14.6 * SCALE, 8.4 * SCALE], fill=_c(BLUE))
    d.polygon(_p([(12.6, 12.4), (11.0, 8.2), (14.2, 8.2)]), fill=_c(BLUE))
    dot(d, 12.6, 6.4, 1.0, WHITE)


def i_group_tag(d, sm):
    for x, y in ((4.6, 11.0), (4.6, 19.4), (10.2, 11.0), (10.2, 19.4)):
        duct(d, x, y, y + 5.4, 3.8, TEAL)
    tag(d, (18.4, 10.6, 27.6, 15.4))
    tag(d, (18.4, 18.8, 27.6, 23.6))
    ln(d, [(14.0, 13.7), (18.4, 13.0)], SLATE, 0.8)
    ln(d, [(14.0, 22.1), (18.4, 21.2)], SLATE, 0.8)


def i_tag_layout(d, sm):
    for y in (7.4, 13.6, 19.8):
        tag(d, (7.6, y, 22.4, y + 4.4))
    dim_v(d, 25.4, 11.8, 13.6, SLATE)
    dim_v(d, 25.4, 18.0, 19.8, SLATE)
    gear(d, 25.4, 25.6, 4.0, BLUE)


def i_tag_left_stack(d, sm):
    dash(d, (6.0, 5.0), (6.0, 27.0), mix(BLUE, 0.30), 0.9)
    tag(d, (6.0, 6.2, 18.6, 10.4))
    tag(d, (6.0, 13.6, 22.4, 17.8))
    tag(d, (6.0, 21.0, 16.2, 25.2))
    dim_v(d, 26.4, 6.2, 13.6, SLATE)
    dim_v(d, 26.4, 13.6, 21.0, SLATE)


def i_tag_left_only(d, sm):
    dash(d, (6.0, 5.0), (6.0, 27.0), mix(BLUE, 0.30), 0.9)
    tag(d, (6.0, 6.6, 21.0, 11.4))
    tag(d, (6.0, 14.6, 26.0, 19.4))
    tag(d, (6.0, 21.8, 17.4, 26.6))


def i_tag_stack_only(d, sm):
    tag(d, (8.4, 6.2, 19.6, 10.4))
    tag(d, (4.6, 13.6, 15.8, 17.8))
    tag(d, (7.2, 21.0, 18.4, 25.2))
    dim_v(d, 27.0, 6.2, 13.6, SLATE)
    dim_v(d, 27.0, 13.6, 21.0, SLATE)


def i_tag_align_settings(d, sm):
    tag(d, (5.4, 7.0, 19.0, 11.4))
    dash(d, (7.8, 5.4), (7.8, 18.4), mix(BLUE, 0.3), 0.8)
    dash(d, (4.6, 18.8), (21.4, 18.8), SLATE, 0.7)
    for x in (6.6, 10.6, 14.6, 18.6):
        ln(d, [(x, 17.6), (x, 20.2)], SLATE, 0.8)
    gear(d, 24.8, 24.4, 4.2, BLUE)


def i_tag60(d, sm):
    tag(d, (4.0, 5.4, 14.6, 10.2))
    kx, ky = 17.6, 7.8
    ln(d, [(14.6, 7.8), (kx, ky)], SLATE, 1.1)
    dash(d, (kx, ky), (28.2, ky), mix(SLATE, 0.30), 0.8)
    ln(d, [(kx, ky), (26.2, 24.4)], SLATE, 1.2)
    dot(d, 26.2, 24.4, 1.6, SLATE)
    pts = []
    for i in range(15):
        t = math.radians(-62 * i / 14.0)
        pts.append((kx + 4.6 * math.cos(t), ky + 4.6 * math.sin(-t)))
    ln(d, pts, TEAL, 1.0)
    dot(d, kx, ky, 0.9, TEAL)


def i_box_leader(d, sm):
    pipe(d, [(4.2, 18.6), (23.6, 18.6)], 3.6, BLUE, sm)
    rrect(d, (8.6, 13.4, 19.4, 23.8), 0.6, outline=_c(SLATE), w=0.9)
    ln(d, [(19.4, 13.4), (25.4, 8.4)], SLATE, 0.9)
    dot(d, 25.4, 8.4, 1.4, SLATE)


# ===== 墙洞工具 =====
def i_wall_hole(d, sm):
    pipe(d, [(2.6, 16.2), (29.4, 16.2)], 3.6, BLUE, sm)
    card(d, (10.6, 4.0, 15.6, 11.4), SLATE)
    card(d, (10.6, 21.0, 15.6, 28.0), SLATE)
    dash_rect(d, (10.0, 11.2, 16.2, 21.2), mix(SLATE, 0.2), 0.7)


def i_wall_hole_settings(d, sm):
    pipe(d, [(2.6, 15.0), (29.4, 15.0)], 3.4, BLUE, sm)
    card(d, (9.4, 3.6, 14.0, 10.6), SLATE)
    card(d, (9.4, 19.4, 14.0, 25.6), SLATE)
    dash_rect(d, (8.8, 10.4, 14.6, 19.6), mix(SLATE, 0.2), 0.7)
    gear(d, 23.4, 23.4, 4.6, BLUE)


# ===== 工作集管理 =====
def i_hide_ws(d, sm):
    for i, y in enumerate((7.4, 13.4, 19.4, 25.4)):
        active = i == 1
        card(d, (3.8, y, 21.4, y + 4.2), BLUE if active else SLATE, fill=not active)
        if active:
            eye(d, 25.8, y + 2.1, 2.5, 1.7, BLUE, open_=True, slash=True)
        else:
            eye(d, 25.8, y + 2.1, 2.5, 1.7, GRAY, open_=True)


def i_isolate_ws(d, sm):
    for i, y in enumerate((7.4, 13.4, 19.4, 25.4)):
        active = i == 1
        card(d, (3.8, y, 21.4, y + 4.2), BLUE if active else SLATE, fill=not active)
        if active:
            eye(d, 25.8, y + 2.1, 2.5, 1.7, BLUE, open_=True)
        else:
            eye(d, 25.8, y + 2.1, 2.5, 1.7, GRAY, open_=False)


def i_show_all_ws(d, sm):
    for y in (7.4, 13.4, 19.4, 25.4):
        card(d, (3.8, y, 20.4, y + 4.2), BLUE)
        eye(d, 25.0, y + 2.1, 2.5, 1.7, BLUE, open_=True)


def i_show_kw_ws(d, sm):
    for i, y in enumerate((7.4, 13.4, 19.4, 25.4)):
        on = i in (0, 2)
        card(d, (3.8, y, 17.0, y + 4.2), BLUE if on else SLATE, fill=not on)
        rrect(d, (19.6, y + 0.6, 23.4, y + 3.6), 0.6, fill=_c(WHITE, 250), outline=_c(SLATE), w=0.8)
        if on:
            check(d, 20.1, y + 2.1, 2.2, TEAL)
        eye(d, 26.6, y + 2.1, 2.1, 1.4, BLUE if on else GRAY, open_=on)


def i_change_ws(d, sm):
    card(d, (4.4, 5.4, 12.4, 13.4), BLUE)
    arrow(d, (9.6, 15.0), (9.6, 20.6), BLUE, 1.3, 2.8)
    for i, y in enumerate((19.6, 24.2)):
        card(d, (6.6, y, 26.4, y + 3.6), BLUE if i == 0 else SLATE, fill=(i == 0))


def i_default_ws(d, sm):
    for i, y in enumerate((17.4, 22.2, 27.0)):
        card(d, (3.8, y, 20.4, y + 3.6), BLUE if i == 0 else SLATE, fill=(i == 0))
    # 星标
    cx, cy, r = 25.4, 8.6, 4.4
    pts = []
    for k in range(10):
        a = math.radians(-90 + k * 36)
        rr = r if k % 2 == 0 else r * 0.45
        pts.append((cx + rr * math.cos(a), cy + rr * math.sin(a)))
    d.polygon(_p(pts), fill=_c(BLUE))
    arrow(d, (20.0, 15.4), (22.2, 12.6), BLUE, 0.9, 2.0)


# ===== 协同工具 =====
def i_sync_relinq(d, sm):
    cloud(d, 13.0, 10.0, 19.0, 9.2, BLUE, edge=BLUE)
    arrow(d, (13.0, 14.8), (13.0, 5.8), BLUE_D, 1.4, 3.0)
    lock(d, 25.6, 24.4, 8.0, ORANGE, open_=True)


def i_relinq_selected(d, sm):
    dash_rect(d, (5.0, 9.4, 17.6, 22.0), mix(BLUE, 0.4), 0.8)
    card(d, (8.2, 12.6, 14.4, 18.8), BLUE)
    lock(d, 24.4, 16.0, 7.0, ORANGE, open_=True)


def i_relinq_except(d, sm):
    card(d, (4.4, 8.6, 12.4, 16.6), BLUE)
    lock(d, 11.0, 21.4, 6.2, SLATE, open_=False)
    card(d, (19.0, 8.6, 26.4, 16.6), SLATE, fill=False)
    lock(d, 24.0, 21.4, 6.2, ORANGE, open_=True)


def i_relinq_all(d, sm):
    lock(d, 10.2, 14.6, 9.6, ORANGE, open_=True)
    lock(d, 21.6, 19.8, 8.4, ORANGE, open_=True)
    arrow(d, (17.0, 8.4), (21.6, 5.2), BLUE, 1.1, 2.4)


# ===== 实用工具 / 修改工具 =====
def i_batch_break(d, sm):
    pipe(d, [(2.8, 16.4), (10.6, 16.4)], 4.0, BLUE, sm)
    pipe(d, [(13.4, 16.4), (19.4, 16.4)], 4.0, BLUE, sm)
    pipe(d, [(22.2, 16.4), (29.2, 16.4)], 4.0, BLUE, sm)
    for x in (12.0, 20.8):
        dash(d, (x, 11.4), (x, 21.4), RED, 0.9, 1.2, 0.9)


def i_remove_dup(d, sm):
    pipe(d, [(4.4, 12.6), (22.4, 12.6)], 3.2, SLATE, sm)
    pipe(d, [(4.4, 19.4), (22.4, 19.4)], 3.2, BLUE, sm)
    xmark(d, 25.8, 16.0, 3.6, RED)


def i_section_rename(d, sm):
    d.ellipse([4.2 * SCALE, 19.4 * SCALE, 13.4 * SCALE, 28.6 * SCALE],
              fill=_c(WHITE, 250), outline=_c(SLATE), width=max(1, round(1.0 * SCALE)))
    ln(d, [(8.8, 19.4), (8.8, 9.0)], SLATE, 1.1)
    ln(d, [(6.4, 6.6), (11.2, 6.6)], SLATE, 1.6)
    pencil(d, 26.6, 27.0, 9.6, 45, BLUE)


def i_system_check(d, sm):
    pipe(d, [(7.4, 8.6), (7.4, 21.4)], 3.2, BLUE, sm)
    pipe(d, [(7.4, 14.6), (15.0, 14.6)], 3.2, BLUE, sm)
    for p in ((7.4, 8.6), (7.4, 14.6), (7.4, 21.4), (15.0, 14.6)):
        dot(d, p[0], p[1], 1.5, DARK)
    d.ellipse([15.4 * SCALE, 15.4 * SCALE, 25.6 * SCALE, 25.6 * SCALE],
              fill=_c(WHITE, 245), outline=_c(SLATE), width=max(1, round(1.0 * SCALE)))
    check(d, 18.0, 20.9, 3.2, TEAL)


def i_last_changed(d, sm):
    person(d, 10.4, 17.4, 8.0, SLATE)
    d.ellipse([17.0 * SCALE, 16.6 * SCALE, 28.4 * SCALE, 28.0 * SCALE], fill=_c(WHITE, 245), outline=_c(SLATE), width=max(1, round(1.0 * SCALE)))
    ln(d, [(22.7, 22.3), (22.7, 19.4)], DARK, 0.9)
    ln(d, [(22.7, 22.3), (25.4, 23.6)], DARK, 0.9)


def i_duct_resize(d, sm):
    d.rectangle([9.4 * SCALE, 10.6 * SCALE, 20.4 * SCALE, 21.6 * SCALE], fill=_c(mix(TEAL, 0.82)), outline=_c(TEAL), width=max(1, round(0.9 * SCALE)))
    dim_h(d, 7.4, 9.4, 20.4, SLATE)
    dim_v(d, 6.2, 10.6, 21.6, SLATE)
    refresh(d, 24.4, 24.4, 3.6, BLUE)


def i_remake(d, sm):
    card(d, (9.6, 10.6, 20.4, 21.4), BLUE)
    refresh(d, 15.0, 16.0, 9.4, BLUE)


def i_change_level(d, sm):
    card(d, (11.0, 9.4, 20.6, 19.0), BLUE)
    dash(d, (3.6, 21.8), (28.4, 21.8), SLATE, 0.8)
    d.polygon(_p([(24.4, 22.6), (27.4, 22.6), (25.9, 25.4)]), fill=_c(SLATE))
    dim_v(d, 6.4, 9.4, 21.8, BLUE)


def i_copy_view_template(d, sm):
    card(d, (3.6, 6.2, 15.4, 18.4), SLATE, fill=False)
    card(d, (8.4, 11.0, 20.2, 23.2), BLUE)
    ln(d, [(4.8, 9.2), (13.8, 9.2)], mix(SLATE, 0.2), 0.7)
    ln(d, [(9.6, 14.0), (18.6, 14.0)], mix(BLUE, 0.2), 0.7)
    arrow(d, (19.6, 16.8), (29.4, 16.8), TEAL, 1.4, 2.8)


def i_copy_filter(d, sm):
    funnel(d, 9.4, 11.6, 6.4, 7.4, SLATE)
    funnel(d, 10.4, 21.4, 5.4, 6.2, BLUE)
    arrow(d, (18.6, 16.0), (29.4, 16.0), TEAL, 1.4, 2.8)


def i_rotate_axis(d, sm):
    dash(d, (3.4, 20.6), (28.6, 20.6), mix(SLATE, 0.30), 0.9)
    pipe(d, [(4.6, 10.6), (19.0, 10.6)], 3.6, BLUE, sm)
    dot(d, 19.0, 20.6, 1.4, SLATE)
    bez(d, (19.6, 11.6), (25.6, 13.6), (26.4, 20.0), TEAL, 1.2)


def i_rotate_view(d, sm):
    rrect(d, (6.0, 6.0, 26.0, 26.0), 1.0, outline=_c(SLATE), w=1.0)
    d.arc([(9.4) * SCALE, 9.4 * SCALE, 22.6 * SCALE, 22.6 * SCALE], start=200, end=340,
          fill=_c(BLUE), width=max(1, round(1.0 * SCALE)))
    arrow_head(d, (21.1, 11.3), -20, BLUE, 2.6)


# ===== 导出工具 =====
def i_export_all(d, sm):
    # 打包盒
    rrect(d, (8.4, 17.4, 23.6, 27.6), 1.0, fill=_c(mix(TEAL, 0.86)), outline=_c(TEAL), w=1.0)
    ln(d, [(8.4, 21.2), (23.6, 21.2)], TEAL, 0.9)
    ln(d, [(16.0, 21.2), (16.0, 27.6)], TEAL, 0.9)
    arrow(d, (16.0, 6.4), (16.0, 16.2), BLUE, 1.2, 2.8)


def i_export_models(d, sm):
    cube(d, 10.4, 16.4, 8.2, BLUE)
    ex_arrow(d, 16.4, True, 19.4, 29.4)


def i_export_cad(d, sm):
    doc(d, 3.6, 8.0, 14.4, 16.6, SLATE, lines=0, title=False)
    ln(d, [(6.2, 11.4), (11.4, 11.4), (11.4, 16.8), (15.4, 16.8)], BLUE, 0.9)
    ln(d, [(6.2, 19.6), (10.4, 15.4)], BLUE, 0.9)
    ex_arrow(d, 16.0)


def i_export_images(d, sm):
    rrect(d, (3.6, 8.6, 17.4, 23.4), 1.0, fill=_c(WHITE, 250), outline=_c(SLATE), w=0.9)
    d.ellipse([6.0 * SCALE, 11.4 * SCALE, 9.4 * SCALE, 14.8 * SCALE], fill=_c(mix(ORANGE, 0.3)))
    d.polygon(_p([(5.4, 21.4), (10.4, 15.4), (14.4, 21.4)]), fill=_c(mix(TEAL, 0.55)))
    d.polygon(_p([(10.0, 21.4), (13.6, 17.0), (16.6, 21.4)]), fill=_c(mix(TEAL, 0.2)))
    ex_arrow(d, 16.0)


def i_export_schedule(d, sm):
    table(d, 3.6, 8.4, 13.6, 15.6, 3, 3, BLUE)
    ex_arrow(d, 16.2)


def i_export_sheet_info(d, sm):
    doc(d, 3.6, 7.6, 14.4, 17.4, BLUE, lines=2, title=True)
    ex_arrow(d, 16.0)


def i_import_sheet_info(d, sm):
    doc(d, 3.6, 7.6, 14.4, 17.4, BLUE, lines=2, title=True)
    ex_arrow(d, 16.0, False)


def i_sync_sheet_info(d, sm):
    doc(d, 3.6, 7.6, 13.4, 20.4, BLUE, lines=2, title=True)
    arrow(d, (20.4, 11.4), (29.4, 11.4), TEAL, 1.5, 2.8)
    arrow(d, (29.4, 21.0), (20.4, 21.0), TEAL, 1.5, 2.8)


def i_rollback_sheet_info(d, sm):
    doc(d, 3.6, 8.4, 13.6, 19.4, SLATE, lines=2, title=True)
    d.arc([13.4 * SCALE, 4.4 * SCALE, 26.4 * SCALE, 15.4 * SCALE], start=330, end=110, fill=_c(RED), width=max(1, round(1.0 * SCALE)))
    arrow_head(d, (20.1, 5.6), 190, RED, 2.4)


def i_export_view_info(d, sm):
    rrect(d, (3.6, 7.4, 17.4, 22.4), 0.8, fill=_c(WHITE, 250), outline=_c(BLUE), w=0.9)
    table(d, 5.6, 10.4, 9.8, 9.4, 2, 3, BLUE, head=False)
    ex_arrow(d, 15.4, True, 19.4, 29.4)


def i_import_view_info(d, sm):
    rrect(d, (3.6, 7.4, 17.4, 22.4), 0.8, fill=_c(WHITE, 250), outline=_c(BLUE), w=0.9)
    table(d, 5.6, 10.4, 9.8, 9.4, 2, 3, BLUE, head=False)
    ex_arrow(d, 15.4, False, 19.4, 29.4)


def i_param_ledger(d, sm):
    rrect(d, (3.4, 7.4, 15.6, 25.4), 0.8, fill=_c(WHITE, 250), outline=_c(BLUE), w=0.9)
    rrect(d, (16.0, 7.4, 28.4, 25.4), 0.8, fill=_c(WHITE, 250), outline=_c(BLUE), w=0.9)
    ln(d, [(15.8, 8.0), (15.8, 25.0)], BLUE, 1.0)
    for i in range(5):
        yy = 10.4 + i * 3.0
        ln(d, [(5.2, yy), (14.0, yy)], mix(BLUE, 0.15), 0.7)
        ln(d, [(17.6, yy), (26.6, yy)], mix(BLUE, 0.15), 0.7)


def i_param_def(d, sm):
    rrect(d, (6.4, 6.2, 25.6, 27.4), 1.0, fill=_c(WHITE, 250), outline=_c(BLUE), w=1.0)
    rrect(d, (11.4, 3.6, 20.6, 8.4), 1.0, fill=_c(mix(BLUE, 0.7)), outline=_c(BLUE), w=0.9)
    for i in range(3):
        yy = 12.4 + i * 4.4
        ln(d, [(9.4, yy), (18.0, yy)], mix(SLATE, 0.1), 0.8)
        check(d, 19.6, yy + 1.2, 2.2, TEAL)


# ---------------- 图标注册表 ----------------
ICONS = {
    "高差三通连接": i_tee3,
    "高差相交双三通连接": i_tee_cross,
    "高低连接 45°": i_hl45,
    "高低连接 90°": i_hl90,
    "高低连接 自定义": i_hl_custom,
    "共线连接": i_collinear,
    "转角连接": i_corner,
    "共线对齐准备": i_align_prep,
    "选择连接组": i_select_conn,
    "选择同系统连接组": i_select_same,
    "连接拓扑诊断": i_probe,
    "高低翻弯": i_saddle_hl,
    "平面拐弯 45°": i_lat45,
    "平面拐弯 90°": i_lat90,
    "平面拐弯 0~90°": i_lat_free,
    "平面翻弯": i_saddle_plan,
    "高低升降 45°": i_up45,
    "高低升降 90°": i_up90,
    "高低升降 0~90°": i_up_free,
    "底部平齐": i_bottom_align,
    "分组排序对齐": i_group_align,
    "指定基准对齐": i_anchor_align,
    "高度分组 TAG": i_group_tag,
    "TAG排版设置": i_tag_layout,
    "标记左对齐并堆叠": i_tag_left_stack,
    "标记仅左对齐": i_tag_left_only,
    "标记仅堆叠": i_tag_stack_only,
    "标记对齐设置": i_tag_align_settings,
    "MEPTag60°": i_tag60,
    "方形引线": i_box_leader,
    "链接墙洞": i_wall_hole,
    "墙洞设置": i_wall_hole_settings,
    "仅隐藏当前工作集": i_hide_ws,
    "仅显示当前工作集": i_isolate_ws,
    "显示所有工作集": i_show_all_ws,
    "显示指定工作集": i_show_kw_ws,
    "改元素到活动工作集": i_change_ws,
    "提取并设为默认工作集": i_default_ws,
    "同步并放权": i_sync_relinq,
    "仅放选中": i_relinq_selected,
    "仅留选中": i_relinq_except,
    "放弃全部": i_relinq_all,
    "批量打断管线": i_batch_break,
    "删除重复图元": i_remove_dup,
    "剖立面视图重命名": i_section_rename,
    "系统检查": i_system_check,
    "修改者查询": i_last_changed,
    "DUCT更新尺寸": i_duct_resize,
    "原地重制": i_remake,
    "修改参照标高": i_change_level,
    "复制视图样板": i_copy_view_template,
    "复制视图过滤器": i_copy_filter,
    "90°旋转(参考轴)": i_rotate_axis,
    "90°旋转(视图)": i_rotate_view,
    "📦 一键打包全部": i_export_all,
    "导出 3D 模型": i_export_models,
    "导出 CAD 底图": i_export_cad,
    "导出 外部图片": i_export_images,
    "导出明细表": i_export_schedule,
    "导出图纸信息": i_export_sheet_info,
    "导入图纸信息": i_import_sheet_info,
    "图纸信息同步": i_sync_sheet_info,
    "撤销图纸导入": i_rollback_sheet_info,
    "导出视图信息": i_export_view_info,
    "导入视图信息": i_import_view_info,
    "图元参数台账": i_param_ledger,
    "参数定义清单": i_param_def,
}


def read_config_texts():
    src = open(CFG, encoding="utf-8-sig").read()
    return re.findall(r'Text\s*=\s*"([^"]+)"', src)


def render(fn, sm):
    global SMALL
    SMALL = sm
    glyph = Image.new("RGBA", (SIZE * SCALE, SIZE * SCALE), (0, 0, 0, 0))
    d = ImageDraw.Draw(glyph, "RGBA")
    fn(d, sm)
    base = Image.new("RGBA", (SIZE * SCALE, SIZE * SCALE), (0, 0, 0, 0))
    db = ImageDraw.Draw(base, "RGBA")
    chip(db, sm)
    if sm:
        k = 1.14                     # 16px 时把图形放大一点，避免细节糊掉
        g2 = glyph.resize((int(SIZE * SCALE * k), int(SIZE * SCALE * k)), Image.LANCZOS)
        base.alpha_composite(g2, ((SIZE * SCALE - g2.width) // 2, (SIZE * SCALE - g2.height) // 2))
    else:
        base.alpha_composite(glyph)
    size = 16 if sm else 32
    return base.resize((size, size), Image.LANCZOS)


def build(preview=False):
    texts = read_config_texts()
    missing = [t for t in texts if t not in ICONS]
    if missing:
        print("[ERROR] 以下功能没有对应图标绘制函数：")
        for t in missing:
            print("   ", t)
        return 1
    extra = [k for k in ICONS if k not in texts]
    if extra:
        print("[WARN] 图标表里有配置中不存在的项：", extra)

    for t in texts:
        fn = ICONS[t]
        render(fn, False).save(os.path.join(OUT_DIR, t + "_32.png"))
        render(fn, True).save(os.path.join(OUT_DIR, t + "_16.png"))
    print("已生成 %d 个功能 / %d 张 PNG -> %s" % (len(texts), len(texts) * 2, OUT_DIR))

    if preview:
        make_preview(texts)
    return 0


def make_preview(texts):
    f = ImageFont.truetype(FONT_CN, 11)
    cols = 8
    cw, ch = 132, 124
    rows = (len(texts) + cols - 1) // cols
    for name, bg in (("preview_light", (242, 243, 245, 255)), ("preview_dark", (52, 54, 58, 255))):
        sheet = Image.new("RGBA", (cols * cw, rows * ch), bg)
        d = ImageDraw.Draw(sheet, "RGBA")
        for idx, t in enumerate(texts):
            r, c = divmod(idx, cols)
            x, y = c * cw + 8, r * ch + 6
            i32 = render(ICONS[t], False).resize((96, 96), Image.NEAREST)
            sheet.alpha_composite(i32, (x, y))
            i16 = render(ICONS[t], True).resize((32, 32), Image.NEAREST)
            sheet.alpha_composite(i16, (x + 100, y + 6))
            d.text((x + 2, y + 100), t, font=f, fill=(40, 44, 50, 255) if bg[0] > 128 else (235, 238, 242, 255))
        sheet.save(os.path.join(HERE, name + ".png"))
    print("预览图已输出：_gen/preview_light.png / preview_dark.png")


if __name__ == "__main__":
    sys.exit(build("--preview" in sys.argv))
