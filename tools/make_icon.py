#!/usr/bin/env python3
"""Generates src/NoDrive.Engine/Assets/nodrive.ico (pure Python, no dependencies): blue rounded square, white cloud, down arrow."""
import struct, zlib, math, pathlib

def inside(x, y):
    """Returns (alpha, r, g, b) for a point in unit coordinates."""
    # rounded square
    cx, cy, hw, rad = 0.5, 0.5, 0.5, 0.22
    dx, dy = abs(x - cx) - (hw - rad), abs(y - cy) - (hw - rad)
    d = math.hypot(max(dx, 0), max(dy, 0)) + min(max(dx, dy), 0) - rad
    if d > 0: return (0, 0, 0, 0)
    t = y
    bg = (int(45 + (20 - 45) * t), int(150 + (90 - 150) * t), int(255 + (216 - 255) * t))
    # cloud
    def circ(px, py, r): return math.hypot(x - px, y - py) <= r
    cloud = circ(0.37, 0.54, 0.14) or circ(0.54, 0.45, 0.19) or circ(0.69, 0.55, 0.13) or (0.27 <= x <= 0.82 and 0.52 <= y <= 0.68)
    if cloud:
        # arrow cut-out in blue
        shaft = 0.465 <= x <= 0.535 and 0.40 <= y <= 0.55
        head = y >= 0.52 and y <= 0.66 and abs(x - 0.5) <= (0.66 - y) * 1.0 + 0.0 and abs(x - 0.5) <= 0.13
        if shaft or head: return (255,) + bg
        return (255, 255, 255, 255)
    return (255,) + bg

def render(n, ss=4):
    rows = []
    for j in range(n):
        row = bytearray()
        for i in range(n):
            a = r = g = b = 0
            for sj in range(ss):
                for si in range(ss):
                    pa, pr, pg, pb = inside((i + (si + .5) / ss) / n, (j + (sj + .5) / ss) / n)
                    a += pa; r += pr * pa; g += pg * pa; b += pb * pa
            cnt = ss * ss
            if a == 0: row += bytes((0, 0, 0, 0))
            else: row += bytes((int(r / a), int(g / a), int(b / a), int(a / cnt)))
        rows.append(bytes(row))
    return rows

def png(n):
    raw = b''.join(b'\x00' + r for r in render(n))
    def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', n, n, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b'')

sizes = [16, 24, 32, 48, 64, 128, 256]
images = [png(s) for s in sizes]
out = struct.pack('<HHH', 0, 1, len(sizes))
offset = 6 + 16 * len(sizes)
for s, im in zip(sizes, images):
    out += struct.pack('<BBBBHHII', s % 256, s % 256, 0, 0, 1, 32, len(im), offset)
    offset += len(im)
out += b''.join(images)
dest = pathlib.Path(__file__).resolve().parent.parent / 'src/NoDrive.Engine/Assets/nodrive.ico'
dest.write_bytes(out)
print(dest, len(out), 'bytes')
