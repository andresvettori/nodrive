#!/usr/bin/env python3
"""Tile and logo PNGs for the MSIX package (pure Python), from the icon drawing in make_icon.py. Output: installer/msix/Assets."""
import struct, zlib, pathlib
here = pathlib.Path(__file__).resolve().parent
ns = {}; exec((here / 'make_icon.py').read_text().split('def png(n)')[0], ns); inside = ns['inside']

def png(path, w, h, icon, ss=3):
    x0, y0 = (w - icon) / 2, (h - icon) / 2
    rows = []
    for j in range(h):
        row = bytearray(b'\x00')
        for i in range(w):
            a = r = g = b = 0
            for sj in range(ss):
                for si in range(ss):
                    u, v = (i + (si + .5) / ss - x0) / icon, (j + (sj + .5) / ss - y0) / icon
                    pa, pr, pg, pb = inside(u, v) if 0 <= u < 1 and 0 <= v < 1 else (0, 0, 0, 0)
                    a += pa; r += pr * pa; g += pg * pa; b += pb * pa
            row += bytes((0, 0, 0, 0)) if a == 0 else bytes((int(r / a), int(g / a), int(b / a), int(a / ss / ss)))
        rows.append(bytes(row))
    def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    path.write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(b''.join(rows), 9)) + chunk(b'IEND', b''))
    print(path.name, w, h)

out = here.parent / 'installer/msix/Assets'
png(out / 'StoreLogo.png', 50, 50, 50)
png(out / 'Square44x44Logo.png', 44, 44, 44)
png(out / 'Square44x44Logo.targetsize-256.png', 256, 256, 256)
png(out / 'Square150x150Logo.png', 150, 150, 120)
png(out / 'Wide310x150Logo.png', 310, 150, 120)
