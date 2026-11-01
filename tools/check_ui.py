#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""UI 门禁：扫描页面 XAML，拦四类违规（规则正文见 DESIGN.md「页面控件门禁」节）。

  controls  禁用控件（无隐式样式，用了即裸奔原生系统控件）：
            ComboBox/GroupBox/TabControl/TabItem/DataGrid/TreeView/Expander/Menu/
            ContextMenu/MenuItem/ToolBar/StatusBar/Calendar/DatePicker 一律禁；
            ListBox/ListView/Slider 须带 Style=（登记过的显式样式）否则禁。
  color     硬编码颜色：画刷属性上的 #RRGGBB[AA] 与命名色（White/Black/Red/…；
            Transparent 豁免）。透明色字节序规范见 DESIGN.md（CSS 序 #RRGGBBAA）。
  fontsize  数字字号 FontSize="数字"（文字只许四档 DynamicResource FontSize*；
            图标元素豁免——标签行附近含 Segoe MDL2 Assets 即视为图标字号）。
  sections  插件设置页结构（PLUGIN_UI_SPEC A）：根 UserControl 的内容必须恰好是
            一个裸 StackPanel 包装层，其子元素只能是以 SettingsSection 开头的区块
            控件（标签局部名比较，兼容 platform:/controls: 前缀）。页框（页头/滚动/
            边距/热键区）宿主管，插件不自建。纯代码页（如 Preview）不产 XAML，
            由 PLUGIN_DEV.md 文字约束。

白名单：ALLOWLIST 按文件豁免指定规则，理由必须同步登记在 DESIGN.md（脚本与文档互为镜像）。
用法：python tools/check_ui.py [--root 仓库根]。退出码 0=通过，1=有违规。
挂载：src/LesTool/LesTool.csproj 的 UiGate 目标（PrepareForBuild，违者构建失败）。
"""
import argparse
import re
import sys
from pathlib import Path

HARD_BANNED = {
    "ComboBox", "GroupBox", "TabControl", "TabItem", "DataGrid", "TreeView",
    "Expander", "Menu", "ContextMenu", "MenuItem", "ToolBar", "StatusBar",
    "Calendar", "DatePicker",
}
STYLE_REQUIRED = {"ListBox", "ListView", "Slider"}  # 仅当带 Style=（登记样式）

COLOR_ATTR = re.compile(
    r'(?:Foreground|Background|BorderBrush|Fill|Stroke|Color)\s*=\s*"([^"]+)"')
HEX_COLOR = re.compile(r"#[0-9A-Fa-f]{3,8}")
NAMED_BANNED = {"White", "Black", "Red", "Green", "Blue", "Gray",
                "Orange", "Yellow"}  # Transparent 豁免
NUM_FONTSIZE = re.compile(r'FontSize="(\d+)"')
COMMENT = re.compile(r"<!--.*?-->", re.S)
MDL2 = "Segoe MDL2 Assets"
PLUGIN_SETTINGS_PAGE = re.compile(r"^src/Plugins/.*SettingsPage\.xaml$")

# 白名单：路径（仓库根相对、正斜杠）→ (豁免规则集, 理由)。规则集 "all" = 全豁免。
# 加白须同步登记 DESIGN.md；"存量待迁移"条目只豁免既有规则，禁止新增同类违例。
ALLOWLIST = {
    "src/Plugins/LesTool.Plugin.Screenshot/OverlayWindow.xaml":
        ("all", "功能窗（截图遮罩）：固定色/图标字号（DESIGN.md 例外白名单2/3/4）"),
    "src/Plugins/LesTool.Plugin.Screenshot/LongBarWindow.xaml":
        ("all", "功能窗（长截图控制条）：固定色"),
    "src/Plugins/LesTool.Plugin.Screenshot/LongFrameWindow.xaml":
        ("all", "功能窗（长截图红框）：固定色"),
    "src/Plugins/LesTool.Plugin.Screenshot/LongPreviewWindow.xaml":
        ("all", "功能窗（长截图预览）：固定色/数字字号"),
    "src/Plugins/LesTool.Plugin.Screenshot/OcrResultWindow.xaml":
        ("all", "功能窗（OCR 结果）：保存绿固定色"),
    "src/Plugins/LesTool.Plugin.Screenshot/QrResultWindow.xaml":
        ("all", "功能窗（二维码结果）：保存绿固定色"),
    "src/Plugins/LesTool.Plugin.Screenshot/PinWindow.xaml":
        ("all", "功能窗（钉图）：黑底固定"),
    "src/Plugins/LesTool.Plugin.Screenshot/RecordBarWindow.xaml":
        ("all", "功能窗（录制控制条）：固定色/数字字号"),
    "src/Plugins/LesTool.Plugin.Screenshot/RecordFrameWindow.xaml":
        ("all", "功能窗（录制红边框）：固定色/倒计时大字号"),
    "src/Plugins/LesTool.Plugin.Screenshot/RecordPreviewWindow.xaml":
        ("all", "功能窗（录制预览播放器）：固定色；裸 Slider 属存量（勿新增）"),
    "src/Plugins/LesTool.Plugin.Preview/PreviewWindow.xaml":
        ("all", "功能窗（文件预览播放器）：阴影色固定"),
    "src/LesTool/Views/Pages/HotkeyRow.xaml":
        ("all", "弹层自绘滑杆模板 Thumb 固定白（DESIGN.md 例外白名单3）"),
    "src/Plugins/LesTool.Plugin.Clipboard/ClipboardPanelWindow.xaml":
        (("controls",), "存量：HistoryList 无样式 ListBox（2026-10-19 已验收），待迁 ItemsControl；勿新增同类"),
    "src/Plugins/LesTool.Plugin.SmartSearch/SmartSearchWindow.xaml":
        (("controls",), "存量：CandidateList 无样式 ListBox（2026-10-29 已验收），待迁移；勿新增同类"),
}

EXCLUDED_DIRS = {"obj", "bin", "Themes", "Skins"}  # Themes/Skins 是样式与皮肤本体，非页面


def tag_pattern(name: str) -> re.Pattern:
    return re.compile(r"<" + name + r"\b[^>]*>", re.S)


def check(root: Path) -> int:
    violations = []
    scanned = 0
    for path in sorted(root.glob("src/**/*.xaml")):
        if any(part in EXCLUDED_DIRS for part in path.relative_to(root).parts):
            continue
        rel = path.relative_to(root).as_posix()
        waived_all, waived = _waivers(rel)
        text = COMMENT.sub("", path.read_text(encoding="utf-8-sig"))
        scanned += 1
        _check_controls(rel, text, waived_all, waived, violations)
        _check_colors(rel, text, waived_all, waived, violations)
        _check_fontsizes(rel, text, waived_all, waived, violations)
        _check_plugin_sections(rel, text, waived_all, waived, violations)

    for rel, line, rule, message in violations:
        print(f"{rel}:{line} [{rule}] {message}")
    if violations:
        print(f"UI 门禁未过：{len(violations)} 处违规（规则见 DESIGN.md「页面控件门禁」）")
        return 1
    print(f"UI 门禁通过：扫描 {scanned} 个 XAML，0 违规")
    return 0


def _waivers(rel: str):
    entry = ALLOWLIST.get(rel)
    if entry is None:
        return False, set()
    rules, _ = entry
    return rules == "all", set() if rules == "all" else set(rules)


def _line_of(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def _check_controls(rel, text, waived_all, waived, violations):
    if waived_all or "controls" in waived:
        return
    for name in sorted(HARD_BANNED):
        for m in tag_pattern(name).finditer(text):
            violations.append((rel, _line_of(text, m.start()), "controls",
                               f"禁用控件 <{name}>（无隐式样式，替代写法见 DESIGN.md 控件速查白名单）"))
    for name in sorted(STYLE_REQUIRED):
        for m in tag_pattern(name).finditer(text):
            if "Style=" not in m.group(0):
                violations.append((rel, _line_of(text, m.start()), "controls",
                                   f"<{name}> 未带 Style=（无隐式样式；确需使用先在 DESIGN.md 登记显式样式）"))


def _check_colors(rel, text, waived_all, waived, violations):
    if waived_all or "color" in waived:
        return
    for m in COLOR_ATTR.finditer(text):
        value = m.group(1)
        if HEX_COLOR.fullmatch(value):
            violations.append((rel, _line_of(text, m.start()), "color",
                               f'硬编码颜色 "{value}"（应走 DynamicResource 语义键）'))
        elif value in NAMED_BANNED:
            violations.append((rel, _line_of(text, m.start()), "color",
                               f'命名颜色 "{value}"（应走 DynamicResource 语义键）'))


def _check_fontsizes(rel, text, waived_all, waived, violations):
    if waived_all or "fontsize" in waived:
        return
    for m in NUM_FONTSIZE.finditer(text):
        start = m.start()
        window = text[max(0, start - 400):start + 200]
        if MDL2 in window:  # 图标元素字号豁免（图标不是文字）
            continue
        violations.append((rel, _line_of(text, start), "fontsize",
                           f'数字字号 FontSize="{m.group(1)}"（文字四档用 DynamicResource FontSize*）'))


TAG_TOKEN = re.compile(
    r"<(/?)([A-Za-z_][\w.\-]*(?::[A-Za-z_][\w.\-]*)?)"
    r"((?:\"[^\"]*\"|'[^']*'|[^>\"'])*?)(/?)>")


def _scan_elements(text):
    """轻量标签扫描（刻意不用 XML 解析器：门禁只服务自家 XAML，正则深度追踪足够，
    也不引入实体扩展面）。产出 (depth, localname)：depth = 父级深度（根元素 depth=0）；
    自闭合不占深度。"""
    depth = 0
    for m in TAG_TOKEN.finditer(text):
        closing, raw_name, _, self_close = m.groups()
        name = raw_name.rsplit(":", 1)[-1]  # 剥 XAML 前缀（controls:SettingsSection → SettingsSection）
        if closing:
            depth = max(0, depth - 1)
            continue
        yield depth, name
        if not self_close:
            depth += 1


def _check_plugin_sections(rel, text, waived_all, waived, violations):
    """插件设置页结构（PLUGIN_UI_SPEC A）：根 = UserControl，内容 = 单个裸 StackPanel，
    其子元素全部是 SettingsSection（任意 XAML 前缀，按局部名比较）。
    页框（页头/滚动/边距/热键区）宿主管，插件不自建。"""
    if waived_all or "sections" in waived or not PLUGIN_SETTINGS_PAGE.match(rel):
        return
    elements = list(_scan_elements(text))
    if not elements or elements[0][1] != "UserControl":
        violations.append((rel, 1, "sections", "插件设置页根元素应为 UserControl"))
        return
    level1 = [name for depth, name in elements if depth == 1]
    if len(level1) != 1 or level1[0] != "StackPanel":
        violations.append((rel, 1, "sections",
                           "插件设置页根内容必须是单个 StackPanel 包装层（页框由宿主统一包装，PLUGIN_UI_SPEC A）"))
        return
    stray = next((name for depth, name in elements if depth == 2 and name != "SettingsSection"), None)
    if stray is not None:
        violations.append((rel, 1, "sections",
                           f"插件设置页只允许 SettingsSection 区块（发现 <{stray}>，PLUGIN_UI_SPEC A）"))


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    parser = argparse.ArgumentParser(description="UI 规范门禁（DESIGN.md）")
    parser.add_argument("--root", default=str(Path(__file__).resolve().parents[1]),
                        help="仓库根目录（默认：脚本上两级）")
    args = parser.parse_args()
    root = Path(args.root).resolve()
    if not (root / "src").is_dir():
        print(f"未找到 src 目录，仓库根不对：{root}")
        return 1
    return check(root)


if __name__ == "__main__":
    sys.exit(main())
