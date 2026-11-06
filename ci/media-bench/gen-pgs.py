#!/usr/bin/env python3
"""Writes a minimal, valid HDMV PGS (.sup) subtitle stream (media bench, #342).

FFmpeg has a PGS decoder but no PGS encoder, so a bitmap-subtitle fixture cannot
be generated with FFmpeg alone. This writes one display set that shows a solid
opaque white rectangle from --start to --end on a --width x --height canvas, then
an empty display set that clears it. The rectangle's geometry is printed so the
bench can measure the burned-in region afterwards.

Usage: gen-pgs.py OUT.sup [--width 640 --height 360 --box 320x60+160+260
                           --start 1.0 --end 29.0]
"""
import argparse
import struct


def seg(kind: int, pts: int, data: bytes) -> bytes:
    return b"PG" + struct.pack(">IIBH", pts, 0, kind, len(data)) + data


def rle_line(width: int, color: int) -> bytes:
    out = bytearray()
    left = width
    while left > 0:
        n = min(left, 16383)
        if n < 3:
            out += bytes([color]) * n
        elif n < 64:
            out += bytes([0x00, 0x80 | n, color])
        else:
            out += bytes([0x00, 0xC0 | (n >> 8), n & 0xFF, color])
        left -= n
    out += b"\x00\x00"  # end of line
    return bytes(out)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--width", type=int, default=640)
    ap.add_argument("--height", type=int, default=360)
    ap.add_argument("--box", default="320x60+160+260")
    ap.add_argument("--start", type=float, default=1.0)
    ap.add_argument("--end", type=float, default=29.0)
    a = ap.parse_args()

    size, x, y = a.box.split("+")
    bw, bh = (int(v) for v in size.split("x"))
    x, y = int(x), int(y)
    t0, t1 = int(a.start * 90000), int(a.end * 90000)

    # Presentation composition: epoch start, one object in window 0.
    pcs = struct.pack(">HHBHBBBB", a.width, a.height, 0x10, 0, 0x80, 0, 0, 1)
    pcs += struct.pack(">HBBHH", 0, 0, 0, x, y)
    wds = struct.pack(">B", 1) + struct.pack(">BHHHH", 0, x, y, bw, bh)
    # Palette 0: entry 1 = opaque white (Y=235, Cr=Cb=128).
    pds = struct.pack(">BB", 0, 0) + struct.pack(">BBBBB", 1, 235, 128, 128, 255)
    rle = b"".join(rle_line(bw, 1) for _ in range(bh))
    ods = struct.pack(">HBB", 0, 0, 0xC0)
    ods += (len(rle) + 4).to_bytes(3, "big") + struct.pack(">HH", bw, bh) + rle

    clear_pcs = struct.pack(">HHBHBBBB", a.width, a.height, 0x10, 1, 0x00, 0, 0, 0)
    clear_wds = struct.pack(">B", 1) + struct.pack(">BHHHH", 0, x, y, bw, bh)

    with open(a.out, "wb") as f:
        f.write(seg(0x16, t0, pcs))
        f.write(seg(0x17, t0, wds))
        f.write(seg(0x14, t0, pds))
        f.write(seg(0x15, t0, ods))
        f.write(seg(0x80, t0, b""))
        f.write(seg(0x16, t1, clear_pcs))
        f.write(seg(0x17, t1, clear_wds))
        f.write(seg(0x80, t1, b""))
    print(f"{bw}x{bh}+{x}+{y}")


if __name__ == "__main__":
    main()
