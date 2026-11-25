# /// script
# requires-python = ">=3.11"
# dependencies = ["pillow>=10", "imageio-ffmpeg>=0.5"]
# ///
"""Cuts the shots from `AvaCodeStats --tour <folder>` into the showcase video: docs/tour.mp4 and docs/tour.gif.

    dotnet run --project src/AvaCodeStats -- --tour artifacts/tour
    uv run scripts/make-video.py artifacts/tour

The window floats on a moving brand background; the camera zooms to the parts tour.json locates (the pace
graph, the profile switch, the ring), with titles, stamps and punch-in cuts.
"""
import json
import math
import subprocess
import sys
from functools import lru_cache
from pathlib import Path

import imageio_ffmpeg
from PIL import Image, ImageDraw, ImageFilter, ImageFont

W, H, FPS = 1920, 1080, 30
GIF_WIDTH, GIF_FPS = 880, 12
INK = (12, 12, 18)
MAGENTA, ORANGE = (206, 5, 105), (255, 88, 0)
PINK_TEXT, ORANGE_TEXT = (255, 70, 150), (255, 130, 40)
FONTS = "C:/Windows/Fonts/"
BLACK, BOLD, SEMI, REG = "seguibl.ttf", "segoeuib.ttf", "seguisb.ttf", "segoeui.ttf"
MINUS = "\u2212"

src = Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts/tour")
out_dir = Path(__file__).resolve().parent.parent / "docs"
manifest = json.loads((src / "tour.json").read_text(encoding="utf-8"))


# ---------- timing ----------

def clamp(x, lo=0.0, hi=1.0):
    return max(lo, min(hi, x))


def span(t, a, b):
    return clamp((t - a) / (b - a))


def ease(t):
    t = clamp(t)
    return 4 * t ** 3 if t < 0.5 else 1 - (-2 * t + 2) ** 3 / 2


def ease_out(t):
    return 1 - (1 - clamp(t)) ** 3


def back(t, s=1.9):
    t = clamp(t) - 1
    return 1 + (s + 1) * t ** 3 + s * t ** 2


def lerp(a, b, t):
    return a + (b - a) * t


# ---------- background ----------

def make_background():
    small = Image.new("RGB", (240, 136), INK)
    d = ImageDraw.Draw(small, "RGBA")
    d.ellipse((-50, -60, 110, 80), fill=MAGENTA + (120,))
    d.ellipse((140, 60, 300, 200), fill=ORANGE + (100,))
    d.ellipse((70, 95, 170, 175), fill=(98, 80, 214, 60))
    return small.filter(ImageFilter.GaussianBlur(20)).resize((2300, 1300), Image.BICUBIC)


BG = make_background()


BG_STILL = BG.crop((190, 110, 190 + W, 110 + H)).convert("RGBA")


def background(t):
    # Still: a moving background changes every pixel of every frame and triples the GIF.
    return BG_STILL.copy()


SCRIM = Image.new("RGBA", (W, H))
for y in range(H - 420, H):
    a = int(225 * ((y - (H - 420)) / 420) ** 1.4)
    ImageDraw.Draw(SCRIM).line([(0, y), (W, y)], fill=(8, 8, 12, a))


# ---------- shots and camera ----------

class Shot:
    def __init__(self, name, width=1580, center=(W / 2, H / 2 + 6), radius=26):
        info = manifest[name]
        img = Image.open(src / info["File"]).convert("RGBA")
        mask = Image.new("L", img.size, 0)
        ImageDraw.Draw(mask).rounded_rectangle((0, 0, img.width - 1, img.height - 1), radius=radius, fill=255)
        img.putalpha(mask)
        self.img = img
        self.k = width / img.width
        self.w, self.h = width, img.height * self.k
        self.x, self.y = center[0] - self.w / 2, center[1] - self.h / 2
        self.rects = info["Rects"]
        self.shadow = shadow_sprite(round(self.w), round(self.h))

    def world(self, part):
        x, y, w, h = self.rects[part]
        return self.x + x * self.k, self.y + y * self.k, w * self.k, h * self.k


@lru_cache(maxsize=8)
def shadow_sprite(w, h, margin=110):
    m = Image.new("L", (w + 2 * margin, h + 2 * margin), 0)
    ImageDraw.Draw(m).rounded_rectangle((margin, margin + 18, margin + w, margin + h + 18), radius=30, fill=200)
    m = m.filter(ImageFilter.GaussianBlur(38))
    sprite = Image.new("RGBA", m.size, (0, 0, 0, 0))
    sprite.putalpha(m)
    return sprite


class Cam:
    """Shows the world centred on (x, y) at zoom z (output pixels per world pixel)."""

    def __init__(self, x=W / 2, y=H / 2, z=1.0):
        self.x, self.y, self.z = x, y, z

    def to_out(self, wx, wy):
        return (wx - self.x) * self.z + W / 2, (wy - self.y) * self.z + H / 2

    def mix(self, other, t):
        return Cam(lerp(self.x, other.x, t), lerp(self.y, other.y, t), math.exp(lerp(math.log(self.z), math.log(other.z), t)))

    def zoomed(self, f):
        return Cam(self.x, self.y, self.z * f)


FULL = Cam()


def fit(rect, pad=1.12, zmax=2.4):
    x, y, w, h = rect
    return Cam(x + w / 2, y + h / 2, min(zmax, W / (w * pad), H / (h * pad)))


def union(*rects):
    x0 = min(r[0] for r in rects)
    y0 = min(r[1] for r in rects)
    x1 = max(r[0] + r[2] for r in rects)
    y1 = max(r[1] + r[3] for r in rects)
    return x0, y0, x1 - x0, y1 - y0


def blit(out, img, wx, wy, ww, wh, cam):
    dx, dy = cam.to_out(wx, wy)
    dw, dh = ww * cam.z, wh * cam.z
    x0, y0, x1, y1 = max(0, dx), max(0, dy), min(W, dx + dw), min(H, dy + dh)
    if x1 - x0 < 1 or y1 - y0 < 1:
        return
    sx, sy = img.width / dw, img.height / dh
    box = ((x0 - dx) * sx, (y0 - dy) * sy, (x1 - dx) * sx, (y1 - dy) * sy)
    piece = img.resize((max(1, round(x1 - x0)), max(1, round(y1 - y0))), Image.BICUBIC, box=box, reducing_gap=2.0)
    out.alpha_composite(piece, (round(x0), round(y0)))


def draw_shot(out, shot, cam):
    m = 110
    blit(out, shot.shadow, shot.x - m, shot.y - m, shot.w + 2 * m, shot.h + 2 * m, cam)
    blit(out, shot.img, shot.x, shot.y, shot.w, shot.h, cam)


def glow_box(out, rect, cam, t):
    """An animated highlight around a world rect."""
    x, y, w, h = rect
    (ax, ay), (bx, by) = cam.to_out(x, y), cam.to_out(x + w, y + h)
    grow = 22 * (1 - ease_out(t / 0.45)) + 6
    layer = Image.new("RGBA", (W, H))
    d = ImageDraw.Draw(layer)
    r = (by - ay) / 2 + grow
    for width, alpha in ((26, 30), (16, 60), (9, 120), (4, 255)):
        d.rounded_rectangle((ax - grow, ay - grow, bx + grow, by + grow), radius=r, outline=ORANGE_TEXT + (int(alpha * clamp(t / 0.15)),), width=width)
    out.alpha_composite(layer)


# ---------- text ----------

@lru_cache(maxsize=None)
def font(name, size):
    return ImageFont.truetype(FONTS + name, size)


def gradient(size, a=PINK_TEXT, b=ORANGE_TEXT):
    ramp = Image.linear_gradient("L").rotate(90, expand=True).resize(size)
    return Image.composite(Image.new("RGBA", size, b + (255,)), Image.new("RGBA", size, a + (255,)), ramp)


@lru_cache(maxsize=None)
def text_sprite(text, face, size, fill="white", tracking=0):
    f = font(face, size)
    widths = [f.getlength(c) + tracking for c in text] if tracking else None
    width = int(sum(widths)) if widths else int(f.getlength(text))
    asc, desc = f.getmetrics()
    pad = size // 2
    mask = Image.new("L", (width + 2 * pad, asc + desc + 2 * pad), 0)
    d = ImageDraw.Draw(mask)
    if widths:
        x = pad
        for c, cw in zip(text, widths):
            d.text((x, pad), c, font=f, fill=255)
            x += cw
    else:
        d.text((pad, pad), text, font=f, fill=255)
    color = gradient(mask.size) if fill == "gradient" else Image.new("RGBA", mask.size, fill)
    shadow = Image.new("RGBA", mask.size, (0, 0, 0, 0))
    shadow.putalpha(mask.filter(ImageFilter.GaussianBlur(max(2, size / 9))).point(lambda a: a * 0.75))
    sprite = Image.new("RGBA", mask.size, (0, 0, 0, 0))
    sprite.alpha_composite(shadow, (0, max(2, size // 22)))
    color.putalpha(mask)
    sprite.alpha_composite(color)
    return sprite


def put(out, text, face, size, pos, fill="white", alpha=1.0, scale=1.0, anchor="c", tracking=0):
    if alpha <= 0.01:
        return
    sp = text_sprite(text, face, size, fill, tracking)
    if abs(scale - 1) > 0.002:
        sp = sp.resize((max(1, round(sp.width * scale)), max(1, round(sp.height * scale))), Image.BICUBIC)
    if alpha < 0.999:
        sp = sp.copy()
        sp.putalpha(sp.getchannel("A").point(lambda a: a * alpha))
    pad = size * scale / 2
    x, y = pos
    if anchor == "c":
        x, y = x - sp.width / 2, y - sp.height / 2
    else:  # "l": left edge of the glyphs, vertical centre
        x, y = x - pad, y - sp.height / 2
    out.alpha_composite(sp, (round(x), round(y)))


def title(out, t, kicker, headline, start=0.25, end=None):
    """Kicker and headline over a scrim at the bottom left, sliding up."""
    a = ease_out(span(t, start, start + 0.35))
    if end is not None:
        a *= 1 - span(t, end - 0.2, end)
    if a <= 0:
        return
    scrim = SCRIM.copy()
    scrim.putalpha(scrim.getchannel("A").point(lambda v: v * a))
    out.alpha_composite(scrim)
    lift = 30 * (1 - a)
    put(out, kicker, BLACK, 34, (110, H - 196 + lift), "gradient", a, anchor="l", tracking=6)
    put(out, headline, BLACK, 72, (106, H - 122 + lift), "white", a, anchor="l")


def flash(out, t, strength=0.55, length=0.2):
    if t < length:
        out.alpha_composite(Image.new("RGBA", (W, H), (255, 255, 255, int(255 * strength * (1 - t / length)))))


def punch(t, amount=0.08, length=0.4):
    return 1 + amount * (1 - ease_out(t / length))


# ---------- the ring (brand mark) ----------

def ring(size, used, pace):
    s = size * 2
    im = Image.new("RGBA", (s, s))
    d = ImageDraw.Draw(im)
    w = int(s * 0.13)
    box = (w / 2, w / 2, s - w / 2, s - w / 2)
    d.arc(box, 0, 360, fill=(255, 255, 255, 38), width=w)

    def arc(a, b, color):
        if b - a > 0.001:
            d.arc(box, -90 + 360 * a, -90 + 360 * b, fill=color, width=w)

    arc(0, min(used, pace), (245, 245, 250, 255))
    if used > pace:
        arc(pace, used, ORANGE + (255,))
    # Pace notch
    ang = math.radians(-90 + 360 * pace)
    c, r0, r1 = s / 2, s / 2 - w * 1.25, s / 2 + w * 0.05
    d.line([(c + r0 * math.cos(ang), c + r0 * math.sin(ang)), (c + r1 * math.cos(ang), c + r1 * math.sin(ang))], fill=(255, 255, 255, 255), width=max(4, w // 4))
    return im.resize((size, size), Image.LANCZOS)


# ---------- scenes ----------

shots = {name: Shot(name) for name in manifest if name != "popup"}
popup = Image.open(src / manifest["popup"]["File"]).convert("RGBA")


def brand(out, t, size, cx, cy, fill_to=0.82, pace=0.62, sweep=(0.15, 1.35)):
    used = fill_to * ease(span(t, *sweep))
    rg = ring(size, used, pace)
    sc = back(span(t, 0, 0.5))
    if sc > 0.02:
        rg = rg.resize((max(1, round(size * sc)),) * 2, Image.BICUBIC)
        out.alpha_composite(rg, (round(cx - rg.width / 2), round(cy - rg.height / 2)))


def intro(t, d):
    out = background(t)
    brand(out, t, 250, 560, H / 2 - 10)
    a = ease_out(span(t, 0.45, 0.85))
    put(out, "AvaCodeStats", BLACK, 128, (730 - 40 * (1 - a), H / 2 - 52), "white", a, anchor="l")
    b = ease_out(span(t, 0.9, 1.3))
    put(out, "Stay on pace with every AI coding subscription", SEMI, 40, (736 - 30 * (1 - b), H / 2 + 62), (210, 210, 220, 255), b, anchor="l")
    return out


def hook(t, d):
    out = background(t + 3)
    words = [("Copilot.", (150, 130, 255, 255)), ("Claude.", ORANGE_TEXT + (255,)), ("Codex.", (90, 160, 255, 255))]
    beat = 0.42
    i = int(t // beat)
    if i < 3:
        lt = t - i * beat
        word, color = words[i]
        put(out, word, BLACK, 240, (W / 2, H / 2), color, 1, scale=lerp(1.45, 1, ease_out(lt / 0.14)))
        flash(out, lt, 0.35, 0.12)
    else:
        lt = t - 3 * beat
        put(out, "Are you on pace?", BLACK, 150, (W / 2, H / 2), "gradient", ease_out(lt / 0.2), scale=lerp(0.86, 1, back(lt / 0.45)))
        flash(out, lt, 0.4, 0.15)
    return out


def pace_graph(t, d):
    s = shots["pace-all"]
    x, y, w, h = s.world("chart")
    chart = fit((x, y, w, h), 1.06)
    busy = fit((x + 0.5 * w, y + 0.34 * h, 0.5 * w, 0.64 * h), 1.0, zmax=3.4)  # where the recent readings are
    busy.x = min(busy.x, x + w + 6 - W / (2 * busy.z))  # keep the chart's right edge in view, not past it
    cam = FULL.zoomed(0.96).mix(chart, ease(span(t, 0.5, 1.4))).mix(busy, ease(span(t, 2.0, 3.3))).zoomed(punch(t))
    out = background(t)
    draw_shot(out, s, cam)
    title(out, t, "THE PACE GRAPH", f"Over (+) or under ({MINUS}) pace, over time", start=1.1)
    flash(out, t)
    return out


def pace_table(t, d):
    s = shots["pace-table"]
    x, y, w, h = s.world("rows")
    cam = fit((x, y - 30, w, 330), 1.04).zoomed(punch(t) * (1 + 0.04 * span(t, 0, d)))
    out = background(t)
    draw_shot(out, s, cam)
    title(out, t, "EVERY QUOTA", f"{MINUS}100% to +100%, side by side", start=0.3)
    flash(out, t)
    return out


def profiles(t, d):
    phases = [("pace-work", "work", "WORK"), ("pace-personal", "personal", "PERSONAL"), ("pace-all", "all", "ALL")]
    beat = 1.15
    i = min(3, int(t // beat))
    lt = t - i * beat
    name, seg, word = phases[min(i, 2)]
    s = shots[name]
    frame = union(s.world("hero"), s.world("switch"), s.world("chart"))
    base = fit(frame, 1.04)
    out = background(t)
    if i < 3:
        draw_shot(out, s, base.zoomed(punch(lt, 0.06, 0.3)))
        glow_box(out, s.world(seg), base.zoomed(punch(lt, 0.06, 0.3)), lt)
        out.alpha_composite(SCRIM)
        put(out, word, BLACK, 190, (W / 2, H - 150), "gradient", 1, scale=lerp(1.5, 1, ease_out(lt / 0.16)), tracking=8)
        flash(out, lt, 0.4, 0.14)
    else:
        cam = base.mix(FULL.zoomed(0.96), ease(lt / 0.6))
        draw_shot(out, s, cam)
        glow_box(out, s.world("switch"), cam, lt)
        title(out, lt, "WORK  \u00b7  PERSONAL  \u00b7  ALL", "Apart or together, one click", start=0.15)
    return out


def burn(t, d):
    s = shots["burn-all"]
    cam = FULL.zoomed(0.96).mix(fit(s.world("chart"), 1.06), ease(span(t, 0.35, 1.3))).zoomed(punch(t))
    out = background(t)
    draw_shot(out, s, cam)
    title(out, t, "BURN", "Every quota, one chart", start=0.9)
    flash(out, t)
    return out


def drill(t, d):
    s = shots["quota"]
    target = fit(union(s.world("ring"), s.world("kpis")), 1.08)
    cam = target.zoomed(0.8).mix(target, ease(span(t, 0, 0.9))).zoomed(punch(t))
    out = background(t)
    draw_shot(out, s, cam)
    title(out, t, "DRILL IN", "Budget per day, projection, history", start=0.4)
    flash(out, t)
    return out


def quick_cuts(t, d):
    cuts = [("limits", "LIMITS", "Grouped by subscription"), ("cost", "COST", "API-price estimate from local logs"), ("tokens", "TOKENS", "Per model, tool and day")]
    beat = d / 3
    i = min(2, int(t // beat))
    lt = t - i * beat
    name, kicker, headline = cuts[i]
    s = shots[name]
    out = background(t)
    draw_shot(out, s, FULL.zoomed(0.96 * punch(lt, 0.07, 0.3) * (1 + 0.03 * lt / beat)))
    title(out, 1, kicker, headline, start=0)
    flash(out, lt, 0.45, 0.14)
    return out


def tray(t, d):
    out = background(t)
    draw_shot(out, shots["limits"], FULL.zoomed(0.96 * (1 + 0.02 * t / d)))
    out.alpha_composite(Image.new("RGBA", (W, H), (6, 6, 10, int(170 * ease_out(t / 0.4)))))
    ph = 980
    pw = round(popup.width * ph / popup.height)
    p = popup.resize((pw, ph), Image.LANCZOS)
    mask = Image.new("L", p.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, pw - 1, ph - 1), radius=18, fill=255)
    p.putalpha(mask)
    y = lerp(H + 40, (H - ph) / 2, back(span(t, 0.1, 0.75), 1.2))
    x = 1180
    sh = shadow_sprite(pw, ph)
    out.alpha_composite(sh, (round(x - 110), round(y - 110)))
    out.alpha_composite(p, (round(x), round(y)))
    a = ease_out(span(t, 0.5, 0.9))
    put(out, "IN YOUR TRAY", BLACK, 34, (150, H / 2 - 90), "gradient", a, anchor="l", tracking=6)
    put(out, "Every subscription,", BLACK, 76, (146, H / 2 - 10), "white", a, anchor="l")
    put(out, "one click away", BLACK, 76, (146, H / 2 + 80), "white", a, anchor="l")
    return out


def outro(t, d):
    out = background(t + 40)
    brand(out, t, 210, 640, H / 2 - 70, sweep=(0.1, 1.0))
    a = ease_out(span(t, 0.3, 0.7))
    put(out, "AvaCodeStats", BLACK, 112, (785 - 40 * (1 - a), H / 2 - 112), "white", a, anchor="l")
    b = ease_out(span(t, 0.6, 1.0))
    put(out, "github.com/jk-avanade/AvaCodeStats", SEMI, 40, (790, H / 2 - 10), "gradient", b, anchor="l")
    c = ease_out(span(t, 1.0, 1.4))
    put(out, "GitHub Copilot  \u00b7  Claude Code  \u00b7  Codex  \u00b7  Antigravity  \u00b7  Grok", REG, 30, (W / 2, H / 2 + 170), (190, 190, 200, 255), c)
    return out


SCENES = [
    (intro, 2.6), (hook, 2.3), (pace_graph, 4.8), (pace_table, 2.8), (profiles, 5.2),
    (burn, 3.0), (drill, 2.8), (quick_cuts, 2.7), (tray, 3.0), (outro, 3.4),
]

if "--frame" in sys.argv:
    # One frame at full size: --frame <scene> <seconds> <out.png>
    i = sys.argv.index("--frame")
    scene, d = next((s, d) for s, d in SCENES if s.__name__ == sys.argv[i + 1])
    scene(float(sys.argv[i + 2]), d).convert("RGB").save(sys.argv[i + 3])
    sys.exit()

if "--stills" in sys.argv:
    # A contact sheet of a few moments per scene, to check layouts without encoding.
    picks = [(s, d, x) for s, d in SCENES for x in (0.3, 0.75)]
    sheet = Image.new("RGB", (960 * 2, 540 * math.ceil(len(picks) / 2)))
    for n, (scene, d, x) in enumerate(picks):
        sheet.paste(scene(d * x, d).convert("RGB").resize((960, 540), Image.LANCZOS), ((n % 2) * 960, (n // 2) * 540))
    sheet.save(sys.argv[sys.argv.index("--stills") + 1])
    sys.exit()

# ---------- encode ----------

ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
mp4 = out_dir / "tour.mp4"
enc = subprocess.Popen(
    [ffmpeg, "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}", "-r", str(FPS), "-i", "-",
     "-c:v", "libx264", "-preset", "slow", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(mp4)],
    stdin=subprocess.PIPE)
total = 0
for scene, duration in SCENES:
    frames = round(duration * FPS)
    for f in range(frames):
        enc.stdin.write(scene(f / FPS, duration).convert("RGB").tobytes())
    total += frames
    print(f"{scene.__name__:<12} {duration:.1f}s", flush=True)
enc.stdin.close()
if enc.wait() != 0:
    sys.exit("ffmpeg failed to encode the MP4")

# GIF for the README (GitHub plays GIFs inline, not MP4s from the repo): one palette for the whole clip.
gif = out_dir / "tour.gif"
subprocess.run(
    [ffmpeg, "-y", "-loglevel", "error", "-i", str(mp4), "-filter_complex",
     f"fps={GIF_FPS},scale={GIF_WIDTH}:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];"
     "[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle", str(gif)],
    check=True)
print(f"{total / FPS:.1f}s")
for f in (mp4, gif):
    print(f"{f}  {f.stat().st_size / 1e6:.1f} MB")
