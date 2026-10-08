#!/usr/bin/env python3
"""Рисует BMP-картинки мастера Inno Setup из icon.svg.

wizard-*.bmp — боковая панель страниц «Добро пожаловать» и «Готово»,
wizard-small-*.bmp — значок в шапке остальных страниц. Несколько размеров
нужны для масштабов 100–150 % (и 200 % для значка): Inno Setup выбирает подходящий сам.
"""

from __future__ import annotations

import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from render_windows_icon import parse_svg, render_rgba  # noqa: E402

PANEL = (246, 243, 238)
ACCENT = (232, 114, 42)
WHITE = (255, 255, 255)
LARGE_SIZES = ((202, 386), (253, 483), (303, 579))
SMALL_SIZES = (58, 73, 87, 116)


def canvas(width: int, height: int, color: tuple[int, int, int]) -> list[list[tuple[int, int, int]]]:
    return [[color] * width for _ in range(height)]


def paste_icon(pixels, icon: bytes, size: int, left: int, top: int) -> None:
    for y in range(size):
        for x in range(size):
            r, g, b, a = icon[(y * size + x) * 4:(y * size + x) * 4 + 4]
            if a == 0:
                continue
            br, bg, bb = pixels[top + y][left + x]
            k = a / 255
            pixels[top + y][left + x] = (round(r * k + br * (1 - k)), round(g * k + bg * (1 - k)), round(b * k + bb * (1 - k)))


def write_bmp(path: Path, pixels) -> None:
    height, width = len(pixels), len(pixels[0])
    row_size = (width * 3 + 3) & ~3
    data = bytearray()
    for row in reversed(pixels):
        line = bytearray()
        for r, g, b in row:
            line += bytes((b, g, r))
        data += line + bytes(row_size - len(line))
    header = struct.pack("<2sIHHI", b"BM", 54 + len(data), 0, 0, 54)
    info = struct.pack("<IiiHHIIiiII", 40, width, height, 1, 24, 0, len(data), 2835, 2835, 0, 0)
    path.write_bytes(header + info + data)


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: render_installer_images.py <icon.svg> <output-dir>", file=sys.stderr)
        return 2
    width, height, shapes = parse_svg(Path(sys.argv[1]))
    out = Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)

    for panel_w, panel_h in LARGE_SIZES:
        pixels = canvas(panel_w, panel_h, PANEL)
        size = round(panel_w * 0.46)
        paste_icon(pixels, render_rgba(width, height, shapes, size), size, (panel_w - size) // 2, round(panel_h * 0.30))
        stripe = max(3, panel_w // 50)
        for y in range(panel_h - stripe, panel_h):
            pixels[y] = [ACCENT] * panel_w
        write_bmp(out / f"wizard-{panel_w}.bmp", pixels)

    for side in SMALL_SIZES:
        pixels = canvas(side, side, WHITE)
        size = round(side * 0.78)
        offset = (side - size) // 2
        paste_icon(pixels, render_rgba(width, height, shapes, size), size, offset, offset)
        write_bmp(out / f"wizard-small-{side}.bmp", pixels)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
