#!/usr/bin/env python3
"""Convert PNG icons to raw RGBA32 for embedding in the mod.

Usage: png2rgba.py OUT_DIR SIZE FILE.png ...

Writes OUT_DIR/<name>.rgba: 8-byte header (width, height as little-endian
uint32) followed by width*height*4 bytes, rows bottom-up as Unity's
Texture2D.LoadRawTextureData expects. Decoding at build time avoids
ImageConversion.LoadImage, whose interop signature takes an Il2Cpp Span.
"""
import os
import struct
import sys

from PIL import Image


def main():
    out_dir, size = sys.argv[1], int(sys.argv[2])
    os.makedirs(out_dir, exist_ok=True)
    for path in sys.argv[3:]:
        img = Image.open(path).convert("RGBA").resize((size, size), Image.LANCZOS)
        img = img.transpose(Image.FLIP_TOP_BOTTOM)
        name = os.path.splitext(os.path.basename(path))[0]
        name = name.replace("_1024", "").replace("_256", "")
        with open(os.path.join(out_dir, name + ".rgba"), "wb") as f:
            f.write(struct.pack("<II", size, size))
            f.write(img.tobytes())
        print(f"{path} -> {name}.rgba ({size}x{size})")


if __name__ == "__main__":
    main()
