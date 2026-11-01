# LesTool 设计规范（Token 化强约束）

风格定案：**Swiss Precision**（极简瑞士风 × 深色优先的冷静效率感）。2026-10-21 由 ui-pipeline 管线生成（ui-ux-pro-max 定方向 + Refactoring UI 定数值纪律），替代旧版手挑 Fluent 约束；同日二次调整为**散光舒适档**（见颜色节）。原则不变：**约束越强越好**——规格全部资源化，控件不写样式也自动合规；与本文冲突的写法一律视为 bug。

> 风格已定案，后续会话**不再重新选风格**；要换风格 = 改 `design-tokens.json` 重新生成，不是在页面里改颜色。

## 控件速查白名单（写页面前先看这张表）

| 直接用（有全局隐式样式/无自绘外观） | 禁用（无样式，用了即裸奔原生系统控件）→ 替代写法 |
|---|---|
| Button / TextBox / CheckBox / RadioButton / ToolTip / ScrollBar | ComboBox → RadioButton 组（选项 ≤5 时）或行内 TextBox |
| TextBlock / Grid / StackPanel / DockPanel / UniformGrid / WrapPanel / Border / ScrollViewer / ItemsControl / ContentControl | ListBox / ListView → ItemsControl + DataTemplate（行内编辑的行模型用可变 class——record 是 init-only，TwoWay 绑不上） |
| Slider —— 仅当带登记样式（MixerSliderStyle / TbSlider） | GroupBox / TabControl → 插件设置页用 `Platform.Controls.SettingsSection`（PLUGIN_UI_SPEC 强制）；其余页面平铺分区：Subtitle 标题 + caption 弱提示 |
| Segoe MDL2 图标元素（图标字号不受四档文字刻度限制） | DataGrid / TreeView / Menu / ToolBar / Expander / Calendar / DatePicker → 一律不用 |

机器门禁：`python tools/check_ui.py`（已挂主工程构建，违者构建失败）；白名单与理由见「页面控件门禁」节。

## 唯一数值来源（本规范的根）

**一切 UI 数值（颜色/字号/间距/圆角/字重/阴影）的唯一来源 = 仓库根 `design-tokens.json`。**

生成链（改值必走，禁止手改生成物）：

```
design-tokens.json
  ├─ emit_tokens.py  → Themes/tokens.xaml（原始刻度：色板/字号/间距/圆角/阴影）
  ├─ emit_tokens.py  → docs/preview.html（可视化色板页，浏览器打开即查）
  └─ tools/emit_themes.py → Themes/Dark.xaml / Light.xaml（语义画刷，45 键）
                          + 后处理 tokens.xaml（sys:Double/冻结阴影）
                          + 校验 Design.xaml 常量与 JSON 一致（漂移即报错退出）
```

具体命令记录在 `design-tokens.json` 的 `$meta.regen`。**改任何颜色/字号/间距：改 JSON → 重跑生成 → 重跑对比度门禁**（`check_contrast.py --matrix design-tokens.json`，27 对全过才算数）。

缺值怎么办：**加一个 token（进 JSON），不写任意值**。微调既有色档是允许的（§5.3.5），新增色档会毁掉刻度。

## 落地机制（WPF 结构）

- `App.xaml` 合并顺序固定：**tokens.xaml（刻度）→ Design.xaml（样式）→ Dark.xaml（主题）**；ThemeManager 运行时只整组替换主题层（内置 Dark/Light 或皮肤包装层，按 `__ThemeLayer` 标记清理，加到末尾）。
- `Themes/Design.xaml`（手写）：全局**隐式样式**（Button / TextBox / CheckBox / RadioButton / ScrollBar / **ToolTip**）+ `LesWindowStyle` + 登记的显式样式。App 级生效，所有窗口（含插件窗）自动继承。
- 旧契约常量键（`FontSizeTitle` 等 8 个 + `PagePadding`）落在 Design.xaml 字面值，**由 tools/emit_themes.py 与 JSON 校验一致性**——BAML 编译器不支持 StaticResource 作资源条目别名，只能以脚本校验代替引用。
- **插件窗口同权**：隐式样式与语义画刷经 App 级资源跨程序集自动继承；插件 XAML 引用宿主资源一律 DynamicResource（pack URI `/LesTool;component/...` 仅用于资产）。
- **隐式样式不继承子类（2026-11-01 页头键位钮实锤）**：`TargetType="Button"` 的隐式样式只命中精确的 Button，自定义类若 `: Button` 拿不到它，会以系统原生皮肤出街——自定义控件要么内嵌纯 Button（CommandRow 模式），要么显式 `SetResourceReference(StyleProperty, typeof(基类))` 挂回制服。
- **页面控件门禁（2026-10-31 立，下载归档页违禁控件事故后；同日新增第 ④ 条）**：`tools/check_ui.py` 扫描 `src/**/*.xaml`
  （排除 Themes/Skins 与下述白名单），四类违规构建即失败（挂 `LesTool.csproj` 的 UiGate 目标，依赖 python 在 PATH）：
  ① **禁用控件**——上表禁用列；ListBox/ListView/Slider 须带 `Style=`（登记过的显式样式）；
  ② **硬编码颜色**——画刷属性上的 `#RRGGBB[AA]` 或命名色（White/Black/Red 等；Transparent 豁免）；
  ③ **数字字号**——`FontSize="数字"`（文字只许四档 `DynamicResource FontSize*`；MDL2 图标元素字号豁免）；
  ④ **插件设置页结构**——`src/Plugins/**/*SettingsPage.xaml` 根内容必须是单个裸 StackPanel、
    子元素只能是 `SettingsSection`（页框宿主管，规则正文见 docs/PLUGIN_UI_SPEC.md）。
  白名单登记在脚本 `ALLOWLIST`，**加白必须同步在本节登记理由**（脚本与文档互为镜像）：
  - 截图/录制功能窗族（Overlay / LongBar / LongFrame / LongPreview / OcrResult / QrResult / Pin / RecordBar / RecordFrame / RecordPreview）：固定色与图标字号，属例外白名单 2/3/4；RecordPreview 的裸 Slider 属存量，勿新增；
  - PreviewWindow（播放器阴影色固定）；HotkeyRow（弹层自绘滑杆模板 Thumb 固定白，例外白名单 3）；
  - ClipboardPanelWindow（HistoryList）、SmartSearchWindow（CandidateList）：**存量无样式 ListBox，待迁移**——只豁免既有控件，禁止新增同类。

## 皮肤包（外置主题，2026-10-21）

**主题选择 = 槽位模型**（2026-10-21 定案，内置深浅主题退役为兜底）：深色组/浅色组各选一套皮肤 + 独立的「跟随系统」开关。不跟随时生效 = 手动点选的那套；跟随时按 Windows 深浅在两组所选间自动切换（设置存 settings.json：`Theme`=生效皮肤 id、`ThemeFollowSystem`、`ThemeDark`、`ThemeLight`；旧值 Dark/Light/System/Skin:x 启动时自动迁移）。

**皮肤 = 磁盘上的独立字典文件**（exe 旁 `themes\<id>.xaml`），与内置主题同一套 45 键契约；运行期由 ThemeManager 扫描目录、设置页两组选卡、**保存文件即热重载**（400ms 防抖；解析失败记 ERROR + Toast 并保持原样）。皮肤缺键时按自身深浅回落内置 Dark/Light 打底（两层合并）。

- **唯一来源**：`design-skins.json`（与 design-tokens.json 同管线）→ `tools/emit_themes.py` 生成 `src/LesTool/Skins/*.xaml`，构建拷到 `themes\`。每套皮肤 14 组对比度门禁（**透明色按实际叠加混合后计算**），全部 ≥4.5:1 且散光舒适带 ≤12:1 才放行。
- **皮肤声明（SkinMeta 资源）**：`Name` 显示名、`IsLight` 深浅（决定分组、兜底基底与 DWM 标题栏）、`Acrylic` 是否玻璃、`SkinGlassTint` 亚克力底色。
- **氛围光**：皮肤可给 `WindowGlowOverlay`（径向渐变画刷）；浅色皮肤/内置主题为全透明。窗口模板里它是独立装饰层，永远 `IsHitTestVisible=False`。
- **亚克力（磨砂）**：面板窗挂 `platform:GlassWindow.Enabled="True"`（命令面板/剪贴板/混音器已挂）；仅当皮肤同时声明 `SkinMeta.Acrylic=true` **且 PanelBackground 带透明度**时才有磨砂效果（实底下模糊不可见）。面板表面**默认全不透明**（用户 2026-10-21 拍板），现四套皮肤均未启用磨砂。主窗口**不挂**亚克力（大窗口失焦闪烁）。
- **手写皮肤文件须知**：必须是**纯画刷叶子字典**——只有 SolidColorBrush/渐变画刷和 SkinMeta 标量，禁止 StaticResource 交叉引用、禁止 x:Class（loose XAML 编译期解析不了）；新皮肤优先走 design-skins.json 生成，别手写。8 位十六进制一律 CSS 顺序 **#RRGGBBAA（透明度在最后）**。
- **现有皮肤**：深色组 `glass` 玻璃拟态（夜色蓝 + 珊瑚橙）、`aurora` 深空极光（深空底 + 紫罗兰 + 氛围光）；浅色组 `fluent` Fluent 2（Win 蓝 + 白卡）、`paper` 暖纸陶土（米色纸面 + 陶土橙）。

## 颜色

四条算法生成的色板（Refactoring UI 第 5 章程序产出，不是手挑）：冷蓝灰 neutral 11 档、靛蓝 primary 11 档、危险红 danger、成功绿 success。整表见 `docs/preview.html`。

- **禁止页面/窗口 XAML 硬编码颜色**，一律 `DynamicResource` 语义键（清单 = Dark/Light.xaml 的 45 个键）。
- **全局唯一强调色 = primary 族**（语义键 `NavAccent`：深色 #8DCDFE / 浅色 #2E4DCE）。强调色只用于：选中态、焦点环、勾选、链接式动作。一页别超过一处强调块。
- **文字三档层级**（`TextPrimary` / `TextSecondary` / `TextHint`），每档对所在背景对比度 ≥4.5:1（矩阵已验证，改色必须复跑门禁）：
  - 深色：#E1EAEF / #C4D1D8 / #A8B6C0（配 #303345 窗口底；对比 10.2 / 8.0 / 6.0）
  - 浅色：#303345 / #50566A / #60687D（配 #E1EAEF 窗口底；对比 10.2 / 6.0 / 4.6）
- **散光舒适档（2026-10-21 用户拍板）**：正文对比压在 **10:1 以内**、提示档贴 4.5 底线——不用近乎纯白的字、不用死黑的底；深色底至少 23% 亮度、浅色底至多 91% 亮度。调色时**不许**把两端重新拉开的"高对比"档加回来。
- **灰字不上彩色底**：要弱化就在同色相里降对比（强调底上用 `AccentForeground`，不是白色半透明）。
- 语义色不作装饰：`DangerBrush` 只表危险（错误文字/删除确认），`SuccessBrush` 只表成功。
- 新增颜色 = JSON 语义树加键（两棵树都要），不进页面。

**功能窗固定色**（不随主题，值在 JSON `color.functional`，插件 XAML 白名单内可直接写死）：遮罩 60% 黑、控制条 90% 黑、录制红 #E53935、录制按钮底、保存绿（= success-500）。

## 字体与字号

- 全局字体：`FontSans` = **Segoe UI, Microsoft YaHei**（zh-CN 桌面规则：拉丁 Segoe UI、中文雅黑；LesWindowStyle 已接线，无样式窗口自行 `DynamicResource FontSans`）。
- **只许两字重**：Normal（400）正文 / SemiBold（600）标题与强调。禁止 Light/Black 及斜体。
- **字号只许四档**（刻度值，禁其他）：

| 档 | 值 | 资源 key | 用途 |
|---|---|---|---|
| Title | 24 | `FontSizeTitle` | 页面主标题（一页一个；插件页头名称） |
| Subtitle | 18 | `FontSizeSubtitle` | 页面级次标题（少量场景） |
| Body | 14 | `FontSizeBody` | 正文、菜单项、控件文字、**区块标题（SemiBold，2026-10-31 起含插件设置页，见 PLUGIN_UI_SPEC）** |
| Caption | 12 | `FontSizeCaption` | 分组标题、弱提示、徽标 |

- 中文正文行高 1.6–1.8（页面里已按此设 LineHeight）；长文左对齐，禁两端对齐。
- 数字（时长/尺寸/计数）用 `FontMono` = **Consolas**，列右对齐。

## 间距与尺寸

- 一切间距取 **space 刻度：4 / 8 / 12 / 16 / 24 / 32 / 48 / 64**；<4px 的微间隙视作描边级，不受此限。
- **组外距必须大于组内距**（Law 5）：卡片间距 12、区块间距 32（插件设置页 PLUGIN_UI_SPEC 统一 32）、组内 4/8。
- 标准控件高度 **32**（`ControlHeight`）；按钮不写 Height，宽度按内容。标题栏高 40（`CaptionHeight`）。
- 页面内容边距统一 `{StaticResource PagePadding}` = 32,24（横向 32、纵向 24）。
- 卡片内边距 16（紧凑侧栏 12）；弹层内边距 8。

## 圆角、描边、状态

- **圆角只许两档**：`CornerRadiusControl` = 6（按钮/输入框/复选框/导航项/气泡）；`CornerRadiusOverlay` = 10（卡片/面板/对话框/窗口）。滚动条滑块的 4 是胶囊几何（= 宽 8 的一半），不属刻度违例。
- **描边一律 1px**；选中/强调环 2px。卡片与窗口靠明度差分层，**不用阴影**（见下）。
- 禁用态统一 Opacity 0.45。

## 动效（强约束）

**所有交互状态变化（hover / checked / focus）必须有过渡**：淡入 **100ms**、淡出 **150ms**；按下（pressed）瞬变（跟手）。

- 实现模式统一：**覆盖层 + Opacity 动画**（Trigger EnterActions/ExitActions + DoubleAnimation），不动画画刷本体。
- 已接线：全局 Button/TextBox/CheckBox/RadioButton/ScrollBar、CaptionBar、导航项、Tab、主题卡片——新控件照抄。
- 例外白名单：截图遮罩工具条选中态（code-behind 即时设色，瞬态操作要求即时反馈）。

## 深度与阴影

- 窗口内分层（卡片 vs 底、侧栏 vs 内容）：**明度差 + 1px 描边**，零阴影（旧版拍板保留：主窗口无阴影）。
- 浮在桌面/任意内容之上的才给阴影（tokens 已冻结，可直接引用）：
  - 面板窗（命令面板/剪贴板/混音器）：`ShadowPanel`（0 10px 24px 20% 黑）
  - 弹层/下拉（Popup）：**不带阴影**——Popup 边距小，承载阴影得不偿失；1px 描边分离即可（2026-10-21 定）
- **分层窗阴影铁律**（2026-10-21 黑角实测）：`AllowsTransparency` 透明窗的根元素直接挂
  Effect，圆角外的透明像素会被合成成黑色三角；且 Border 铺满窗口时阴影整圈被裁掉（等于白挂）。
  正确做法：根 Border 加 `Margin`（≥BlurRadius，面板壳用 32）留出阴影空间，窗宽 = 视觉宽 + 两侧边距，
  窗 `Background=x:Null` 让边距区点击穿透。剪贴板/混音器尚未按此补阴影。
- 深色主题里"更亮 = 更浮起"（卡片亮于窗口、悬停亮于卡片）。

## 滚动条（强约束）

沿用：宽/高 8 胶囊滑块、全透明轨道、无箭头、轨道空白点击翻页；静止 0.5 → 悬停 0.9（100/150ms）→ 拖拽 1.0。恒深色浮层可在自身 Resources 覆盖 `ScrollBarThumbBrush` 为白（白名单）。

## 层级与动作（每页自查）

- **一页至多一个主操作**：主操作用 `PrimaryButtonStyle`（强调实心底）；常规动作用隐式 Button（次要档）；三级动作做成文字链接式。
- 危险动作在页面上通常是**三级**（DangerBrush 文字），大红实心只出现在确认对话框。
- 侧栏导航"以弱化衬选中"：选中项亮，未选中集体让路，别加粗加码。

## 窗口分级

| 级别 | 窗口 | 规则 |
|---|---|---|
| 常规窗口 | 主窗口、录制预览、长截图预览/结果 | **自绘标题栏**（LesWindowStyle + CaptionBar）+ 主题色、圆角 10 + 1px 描边、无阴影 |
| 面板窗 | 命令面板（PanelShellWindow） | 无边框透明窗、无标题栏；表面 `PanelBackground`（**默认全不透明**，2026-10-21 用户拍板）；圆角 10 + 1px 描边 + **ShadowPanel**；抢焦点、屏顶居中、失焦即收 |
| 浮窗 | 音量混音器、剪贴板历史 | 面板窗同款（表面/圆角/描边/阴影）；光标屏顶居中向下生长；失焦即收 |
| 功能窗 | 截图遮罩、轮盘、钉图、录制控制条/红边框 | 无边栏；表面固定深色（functional 族）；内部控件仍吃隐式样式 |

## 自绘标题栏（术语：「标题栏」=自绘，「原生标题栏」=系统）

1. 窗口根 `Style="{StaticResource LesWindowStyle}"`（透明窗口 + 圆角 10 + 1px 主题描边 + `FontSans`；系统交互由 `BorderlessWindow` 接管）。
2. 内容外包 `DockPanel`，顶部 `<controls:CaptionBar DockPanel.Dock="Top"/>`（三按钮 44x32；关闭键 hover 红 = `CaptionCloseBackground`）。

已知取舍：透明窗口的 Aero Snap 不保证（Win10 系统行为），实测为准。

## 登记的显式样式特例

| 样式 | 位置 | 原因 |
|---|---|---|
| `LesWindowStyle` / `CommandItemStyle` / `PrimaryButtonStyle` | Design.xaml | 窗口基样式 / 命令清单行 / 主按钮（强调实心） |
| `NavItemStyle` / `GroupHeaderStyle` | MainWindow | 侧栏导航项（左指示条）/ 分组小标题 |
| `TabButtonStyle` | RadialGesturePage | 页内 Tab（下划线选中） |
| `OptionCardStyle` | SettingsPage | 主题/周期选择卡片（2px 选中环） |
| `ActionItemStyle` | HotkeyRow | 动作弹层菜单项 |
| `CaptionButtonStyle` / `CaptionCloseButtonStyle` | CaptionBar | 标题栏按钮（44x32，关闭 hover 红） |
| `MixerSliderStyle` | VolumeMixer 插件 | 混音器滑杆（4px 轨道 + 14 圆点） |
| `RowItemStyle` / `TabRadioStyle` / 历史行悬停操作组 | ClipboardPanelWindow | 历史行/类别 Tab/悬停按钮组 |

新显式样式须先在此登记。

## 例外白名单（允许硬编码颜色/值的地方）

1. SettingsPage 主题预览色块——必须固定展示某主题的样子（值取自新色板）。
2. 功能窗固定表面色（遮罩/控制条/录制红/保存绿——即 JSON `color.functional` 记录的值）。
3. 功能窗内按钮白字（深色表面上的固定白）。
4. code-behind 按选中态直接设 Background（截图遮罩工具条）。

## 插件迁移备注

插件 XAML 里旧的内联圆角/内边距字面值（8/4 等）尚未替换为 DynamicResource token——键已就位（`CornerRadiusOverlay` 等），后续迭代逐窗替换即可，不影响当前运行。

存量无样式 ListBox 两处（页面控件门禁白名单内，勿新增同类）：ClipboardPanelWindow 的 HistoryList、SmartSearchWindow 的 CandidateList——待迁移 ItemsControl + DataTemplate。
