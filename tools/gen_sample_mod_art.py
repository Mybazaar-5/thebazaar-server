#!/usr/bin/env python3
"""
原创示例 Mod 扩展包卡图生成器（tools/gen_sample_mod_art.py）。

为 `mods/bazaar-chronicles-sample/assets/` 生成 6 张大巴扎鎏金艺术风格 RGBA PNG 卡图：
  1. starport_compass_blade.png      (512x1024) 星港罗盘短刃（Small 武器）
  2. aether_bulwark_lantern.png      (512x512)  以太壁垒提灯（Medium 护盾/工具）
  3. solar_kiln_furnace.png          (512x512)  日耀熔炉（Medium 灼烧核心）
  4. starport_dreadnought_vault.png  (512x1024) 星港无畏宝库舰（Large 旗舰）
  5. starport_trade_charter.png      (512x512)  星港贸易特许状（Skill 圆形徽章）
  6. ember_tide_resonance.png        (512x512)  余烬潮汐共鸣（Skill 圆形徽章）

特性：
- 优先使用 Pillow 以 2x 超采样抗锯齿渲染精细径向渐变、大巴扎鎏金双边框、星芒角花与中心神器图形；
- 若目标环境未安装 Pillow，自动回退至纯 Python 标准库（zlib + struct）PNG 编码器，确保零外部依赖必成功；
- 生成完成后自动校验每张 PNG 的魔数头、IHDR 尺寸与 SHA-256，并输出 `assets/art_manifest.json` 供 Mod 配置引用。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import pathlib
import struct
import sys
import zlib
from typing import Any

REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
DEFAULT_ASSETS_DIR = REPO_ROOT / "mods" / "bazaar-chronicles-sample" / "assets"

ART_SPECS: list[dict[str, Any]] = [
    {
        "id": "bazaar-chronicles-sample:starport_compass_blade",
        "filename": "starport_compass_blade.png",
        "width": 512,
        "height": 1024,
        "theme": "compass_blade",
        "bg_inner": (38, 66, 128),
        "bg_outer": (10, 18, 38),
        "accent": (96, 210, 255),
        "gold": (232, 190, 88),
        "titleZh": "星港罗盘短刃",
    },
    {
        "id": "bazaar-chronicles-sample:aether_bulwark_lantern",
        "filename": "aether_bulwark_lantern.png",
        "width": 512,
        "height": 512,
        "theme": "bulwark_lantern",
        "bg_inner": (26, 112, 108),
        "bg_outer": (8, 28, 36),
        "accent": (94, 245, 214),
        "gold": (230, 192, 92),
        "titleZh": "以太壁垒提灯",
    },
    {
        "id": "bazaar-chronicles-sample:solar_kiln_furnace",
        "filename": "solar_kiln_furnace.png",
        "width": 512,
        "height": 512,
        "theme": "solar_kiln",
        "bg_inner": (165, 58, 20),
        "bg_outer": (34, 10, 8),
        "accent": (255, 168, 48),
        "gold": (245, 204, 102),
        "titleZh": "日耀熔炉",
    },
    {
        "id": "bazaar-chronicles-sample:starport_dreadnought_vault",
        "filename": "starport_dreadnought_vault.png",
        "width": 512,
        "height": 1024,
        "theme": "dreadnought_vault",
        "bg_inner": (104, 48, 128),
        "bg_outer": (18, 12, 34),
        "accent": (255, 198, 84),
        "gold": (242, 200, 96),
        "titleZh": "星港无畏宝库舰",
    },
    {
        "id": "bazaar-chronicles-sample:starport_trade_charter",
        "filename": "starport_trade_charter.png",
        "width": 512,
        "height": 512,
        "theme": "trade_charter",
        "bg_inner": (122, 86, 26),
        "bg_outer": (26, 18, 10),
        "accent": (255, 224, 122),
        "gold": (244, 204, 100),
        "titleZh": "星港贸易特许状",
    },
    {
        "id": "bazaar-chronicles-sample:ember_tide_resonance",
        "filename": "ember_tide_resonance.png",
        "width": 512,
        "height": 512,
        "theme": "ember_tide",
        "bg_inner": (68, 48, 108),
        "bg_outer": (14, 12, 28),
        "accent": (255, 122, 58),
        "gold": (235, 194, 94),
        "titleZh": "余烬潮汐共鸣",
    },
]


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * max(0.0, min(1.0, t))


def star_points(cx: float, cy: float, r_outer: float, r_inner: float, spikes: int, angle_offset: float = -math.pi / 2) -> list[tuple[float, float]]:
    pts: list[tuple[float, float]] = []
    for i in range(spikes * 2):
        r = r_outer if i % 2 == 0 else r_inner
        ang = angle_offset + i * math.pi / spikes
        pts.append((cx + r * math.cos(ang), cy + r * math.sin(ang)))
    return pts


def hex_points(cx: float, cy: float, radius: float, angle_offset: float = -math.pi / 2) -> list[tuple[float, float]]:
    return [
        (cx + radius * math.cos(angle_offset + i * math.pi / 3), cy + radius * math.sin(angle_offset + i * math.pi / 3))
        for i in range(6)
    ]


def render_with_pillow(spec: dict[str, Any], out_path: pathlib.Path) -> None:
    from PIL import Image, ImageDraw

    w, h = spec["width"], spec["height"]
    scale = 2
    sw, sh = w * scale, h * scale

    bg_in = spec["bg_inner"]
    bg_out = spec["bg_outer"]
    accent = spec["accent"]
    gold = spec["gold"]
    gold_dark = (148, 108, 36, 255)
    gold_bright = (255, 236, 168, 255)
    gold_mid = (*gold, 255)
    accent_rgba = (*accent, 255)

    # 先构建平滑径向背景渐变（在目标尺寸生成再放大，速度极快且平滑）
    bg = Image.new("RGBA", (w, h))
    px = bg.load()
    cx0, cy0 = w * 0.5, h * 0.46
    max_dist = math.hypot(w * 0.6, h * 0.58)
    for y in range(h):
        dy = y - cy0
        for x in range(w):
            d = math.hypot(x - cx0, dy) / max_dist
            t = min(1.0, d ** 0.85)
            # 叠加微妙星云斜向光带
            band = 0.08 * math.sin((x + y) * 0.025)
            t2 = max(0.0, min(1.0, t + band))
            r = int(lerp(bg_in[0], bg_out[0], t2))
            g = int(lerp(bg_in[1], bg_out[1], t2))
            b = int(lerp(bg_in[2], bg_out[2], t2))
            px[x, y] = (r, g, b, 255)

    canvas = bg.resize((sw, sh), Image.Resampling.BILINEAR)
    cx, cy = sw / 2.0, sh / 2.0
    unit = min(sw, sh)

    # 使用独立透明叠层 + Image.alpha_composite 合成柔和暗金光晕与放射暗纹，保持不透明深邃底色
    glow_layer = Image.new("RGBA", (sw, sh), (0, 0, 0, 0))
    gdraw = ImageDraw.Draw(glow_layer, "RGBA")
    for ring_i in range(6, 0, -1):
        rad = unit * (0.12 + ring_i * 0.068)
        alpha = int(14 + (6 - ring_i) * 9)
        gdraw.ellipse(
            (cx - rad, cy - rad, cx + rad, cy + rad),
            fill=(accent[0], accent[1], accent[2], alpha),
        )
    for i in range(36):
        ang = i * (2 * math.pi / 36)
        r1 = unit * 0.14
        r2 = max(sw, sh) * 0.62
        gdraw.line(
            (cx + r1 * math.cos(ang), cy + r1 * math.sin(ang), cx + r2 * math.cos(ang), cy + r2 * math.sin(ang)),
            fill=(gold[0], gold[1], gold[2], 32 if i % 3 == 0 else 14),
            width=4 if i % 3 == 0 else 2,
        )
    canvas = Image.alpha_composite(canvas, glow_layer)

    fg_layer = Image.new("RGBA", (sw, sh), (0, 0, 0, 0))
    draw = ImageDraw.Draw(fg_layer, "RGBA")

    # 星点装饰（确定性伪随机，保证构建可复现）
    seed = int(hashlib.md5(spec["id"].encode("utf-8")).hexdigest()[:8], 16)
    for k in range(42):
        sx = ((seed * (k + 7) * 2654435761) & 0xFFFF) / 65535.0 * (sw - 120) + 60
        sy = ((seed * (k + 19) * 2246822519) & 0xFFFF) / 65535.0 * (sh - 120) + 60
        sr = 3 + (k % 4) * 2
        draw.ellipse((sx - sr, sy - sr, sx + sr, sy + sr), fill=(255, 244, 200, 140 + (k % 3) * 35))

    theme = spec["theme"]

    if theme == "compass_blade":
        # 星港罗盘短刃：中央鎏金星盘 + 锋利冷银蓝光短刃
        draw.ellipse((cx - 280, cy - 280, cx + 280, cy + 280), outline=gold_mid, width=10)
        draw.ellipse((cx - 240, cy - 240, cx + 240, cy + 240), outline=(accent[0], accent[1], accent[2], 160), width=5)
        draw.polygon(star_points(cx, cy, 290, 95, 8), fill=(gold[0], gold[1], gold[2], 55), outline=gold_mid)
        # 刀锋主体（竖直向上）
        blade_outer = [
            (cx, cy - 620),
            (cx + 125, cy - 160),
            (cx + 78, cy + 210),
            (cx - 78, cy + 210),
            (cx - 125, cy - 160),
        ]
        blade_inner = [
            (cx, cy - 540),
            (cx + 68, cy - 160),
            (cx + 35, cy + 170),
            (cx - 35, cy + 170),
            (cx - 68, cy - 160),
        ]
        draw.polygon(blade_outer, fill=(196, 232, 255, 255), outline=gold_bright)
        draw.polygon(blade_inner, fill=(94, 195, 255, 255))
        draw.line((cx, cy - 580, cx, cy + 190), fill=(245, 252, 255, 240), width=8)
        # 鎏金护手与星辰握柄
        guard = [(cx - 190, cy + 205), (cx + 190, cy + 205), (cx + 145, cy + 270), (cx - 145, cy + 270)]
        draw.polygon(guard, fill=gold_mid, outline=gold_dark)
        draw.rounded_rectangle((cx - 42, cy + 265, cx + 42, cy + 530), radius=18, fill=(52, 34, 24, 255), outline=gold_mid, width=8)
        for gy in range(int(cy + 305), int(cy + 500), 42):
            draw.line((cx - 38, gy, cx + 38, gy + 18), fill=gold_mid, width=6)
        draw.ellipse((cx - 66, cy + 510, cx + 66, cy + 642), fill=gold_mid, outline=gold_dark, width=8)
        draw.polygon(star_points(cx, cy + 576, 42, 16, 4), fill=accent_rgba, outline=gold_bright)
        # 护手中心蓝宝石
        draw.polygon(star_points(cx, cy + 235, 58, 22, 4), fill=accent_rgba, outline=gold_bright)

    elif theme == "bulwark_lantern":
        # 以太壁垒提灯：六边形翡翠护盾结界 + 鎏金提灯核心
        draw.polygon(hex_points(cx, cy, 355), fill=(36, 175, 162, 65), outline=(120, 255, 230, 210))
        draw.polygon(hex_points(cx, cy, 325), outline=gold_mid)
        # 提灯顶环与穹顶
        draw.arc((cx - 86, cy - 355, cx + 86, cy - 195), start=180, end=360, fill=gold_mid, width=16)
        draw.polygon([(cx - 170, cy - 180), (cx + 170, cy - 180), (cx + 100, cy - 275), (cx - 100, cy - 275)], fill=gold_mid, outline=gold_dark)
        # 提灯琉璃灯腹
        draw.rounded_rectangle((cx - 150, cy - 180, cx + 150, cy + 175), radius=34, fill=(22, 74, 86, 240), outline=gold_mid, width=14)
        # 核心以太光球与八芒符文
        for r_glow, a_glow in ((128, 70), (96, 140), (62, 220), (34, 255)):
            draw.ellipse((cx - r_glow, cy - r_glow, cx + r_glow, cy + r_glow), fill=(115, 255, 228, a_glow))
        draw.polygon(star_points(cx, cy, 118, 42, 8), fill=(225, 255, 248, 200), outline=gold_bright)
        # 提灯护栏竖棱与底座
        for lx in (-90, 0, 90):
            draw.line((cx + lx, cy - 180, cx + lx, cy + 175), fill=gold_mid, width=10)
        draw.polygon([(cx - 190, cy + 175), (cx + 190, cy + 175), (cx + 140, cy + 265), (cx - 140, cy + 265)], fill=gold_mid, outline=gold_dark)

    elif theme == "solar_kiln":
        # 日耀熔炉：烈焰齿轮外环 + 重装鎏金熔炉 + 炽热太阳核
        draw.polygon(star_points(cx, cy, 370, 290, 12), fill=(168, 78, 26, 140), outline=gold_mid)
        draw.ellipse((cx - 285, cy - 285, cx + 285, cy + 285), fill=(48, 22, 16, 250), outline=gold_mid, width=18)
        draw.ellipse((cx - 228, cy - 228, cx + 228, cy + 228), fill=(120, 32, 12, 255), outline=gold_dark, width=10)
        # 炽烈太阳焰心
        for rad, col in (
            (195, (255, 92, 20, 220)),
            (152, (255, 152, 28, 240)),
            (108, (255, 214, 72, 255)),
            (62, (255, 248, 205, 255)),
        ):
            draw.ellipse((cx - rad, cy - rad, cx + rad, cy + rad), fill=col)
        draw.polygon(star_points(cx, cy, 195, 86, 8), fill=(255, 200, 62, 190), outline=(255, 244, 180, 230))
        # 熔炉十字格栅
        draw.line((cx - 230, cy, cx + 230, cy), fill=(62, 28, 16, 220), width=16)
        draw.line((cx, cy - 230, cx, cy + 230), fill=(62, 28, 16, 220), width=16)
        draw.ellipse((cx - 72, cy - 72, cx + 72, cy + 72), fill=(255, 234, 132, 255), outline=gold_mid, width=10)

    elif theme == "dreadnought_vault":
        # 星港无畏宝库舰：巨型鎏金浮空战舰舰体 + 能量主炮 + 宝库穹顶
        # 上方星穹光环
        draw.ellipse((cx - 320, cy - 520, cx + 320, cy + 120), outline=(gold[0], gold[1], gold[2], 140), width=8)
        draw.polygon(star_points(cx, cy - 350, 210, 68, 8), fill=(255, 200, 90, 120), outline=gold_bright)
        # 旗舰双翼帆板
        left_wing = [(cx - 80, cy - 120), (cx - 420, cy - 260), (cx - 370, cy + 160), (cx - 100, cy + 220)]
        right_wing = [(cx + 80, cy - 120), (cx + 420, cy - 260), (cx + 370, cy + 160), (cx + 100, cy + 220)]
        draw.polygon(left_wing, fill=(116, 56, 152, 230), outline=gold_mid)
        draw.polygon(right_wing, fill=(116, 56, 152, 230), outline=gold_mid)
        # 旗舰主船体（尖首厚甲）
        hull = [
            (cx, cy - 580),
            (cx + 185, cy - 190),
            (cx + 215, cy + 260),
            (cx + 110, cy + 540),
            (cx - 110, cy + 540),
            (cx - 215, cy + 260),
            (cx - 185, cy - 190),
        ]
        draw.polygon(hull, fill=(46, 36, 68, 255), outline=gold_mid)
        hull_inner = [
            (cx, cy - 490),
            (cx + 125, cy - 170),
            (cx + 145, cy + 230),
            (cx + 70, cy + 455),
            (cx - 70, cy + 455),
            (cx - 145, cy + 230),
            (cx - 125, cy - 170),
        ]
        draw.polygon(hull_inner, fill=(68, 52, 102, 255), outline=gold_dark)
        # 中央鎏金宝库核心与烈焰引擎尾焰
        draw.polygon(hex_points(cx, cy + 20, 145), fill=gold_mid, outline=gold_dark)
        draw.polygon(hex_points(cx, cy + 20, 102), fill=(255, 138, 42, 255), outline=gold_bright)
        draw.polygon(star_points(cx, cy + 20, 76, 30, 6), fill=(255, 245, 190, 255))
        # 引擎推进焰
        for ex in (-95, 0, 95):
            flame = [(cx + ex - 38, cy + 535), (cx + ex + 38, cy + 535), (cx + ex, cy + 725 if ex == 0 else cy + 665)]
            draw.polygon(flame, fill=(255, 168, 56, 230), outline=(255, 235, 145, 255))

    elif theme == "trade_charter":
        # 星港贸易特许状（圆形技能徽章）：双环鎏金印章 + 天平星徽
        draw.ellipse((cx - 385, cy - 385, cx + 385, cy + 385), fill=(48, 30, 14, 250), outline=gold_mid, width=22)
        draw.ellipse((cx - 335, cy - 335, cx + 335, cy + 335), outline=gold_bright, width=6)
        draw.polygon(star_points(cx, cy, 315, 240, 16), fill=(168, 118, 34, 120), outline=gold_mid)
        # 羊皮纸卷轴底
        draw.rounded_rectangle((cx - 195, cy - 220, cx + 195, cy + 210), radius=24, fill=(238, 214, 158, 245), outline=gold_dark, width=10)
        for ly in (-140, -85, -30, 25):
            draw.line((cx - 135, cy + ly, cx + 135, cy + ly), fill=(122, 82, 34, 210), width=10)
        # 鎏金金币蜡封
        draw.ellipse((cx - 98, cy + 48, cx + 98, cy + 244), fill=(198, 46, 38, 255), outline=gold_bright, width=12)
        draw.polygon(star_points(cx, cy + 146, 66, 26, 6), fill=gold_bright, outline=gold_dark)

    elif theme == "ember_tide":
        # 余烬潮汐共鸣（圆形技能徽章）：烈焰赤金与护盾蔚蓝双色太极涡旋
        draw.ellipse((cx - 385, cy - 385, cx + 385, cy + 385), fill=(26, 20, 52, 250), outline=gold_mid, width=22)
        draw.pieslice((cx - 320, cy - 320, cx + 320, cy + 320), start=180, end=360, fill=(242, 96, 32, 235), outline=gold_bright, width=8)
        draw.pieslice((cx - 320, cy - 320, cx + 320, cy + 320), start=0, end=180, fill=(44, 182, 232, 235), outline=gold_bright, width=8)
        # 双色回旋子核
        draw.ellipse((cx - 160, cy - 160, cx + 160, cy + 160), fill=(255, 224, 118, 60), outline=gold_bright, width=10)
        draw.ellipse((cx - 190, cy - 95, cx, cy + 95), fill=(242, 96, 32, 255))
        draw.ellipse((cx, cy - 95, cx + 190, cy + 95), fill=(44, 182, 232, 255))
        draw.polygon(star_points(cx - 95, cy, 54, 20, 6), fill=(255, 240, 170, 255))
        draw.polygon(star_points(cx + 95, cy, 54, 20, 6), fill=(215, 252, 255, 255))
        draw.ellipse((cx - 320, cy - 320, cx + 320, cy + 320), outline=gold_mid, width=14)

    # 统一的大巴扎鎏金双层华丽画框与四角宝石星芒
    pad1, pad2 = 24, 48
    draw.rounded_rectangle((pad1, pad1, sw - pad1, sh - pad1), radius=36, outline=gold_mid, width=16)
    draw.rounded_rectangle((pad2, pad2, sw - pad2, sh - pad2), radius=24, outline=gold_bright, width=5)
    for corner_x, corner_y in (
        (pad2 + 12, pad2 + 12),
        (sw - pad2 - 12, pad2 + 12),
        (pad2 + 12, sh - pad2 - 12),
        (sw - pad2 - 12, sh - pad2 - 12),
    ):
        draw.polygon(star_points(corner_x, corner_y, 34, 13, 4), fill=gold_bright, outline=gold_dark)
        draw.ellipse((corner_x - 10, corner_y - 10, corner_x + 10, corner_y + 10), fill=accent_rgba)

    canvas = Image.alpha_composite(canvas, fg_layer)
    final_img = canvas.resize((w, h), Image.Resampling.LANCZOS)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    final_img.save(out_path, format="PNG", optimize=True)


def write_png_stdlib(width: int, height: int, rgba_rows: list[bytes], out_path: pathlib.Path) -> None:
    """零第三方依赖纯标准库 RGBA PNG 编码器（Pillow 缺失时自动回退）。"""
    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    raw = b"".join(b"\x00" + row for row in rgba_rows)
    idat = zlib.compress(raw, level=9)
    png_bytes = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", idat) + chunk(b"IEND", b"")
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(png_bytes)


def render_stdlib_fallback(spec: dict[str, Any], out_path: pathlib.Path) -> None:
    w, h = spec["width"], spec["height"]
    bg_in, bg_out = spec["bg_inner"], spec["bg_outer"]
    accent, gold = spec["accent"], spec["gold"]
    cx, cy = w * 0.5, h * 0.5
    rad_max = math.hypot(w * 0.5, h * 0.5)
    core_r = min(w, h) * 0.24
    rows: list[bytes] = []
    for y in range(h):
        row = bytearray(w * 4)
        for x in range(w):
            in_border = x < 16 or x >= w - 16 or y < 16 or y >= h - 16
            in_inner = (22 <= x < 26 or w - 26 <= x < w - 22) and (22 <= y < h - 22) or (22 <= y < 26 or h - 26 <= y < h - 22) and (22 <= x < w - 22)
            d = math.hypot(x - cx, y - cy)
            if in_border or in_inner:
                r, g, b = gold
            elif d < core_r:
                t = d / core_r
                r = int(lerp(255, accent[0], t))
                g = int(lerp(245, accent[1], t))
                b = int(lerp(210, accent[2], t))
            elif abs(d - core_r * 1.25) < 6:
                r, g, b = gold
            else:
                t = min(1.0, d / rad_max)
                r = int(lerp(bg_in[0], bg_out[0], t))
                g = int(lerp(bg_in[1], bg_out[1], t))
                b = int(lerp(bg_in[2], bg_out[2], t))
            idx = x * 4
            row[idx : idx + 4] = bytes((r, g, b, 255))
        rows.append(bytes(row))
    write_png_stdlib(w, h, rows, out_path)


def verify_png(path: pathlib.Path, expected_w: int, expected_h: int) -> dict[str, Any]:
    raw = path.read_bytes()
    if len(raw) < 33 or raw[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"非法 PNG 魔数头: {path}")
    if raw[12:16] != b"IHDR":
        raise ValueError(f"缺少 IHDR 块: {path}")
    w, h, bit_depth, color_type = struct.unpack(">IIBB", raw[16:26])
    if w != expected_w or h != expected_h:
        raise ValueError(f"尺寸不符 {path.name}: 实际 {w}x{h} != 期望 {expected_w}x{expected_h}")
    if bit_depth != 8 or color_type != 6:
        raise ValueError(f"色彩格式非 8-bit RGBA: depth={bit_depth}, color_type={color_type}")
    sha256 = hashlib.sha256(raw).hexdigest()
    return {
        "filename": path.name,
        "width": w,
        "height": h,
        "bytes": len(raw),
        "sha256": sha256,
        "customArtHash": f"sha256:{sha256}",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="生成并校验示例 Mod 的 6 张大巴扎鎏金风格 PNG 卡图。")
    parser.add_argument("--out-dir", type=pathlib.Path, default=DEFAULT_ASSETS_DIR, help="PNG 输出目录")
    parser.add_argument("--stdlib-only", action="store_true", help="强制使用纯标准库渲染器（跳过 Pillow）")
    args = parser.parse_args()

    use_pillow = not args.stdlib_only
    if use_pillow:
        try:
            import PIL  # noqa: F401
        except ImportError:
            use_pillow = False

    engine_name = "Pillow (2x 超采样抗锯齿)" if use_pillow else "Python 标准库 (zlib+struct)"
    print(f"[gen_sample_mod_art] 渲染引擎: {engine_name}")
    print(f"[gen_sample_mod_art] 输出目录: {args.out_dir}")

    manifest_items: list[dict[str, Any]] = []
    for spec in ART_SPECS:
        out_path = args.out_dir / spec["filename"]
        if use_pillow:
            render_with_pillow(spec, out_path)
        else:
            render_stdlib_fallback(spec, out_path)
        info = verify_png(out_path, spec["width"], spec["height"])
        info["cardId"] = spec["id"]
        info["titleZh"] = spec["titleZh"]
        info["logicalPath"] = f"assets/{spec['filename']}"
        manifest_items.append(info)
        print(
            f"  [OK] {info['filename']:34s} {info['width']}x{info['height']} "
            f"{info['bytes']:>7,} B  sha256={info['sha256'][:16]}..."
        )

    manifest_path = args.out_dir / "art_manifest.json"
    manifest_path.write_text(
        json.dumps({"schemaVersion": 1, "engine": engine_name, "assets": manifest_items}, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )

    # 若同级存在 ../cards/custom_cards.json 且属于同一 Mod 目录，自动同步最新 PNG 哈希
    cards_json = args.out_dir.parent / "cards" / "custom_cards.json"
    if not args.stdlib_only and cards_json.is_file():
        by_id = {m["cardId"]: m for m in manifest_items}
        cards_doc = json.loads(cards_json.read_text(encoding="utf-8"))
        changed = False
        for card in cards_doc.get("cards") or []:
            info = by_id.get(card.get("customId"))
            if info and isinstance(card.get("customArt"), dict):
                if card["customArt"].get("sha256") != info["sha256"]:
                    card["customArt"]["sha256"] = info["sha256"]
                    card["customArt"]["customArtHash"] = info["customArtHash"]
                    changed = True
        if changed:
            cards_json.write_text(json.dumps(cards_doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            print(f"[gen_sample_mod_art] 已自动同步更新 {cards_json} 中的 PNG SHA-256 哈希。")

    print(f"[gen_sample_mod_art] 全部 6 张 PNG 生成并校验通过！清单已写入 {manifest_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
