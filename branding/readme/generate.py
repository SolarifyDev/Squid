"""Generate the README visuals: hero, feature grid, and free card in zh/en x light/dark.

Usage: python3 branding/readme/generate.py [output-dir]
Output defaults to this script's directory. Edit COPY / THEMES, then re-run.
"""
import os, sys

OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
os.makedirs(OUT, exist_ok=True)

FONT = "-apple-system, BlinkMacSystemFont, 'SF Pro Display', 'Segoe UI', 'PingFang SC', 'Hiragino Sans GB', 'Microsoft YaHei', 'Noto Sans CJK SC', Helvetica, Arial, sans-serif"
MONO = "ui-monospace, 'SF Mono', Menlo, Consolas, monospace"

THEMES = {
    "dark": dict(bg="#05080F", card="#0B1220", stroke="#1C2638", title="#F5F7FA", body="#8B98AD",
                 faint="#4A5568", pill="#0E1626", glowA="#14B8A6", glowB="#6366F1", glowOp="0.38",
                 gradA="#FFFFFF", gradB="#9FB3C8", accent="#2DD4BF", iconBg="#0F1D2B"),
    "light": dict(bg="#F7F9FC", card="#FFFFFF", stroke="#E4E9F0", title="#0A1628", body="#5B6B82",
                  faint="#A0AEC0", pill="#FFFFFF", glowA="#5EEAD4", glowB="#A5B4FC", glowOp="0.45",
                  gradA="#0A1628", gradB="#33507A", accent="#0D9488", iconBg="#ECFDF8"),
}

ICONS = {
    "free": '<circle cx="12" cy="12" r="9"/><path d="M8 12.5l2.6 2.6L16 9.6"/>',
    "bolt": '<path d="M13 2.5L4.5 13.5H11l-1 8 8.5-11H12z"/>',
    "layers": '<path d="M12 3l9 5-9 5-9-5z"/><path d="M3 12.5l9 5 9-5"/><path d="M3 16.5l9 5 9-5"/>',
    "hex": '<path d="M12 2.5l8.5 4.9v9.2L12 21.5l-8.5-4.9V7.4z"/><circle cx="12" cy="12" r="3.2"/>',
    "shield": '<path d="M12 3l8 3v6c0 4.6-3.4 8.2-8 9-4.6-.8-8-4.4-8-9V6z"/><path d="M8.6 12l2.4 2.4 4.4-4.6"/>',
    "plug": '<rect x="3.5" y="3.5" width="7" height="7" rx="1.6"/><rect x="13.5" y="3.5" width="7" height="7" rx="1.6"/><rect x="3.5" y="13.5" width="7" height="7" rx="1.6"/><path d="M17 13.5v7M13.5 17h7"/>',
    "k8s": '<path d="M12 2.5l8.5 4.9v9.2L12 21.5l-8.5-4.9V7.4z"/><circle cx="12" cy="12" r="3.2"/>',
    "linux": '<rect x="3" y="4" width="18" height="16" rx="2.5"/><path d="M7 10l3 2.5L7 15M12.5 15H17"/>',
    "windows": '<path d="M3.5 5.5l7-1v7h-7zM12.5 4.2l8-1.2v8.5h-8zM3.5 13h7v7l-7-1zM12.5 13h8v8.5l-8-1.2z"/>',
    "ssh": '<rect x="4" y="10" width="16" height="11" rx="2.5"/><path d="M8 10V7a4 4 0 018 0v3"/>',
    "ai": '<path d="M12 3l1.9 7.1L21 12l-7.1 1.9L12 21l-1.9-7.1L3 12l7.1-1.9z"/>',
}

COPY = {
    "zh": dict(
        eyebrow="SQUID", headline="发布，本该如此简单。",
        sub="免费、开箱即用的企业级发布平台",
        flow="一条流水线 · 覆盖所有目标",
        targets=[("k8s", "Kubernetes"), ("linux", "Linux"), ("windows", "Windows"), ("ssh", "SSH"), ("ai", "AI Agent")],
        features=[
            ("free", "免费自托管", ["不限项目、用户与目标", "无任何授权费用"]),
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
        eyebrow="SQUID", headline="Release, simplified.",
        sub="The free, ready-to-use release platform with enterprise-grade features.",
        flow="ONE PIPELINE · EVERY TARGET",
        targets=[("k8s", "Kubernetes"), ("linux", "Linux"), ("windows", "Windows"), ("ssh", "SSH"), ("ai", "AI Agent")],
        features=[
            ("free", "Free &amp; self-hosted", ["Unlimited projects, users,", "and targets. No license fees."]),
            ("bolt", "Ready to use", ["One service, one database.", "Agents install in one command."]),
            ("layers", "One release model", ["Environments, lifecycles, releases.", "One model for every target."]),
            ("hex", "Cloud native", ["Kubernetes Agent and API modes.", "Helm, Kustomize, YAML built in."]),
            ("shield", "Enterprise governance", ["RBAC, approvals, and audit.", "Secrets encrypted and masked."]),
            ("plug", "Extensible", ["New targets and actions plug in", "without touching the pipeline."]),
        ],
        free_title="Free. For good.", free_sub=["MIT-licensed, every feature included.", "Scale up without adding software cost."],
        cols=("Squid", "Commercial platforms"),
        rows=[("Software cost", "Free", "Tiered subscription"), ("Projects &amp; users", "Unlimited", "Limited by tier"), ("Deployment targets", "Unlimited", "Billed per node")],
    ),
}


def icon(name, x, y, size, color, width=1.7):
    s = size / 24
    return (f'<g transform="translate({x} {y}) scale({s})" fill="none" stroke="{color}" '
            f'stroke-width="{width:.2f}" stroke-linecap="round" stroke-linejoin="round">{ICONS[name]}</g>')


def svg(w, h, title, body, defs=""):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}" '
            f'fill="none" role="img" aria-label="{title}"><title>{title}</title><defs>{defs}</defs>{body}</svg>\n')


def hero(lang, t):
    c, T = COPY[lang], THEMES[t]
    W, H = 1280, 640
    defs = f'''
<radialGradient id="g1" cx="0.32" cy="0.05" r="0.6"><stop offset="0" stop-color="{T['glowA']}" stop-opacity="{T['glowOp']}"/><stop offset="1" stop-color="{T['glowA']}" stop-opacity="0"/></radialGradient>
<radialGradient id="g2" cx="0.72" cy="0.0" r="0.55"><stop offset="0" stop-color="{T['glowB']}" stop-opacity="{T['glowOp']}"/><stop offset="1" stop-color="{T['glowB']}" stop-opacity="0"/></radialGradient>
<linearGradient id="hl" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{T['gradA']}"/><stop offset="1" stop-color="{T['gradB']}"/></linearGradient>
<linearGradient id="ln" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="{T['accent']}" stop-opacity="0"/><stop offset="0.5" stop-color="{T['accent']}" stop-opacity="0.9"/><stop offset="1" stop-color="{T['accent']}" stop-opacity="0"/></linearGradient>
<pattern id="grid" width="40" height="40" patternUnits="userSpaceOnUse"><path d="M40 0H0V40" stroke="{T['stroke']}" stroke-width="1" opacity="0.55"/></pattern>
<linearGradient id="fade" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity="0.9"/><stop offset="0.75" stop-color="#fff" stop-opacity="0"/></linearGradient>
<mask id="gm"><rect width="{W}" height="{H}" fill="url(#fade)"/></mask>
<clipPath id="clip"><rect width="{W}" height="{H}" rx="28"/></clipPath>'''
    hs = 86 if lang == "en" else 80
    parts = [f'<g clip-path="url(#clip)"><rect width="{W}" height="{H}" fill="{T["bg"]}"/>',
             f'<rect width="{W}" height="{H}" fill="url(#grid)" mask="url(#gm)"/>',
             f'<rect width="{W}" height="{H}" fill="url(#g1)"/><rect width="{W}" height="{H}" fill="url(#g2)"/></g>',
             f'<rect x="0.5" y="0.5" width="{W - 1}" height="{H - 1}" rx="27.5" stroke="{T["stroke"]}"/>',
             f'<g font-family="{FONT}" text-anchor="middle">',
             f'<text x="{W / 2}" y="140" fill="{T["accent"]}" font-size="19" font-weight="600" letter-spacing="5">{c["eyebrow"]}</text>',
             f'<text x="{W / 2}" y="252" fill="url(#hl)" font-size="{hs}" font-weight="700" letter-spacing="-2">{c["headline"]}</text>',
             f'<text x="{W / 2}" y="324" fill="{T["body"]}" font-size="29" font-weight="400">{c["sub"]}</text>',
             f'<text x="{W / 2}" y="416" fill="{T["faint"]}" font-size="17" font-weight="600" letter-spacing="3">{c["flow"]}</text>',
             '</g>',
             f'<path d="M150 446H1130" stroke="url(#ln)" stroke-width="1.5"/>']
    pw, gap = 200, 16
    x0 = (W - (pw * 5 + gap * 4)) / 2
    for i, (ic, label) in enumerate(c["targets"]):
        x = x0 + i * (pw + gap)
        parts.append(f'<path d="M{x + pw / 2} 446V478" stroke="{T["accent"]}" stroke-opacity="0.45" stroke-width="1.5"/>')
        parts.append(f'<circle cx="{x + pw / 2}" cy="446" r="3.5" fill="{T["accent"]}"/>')
        parts.append(f'<rect x="{x}" y="478" width="{pw}" height="78" rx="20" fill="{T["pill"]}" stroke="{T["stroke"]}"/>')
        parts.append(icon(ic, x + 26, 504, 26, T["accent"]))
        parts.append(f'<text x="{x + 64}" y="526" fill="{T["title"]}" font-family="{FONT}" font-size="22" font-weight="600">{label}</text>')
    return svg(W, H, f'Squid — {c["headline"]} {c["sub"]}', "".join(parts), defs)


def features(lang, t):
    c, T = COPY[lang], THEMES[t]
    W, cw, ch = 1280, 405, 256
    gap = (W - cw * 3) / 2
    H = ch * 2 + gap
    parts = []
    for i, (ic, title, lines) in enumerate(c["features"]):
        x, y = (i % 3) * (cw + gap), (i // 3) * (ch + gap)
        parts.append(f'<rect x="{x + 0.5}" y="{y + 0.5}" width="{cw - 1}" height="{ch - 1}" rx="24" fill="{T["card"]}" stroke="{T["stroke"]}"/>')
        parts.append(f'<rect x="{x + 36}" y="{y + 36}" width="58" height="58" rx="16" fill="{T["iconBg"]}"/>')
        parts.append(icon(ic, x + 50, y + 50, 30, T["accent"]))
        parts.append(f'<text x="{x + 36}" y="{y + 148}" fill="{T["title"]}" font-family="{FONT}" font-size="29" font-weight="650" letter-spacing="-0.4">{title}</text>')
        for j, line in enumerate(lines):
            parts.append(f'<text x="{x + 36}" y="{y + 188 + j * 30}" fill="{T["body"]}" font-family="{FONT}" font-size="20">{line}</text>')
    alt = "; ".join(f'{ti}: {"".join(l) if lang == "zh" else " ".join(l)}' for _, ti, l in c["features"])
    return svg(W, int(H), alt, "".join(parts))


def free(lang, t):
    c, T = COPY[lang], THEMES[t]
    W, H = 1280, 380
    defs = f'''<radialGradient id="fg" cx="0.12" cy="0.2" r="0.55"><stop offset="0" stop-color="{T['glowA']}" stop-opacity="{float(T['glowOp']) * 0.7}"/><stop offset="1" stop-color="{T['glowA']}" stop-opacity="0"/></radialGradient>
<linearGradient id="fh" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{T['gradA']}"/><stop offset="1" stop-color="{T['gradB']}"/></linearGradient>
<clipPath id="fc"><rect width="{W}" height="{H}" rx="28"/></clipPath>'''
    parts = [f'<g clip-path="url(#fc)"><rect width="{W}" height="{H}" fill="{T["card"]}"/><rect width="{W}" height="{H}" fill="url(#fg)"/></g>',
             f'<rect x="0.5" y="0.5" width="{W - 1}" height="{H - 1}" rx="27.5" stroke="{T["stroke"]}"/>',
             f'<g font-family="{FONT}">',
             f'<text x="64" y="168" fill="url(#fh)" font-size="64" font-weight="700" letter-spacing="-1.5">{c["free_title"]}</text>']
    for j, line in enumerate(c["free_sub"]):
        parts.append(f'<text x="64" y="{226 + j * 34}" fill="{T["body"]}" font-size="22">{line}</text>')
    tx, c1, c2 = 620, 890, 1100
    parts.append(f'<text x="{c1}" y="96" fill="{T["accent"]}" font-size="18" font-weight="700" text-anchor="middle" letter-spacing="1">{c["cols"][0]}</text>')
    parts.append(f'<text x="{c2}" y="96" fill="{T["body"]}" font-size="18" font-weight="600" text-anchor="middle" letter-spacing="0.5">{c["cols"][1]}</text>')
    for j, (label, a, b) in enumerate(c["rows"]):
        y = 164 + j * 72
        parts.append(f'<path d="M{tx} {y - 42}H{W - 64}" stroke="{T["stroke"]}"/>')
        parts.append(f'<text x="{tx}" y="{y}" fill="{T["body"]}" font-size="21">{label}</text>')
        parts.append(f'<text x="{c1}" y="{y}" fill="{T["title"]}" font-size="22" font-weight="650" text-anchor="middle">{a}</text>')
        parts.append(f'<text x="{c2}" y="{y}" fill="{T["body"]}" font-size="20" text-anchor="middle">{b}</text>')
    parts.append('</g>')
    alt = f'{c["free_title"]} ' + "; ".join(f"{l}: {a} vs {b}" for l, a, b in c["rows"])
    return svg(W, H, alt, "".join(parts), defs)


for lang in COPY:
    for t in THEMES:
        for name, fn in (("hero", hero), ("features", features), ("free", free)):
            with open(os.path.join(OUT, f"{name}-{lang}-{t}.svg"), "w", encoding="utf-8") as f:
                f.write(fn(lang, t))
print("ok")
