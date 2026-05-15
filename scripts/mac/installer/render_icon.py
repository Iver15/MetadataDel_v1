#!/usr/bin/env python3
from __future__ import annotations

import argparse
import struct
import zlib
from pathlib import Path


def clamp(value: float) -> int:
    return max(0, min(255, int(round(value))))


def inside_rounded_rect(px: float, py: float, x: float, y: float, w: float, h: float, r: float) -> bool:
    if px < x or px >= x + w or py < y or py >= y + h:
        return False
    cx = min(max(px, x + r), x + w - r)
    cy = min(max(py, y + r), y + h - r)
    return (px - cx) * (px - cx) + (py - cy) * (py - cy) <= r * r


def blend(dst: tuple[int, int, int, int], src: tuple[int, int, int, int]) -> tuple[int, int, int, int]:
    sr, sg, sb, sa = src
    dr, dg, db, da = dst
    a = sa / 255
    out_a = a + da / 255 * (1 - a)
    if out_a <= 0:
        return (0, 0, 0, 0)
    return (
        clamp((sr * a + dr * da / 255 * (1 - a)) / out_a),
        clamp((sg * a + dg * da / 255 * (1 - a)) / out_a),
        clamp((sb * a + db * da / 255 * (1 - a)) / out_a),
        clamp(out_a * 255),
    )


def draw_icon(size: int) -> bytes:
    scale = size / 64
    pixels = [(0, 0, 0, 0)] * (size * size)
    black = (26, 26, 26, 255)
    orange = (232, 114, 42, 255)
    shadow = (0, 0, 0, 40)

    shapes = [
        ("rect", 6, 6, 22, 22, 3, black),
        ("circle", 47, 17, 11, 0, 0, orange),
        ("rect", 6, 36, 22, 22, 3, black),
        ("rect", 36, 36, 22, 22, 3, black),
    ]

    for y in range(size):
        for x in range(size):
            sx = (x + 0.5) / scale
            sy = (y + 0.5) / scale
            color = pixels[y * size + x]

            for shape in shapes:
                if shape[0] == "rect":
                    _, rx, ry, rw, rh, rr, fill = shape
                    if inside_rounded_rect(sx - 1.2, sy - 1.4, rx, ry, rw, rh, rr):
                        color = blend(color, shadow)
                    if inside_rounded_rect(sx, sy, rx, ry, rw, rh, rr):
                        color = blend(color, fill)
                else:
                    _, cx, cy, radius, _, _, fill = shape
                    if (sx - (cx + 1.2)) ** 2 + (sy - (cy + 1.4)) ** 2 <= radius ** 2:
                        color = blend(color, shadow)
                    if (sx - cx) ** 2 + (sy - cy) ** 2 <= radius ** 2:
                        color = blend(color, fill)

            pixels[y * size + x] = color

    rows = []
    for y in range(size):
        row = bytearray([0])
        for x in range(size):
            row.extend(pixels[y * size + x])
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")
    return png


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("output_dir")
    args = parser.parse_args()

    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    for base in (16, 32, 128, 256, 512):
        (output_dir / f"icon_{base}x{base}.png").write_bytes(draw_icon(base))
        (output_dir / f"icon_{base}x{base}@2x.png").write_bytes(draw_icon(base * 2))


if __name__ == "__main__":
    main()
