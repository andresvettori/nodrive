#!/usr/bin/env python3
"""Generates Microsoft Store art (pure Python): 1:1 box art 1080/2160 and 2:3 poster 1440x2160, from the icon drawing in make_icon.py."""
import struct, zlib, pathlib
here = pathlib.Path(__file__).resolve().parent
src = (here / 'make_icon.py').read_text().split('def png(n)')[0]
ns = {}; exec(src, ns); inside = ns['inside']

def write(path, w, h, pixel):
    rows = []
    for j in range(h):
        row = bytearray(b'\x00')
        for i in range(w): row += bytes(pixel(i, j))
        rows.append(bytes(row))
    def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    data = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(b''.join(rows), 9)) + chunk(b'IEND', b'')
    pathlib.Path(path).write_bytes(data); print(path, w, h, len(data))

def art(w, h, icon_size, cy):
    x0, y0 = (w - icon_size) / 2, h * cy - icon_size / 2
    def pixel(i, j):
        t = j / h
        bg = (int(16 + 14 * t), int(30 + 30 * t), int(70 + 60 * t))   # deep blue gradient
        r = g = b = 0; a = 0
        for sj in range(2):
            for si in range(2):
                pa, pr, pg, pb = inside((i + (si + .5) / 2 - x0) / icon_size, (j + (sj + .5) / 2 - y0) / icon_size) if 0 <= i - x0 < icon_size and 0 <= j - y0 < icon_size else (0, 0, 0, 0)
                a += pa; r += pr * pa; g += pg * pa; b += pb * pa
        if a == 0: return bg
        al = a / 4 / 255; r, g, b = r / a, g / a, b / a
        return (int(r * al + bg[0] * (1 - al)), int(g * al + bg[1] * (1 - al)), int(b * al + bg[2] * (1 - al)))
    return pixel

out = here.parent / 'store-art'
write(out / 'box-art-1080.png', 1080, 1080, art(1080, 1080, 800, .5))
write(out / 'box-art-2160.png', 2160, 2160, art(2160, 2160, 1600, .5))
write(out / 'poster-1440x2160.png', 1440, 2160, art(1440, 2160, 1000, .42))
