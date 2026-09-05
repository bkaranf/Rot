"""Derive Rot's Windows/WebView assets from the original generated mark."""

from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image, ImageEnhance, ImageOps


ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "assets"
TRANSPARENT = (0, 0, 0, 0)


def square_rgba(source: Image.Image, size: int = 256) -> Image.Image:
    rgba = source.convert("RGBA")
    bounds = rgba.getchannel("A").getbbox()
    if bounds:
        left, top, right, bottom = bounds
        padding = max(1, round(max(right - left, bottom - top) * 0.04))
        rgba = rgba.crop((
            max(0, left - padding),
            max(0, top - padding),
            min(rgba.width, right + padding),
            min(rgba.height, bottom + padding),
        ))
    contained = ImageOps.contain(rgba, (size - 16, size - 16), Image.Resampling.LANCZOS)
    output = Image.new("RGBA", (size, size), TRANSPARENT)
    output.alpha_composite(contained, ((size - contained.width) // 2, (size - contained.height) // 2))
    return output


def save_quantized(image: Image.Image, path: Path, colors: int) -> None:
    quantized = image.convert("RGBA").quantize(
        colors=colors,
        method=Image.Quantize.FASTOCTREE,
        dither=Image.Dither.FLOYDSTEINBERG,
    )
    quantized.save(path, format="PNG", optimize=True)


def build(source_path: Path) -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    with Image.open(source_path) as source:
        if "A" not in source.getbands():
            raise ValueError("source mark must contain an alpha channel")
        color = square_rgba(source)

    # Keep small icon variants below 30 KB while preserving the silhouette and alpha.
    save_quantized(color, ASSETS / "icon-color.png", colors=48)

    gray = ImageOps.grayscale(color.convert("RGB"))
    gray = ImageEnhance.Contrast(gray).enhance(1.08).convert("RGBA")
    gray.putalpha(color.getchannel("A"))
    save_quantized(gray, ASSETS / "icon-gray.png", colors=24)

    color.save(ASSETS / "window-icon.png", format="PNG", optimize=True)
    color.save(ASSETS / "splash.png", format="PNG", optimize=True)

    rgba = color.convert("RGBA")
    rgba.save(
        ASSETS / "launcher.ico",
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (48, 48), (64, 64), (256, 256)],
    )


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: build-assets.py PATH_TO_SOURCE_MARK")
    build(Path(sys.argv[1]).resolve())
