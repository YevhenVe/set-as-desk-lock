"""Builds the theme icons from assets/icon-source.png using the standard library only.

The blue lock keeps its colour. The black lines become white for dark themes.
"""
import os
import struct
import sys
import zlib

ASSETS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets")
BLUE = (12, 140, 233)
BLACK = (0, 0, 0)
WHITE = (255, 255, 255)


def read_png(path):
    data = open(path, "rb").read()
    pos, idat = 8, b""
    while pos < len(data):
        length, tag = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if tag == b"IHDR":
            width, height, depth, kind, _, _, interlace = struct.unpack(">IIBBBBB", body)
            if (depth, kind, interlace) != (8, 6, 0):
                raise ValueError("Expected a non-interlaced 8-bit RGBA PNG")
        elif tag == b"IDAT":
            idat += body
    raw, stride, prev, rows, off = zlib.decompress(idat), width * 4, bytearray(width * 4), [], 0
    for _ in range(height):
        kind, cur = raw[off], bytearray(raw[off + 1:off + 1 + stride])
        off += 1 + stride
        for i in range(stride):
            a = cur[i - 4] if i >= 4 else 0
            b = prev[i]
            c = prev[i - 4] if i >= 4 else 0
            if kind == 1:
                cur[i] = (cur[i] + a) & 255
            elif kind == 2:
                cur[i] = (cur[i] + b) & 255
            elif kind == 3:
                cur[i] = (cur[i] + (a + b) // 2) & 255
            elif kind == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                cur[i] = (cur[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(cur)
        prev = cur
    return width, height, rows


def recolor(source, ink):
    """Returns premultiplied float pixels with black swapped for ink."""
    width, height, rows = source
    pixels = []
    for row in rows:
        line = []
        for x in range(width):
            blue_share = min(1.0, row[x * 4 + 2] / BLUE[2])
            alpha = row[x * 4 + 3] / 255
            line.append(tuple((BLUE[k] * blue_share + ink[k] * (1 - blue_share)) / 255 * alpha for k in range(3)) + (alpha,))
        pixels.append(line)
    return pixels


def resample(source, ink, size):
    """Fits the art into a transparent square using area averaging, kept straight-alpha."""
    width, height, _ = source
    pixels = recolor(source, ink)
    side = max(width, height)
    scale = side / size
    pad_x, pad_y = (side - width) / 2, (side - height) / 2
    raw = bytearray()
    for j in range(size):
        raw.append(0)
        y0, y1 = j * scale - pad_y, (j + 1) * scale - pad_y
        for i in range(size):
            x0, x1 = i * scale - pad_x, (i + 1) * scale - pad_x
            total = [0.0, 0.0, 0.0, 0.0]
            for y in range(max(0, int(y0)), min(height, int(y1) + 1)):
                wy = min(y + 1, y1) - max(y, y0)
                if wy <= 0:
                    continue
                for x in range(max(0, int(x0)), min(width, int(x1) + 1)):
                    wx = min(x + 1, x1) - max(x, x0)
                    if wx > 0:
                        for k in range(4):
                            total[k] += pixels[y][x][k] * wx * wy
            area = scale * scale
            alpha = total[3] / area
            rgb = [total[k] / area / alpha if alpha > 0 else 0 for k in range(3)]
            raw += bytes(min(255, int(v * 255 + 0.5)) for v in rgb + [alpha])
    return bytes(raw)


def encode(width, height, raw):
    def chunk(tag, body):
        return struct.pack(">I", len(body)) + tag + body + struct.pack(">I", zlib.crc32(tag + body))

    header = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header)
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def png(source, ink, size):
    return encode(size, size, resample(source, ink, size))


def write(name, data):
    with open(os.path.join(ASSETS, name), "wb") as f:
        f.write(data)


def ico(source, name, ink):
    sizes = [16, 20, 24, 32, 40, 48, 64, 256]
    images = [png(source, ink, s) for s in sizes]
    offset = 6 + 16 * len(sizes)
    head = struct.pack("<HHH", 0, 1, len(sizes))
    for s, data in zip(sizes, images):
        head += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    write(name, head + b"".join(images))


def preview(source, path):
    """Writes the menu sizes on menu-like backgrounds, magnified 6x."""
    zoom, sizes, pad = 6, [16, 20, 24, 32, 48], 8
    width = sum(sizes) * zoom + pad * (len(sizes) + 1)
    band = 48 * zoom + 2 * pad
    canvas = [None] * (2 * band)
    for index, (bg, ink) in enumerate((((44, 44, 44), WHITE), ((249, 249, 249), BLACK))):
        for y in range(band):
            canvas[index * band + y] = [bg] * width
        x0 = pad
        for s in sizes:
            raw = resample(source, ink, s)
            for j in range(s * zoom):
                for i in range(s * zoom):
                    o = (j // zoom) * (s * 4 + 1) + 1 + (i // zoom) * 4
                    a = raw[o + 3] / 255
                    canvas[index * band + pad + j][x0 + i] = tuple(int(raw[o + k] * a + bg[k] * (1 - a)) for k in range(3))
            x0 += s * zoom + pad
    raw = b"".join(b"\0" + b"".join(bytes(px) + b"\xff" for px in row) for row in canvas)
    with open(path, "wb") as f:
        f.write(encode(width, 2 * band, raw))


def main():
    source = read_png(os.path.join(ASSETS, "icon-source.png"))
    if len(sys.argv) == 3 and sys.argv[1] == "--preview":
        preview(source, sys.argv[2])
        return
    ico(source, "SetDeskLock.ico", BLACK)
    ico(source, "SetDeskLockDark.ico", WHITE)
    write("Square44x44Logo.png", png(source, BLACK, 44))
    write("Square150x150Logo.png", png(source, BLACK, 150))
    write("StoreLogo.png", png(source, BLACK, 50))


main()
