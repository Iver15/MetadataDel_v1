#!/usr/bin/env python3

from __future__ import annotations

import math
import struct
import sys
import zlib
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable
from xml.etree import ElementTree as ET


DEFAULT_SIZES = (16, 32, 48, 64, 128, 256)
SUPERSAMPLING = 4


@dataclass(frozen=True)
class Color:
    r: int
    g: int
    b: int
    a: int = 255


@dataclass(frozen=True)
class Rect:
    x: float
    y: float
    width: float
    height: float
    rx: float
    ry: float
    color: Color


@dataclass(frozen=True)
class Circle:
    cx: float
    cy: float
    radius: float
    color: Color


Shape = Rect | Circle


def parse_color(value: str) -> Color:
    value = value.strip()
    if not value.startswith("#"):
        raise ValueError(f"Unsupported color format: {value}")

    hex_value = value[1:]
    if len(hex_value) == 3:
        hex_value = "".join(part * 2 for part in hex_value)

    if len(hex_value) != 6:
        raise ValueError(f"Unsupported color format: {value}")

    return Color(
        r=int(hex_value[0:2], 16),
        g=int(hex_value[2:4], 16),
        b=int(hex_value[4:6], 16),
    )


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def parse_svg(svg_path: Path) -> tuple[float, float, list[Shape]]:
    root = ET.parse(svg_path).getroot()
    view_box = root.attrib.get("viewBox")
    if not view_box:
        raise ValueError("SVG must define viewBox.")

    min_x, min_y, width, height = map(float, view_box.split())
    if min_x != 0 or min_y != 0:
        raise ValueError("Only SVG files with a 0 0 origin viewBox are supported.")

    shapes: list[Shape] = []
    for element in root:
        tag = local_name(element.tag)
        fill = element.attrib.get("fill")
        if not fill or fill.lower() == "none":
            continue

        color = parse_color(fill)
        if tag == "rect":
            width_value = float(element.attrib["width"])
            height_value = float(element.attrib["height"])
            rx = float(element.attrib.get("rx", element.attrib.get("ry", "0")))
            ry = float(element.attrib.get("ry", element.attrib.get("rx", "0")))
            shapes.append(
                Rect(
                    x=float(element.attrib["x"]),
                    y=float(element.attrib["y"]),
                    width=width_value,
                    height=height_value,
                    rx=min(rx, width_value / 2.0),
                    ry=min(ry, height_value / 2.0),
                    color=color,
                )
            )
        elif tag == "circle":
            shapes.append(
                Circle(
                    cx=float(element.attrib["cx"]),
                    cy=float(element.attrib["cy"]),
                    radius=float(element.attrib["r"]),
                    color=color,
                )
            )
        else:
            raise ValueError(f"Unsupported SVG shape: {tag}")

    return width, height, shapes


def point_in_rect(rect: Rect, px: float, py: float) -> bool:
    if px < rect.x or py < rect.y or px > rect.x + rect.width or py > rect.y + rect.height:
        return False

    if rect.rx <= 0 or rect.ry <= 0:
        return True

    inner_x0 = rect.x + rect.rx
    inner_x1 = rect.x + rect.width - rect.rx
    inner_y0 = rect.y + rect.ry
    inner_y1 = rect.y + rect.height - rect.ry

    if inner_x0 <= px <= inner_x1:
        return True

    if inner_y0 <= py <= inner_y1:
        return True

    for cx, cy in (
        (inner_x0, inner_y0),
        (inner_x1, inner_y0),
        (inner_x0, inner_y1),
        (inner_x1, inner_y1),
    ):
        dx = (px - cx) / rect.rx
        dy = (py - cy) / rect.ry
        if dx * dx + dy * dy <= 1.0:
            return True

    return False


def point_in_circle(circle: Circle, px: float, py: float) -> bool:
    dx = px - circle.cx
    dy = py - circle.cy
    return dx * dx + dy * dy <= circle.radius * circle.radius


def point_in_shape(shape: Shape, px: float, py: float) -> bool:
    if isinstance(shape, Rect):
        return point_in_rect(shape, px, py)

    return point_in_circle(shape, px, py)


def render_rgba(width: float, height: float, shapes: Iterable[Shape], size: int) -> bytes:
    high_size = size * SUPERSAMPLING
    scale_x = width / high_size
    scale_y = height / high_size
    high = bytearray(high_size * high_size * 4)

    for y in range(high_size):
        py = (y + 0.5) * scale_y
        row_offset = y * high_size * 4
        for x in range(high_size):
            px = (x + 0.5) * scale_x
            color = Color(0, 0, 0, 0)
            for shape in shapes:
                if point_in_shape(shape, px, py):
                    color = shape.color

            pixel = row_offset + x * 4
            high[pixel + 0] = color.r
            high[pixel + 1] = color.g
            high[pixel + 2] = color.b
            high[pixel + 3] = color.a

    rgba = bytearray(size * size * 4)
    area = SUPERSAMPLING * SUPERSAMPLING
    for y in range(size):
        for x in range(size):
            r = g = b = a = 0
            for sy in range(SUPERSAMPLING):
                for sx in range(SUPERSAMPLING):
                    high_x = x * SUPERSAMPLING + sx
                    high_y = y * SUPERSAMPLING + sy
                    pixel = (high_y * high_size + high_x) * 4
                    r += high[pixel + 0]
                    g += high[pixel + 1]
                    b += high[pixel + 2]
                    a += high[pixel + 3]

            pixel = (y * size + x) * 4
            rgba[pixel + 0] = round(r / area)
            rgba[pixel + 1] = round(g / area)
            rgba[pixel + 2] = round(b / area)
            rgba[pixel + 3] = round(a / area)

    return bytes(rgba)


def png_chunk(chunk_type: bytes, data: bytes) -> bytes:
    return (
        struct.pack(">I", len(data))
        + chunk_type
        + data
        + struct.pack(">I", zlib.crc32(chunk_type + data) & 0xFFFFFFFF)
    )


def build_png(size: int, rgba: bytes) -> bytes:
    raw = bytearray()
    stride = size * 4
    for y in range(size):
        raw.append(0)
        raw.extend(rgba[y * stride : (y + 1) * stride])

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    idat = zlib.compress(bytes(raw), level=9)
    signature = b"\x89PNG\r\n\x1a\n"
    return signature + png_chunk(b"IHDR", ihdr) + png_chunk(b"IDAT", idat) + png_chunk(b"IEND", b"")


def build_ico(frames: list[tuple[int, bytes]]) -> bytes:
    header = struct.pack("<HHH", 0, 1, len(frames))
    directory = bytearray()
    payload = bytearray()
    offset = 6 + 16 * len(frames)

    for size, png_bytes in frames:
        directory.extend(
            struct.pack(
                "<BBBBHHII",
                0 if size >= 256 else size,
                0 if size >= 256 else size,
                0,
                0,
                1,
                32,
                len(png_bytes),
                offset,
            )
        )
        payload.extend(png_bytes)
        offset += len(png_bytes)

    return header + bytes(directory) + bytes(payload)


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: render_windows_icon.py <input.svg> <output.ico>", file=sys.stderr)
        return 1

    source = Path(sys.argv[1])
    destination = Path(sys.argv[2])

    width, height, shapes = parse_svg(source)
    frames = []
    for size in DEFAULT_SIZES:
        rgba = render_rgba(width, height, shapes, size)
        frames.append((size, build_png(size, rgba)))

    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(build_ico(frames))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
