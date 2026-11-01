# 动作契约层重构方案（2026-10-31）

> **状态：已于 2026-10-31 全部实施**（五步各构建 0 错）。一处行为保全修正：smartsearch.go
> 的 SelectedText/Cursor 归**选填**而非方案表中写的必填——其"无选中 → 输入条"降级是已验收
> 行为，必填会被平台拦截而砍掉该降级。以下为原始方案，留档备查。
>
> **2026-11-01 追加修订（用户拍板）**：热键区交互不照搬旧热键页——行 = 命令，
> 每行就是 命令名 + 键位控件（点击捕获、Backspace 清除、Esc 取消），删除"添加绑定/
> 选动作/启停开关/删行"整套；**一命令一键位**（提交时收敛该命令全部旧绑定，
> 原 §7 验收项③的"绑第二个键"随之作废）；HotkeyRow 整个控件退役删除；
> 插件卸载遗留的无主绑定改由启动期 CleanupOrphanHotkeys 统一清理（新 UI 看不见它们）。
>
> 落实 2026-10-26/27/31 系列拍板：动作双属性（触发场景 + 必填/选填上下文）、入口能力表、
> Focus 命名、空格预览退注册、快捷键页迁入插件页。菜单收敛（砍右键菜单/事件触发/
> 命令总览/后台动作）已于 2026-10-29 实施，本批是其续篇。全部拍板依据见项目记忆
> lestool-dual-input-design；与代码不符时以代码为准、顺手更新本文档。

## 0. 不动的东西（边界）

- **settings.json 存储格式**：Hotkeys 绑定列表原样，只拆 UI 不动存储；
- **HotkeyService**：全局注册/冲突检测链路、启动冲突 Toast 原样；
- **轮盘场景系统与轮盘设置页**：跨插件全局编排，留在全局（界线=单命令属性跟插件走、多命令编排留全局）；
- **命令面板浮窗本体**；**空格预览插件内部行为**（仅删其命令注册）；
- **轮盘展示前 UIA 探测门禁**（2026-10-29 修订的"显示期可见性"，不属本批）；
- **触发来源是数据**（TriggerSource/LesContext.Source，2026-10-16 拍板）：Scenarios 只是绑定 UI 的静态声明，两者并存。

## 1. Platform 契约（src/LesTool.Platform）

### 1.1 新枚举 CommandScenarios（Contracts/LesCommand.cs 内）

```csharp
[Flags]
public enum CommandScenarios
{
    None = 0,
    Hotkey = 1 << 0,
    Palette = 1 << 1,
    Radial = 1 << 2,
    All = Hotkey | Palette | Radial,   // 缺省=存量兼容（无声明三源全开）
}
```

### 1.2 ContextKind.ActiveWindow → Focus

位值不变（1 << 3）。settings.json 不存 ContextKind，无序列化牵连。
理由（2026-10-27 拍板）：面板路径下字段值来自弹出前快照，派发时刻字面"前台窗口"是面板自己；
"焦点"（触发前用户焦点所在窗口）名实相符，且与 Cursor/Clipboard 短名对仗。

### 1.3 LesCommand 增属性

`CommandScenarios Scenarios`、`ContextKind Optional`；构造函数变
`(id, name, glyph, keywords, kind, scenarios, requires, optional, callback)`。
Requires 语义收窄为**必填**（缺了动作无意义）；Optional 为**选填**（缺了降级不失效）。

### 1.4 LesContext / TriggerSnapshot 字段更名

`ActiveWindowTitle → FocusWindowTitle`、`ActiveWindowHandle → FocusWindowHandle`。
消费点共 3 处：ContextProvider（ActionContext.cs）、PanelShellWindow 快照日志、ClipboardPlugin。

### 1.5 ILesHost.RegisterCommand / ActionRegistry.Register 追加可选参数

```csharp
void RegisterCommand(string id, string name, string glyph, Action<LesContext> callback,
    string keywords = "", ContextKind requires = ContextKind.None,
    CommandScenarios scenarios = CommandScenarios.All, ContextKind optional = ContextKind.None);
```

追加在现有可选参之后；同仓插件同批重编。**Platform 版本 1.0.0 → 1.1.0**（接口加参是
破坏性变更，外部插件需重编；旧声明经缺省 All 语义不变）。

## 2. 注册表门禁（src/LesTool/Services）

### 2.1 ScenarioCapabilities（新文件）

入口能力表（2026-10-26 拍板 + 10-29 面板取不到 SelectedText 的既定事实）：

| 入口 | 能力 |
|---|---|
| Hotkey / Radial | 全量（ContextKind 全部成员） |
| Palette | 全量 & ~(SelectedFiles \| SelectedText)（面板抢焦点，选中项结构性取不到） |

Searchable 现有的 SelectedText 特判**随本表落地同时删除**，避免双轨规则漂移。

### 2.2 ActionRegistry

- `ForScenario(CommandScenarios)` 替代 Bindable/Searchable：
  `Scenarios.HasFlag(s) && ScenarioCapabilities.Provides(s, Requires)`；
  `Kind != Builtin` 一刀切与"无插件命令回退全量"删除（panel.launcher 改显式声明后不再需要）；
- **注册期矛盾检测**：scenarios 含某入口而 Requires ⊄ 该入口能力 → `Log.Warning`
  （AI 写插件自相矛盾在注册期暴露，错误到不了用户）；
- **Execute 必填拦截**：Build(requires | optional) 后逐项判空——SelectedText/ClipboardText/
  SelectedFiles 为空 → Toast「{命令名}需要{上下文中文名}，当前不可用」+ `Log.Information`，
  不进回调；选填字段空照常派发（动作自判）。派发日志拆 requires/optional 两段。

### 2.3 ContextProvider（ActionContext.cs）

字段与注释随 Focus 更名；快照优先逻辑不变；Build 入参改为合并后的 kind。

## 3. 存量声明改写（现有 14 条 → 13 条）

| 注册点 | Scenarios | Requires | Optional |
|---|---|---|---|
| panel.launcher（App.xaml.cs，内置） | **Hotkey** | — | — |
| screenshot.region | All | — | — |
| volume.mixer | All | — | — |
| clipboard.panel | All | — | **Focus**（原 requires:ActiveWindow 改选填） |
| downloads.organize | All | — | — |
| smartsearch.go | **Hotkey \| Radial** | SelectedText | — |
| theme.toggle | All | — | — |
| browser.* ×7 | All | — | — |

**preview.toggle：删除注册**（PreviewPlugin.cs RegisterCommand 调用 + 文件头注释
"三入口均可触发"作废）。空格监听不受影响；已核实无任何默认绑定指向它。

## 4. 快捷键页迁入插件页

### 4.1 HotkeySection 通用控件

HotkeyPage 逻辑泛化：构造参数=命令 id 前缀（插件 Id）；列出该前缀命令的现有绑定，
添加行候选=前缀内命令 ∧ ForScenario(Hotkey)；行内重复检测跨全部行（全局）；
PersistAndRefresh 照旧=保存 settings.json → HotkeyService.ApplyAll → 按注册结果刷状态。
HotkeyRow 原样复用。

### 4.2 PluginHost 自动注入

PluginHostContext 记录本插件注册的命令 Id；插件 Initialize 返回后统一处理：

- 有命令、无设置页（**Browser / Screenshot / Theme / Volume**，2026-10-31 核实）→
  自动 RegisterSettingsPage(manifest.Name, 首命令 glyph, () => 仅含 HotkeySection 的页)；
- 有命令、有设置页 → 包装既有工厂：原 UI + 分隔标题"快捷键" + HotkeySection。

### 4.3 命令面板页转正

"panelcfg" 路由占位页 → 真 PanelSettingsPage（一句话简介 + HotkeySection("panel.")）；
占位文案"在「快捷键」页可查看与调整"随之删除。

### 4.4 删除与菜单

删 HotkeyPage.xaml(.cs)；菜单去"快捷键"项；默认落地页=命令面板（触发场景组第一项）；
App.xaml.cs 通道分工注释段重写。导航最终形态：**命令面板 / 轮盘手势 | 设置 | 插件组**。

## 5. 文档同步

- PLUGIN_DEV.md：§4 契约 API 新签名；§5 命令——双属性语义、能力表、"四触发源全量可用"
  旧句重写、热键区自动注入说明（注册命令即免费获得，作者零成本）；
- PROJECT_STRUCTURE.md：页面清单（HotkeyPage 出、HotkeySection/PanelSettingsPage 入）；
- ILesPlugin.cs 注释中 ContextKind 举例同步。

## 6. 实施顺序（每步 Release 构建 0 错再进下一步）

1. Platform 契约改动 + Focus 更名机械收尾（编译器驱动，改完 3 处消费点）；
2. 能力表 + ForScenario + 注册期矛盾检测 + 运行时必填拦截 + 存量声明改写；
3. preview.toggle 退注册；
4. HotkeySection + PluginHost 注入 + 面板页转正 + 删 HotkeyPage + 菜单更新；
5. 文档同步。

## 7. 验证

- 每步 `dotnet build -c Release` 0 错（构建不启动应用）；
- 动态验收由用户执行，清单：
  ① 命令面板清单含截图/音量/剪切板/主题/浏览器/下载整理，**不含**智能搜索与空格预览；
  ② 任一插件页底部有"快捷键"区，可添加/改键/删除并实际生效，跨插件同键冲突有提示；
  ③ "命令面板"页可改面板默认键、可为 panel.launcher 绑第二个键；
  ④ 资源管理器无选中时轮盘不显示智能搜索（原有行为不回归）；轮盘槽位候选清单正常；
  ⑤ 空格预览功能照常，三入口均不再出现该命令；
  ⑥ 日志：派发行含 requires/optional 分段；无"声明矛盾"Warning；必填缺失触发拦截 INFO。

## 8. 风险与对策

- **接口加参=破坏性变更**：同仓 8 个插件同批重编；Platform 1.1.0；缺省 All 保语义兼容；
- **热键区嵌插件页的排版**：统一 StackPanel + 分隔标题，遵循 PagePadding 与设计章程；
- **双轨门禁残留**：Searchable 的 SelectedText 特判必须与能力表同一步删除。
