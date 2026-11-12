"""Generate the README visuals (orbit, features, free) in zh/en x light/dark.

Usage:
    python3 branding/readme/generate.py --fonts <dir> [--out <dir>]

Requires `fonttools` and `brotli` (pip install fonttools brotli) and two
SIL OFL fonts in <dir>, downloaded from https://github.com/google/fonts:
    Inter.ttf       ofl/inter/Inter[opsz,wght].ttf
    NotoSansSC.ttf  ofl/notosanssc/NotoSansSC[wght].ttf
Only the glyphs each SVG uses are embedded (WOFF2), so the visuals render
identically on every OS. Edit COPY / THEMES, then re-run.
"""
import argparse, base64, io, math, os, random
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont
from fontTools import subset

HERE = os.path.dirname(os.path.abspath(__file__))
W = 1280

THEMES = {
    "light": dict(text="#121317", muted="#5F6368", rule="#E3E5E8",
                  tile="#FFFFFF", card="#FFFFFF", cardOp="0.86", cardRule="#ECEEF1", aurora="0.55",
                  auroraColors=("#2DD4BF", "#60A5FA", "#A78BFA", "#FCD34D")),
    "dark": dict(text="#E8EAED", muted="#9AA0A6", rule="#2A2F36",
                 tile="#0A0C10", card="#111418", cardOp="0.82", cardRule="#23272E", aurora="0.5",
                 auroraColors=("#14B8A6", "#3B82F6", "#8B5CF6", "#EC4899")),
}

ICONS = {
    "free": '<circle cx="12" cy="12" r="9"/><path d="M8 12.5l2.6 2.6L16 9.6"/>',
    "bolt": '<path d="M13 2.5L4.5 13.5H11l-1 8 8.5-11H12z"/>',
    "layers": '<path d="M12 3l9 5-9 5-9-5z"/><path d="M3 12.5l9 5 9-5"/><path d="M3 16.5l9 5 9-5"/>',
    "hex": '<path d="M12 2.5l8.5 4.9v9.2L12 21.5l-8.5-4.9V7.4z"/><circle cx="12" cy="12" r="3.2"/>',
    "shield": '<path d="M12 3l8 3v6c0 4.6-3.4 8.2-8 9-4.6-.8-8-4.4-8-9V6z"/><path d="M8.6 12l2.4 2.4 4.4-4.6"/>',
    "plug": '<rect x="3.5" y="3.5" width="7" height="7" rx="1.6"/><rect x="13.5" y="3.5" width="7" height="7" rx="1.6"/><rect x="3.5" y="13.5" width="7" height="7" rx="1.6"/><path d="M17 13.5v7M13.5 17h7"/>',
}

COPY = {
    "zh": dict(
        orbit_caption="一条流水线，覆盖每一个目标",
        targets=["Kubernetes", "Linux", "Windows", "SSH", "AI Agent"],
        heading=["为团队打造的", "企业级发布能力"],
        intro=["从单个服务到成百上千个目标，", "用同一套模型完成发布、审批与审计。"],
        features=[
            ("free", "免费自托管", ["不限项目、用户与目标", "没有任何授权费用"]),
            ("bolt", "开箱即用", ["一个服务、一个数据库", "Agent 一行命令安装"]),
            ("layers", "统一发布模型", ["环境、生命周期与 Release", "一套概念贯穿所有目标"]),
            ("hex", "云原生", ["Kubernetes Agent 与 API 双模式", "原生 Helm、Kustomize、YAML"]),
            ("shield", "企业级治理", ["RBAC、人工审批与审计", "敏感变量加密与日志脱敏"]),
            ("plug", "可扩展", ["新目标与新动作独立接入", "无需改动部署流水线"]),
        ],
        free_title="永久免费。", free_sub=["MIT 协议开源，全部功能可用。", "规模增长，不增加软件成本。"],
        cols=("Squid", "商业发布平台"),
        rows=[("软件费用", "免费", "按档位订阅"), ("项目与用户", "不限", "随档位限制"), ("部署目标", "不限", "按节点计费")],
    ),
    "en": dict(
        orbit_caption="One pipeline. Every target.",
        targets=["Kubernetes", "Linux", "Windows", "SSH", "AI Agent"],
        heading=["Built for teams.", "Enterprise-grade by default."],
        intro=["From one service to thousands", "of targets, one model for releases,", "approvals, and audit."],
        features=[
            ("free", "Free and self-hosted", ["Unlimited projects, users,", "and targets. No license fees."]),
            ("bolt", "Ready to use", ["One service, one database.", "Agents install in one command."]),
            ("layers", "One release model", ["Environments, lifecycles,", "releases. One model throughout."]),
            ("hex", "Cloud native", ["Kubernetes Agent and API modes.", "Helm, Kustomize, YAML built in."]),
            ("shield", "Enterprise governance", ["RBAC, approvals, and audit.", "Secrets encrypted and masked."]),
            ("plug", "Extensible", ["New targets and actions plug in", "without touching the pipeline."]),
        ],
        free_title="Free. For good.", free_sub=["MIT-licensed, every feature included.", "Scale up without adding software cost."],
        cols=("Squid", "Commercial platforms"),
        rows=[("Software cost", "Free", "Tiered subscription"), ("Projects & users", "Unlimited", "Limited by tier"), ("Targets", "Unlimited", "Billed per node")],
    ),
}


# ── Fonts ────────────────────────────────────────────────────────────────

class Fonts:
    """Static instances of the variable fonts, subset per SVG and embedded as WOFF2."""

    FACES = {  # family -> (file, axes per weight)
        "SqDisplay": ("Inter.ttf", lambda w: {"wght": w, "opsz": 32}),
        "SqText": ("Inter.ttf", lambda w: {"wght": w, "opsz": 14}),
        "SqCJK": ("NotoSansSC.ttf", lambda w: {"wght": w}),
    }

    def __init__(self, font_dir, cache_dir):
        self.font_dir, self.cache_dir, self.metrics = font_dir, cache_dir, {}
        os.makedirs(cache_dir, exist_ok=True)

    def instance_path(self, family, weight):
        path = os.path.join(self.cache_dir, f"{family}-{weight}.ttf")
        if os.path.exists(path): return path

        file, axes = self.FACES[family]
        inst = instantiateVariableFont(TTFont(os.path.join(self.font_dir, file)), axes(weight))
        inst.save(path)
        return path

    def face_css(self, family, weight, chars):
        font = TTFont(self.instance_path(family, weight))
        sub = subset.Subsetter(subset.Options(flavor="woff2", layout_features=["kern", "liga", "calt"], name_IDs=[0, 1, 2, 13, 14]))
        sub.populate(text=chars)
        sub.subset(font)
        font.flavor = "woff2"
        font.recalcTimestamp = False
        font["head"].modified = font["head"].created  # deterministic output across runs
        buf = io.BytesIO()
        font.save(buf)
        data = base64.b64encode(buf.getvalue()).decode()
        return f"@font-face{{font-family:{family};font-weight:{weight};src:url(data:font/woff2;base64,{data}) format('woff2')}}"

    def width(self, family, weight, text, size, spacing=0.0):
        """Approximate advance width (no kerning) — used to assert copy fits its column."""
        key = (family, weight)
        if key not in self.metrics:
            latin, cjk = TTFont(self.instance_path(family, weight)), TTFont(self.instance_path("SqCJK", weight))
            self.metrics[key] = [(f.getBestCmap(), f["hmtx"].metrics, f["head"].unitsPerEm) for f in (latin, cjk)]
        total = 0.0
        for ch in text:
            for cmap, hmtx, upm in self.metrics[key]:
                if ord(ch) in cmap:
                    total += hmtx[cmap[ord(ch)]][0] / upm * size + spacing
                    break
        return total


class Canvas:
    """Collects SVG parts and records which glyphs each font face needs."""

    def __init__(self, fonts, theme):
        self.fonts, self.T, self.parts, self.defs, self.used = fonts, THEMES[theme], [], [], {}

    def text(self, x, y, s, size, family="SqText", weight=400, fill=None, anchor="start", spacing=0, extra=""):
        self.used.setdefault((family, weight), set()).update(s)
        self.used.setdefault(("SqCJK", weight), set()).update(s)
        fill = fill or self.T["text"]
        self.parts.append(f'<text x="{x:.1f}" y="{y:.1f}" font-family="{family},SqCJK,sans-serif" font-size="{size}" '
                          f'font-weight="{weight}" letter-spacing="{spacing}" fill="{fill}" text-anchor="{anchor}"{extra}>{esc(s)}</text>')

    def fit(self, s, size, max_width, family="SqText", weight=400, spacing=0):
        w = self.fonts.width(family, weight, s, size, spacing)
        assert w <= max_width, f"'{s}' is {w:.0f}px, exceeds {max_width}px"

    def render(self, h, label):
        css = "".join(self.fonts.face_css(f, w, "".join(sorted(c))) for (f, w), c in sorted(self.used.items()))
        return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{h}" viewBox="0 0 {W} {h}" fill="none" '
                f'role="img" aria-label="{esc(label)}"><title>{esc(label)}</title><defs><style>{css}</style>{"".join(self.defs)}</defs>'
                f'{"".join(self.parts)}</svg>\n')


def esc(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


def icon(name, x, y, size, color):
    s = size / 24
    return (f'<g transform="translate({x} {y}) scale({s})" fill="none" stroke="{color}" stroke-width="1.5" '
            f'stroke-linecap="round" stroke-linejoin="round">{ICONS[name]}</g>')


def orbit(fonts, lang, theme):
    c, cv = COPY[lang], Canvas(fonts, theme)
    H, cx, cy, r = 600, W / 2, 280, 168
    cv.defs.append('''
<clipPath id="tile"><rect width="1280" height="600" rx="36"/></clipPath>
<linearGradient id="ring" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#3B82F6"/><stop offset="0.5" stop-color="#14B8A6"/><stop offset="1" stop-color="#6366F1"/></linearGradient>
<linearGradient id="edge" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#BFDBFE"/><stop offset="0.5" stop-color="#99F6E4"/><stop offset="1" stop-color="#C7D2FE"/></linearGradient>
<radialGradient id="core"><stop offset="0" stop-color="#000" stop-opacity="1"/><stop offset="0.8" stop-color="#000" stop-opacity="0.9"/><stop offset="1" stop-color="#000" stop-opacity="0"/></radialGradient>
<filter id="haze" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="38"/></filter>
<filter id="glow" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="9"/></filter>
<filter id="soft" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="3"/></filter>''')
    p = cv.parts
    p.append('<g clip-path="url(#tile)"><rect width="1280" height="600" fill="#030406"/>')
    rnd = random.Random(7)
    for _ in range(90):
        p.append(f'<circle cx="{rnd.uniform(0, W):.0f}" cy="{rnd.uniform(0, H):.0f}" r="{rnd.uniform(0.6, 1.5):.1f}" fill="#FFFFFF" opacity="{rnd.uniform(0.08, 0.4):.2f}"/>')
    p.append(f'<circle cx="{cx}" cy="{cy}" r="{r}" stroke="url(#ring)" stroke-width="80" opacity="0.55" filter="url(#haze)"/>')
    p.append(f'<circle cx="{cx}" cy="{cy}" r="{r}" stroke="url(#ring)" stroke-width="20" opacity="0.9" filter="url(#glow)"/>')
    p.append(f'<circle cx="{cx}" cy="{cy}" r="{r - 14}" fill="url(#core)"/>')
    p.append(f'<circle cx="{cx}" cy="{cy}" r="{r}" stroke="url(#edge)" stroke-width="1.6" opacity="0.9"/>')
    for angle, label in zip((270, 210, 150, 330, 30), c["targets"]):
        a = math.radians(angle)
        x, y = cx + r * math.cos(a), cy + r * math.sin(a)
        p.append(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="9" fill="#E0F2FE" opacity="0.6" filter="url(#soft)"/><circle cx="{x:.1f}" cy="{y:.1f}" r="3.6" fill="#FFFFFF"/>')
        lx, ly = cx + (r + 30) * math.cos(a), cy + (r + 30) * math.sin(a) + 7
        anchor = "middle" if angle == 270 else ("end" if 90 < angle < 270 else "start")
        cv.text(lx, ly - (10 if angle == 270 else 0), label, 21, fill="#C4C9D1", anchor=anchor)
    cv.text(cx, cy + 16, "Squid", 50, "SqDisplay", 400, fill="#FFFFFF", anchor="middle", spacing=-1, extra=' opacity="0.55" filter="url(#glow)"')
    cv.text(cx, cy + 16, "Squid", 50, "SqDisplay", 400, fill="#FFFFFF", anchor="middle", spacing=-1)
    cv.text(cx, 552, c["orbit_caption"], 20, fill="#8A9099", anchor="middle", spacing=0.2)
    p.append('</g>')
    return cv.render(H, f'{c["orbit_caption"]} {", ".join(c["targets"])}')


def features(fonts, lang, theme):
    c, cv = COPY[lang], Canvas(fonts, theme)
    T, col, gap = cv.T, (W - 2 * 56) / 3, 56
    intro_x = 2 * (col + gap)                         # intro sits on the third grid column
    baseline = 52 + 58 * (len(c["heading"]) - 1)       # heading and intro share their last baseline
    for i, line in enumerate(c["heading"]):
        cv.fit(line, 44, intro_x - gap, "SqDisplay", 400, -0.9)
        cv.text(0, 52 + i * 58, line, 44, "SqDisplay", 400, spacing=-0.9)
    for i, line in enumerate(c["intro"]):
        cv.fit(line, 20, col)
        cv.text(intro_x, baseline - (len(c["intro"]) - 1 - i) * 30, line, 20, fill=T["muted"])
    top = baseline + 56
    for i, (ic, title, lines) in enumerate(c["features"]):
        x, y = (i % 3) * (col + gap), top + (i // 3) * 236
        cv.parts.append(f'<path d="M{x:.1f} {y}H{x + col:.1f}" stroke="{T["rule"]}"/>')
        cv.parts.append(icon(ic, x, y + 36, 28, T["text"]))
        cv.text(x, y + 112, title, 25, weight=500, spacing=-0.3)
        for j, line in enumerate(lines):
            cv.fit(line, 20, col)
            cv.text(x, y + 150 + j * 30, line, 20, fill=T["muted"])
    alt = "; ".join(f'{t}: {"".join(l) if lang == "zh" else " ".join(l)}' for _, t, l in c["features"])
    return cv.render(top + 236 + 200, f'{" ".join(c["heading"])} {alt}')


def free(fonts, lang, theme):
    c, cv = COPY[lang], Canvas(fonts, theme)
    T, H, tx = cv.T, 520, 600
    cv.fit(c["free_title"], 60, 540, "SqDisplay", 400, -1.4)
    cv.text(0, 236, c["free_title"], 60, "SqDisplay", 400, spacing=-1.4)
    for j, line in enumerate(c["free_sub"]):
        cv.fit(line, 21, 540)
        cv.text(0, 296 + j * 34, line, 21, fill=T["muted"])
    cv.defs.append(f'''<clipPath id="tile"><rect x="{tx}" width="{W - tx}" height="{H}" rx="36"/></clipPath>
<filter id="aurora" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="56"/></filter>''')
    p = cv.parts
    p.append(f'<g clip-path="url(#tile)"><rect x="{tx}" width="{W - tx}" height="{H}" fill="{T["tile"]}"/>')
    teal, blue, violet, warm = T["auroraColors"]
    p.append(f'<g filter="url(#aurora)" opacity="{T["aurora"]}">'
             f'<ellipse cx="{tx + 40}" cy="70" rx="240" ry="160" fill="{teal}"/>'
             f'<ellipse cx="{W - 40}" cy="40" rx="260" ry="150" fill="{blue}"/>'
             f'<ellipse cx="{W - 20}" cy="{H - 30}" rx="240" ry="160" fill="{violet}"/>'
             f'<ellipse cx="{tx + 60}" cy="{H - 10}" rx="230" ry="140" fill="{warm}"/></g></g>')
    cx0, cy0, cw, ch = tx + 52, 92, W - tx - 104, 336
    p.append(f'<rect x="{cx0}" y="{cy0}" width="{cw}" height="{ch}" rx="24" fill="{T["card"]}" fill-opacity="{T["cardOp"]}" stroke="{T["cardRule"]}"/>')
    lx, c1, c2 = cx0 + 36, cx0 + 280, cx0 + 460
    cv.text(c1, cy0 + 58, c["cols"][0], 17, weight=500, anchor="middle")
    cv.text(c2, cy0 + 58, c["cols"][1], 17, fill=T["muted"], anchor="middle")
    for j, (label, a, b) in enumerate(c["rows"]):
        y = cy0 + 136 + j * 76
        p.append(f'<path d="M{lx} {y - 46}H{cx0 + cw - 36}" stroke="{T["cardRule"]}"/>')
        cv.fit(label, 20, c1 - lx - 72)
        cv.text(lx, y, label, 20, fill=T["muted"])
        cv.text(c1, y, a, 21, weight=500, anchor="middle")
        cv.fit(b, 19, 2 * (cx0 + cw - 20 - c2))
        cv.text(c2, y, b, 19, fill=T["muted"], anchor="middle")
    alt = f'{c["free_title"]} ' + "; ".join(f"{l}: {a} vs {b}" for l, a, b in c["rows"])
    return cv.render(H, alt)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fonts", required=True)
    ap.add_argument("--out", default=HERE)
    args = ap.parse_args()
    fonts = Fonts(args.fonts, os.path.join(args.fonts, ".instances"))
    for lang in COPY:
        for theme in THEMES:
            for name, fn in (("orbit", orbit), ("features", features), ("free", free)):
                with open(os.path.join(args.out, f"{name}-{lang}-{theme}.svg"), "w", encoding="utf-8") as f:
                    f.write(fn(fonts, lang, theme))
    print("ok")


if __name__ == "__main__":
    main()
