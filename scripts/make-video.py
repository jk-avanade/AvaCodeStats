# /// script
# requires-python = ">=3.11"
# dependencies = ["pillow>=10", "imageio-ffmpeg>=0.5"]
# ///
"""Turns the frames from `AvaCodeStats --tour <folder>` into docs/tour.mp4 and docs/tour.gif.

    dotnet run --project src/AvaCodeStats -- --tour artifacts/tour
    uv run scripts/make-video.py artifacts/tour

Each frame is held for its time in tour.json, cross-fades in where marked, and carries a caption. The tray
popup (popup.png) is laid over the dimmed last frame.
"""
import json
import subprocess
import sys
from pathlib import Path

import imageio_ffmpeg
from PIL import Image, ImageDraw, ImageEnhance, ImageFont

FPS = 30
FADE = 0.4  # seconds
GIF_WIDTH = 960
GIF_FPS = 12

src = Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/tour")
out = Path(__file__).resolve().parent.parent / "docs"
shots = json.loads((src / "tour.json").read_text(encoding="utf-8"))
size = Image.open(src / shots[0]["File"]).size
font = ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf", round(size[1] / 46))


def caption(img: Image.Image, text: str | None) -> Image.Image:
    if not text:
        return img
    img = img.copy()
    d = ImageDraw.Draw(img, "RGBA")
    l, t, r, b = d.textbbox((0, 0), text, font=font)
    pad_x, pad_y = font.size, font.size * 0.55
    w, h = r - l + 2 * pad_x, b - t + 2 * pad_y
    x, y = (size[0] - w) / 2, size[1] - h - font.size * 1.6
    d.rounded_rectangle((x, y, x + w, y + h), radius=h / 2, fill=(20, 20, 20, 225), outline=(255, 255, 255, 40))
    d.text((x + pad_x - l, y + pad_y - t), text, font=font, fill=(255, 255, 255, 255))
    return img


def frame(shot: dict, previous: Image.Image | None) -> Image.Image:
    img = Image.open(src / shot["File"]).convert("RGB")
    if shot["File"] == "popup.png" and previous is not None:
        # The popup over the dimmed dashboard, scaled to fit.
        base = ImageEnhance.Brightness(previous).enhance(0.45)
        scale = min(1.0, (size[1] * 0.92) / img.height)
        img = img.resize((round(img.width * scale), round(img.height * scale)), Image.LANCZOS)
        base.paste(img, ((size[0] - img.width) // 2, (size[1] - img.height) // 2 - round(size[1] * 0.02)))
        img = base
    return img


ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
mp4 = out / "tour.mp4"
enc = subprocess.Popen(
    [ffmpeg, "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{size[0]}x{size[1]}",
     "-r", str(FPS), "-i", "-", "-c:v", "libx264", "-preset", "slow", "-crf", "22", "-pix_fmt", "yuv420p",
     "-movflags", "+faststart", str(mp4)],
    stdin=subprocess.PIPE)

last_raw: Image.Image | None = None
last_shown: Image.Image | None = None
for shot in shots:
    raw = frame(shot, last_raw)
    shown = caption(raw, shot.get("Caption"))
    hold = round(shot["Seconds"] * FPS)
    if shot["Fade"] and last_shown is not None:
        steps = round(FADE * FPS)
        for i in range(1, steps + 1):
            enc.stdin.write(Image.blend(last_shown, shown, i / steps).tobytes())
        hold = max(1, hold - steps)
    for _ in range(max(1, hold)):
        enc.stdin.write(shown.tobytes())
    last_raw, last_shown = raw, shown

enc.stdin.close()
if enc.wait() != 0:
    sys.exit("ffmpeg failed to encode the MP4")

# GIF for the README (GitHub plays GIFs inline, not MP4s from the repo): one palette for the whole clip.
gif = out / "tour.gif"
subprocess.run(
    [ffmpeg, "-y", "-loglevel", "error", "-i", str(mp4), "-filter_complex",
     f"fps={GIF_FPS},scale={GIF_WIDTH}:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=192:stats_mode=diff[p];"
     "[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle", str(gif)],
    check=True)

for f in (mp4, gif):
    print(f"{f}  {f.stat().st_size / 1e6:.1f} MB")
