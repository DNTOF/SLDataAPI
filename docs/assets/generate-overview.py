#!/usr/bin/env python3
# 生成 README 架构图：python3 docs/assets/generate-overview.py docs/assets
import sys
from xml.sax.saxutils import escape as esc

W, H = 1280, 680

THEMES = {
    "dark": dict(
        bg0="#070B17", bg1="#0D1426", grid="#94A3B8", grid_op="0.07",
        card="#0F172B", card2="#141D35", stroke="#22304F",
        text="#E8ECF5", muted="#93A0BA", faint="#5D6A86",
        violet="#8B6CFF", cyan="#22D3EE", pink="#F472B6", amber="#FBBF24",
        green="#34D399", blue="#60A5FA",
        glow_op="0.30", tint_op="0.16", code_bg="#0A1022", code_stroke="#1E2A47",
        title_a="#FFFFFF", title_b="#A9B4FF",
    ),
    "light": dict(
        bg0="#FBFCFF", bg1="#F1F4FB", grid="#0F172A", grid_op="0.05",
        card="#FFFFFF", card2="#F6F8FD", stroke="#E1E6F0",
        text="#0F172A", muted="#55607A", faint="#94A0B8",
        violet="#6D4AFF", cyan="#0891B2", pink="#DB2777", amber="#D97706",
        green="#059669", blue="#2563EB",
        glow_op="0.16", tint_op="0.11", code_bg="#0F172A", code_stroke="#0F172A",
        title_a="#0F172A", title_b="#6D4AFF",
    ),
}

SANS = ("-apple-system, BlinkMacSystemFont, 'Segoe UI', 'PingFang SC', 'Hiragino Sans GB', "
        "'Microsoft YaHei', 'Noto Sans CJK SC', 'WenQuanYi Micro Hei', Roboto, Helvetica, Arial, sans-serif")
MONO = ("'JetBrains Mono', 'SFMono-Regular', 'Cascadia Code', Consolas, Menlo, "
        "'Noto Sans Mono CJK SC', 'WenQuanYi Micro Hei Mono', monospace")


def t(x, y, s, cls, anchor=None, fill=None, extra=""):
    a = f' text-anchor="{anchor}"' if anchor else ""
    f = f' fill="{fill}"' if fill else ""
    return f'<text x="{x}" y="{y}" class="{cls}"{a}{f}{extra}>{esc(s)}</text>'


def icon(kind, cx, cy, c):
    """Small 16px glyphs centred on (cx, cy)."""
    s = f'stroke="{c}" stroke-width="1.8" fill="none" stroke-linecap="round" stroke-linejoin="round"'
    if kind == "chart":
        return (f'<path d="M{cx-6} {cy+6} V{cy+1} M{cx} {cy+6} V{cy-6} M{cx+6} {cy+6} V{cy-2}" {s}/>')
    if kind == "terminal":
        return (f'<path d="M{cx-7} {cy-5} L{cx-2} {cy} L{cx-7} {cy+5} M{cx} {cy+6} H{cx+7}" {s}/>')
    if kind == "bolt":
        return (f'<path d="M{cx+1} {cy-8} L{cx-5} {cy+1} H{cx} L{cx-1} {cy+8} L{cx+5} {cy-1} H{cx} Z" '
                f'stroke="{c}" stroke-width="1.6" fill="{c}" fill-opacity="0.25" stroke-linejoin="round"/>')
    if kind == "wave":
        hs = [4, 9, 14, 9, 5]
        d = " ".join(f"M{cx-8+i*4} {cy-h/2} V{cy+h/2}" for i, h in enumerate(hs))
        return f'<path d="{d}" {s}/>'
    if kind == "rec":
        return (f'<circle cx="{cx}" cy="{cy}" r="7" {s}/>'
                f'<circle cx="{cx}" cy="{cy}" r="3" fill="{c}"/>')
    if kind == "doc":
        return (f'<path d="M{cx-6} {cy-8} H{cx+3} L{cx+6} {cy-5} V{cy+8} H{cx-6} Z '
                f'M{cx-3} {cy-2} H{cx+3} M{cx-3} {cy+2} H{cx+3} M{cx-3} {cy+5} H{cx+1}" {s}/>')
    if kind == "lock":
        return (f'<rect x="{cx-6}" y="{cy-2}" width="12" height="9" rx="2" {s}/>'
                f'<path d="M{cx-3.5} {cy-2} V{cy-5} a3.5 3.5 0 0 1 7 0 V{cy-2}" {s}/>')
    if kind == "phone":
        return (f'<rect x="{cx-5}" y="{cy-8}" width="10" height="16" rx="2.5" {s}/>'
                f'<path d="M{cx-1.5} {cy+5} H{cx+1.5}" {s}/>')
    if kind == "browser":
        return (f'<rect x="{cx-8}" y="{cy-6}" width="16" height="12" rx="2.5" {s}/>'
                f'<path d="M{cx-8} {cy-2} H{cx+8}" {s}/>')
    if kind == "bot":
        return (f'<rect x="{cx-7}" y="{cy-4}" width="14" height="10" rx="3" {s}/>'
                f'<path d="M{cx} {cy-4} V{cy-7}" {s}/><circle cx="{cx}" cy="{cy-8}" r="1.3" fill="{c}"/>'
                f'<circle cx="{cx-3}" cy="{cy+1}" r="1.2" fill="{c}"/><circle cx="{cx+3}" cy="{cy+1}" r="1.2" fill="{c}"/>')
    if kind == "code":
        return (f'<path d="M{cx-4} {cy-6} L{cx-9} {cy} L{cx-4} {cy+6} M{cx+4} {cy-6} L{cx+9} {cy} L{cx+4} {cy+6}" {s}/>')
    if kind == "user":
        return (f'<circle cx="{cx}" cy="{cy-3}" r="3.5" {s}/>'
                f'<path d="M{cx-6.5} {cy+7} a6.5 6 0 0 1 13 0" {s}/>')
    if kind == "flag":
        return (f'<path d="M{cx-5} {cy+8} V{cy-7} H{cx+6} L{cx+3} {cy-3} L{cx+6} {cy+1} H{cx-5}" {s}/>')
    if kind == "map":
        return (f'<path d="M{cx-8} {cy-5} L{cx-3} {cy-7} L{cx+3} {cy-5} L{cx+8} {cy-7} V{cy+5} '
                f'L{cx+3} {cy+7} L{cx-3} {cy+5} L{cx-8} {cy+7} Z M{cx-3} {cy-7} V{cy+5} M{cx+3} {cy-5} V{cy+7}" {s}/>')
    if kind == "mic":
        return (f'<rect x="{cx-3}" y="{cy-8}" width="6" height="10" rx="3" {s}/>'
                f'<path d="M{cx-6} {cy-1} a6 6 0 0 0 12 0 M{cx} {cy+5} V{cy+8}" {s}/>')
    raise ValueError(kind)


def chip(x, y, w, h, color, p, op=None):
    return (f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{min(10, h/2)}" '
            f'fill="{color}" fill-opacity="{op or p["tint_op"]}"/>')


def build(name):
    p = THEMES[name]
    o = []
    o.append(f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" '
             f'role="img" aria-labelledby="title desc">')
    o.append('<title id="title">SLDataAPI 架构总览</title>')
    o.append('<desc id="desc">SCP:SL 专用服务器经 LabAPI 加载 SLDataAPI。SLDataAPI 提供数据快照、远程控制、实时事件流、'
             '语音转发、录音取证与审计日志，通过 8081 端口（HTTP / WebSocket）和 8082 端口（语音 WebSocket）对外服务，'
             '两个端口均可同端口启用自签 TLS。SLConsole、FoundationConsole、AstrBot 机器人与自定义脚本等客户端凭 '
             'verify_token 或 API Key 接入。</desc>')

    o.append(f"""<defs>
<style>
.sans{{font-family:{SANS}}}
.mono{{font-family:{MONO}}}
.h1{{font-family:{SANS};font-size:62px;font-weight:800;letter-spacing:-1.5px}}
.sub{{font-family:{SANS};font-size:19px;fill:{p['muted']}}}
.lbl{{font-family:{SANS};font-size:11px;font-weight:700;letter-spacing:2.2px;fill:{p['faint']}}}
.ct{{font-family:{SANS};font-size:17px;font-weight:700;fill:{p['text']}}}
.cs{{font-family:{SANS};font-size:12px;fill:{p['muted']}}}
.row{{font-family:{SANS};font-size:14px;font-weight:600;fill:{p['text']}}}
.tt{{font-family:{SANS};font-size:15px;font-weight:700;fill:{p['text']}}}
.ts{{font-family:{MONO};font-size:11.5px;fill:{p['muted']}}}
.port{{font-family:{MONO};font-size:20px;font-weight:700}}
.code{{font-family:{MONO};font-size:12.5px}}
.flow{{stroke-dasharray:3 7;animation:flow 1.4s linear infinite}}
.pulse{{animation:pulse 2.4s ease-in-out infinite}}
@keyframes flow{{to{{stroke-dashoffset:-20}}}}
@keyframes pulse{{0%,100%{{opacity:1}}50%{{opacity:.35}}}}
@media (prefers-reduced-motion:reduce){{.flow,.pulse{{animation:none}}}}
</style>
<linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">
  <stop offset="0" stop-color="{p['bg0']}"/><stop offset="1" stop-color="{p['bg1']}"/>
</linearGradient>
<radialGradient id="glowA" cx="0.5" cy="0.5" r="0.5">
  <stop offset="0" stop-color="{p['violet']}" stop-opacity="{p['glow_op']}"/><stop offset="1" stop-color="{p['violet']}" stop-opacity="0"/>
</radialGradient>
<radialGradient id="glowB" cx="0.5" cy="0.5" r="0.5">
  <stop offset="0" stop-color="{p['cyan']}" stop-opacity="{p['glow_op']}"/><stop offset="1" stop-color="{p['cyan']}" stop-opacity="0"/>
</radialGradient>
<linearGradient id="titleGrad" x1="0" y1="0" x2="1" y2="0">
  <stop offset="0" stop-color="{p['title_a']}"/><stop offset="1" stop-color="{p['title_b']}"/>
</linearGradient>
<linearGradient id="coreStroke" x1="0" y1="0" x2="1" y2="1">
  <stop offset="0" stop-color="{p['violet']}"/><stop offset="0.55" stop-color="{p['cyan']}"/><stop offset="1" stop-color="{p['pink']}"/>
</linearGradient>
<linearGradient id="logo" x1="0" y1="0" x2="1" y2="1">
  <stop offset="0" stop-color="{p['violet']}"/><stop offset="1" stop-color="{p['cyan']}"/>
</linearGradient>
<linearGradient id="srcLogo" x1="0" y1="0" x2="1" y2="1">
  <stop offset="0" stop-color="#E5484D"/><stop offset="1" stop-color="#F59E0B"/>
</linearGradient>
<pattern id="grid" width="32" height="32" patternUnits="userSpaceOnUse">
  <path d="M32 0 H0 V32" fill="none" stroke="{p['grid']}" stroke-opacity="{p['grid_op']}" stroke-width="1"/>
</pattern>
<filter id="blur" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur stdDeviation="22"/></filter>
<filter id="shadow" x="-10%" y="-10%" width="120%" height="130%">
  <feDropShadow dx="0" dy="8" stdDeviation="12" flood-color="#0B1020" flood-opacity="{'0.35' if name == 'dark' else '0.07'}"/>
</filter>
<marker id="arrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="7" markerHeight="7" orient="auto">
  <path d="M1 1 L8 5 L1 9" fill="none" stroke="{p['faint']}" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>
</marker>
<clipPath id="frame"><rect width="{W}" height="{H}" rx="24"/></clipPath>
</defs>""")

    o.append('<g clip-path="url(#frame)">')
    o.append(f'<rect width="{W}" height="{H}" fill="url(#bg)"/>')
    o.append(f'<rect width="{W}" height="{H}" fill="url(#grid)"/>')
    o.append(f'<ellipse cx="200" cy="40" rx="420" ry="260" fill="url(#glowA)"/>')
    o.append(f'<ellipse cx="610" cy="420" rx="360" ry="260" fill="url(#glowA)"/>')
    o.append(f'<ellipse cx="1120" cy="620" rx="380" ry="240" fill="url(#glowB)"/>')
    o.append('</g>')
    o.append(f'<rect x="0.5" y="0.5" width="{W-1}" height="{H-1}" rx="24" fill="none" stroke="{p["stroke"]}"/>')

    # ---------- header ----------
    o.append(chip(56, 46, 250, 28, p["violet"], p, op="0.14"))
    o.append(f'<rect x="56.5" y="46.5" width="249" height="27" rx="13.5" fill="none" stroke="{p["violet"]}" stroke-opacity="0.35"/>')
    o.append(f'<circle cx="74" cy="60" r="4" fill="{p["green"]}" class="pulse"/>')
    o.append(t(86, 65, "LabAPI 原生插件 · v2.6.2", "sans", fill=p["violet"],
               extra=' font-size="13" font-weight="600"'))
    o.append(f'<text x="52" y="138" class="h1" fill="url(#titleGrad)">SLDataAPI</text>')
    o.append(t(56, 176, "把 SCP:SL 服务器变成可编程的平台", "sub"))
    o.append(t(56, 202, "读数据 · 下指令 · 收事件 · 听语音", "sub", extra=' font-size="15" fill-opacity="0.85"'))

    # code window
    cx0, cy0, cw, chh = 724, 44, 500, 168
    o.append(f'<g filter="url(#shadow)"><rect x="{cx0}" y="{cy0}" width="{cw}" height="{chh}" rx="14" '
             f'fill="{p["code_bg"]}" stroke="{p["code_stroke"]}"/></g>')
    for i, c in enumerate(["#FF5F57", "#FEBC2E", "#28C840"]):
        o.append(f'<circle cx="{cx0+22+i*18}" cy="{cy0+20}" r="5.5" fill="{c}"/>')
    o.append(t(cx0 + cw / 2, cy0 + 25, "GET /get_sl_data", "mono", anchor="middle",
               fill="#6B7894", extra=' font-size="12"'))
    o.append(f'<path d="M{cx0} {cy0+40} H{cx0+cw}" stroke="#1E2A47"/>')
    k, s_, n, pu, dim = "#7DD3FC", "#A7F3D0", "#FBBF24", "#C4B5FD", "#64748B"
    lines = [
        [("$ ", dim), ("curl ", "#E2E8F0"), ("-H ", pu), ('"Authorization: Bearer ••••" ', s_), (":8081/get_sl_data", "#E2E8F0")],
        [("{ ", "#E2E8F0"), ('"success"', k), (": ", "#E2E8F0"), ("true", n), (", ", "#E2E8F0"),
         ('"players_count"', k), (": ", "#E2E8F0"), ("24", n), (", ", "#E2E8F0"), ('"max_players"', k), (": ", "#E2E8F0"), ("30", n), (",", "#E2E8F0")],
        [("  ", "#E2E8F0"), ('"current_phase"', k), (": ", "#E2E8F0"), ('"进行中"', s_), (", ", "#E2E8F0"),
         ('"game_version"', k), (": ", "#E2E8F0"), ('"14.2.8"', s_), (",", "#E2E8F0")],
        [("  ", "#E2E8F0"), ('"active_holiday"', k), (": ", "#E2E8F0"), ('"Halloween"', s_), (", ", "#E2E8F0"),
         ('"players"', k), (": ", "#E2E8F0"), ("[ … ]", dim), (" }", "#E2E8F0")],
    ]
    for i, segs in enumerate(lines):
        y = cy0 + 70 + i * 24
        tsp = "".join(f'<tspan fill="{c}">{esc(sx)}</tspan>' for sx, c in segs)
        o.append(f'<text x="{cx0+22}" y="{y}" class="code" xml:space="preserve">{tsp}</text>')
    o.append(f'<rect x="{cx0+22+7.6*0}" y="{cy0+chh-22}" width="8" height="14" fill="{p["cyan"]}" class="pulse" opacity="0.9"/>')

    # ---------- column labels ----------
    top = 278
    labels = [(56, "游戏服务器"), (364, "SLDataAPI 核心"), (858, "端口"), (1010, "客户端")]
    for x, s in labels:
        o.append(t(x, top - 14, s, "lbl"))

    # ---------- source card ----------
    sx, sy, sw, sh = 56, top, 248, 340
    o.append(f'<g filter="url(#shadow)"><rect x="{sx}" y="{sy}" width="{sw}" height="{sh}" rx="18" '
             f'fill="{p["card"]}" stroke="{p["stroke"]}"/></g>')
    o.append(f'<rect x="{sx+20}" y="{sy+20}" width="40" height="40" rx="11" fill="url(#srcLogo)"/>')
    o.append(t(sx + 40, sy + 46, "SL", "sans", anchor="middle", fill="#FFFFFF",
               extra=' font-size="15" font-weight="800"'))
    o.append(t(sx + 72, sy + 38, "SCP:SL 服务器", "ct"))
    o.append(t(sx + 72, sy + 56, "Dedicated Server + LabAPI", "cs"))
    rows = [("user", "玩家与物品栏", p["violet"]), ("flag", "回合与核弹", p["amber"]),
            ("map", "地图与设施", p["cyan"]), ("mic", "语音频道", p["pink"]),
            ("bolt", "游戏事件", p["green"])]
    src_ports = []
    for i, (ic, label, c) in enumerate(rows):
        ry = sy + 84 + i * 48
        o.append(f'<rect x="{sx+16}" y="{ry}" width="{sw-32}" height="38" rx="10" fill="{p["card2"]}" stroke="{p["stroke"]}"/>')
        o.append(chip(sx + 24, ry + 7, 24, 24, c, p))
        o.append(icon(ic, sx + 36, ry + 19, c))
        o.append(t(sx + 60, ry + 24, label, "row"))
        src_ports.append(ry + 19)

    # ---------- core card ----------
    kx, ky, kw, kh = 364, top, 470, 340
    o.append(f'<rect x="{kx+30}" y="{ky+30}" width="{kw-60}" height="{kh-60}" rx="30" fill="{p["violet"]}" '
             f'opacity="{p["glow_op"]}" filter="url(#blur)"/>')
    o.append(f'<rect x="{kx}" y="{ky}" width="{kw}" height="{kh}" rx="20" fill="{p["card"]}"/>')
    o.append(f'<rect x="{kx+0.75}" y="{ky+0.75}" width="{kw-1.5}" height="{kh-1.5}" rx="19.5" fill="none" '
             f'stroke="url(#coreStroke)" stroke-width="1.5"/>')
    hx, hy = kx + 40, ky + 40
    hexp = " ".join(f"{hx+dx:.1f},{hy+dy:.1f}" for dx, dy in
                    [(0, -20), (17.3, -10), (17.3, 10), (0, 20), (-17.3, 10), (-17.3, -10)])
    o.append(f'<polygon points="{hexp}" fill="url(#logo)"/>')
    o.append(f'<path d="M{hx-7} {hy+5} L{hx-1} {hy-1} L{hx+3} {hy+3} L{hx+8} {hy-5}" stroke="#FFFFFF" '
             f'stroke-width="2.2" fill="none" stroke-linecap="round" stroke-linejoin="round"/>')
    o.append(t(kx + 72, ky + 38, "SLDataAPI", "ct", extra=' font-size="19"'))
    o.append(t(kx + 72, ky + 57, "运行于服务器进程内 · 主线程安全调度", "cs"))
    o.append(chip(kx + kw - 108, ky + 26, 88, 24, p["green"], p))
    o.append(f'<circle cx="{kx+kw-94}" cy="{ky+38}" r="3.5" fill="{p["green"]}" class="pulse"/>')
    o.append(t(kx + kw - 84, ky + 42, "运行中", "sans", fill=p["green"], extra=' font-size="12" font-weight="700"'))

    tiles = [
        ("chart", "数据快照", "/get_sl_data", p["violet"]),
        ("terminal", "远程控制", "40+ 端点", p["cyan"]),
        ("bolt", "事件流", "19 种事件", p["amber"]),
        ("wave", "语音转发", "48 kHz PCM", p["pink"]),
        ("rec", "录音取证", "WAV + TSV", p["green"]),
        ("doc", "审计日志", "control_log", p["blue"]),
    ]
    tw, th, gap = 134, 100, 12
    for i, (ic, title, sub, c) in enumerate(tiles):
        col, rw = i % 3, i // 3
        x = kx + 20 + col * (tw + gap)
        y = ky + 80 + rw * (th + gap)
        o.append(f'<rect x="{x}" y="{y}" width="{tw}" height="{th}" rx="12" fill="{p["card2"]}" stroke="{p["stroke"]}"/>')
        o.append(f'<rect x="{x}" y="{y+16}" width="2.5" height="22" rx="1.25" fill="{c}"/>')
        o.append(chip(x + 14, y + 14, 30, 30, c, p))
        o.append(icon(ic, x + 29, y + 29, c))
        o.append(t(x + 14, y + 70, title, "tt"))
        o.append(t(x + 14, y + 88, sub, "ts"))

    by = ky + kh - 42
    o.append(f'<rect x="{kx+20}" y="{by}" width="{kw-40}" height="28" rx="8" fill="{p["card2"]}" stroke="{p["stroke"]}"/>')
    o.append(icon("lock", kx + 38, by + 14, p["violet"]))
    o.append(t(kx + 54, by + 18.5, "API Key 分级授权 · 指纹存储 · 弱口令 fail-closed · 操作审计", "cs",
               extra=f' fill="{p["text"]}" fill-opacity="0.8"'))

    # source -> core connectors
    entry_x = kx - 2
    targets = [ky + 110, ky + 140, ky + 170, ky + 200, ky + 230]
    for yv, ty, (_, _, c) in zip(src_ports, targets, rows):
        x1 = sx + sw - 16
        o.append(f'<path d="M{x1} {yv} C{x1+40} {yv} {entry_x-40} {ty} {entry_x} {ty}" stroke="{c}" '
                 f'stroke-opacity="0.75" stroke-width="1.6" fill="none" class="flow"/>')
        o.append(f'<circle cx="{entry_x}" cy="{ty}" r="3" fill="{c}"/>')

    # ---------- ports ----------
    px, pw, ph = 858, 124, 96
    ports = [
        (ky + 40, ":8081", "HTTP · WS", "数据 · 控制 · 事件", p["cyan"]),
        (ky + 168, ":8082", "Voice WS", "语音转发", p["pink"]),
    ]
    port_mid = []
    for py, num, proto, desc, c in ports:
        o.append(f'<g filter="url(#shadow)"><rect x="{px}" y="{py}" width="{pw}" height="{ph}" rx="14" '
                 f'fill="{p["card"]}" stroke="{c}" stroke-opacity="0.55"/></g>')
        o.append(t(px + 16, py + 32, num, "port", fill=c))
        o.append(t(px + 16, py + 54, proto, "sans", fill=p["text"], extra=' font-size="13" font-weight="600"'))
        o.append(t(px + 16, py + 76, desc, "cs", extra=' font-size="11.5"'))
        port_mid.append((py + ph / 2, c))
        o.append(f'<path d="M{kx+kw} {py+ph/2} H{px-4}" stroke="{c}" stroke-width="1.8" fill="none" class="flow"/>')
        o.append(f'<circle cx="{kx+kw}" cy="{py+ph/2}" r="3.5" fill="{c}"/>')

    ty0 = ky + 284
    o.append(f'<rect x="{px}" y="{ty0}" width="{pw}" height="56" rx="12" fill="{p["violet"]}" fill-opacity="{p["tint_op"]}" '
             f'stroke="{p["violet"]}" stroke-opacity="0.4" stroke-dasharray="4 4"/>')
    o.append(icon("lock", px + 22, ty0 + 20, p["violet"]))
    o.append(t(px + 36, ty0 + 25, "同端口 TLS", "sans", fill=p["violet"], extra=' font-size="13" font-weight="700"'))
    o.append(t(px + 16, ty0 + 44, "自签 · 指纹固定", "cs", extra=' font-size="11.5"'))
    o.append(f'<path d="M{px+pw/2} {ports[1][0]+ph} V{ty0}" stroke="{p["violet"]}" stroke-opacity="0.5" '
             f'stroke-dasharray="3 4" fill="none"/>')

    # ---------- clients ----------
    lx, lw, lh, lg = 1010, 214, 73, 16
    clients = [
        ("phone", "SLConsole", "Android 原生客户端", p["green"], 0),
        ("browser", "FoundationConsole", "自部署 Web 管理平台", p["blue"], 0),
        ("bot", "AstrBot", "QQ 机器人查询", p["amber"], 0),
        ("code", "你的集成", "curl · Python · Node", p["pink"], 1),
    ]
    for i, (ic, title, sub, c, _) in enumerate(clients):
        y = top + i * (lh + lg)
        o.append(f'<g filter="url(#shadow)"><rect x="{lx}" y="{y}" width="{lw}" height="{lh}" rx="14" '
                 f'fill="{p["card"]}" stroke="{p["stroke"]}"/></g>')
        o.append(chip(lx + 14, y + 20, 34, 34, c, p))
        o.append(icon(ic, lx + 31, y + 37, c))
        o.append(t(lx + 60, y + 33, title, "tt", extra=' font-size="14"'))
        o.append(t(lx + 60, y + 52, sub, "cs"))
        cyv = y + lh / 2
        for (pm, pc), j in zip(port_mid, range(2)):
            if j == 1 and i not in (0, 1, 3):
                continue
            x1 = px + pw
            o.append(f'<path d="M{x1} {pm} C{x1+18} {pm} {lx-18} {cyv} {lx-2} {cyv}" stroke="{pc}" '
                     f'stroke-opacity="{0.6 if j == 0 else 0.45}" stroke-width="1.4" fill="none" class="flow"/>')
        o.append(f'<circle cx="{lx}" cy="{cyv}" r="3" fill="{p["faint"]}"/>')

    # ---------- footer ----------
    fy = H - 30
    foot = ["SCP:SL 14.2.8", "LabAPI", ".NET Framework 4.8", "Apache-2.0", "github.com/DNTOF/SLDataAPI"]
    o.append(t(56, fy, "   ·   ".join(foot), "sans", fill=p["faint"], extra=' font-size="12.5" xml:space="preserve"'))
    o.append(t(W - 56, fy, "HTTP  ·  WebSocket  ·  TLS  ·  48 kHz Voice", "mono", anchor="end",
               fill=p["faint"], extra=' font-size="12" xml:space="preserve"'))

    o.append("</svg>")
    return "\n".join(o) + "\n"


if __name__ == "__main__":
    out = sys.argv[1]
    for name in THEMES:
        with open(f"{out}/sldataapi-overview-{name}.svg", "w", encoding="utf-8") as f:
            f.write(build(name))
