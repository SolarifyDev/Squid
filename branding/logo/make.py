"""Generate the Squid mark: a flat squid silhouette (rocket nose, fins, five arms) on a sky-blue tile.

Usage: python3 branding/logo/make.py [out_dir]   (default: ./out)
"""
import math, pathlib, sys
OUT = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "out"); OUT.mkdir(parents=True, exist_ok=True)

# Mantle: pointed rocket nose, swept squid fins, plump body.
MANTLE = ("M 256 30 C 290 54 316 90 330 128 C 372 122 404 146 414 184 C 390 190 366 200 350 214 "
          "C 358 240 358 280 352 304 C 346 336 316 350 256 350 C 196 350 166 336 160 304 C 154 280 154 240 162 214 C 146 200 122 190 98 184 C 108 146 140 122 182 128 C 196 90 222 54 256 30 Z")

def bez(p, t):
    a, b, c, d = p
    mt = 1 - t
    return tuple(mt**3*a[i] + 3*mt*mt*t*b[i] + 3*mt*t*t*c[i] + t**3*d[i] for i in (0, 1))

def arm(p, w0, w1, n=48):
    """Tapered arm along cubic centerline p with a round tip cap."""
    pts = [bez(p, i / n) for i in range(n + 1)]
    left, right = [], []
    for i, (x, y) in enumerate(pts):
        x0, y0 = pts[max(i - 1, 0)]; x1, y1 = pts[min(i + 1, n)]
        dx, dy = x1 - x0, y1 - y0; L = math.hypot(dx, dy)
        nx, ny = -dy / L, dx / L
        w = (w0 + (w1 - w0) * (i / n) ** 1.2) / 2
        left.append((x + nx * w, y + ny * w)); right.append((x - nx * w, y - ny * w))
    r = w1 / 2
    seg = " ".join(f"L {x:.1f} {y:.1f}" for x, y in left[1:])
    back = " ".join(f"L {x:.1f} {y:.1f}" for x, y in reversed(right[:-1]))
    tx, ty = pts[-1]
    cap = f"M {tx - r:.1f} {ty:.1f} a {r} {r} 0 1 0 {2 * r} 0 a {r} {r} 0 1 0 {-2 * r} 0 Z"
    return f"M {left[0][0]:.1f} {left[0][1]:.1f} {seg} L {right[-1][0]:.1f} {right[-1][1]:.1f} {back} Z {cap}"

CENTER = [((256, 290), (256, 380), (256, 430), (256, 474))]
SIDE = [(((222, 290), (218, 380), (198, 432), (176, 460)), 46, 26),
        (((190, 290), (178, 370), (126, 404), (96, 380)), 46, 24)]

def mirror(p):
    return tuple((512 - x, y) for x, y in p)

def arms():
    out = [arm(CENTER[0], 48, 28)]
    for p, w0, w1 in SIDE:
        out += [arm(p, w0, w1), arm(mirror(p), w0, w1)]
    return out

def skirt():
    return " ".join(arms())

EYES = '<ellipse cx="216" cy="262" rx="17" ry="20" fill="#000"/><ellipse cx="296" cy="262" rx="17" ry="20" fill="#000"/>'

def glyph(fill):
    return (f'<mask id="m" maskUnits="userSpaceOnUse" x="0" y="0" width="512" height="512">'
            f'<path d="{MANTLE}" fill="#fff"/><path d="{skirt()}" fill="#fff"/>{EYES}</mask>'
            f'<rect width="512" height="512" fill="{fill}" mask="url(#m)"/>')

def svg(body):
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512">{body}</svg>\n'

BRAND = "#0EA5E9"   # sky

def tile(fg, bg):
    # app-icon tile: glyph scaled into a rounded square
    return (f'<rect width="512" height="512" rx="116" fill="{bg}"/>'
            f'<g transform="translate(256 262) scale(0.74) translate(-256 -256)">{glyph(fg)}</g>')

if __name__ == "__main__":
    (OUT / "squid-mark.svg").write_text(svg(tile("#FFFFFF", BRAND)))
