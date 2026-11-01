#!/usr/bin/env python3
"""Generate WPF resource dictionaries from design-tokens.json + design-skins.json.

Outputs (never hand-edit; change the JSON and re-run this script):
  src/LesTool/Themes/Dark.xaml / Light.xaml   built-in theme dictionaries
  src/LesTool/Skins/<id>.xaml                 external skin packs (copied to themes\\ at build)
  src/LesTool/Themes/tokens.xaml              post-processed (sys:Double, frozen shadows, BOM)

Gates (script exits non-zero on failure):
  - skin contrast matrix: alpha colors are alpha-blended over their declared
    background stack before the WCAG ratio is computed; every pair >= 4.5:1
  - Design.xaml legacy constants must match design-tokens.json values
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TOKENS_JSON = ROOT / "design-tokens.json"
SKINS_JSON = ROOT / "design-skins.json"
THEMES = ROOT / "src" / "LesTool" / "Themes"
SKINS_DIR = ROOT / "src" / "LesTool" / "Skins"

REF = re.compile(r"^\{([a-zA-Z0-9_.-]+)\}$")
HEX = re.compile(r"^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")


# ---------- color helpers ----------

def parse_hex(v):
    """'#rrggbb' or css '#rrggbbaa' -> (r, g, b, a) each 0-255."""
    h = HEX.match(v.strip())
    if not h:
        raise SystemExit(f"not a hex color: {v}")
    x = h.group(1)
    if len(x) == 6:
        x += "FF"
    r, g, b, a = int(x[0:2], 16), int(x[2:4], 16), int(x[4:6], 16), int(x[6:8], 16)
    return r, g, b, a


def to_wpf_color(v):
    """css '#rrggbb(aa)' -> WPF '#aarrggbb'."""
    r, g, b, a = parse_hex(v)
    return f"#{a:02X}{r:02X}{g:02X}{b:02X}"


def blend(fg, bg):
    """Alpha-composite fg over bg (straight alpha, sRGB)."""
    fr, fgc, fb, fa = fg
    br, bgc, bb, ba = bg
    if ba == 0:
        return fg
    f = fa / 255
    b = ba / 255
    out_a = f + b * (1 - f)
    if out_a == 0:
        return (0, 0, 0, 0)
    def mix(fc, bc):
        return round((fc * f + bc * b * (1 - f)) / out_a)
    return (mix(fr, br), mix(fgc, bgc), mix(fb, bb), round(out_a * 255))


def _lin(c):
    c = c / 255
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def ratio(fg, bg):
    fr, fgc, fb, _ = fg
    br, bgc, bb, _ = bg
    l1 = 0.2126 * _lin(fr) + 0.7152 * _lin(fgc) + 0.0722 * _lin(fb)
    l2 = 0.2126 * _lin(br) + 0.7152 * _lin(bgc) + 0.0722 * _lin(bb)
    hi, lo = max(l1, l2), min(l1, l2)
    return (hi + 0.05) / (lo + 0.05)


# ---------- token resolution ----------

def resolve(value, primitive):
    m = REF.match(str(value).strip())
    if not m:
        return value
    node = primitive
    for part in m.group(1).split("."):
        node = node[part]
    return resolve(node, primitive)


def pascal(name):
    return "".join(seg[:1].upper() + seg[1:] for seg in name.split("-"))


# ---------- built-in themes ----------

HEADER = """<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- {title}（由 tools/emit_themes.py 从 design-tokens.json 生成，勿手改；改 JSON 后重跑） -->
"""


def emit_theme(tree, primitive, title, out_path):
    lines = [HEADER.format(title=title)]
    for key, val in tree.items():
        color = to_wpf_color(resolve(val, primitive))
        lines.append(f'    <SolidColorBrush x:Key="{pascal(key)}" Color="{color}"/>')
    lines.append("</ResourceDictionary>")
    out_path.write_text("\n".join(lines) + "\n", encoding="utf-8-sig")
    print(f"wrote {out_path} ({len(tree)} brushes)")


# ---------- skin packs ----------

SKIN_HEADER = """<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="clr-namespace:System;assembly=mscorlib">
    <!-- 皮肤包「{title}」：由 tools/emit_themes.py 从 design-skins.json 生成（勿手改；改 JSON 后重跑）。
         部署位置 themes\\{id}.xaml；热重载=直接保存本文件。缺省键热加载时回落内置深色。
         对比度门禁：{pairs} 对全过（透明色按叠加实算）。 -->
"""


def glow_brush_xaml(glow):
    c = to_wpf_color(glow["color"])          # #AARRGGBB
    rgb = c[3:]
    cx, cy = glow["center"].split(",")
    rx, ry = glow.get("radius", "0.9,0.65").split(",")
    return (f'    <RadialGradientBrush x:Key="WindowGlowOverlay" Center="{cx},{cy}" '
            f'GradientOrigin="{cx},{cy}" RadiusX="{rx}" RadiusY="{ry}">\n'
            f'        <GradientStop Color="{c}" Offset="0"/>\n'
            f'        <GradientStop Color="#00{rgb}" Offset="1"/>\n'
            f'    </RadialGradientBrush>')


def merged_primitive(base, skin):
    prim = json.loads(json.dumps(base))      # deep copy
    for k, v in skin.get("primitive", {}).items():
        if isinstance(v, dict) and isinstance(prim.get(k), dict):
            prim[k].update(v)
        else:
            prim[k] = v
    return prim


def check_skin_pairs(skin_id, skin, primitive):
    sem = skin["semantic"]

    def value_of(v):
        """Follow the chain: {other} -> skin semantic key -> often {color.*} -> primitive hex."""
        for _ in range(8):
            m = REF.match(str(v).strip())
            if not m:
                return v
            path = m.group(1)
            if path.startswith("color."):
                return resolve(v, primitive)
            v = sem[path]
        raise SystemExit(f"semantic reference chain too deep: {v}")

    failures, warns = [], []
    for pair in skin["pairs"]:
        fg_v = value_of(pair["fg"])
        bg_spec = pair["bg"] if isinstance(pair["bg"], list) else [pair["bg"]]
        eff_bg = parse_hex("#00000000")
        for layer in bg_spec:
            eff_bg = blend(parse_hex(value_of(layer)), eff_bg)
        eff_fg = blend(parse_hex(fg_v), eff_bg)
        r = ratio(eff_fg, eff_bg)
        if r < 4.5:
            failures.append(f"  FAIL {skin_id} {pair['name']}: {r:.2f}:1")
        elif r > 12:
            warns.append(f"  warn {skin_id} {pair['name']}: {r:.2f}:1 （散光舒适带建议 ≤12）")
        else:
            print(f"  ok   {skin_id} {pair['name']}: {r:.2f}:1")
    return failures, warns


def emit_skin(skin_id, skin, base_primitive):
    prim = merged_primitive(base_primitive, skin)
    failures, warns = check_skin_pairs(skin_id, skin, prim)
    for w in warns:
        print(w)
    if failures:
        print("\n".join(failures))
        raise SystemExit(f"skin {skin_id} failed the contrast gate")

    SKINS_DIR.mkdir(parents=True, exist_ok=True)
    lines = [SKIN_HEADER.format(title=skin["title"], id=skin_id, pairs=len(skin["pairs"]))]
    lines.append(f'    <sys:String x:Key="SkinMeta.Id">{skin_id}</sys:String>')
    lines.append(f'    <sys:String x:Key="SkinMeta.Name">{skin["title"]}</sys:String>')
    lines.append(f'    <sys:Boolean x:Key="SkinMeta.IsLight">{str(skin.get("isLight", False)).lower()}</sys:Boolean>')
    lines.append(f'    <sys:Boolean x:Key="SkinMeta.Acrylic">{str(skin.get("acrylic", False)).lower()}</sys:Boolean>')
    if skin.get("acrylic") and skin.get("glassTint"):
        lines.append(f'    <Color x:Key="SkinGlassTint">{to_wpf_color(skin["glassTint"])}</Color>')
    if skin.get("glow"):
        lines.append(glow_brush_xaml(skin["glow"]))
    for key, val in skin["semantic"].items():
        color = to_wpf_color(resolve(val, prim))
        lines.append(f'    <SolidColorBrush x:Key="{pascal(key)}" Color="{color}"/>')
    lines.append("</ResourceDictionary>")
    out = SKINS_DIR / f"{skin_id}.xaml"
    out.write_text("\n".join(lines) + "\n", encoding="utf-8-sig")
    print(f"wrote {out} ({len(skin['semantic'])} brushes, gate passed)")


# ---------- tokens.xaml post-pass ----------

def freeze_shadows():
    tokens = THEMES / "tokens.xaml"
    # utf-8-sig on read: strip any BOM left by a previous pass, or it doubles on write
    text = tokens.read_text(encoding="utf-8-sig")
    changed = False
    if "DropShadowEffect" in text:
        text = text.replace(
            '<ResourceDictionary\n    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"\n    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
            '<ResourceDictionary\n    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"\n    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"\n    xmlns:sys="clr-namespace:System;assembly=mscorlib"\n    xmlns:po="http://schemas.microsoft.com/winfx/2006/xaml/presentation/options">'
            + "\n    <!-- sys:Double / po:Freeze normalized by tools/emit_themes.py (BAML compiler needs sys:Double; shared shadows must be frozen) -->",
            1,
        )
        text = re.sub(r'<DropShadowEffect (?!po:Freeze)', '<DropShadowEffect po:Freeze="True" ', text)
        text = text.replace("<x:Double ", "<sys:Double ").replace("</x:Double>", "</sys:Double>")
        # XML comments must not contain '--' (the emitter's section header does)
        text = re.sub(r"<!--(.+?)-->", lambda m: "<!--" + m.group(1).replace("--", "·") + "-->", text)
        changed = True
    if changed:
        tokens.write_text(text, encoding="utf-8-sig")
        print(f"patched {tokens} (sys:Double + frozen shadows)")


def verify_design_constants(primitive):
    """Design.xaml carries the legacy constant keys as literals (BAML cannot compile
    StaticResource-as-resource-entry aliases). Verify they match the JSON so the
    JSON stays the single source of truth."""
    design = THEMES / "Design.xaml"
    text = design.read_text(encoding="utf-8-sig")
    expected = {
        "FontSizeTitle": primitive["text"]["title"],
        "FontSizeSubtitle": primitive["text"]["subtitle"],
        "FontSizeBody": primitive["text"]["body"],
        "FontSizeCaption": primitive["text"]["caption"],
        "CornerRadiusControl": primitive["radius"]["control"],
        "CornerRadiusOverlay": primitive["radius"]["overlay"],
        "ControlHeight": primitive["size"]["control-height"],
        "CaptionHeight": primitive["size"]["caption-height"],
    }
    errors = []
    for key, val in expected.items():
        if f'x:Key="{key}">{val}<' not in text:
            errors.append(f"{key} != {val}")
    page_pad = (f'{primitive["space"]["32"]},{primitive["space"]["24"]},'
                f'{primitive["space"]["32"]},{primitive["space"]["24"]}')
    if f'x:Key="PagePadding">{page_pad}<' not in text:
        errors.append(f"PagePadding != {page_pad}")
    if errors:
        raise SystemExit("Design.xaml constants drifted from design-tokens.json: "
                         + "; ".join(errors) + " — fix Design.xaml to match")


def main():
    data = json.loads(TOKENS_JSON.read_text(encoding="utf-8"))
    primitive = data["primitive"]
    sem = data["semantic"]["color"]
    emit_theme(sem["dark"], primitive, "深色主题（默认）", THEMES / "Dark.xaml")
    emit_theme(sem["light"], primitive, "浅色主题", THEMES / "Light.xaml")
    freeze_shadows()
    verify_design_constants(primitive)

    if SKINS_JSON.exists():
        skins = json.loads(SKINS_JSON.read_text(encoding="utf-8"))
        for skin_id, skin in skins.items():
            if skin_id == "$meta":
                continue
            emit_skin(skin_id, skin, primitive)
        print("Design.xaml constants verified")
    return 0


if __name__ == "__main__":
    sys.exit(main())
