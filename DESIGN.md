# LesTool 设计规范（强约束）

基于 Fluent Design 2（Win11 规范）裁剪。原则：**约束越强越好**——规格全部资源化，控件不写样式也自动合规；与本文冲突的写法一律视为 bug。

## 落地机制

- `Themes/Design.xaml`：规格常量 + **全局隐式样式**（Button / TextBox / CheckBox / RadioButton），App 级生效，所有窗口（含功能窗、以后新增的）自动继承。
- `Themes/Dark.xaml` / `Light.xaml`：主题色画刷，`ThemeManager` 运行时整组替换；`Design.xaml` 常驻不动。
- 显式样式（如导航项 `NavItemStyle`）允许存在，但仅限列表中登记的特例。

## 字号（只许四档）

| 档 | 值 | 资源 key | 用途 |
|---|---|---|---|
| Title | 28 | `FontSizeTitle` | 页面主标题 |
| Subtitle | 20 | `FontSizeSubtitle` | 页面内区块大标题 |
| Body | 14 | `FontSizeBody` | 正文、菜单项、控件内文字 |
| Caption | 12 | `FontSizeCaption` | 分组标题、弱提示、辅助说明 |

禁止其他字号。等宽数字（时长/尺寸）用 Consolas + Body/Caption 档。

## 圆角（只许两档）

| 值 | 资源 key | 用途 |
|---|---|---|
| 4 | `CornerRadiusControl` | 按钮、输入框、复选框、导航项、小浮条 |
| 8 | `CornerRadiusOverlay` | 卡片、面板、对话框级浮层 |

Win10 无窗口圆角（系统限制，不做）。禁止 2 / 6 等其它值。

## 间距与尺寸

- 一切间距取 **4px 网格**（4/8/12/16/20/24/32/36…）。
- 标准控件高度 **32**（`ControlHeight`）；按钮不写 Height，宽度按内容需要单独给。
- 页面内容边距统一 `Margin="36,32,36,0"`（横向 36、顶部 32，底部自适应）。
- 卡片内边距 16,14；紧凑控制条 10,5。

## 状态过渡（强约束）

**所有交互状态变化（hover / checked / focus）必须有过渡动画**：淡入 100ms、淡出 150ms；按下（pressed）允许瞬变（要求跟手）。

- 实现模式统一：**覆盖层 + Opacity 动画**（Trigger 的 EnterActions/ExitActions + DoubleAnimation），不对画刷本体做动画（DynamicResource 资源无法作为动画目标）。
- 已接线：全局 Button/CheckBox/RadioButton/TextBox（focus 描边）、CaptionBar 按钮、导航项、设置页主题卡片——新控件照抄此模式。
- 例外白名单：截图遮罩工具条的选中态（code-behind 即时设 Background，瞬态操作界面要求即时反馈）。

## 颜色

- **禁止在页面/窗口 XAML 硬编码颜色**，一律 `DynamicResource` 引用主题画刷（key 清单见两个主题字典）。
- 新增颜色 → 同时进 Dark/Light 两套字典，语义命名（`XxxBackground` / `XxxForeground` / `XxxBorderColor`）。
- **语义色不随主题变**（固定值）：保存绿 `#2E9E5B`、录制红 `#E53935`、录制按钮底 `#B33A3A4A`、遮罩黑 `#E6101010`。
- 按钮状态色：`ButtonHoverBackground` / `ButtonPressedBackground`（主题字典），隐式样式已接线。

## 窗口分级

| 级别 | 窗口 | 规则 |
|---|---|---|
| 常规窗口 | 主窗口、录制预览、长截图预览/结果 | **自绘标题栏**（LesWindowStyle + CaptionBar，见下）+ 主题色 |
| 面板窗 | 命令面板（PanelShellWindow） | 无边框透明窗、无标题栏；表面跟主题（`PanelBackground`，两字典各一份）；圆角 8 + 1px 描边；抢焦点、顶部居中弹出、失焦即收 |
| 托盘浮窗 | 音量混音器（VolumeMixerWindow） | 面板窗同款表面/圆角/描边；主屏工作区右下角浮出、底边钉住（列表只向上长）、失焦即收；托盘左键呼出 |
| 功能窗 | 截图遮罩、轮盘、钉图、录制控制条/红边框 | 无边栏（WindowStyle=None）；表面固定深色半透明（功能性）；内部控件仍吃全局隐式样式，按钮文字白色 |

## 自绘标题栏（术语：本项目说「标题栏」默认指自绘，说「原生标题栏」才是系统标题栏）

常规窗口一律自绘，不用系统标题栏：

1. 窗口根元素加 `Style="{StaticResource LesWindowStyle}"`（Design.xaml：`WindowStyle=None + AllowsTransparency` 透明窗口，圆角 8（`CornerRadiusOverlay`）+ 1px 主题描边，**无阴影**（用户拍板）；系统交互由 `BorderlessWindow` 行为接管——WS 样式 + NCCALCSIZE 吞非客户区 + NCHITTEST 命中 + GETMINMAXINFO 钉工作区）。
2. 内容外包 `DockPanel`，顶部加 `<controls:CaptionBar DockPanel.Dock="Top"/>`（logo + 标题自动取窗口 Title；三按钮 44x32，悬停为内缩圆角色块，关闭键 hover 红）。

已知取舍：透明窗口路线下拖到屏幕边缘的 Aero Snap 不保证生效（Win10 对无边框透明窗口的系统行为），实测为准。

## 登记的显式样式特例

| 样式 | 位置 | 原因 |
|---|---|---|
| `NavItemStyle` | MainWindow | 侧栏导航项（左指示条形态） |
| `ThemeOptionStyle` | SettingsPage | 主题选择卡片 |
| `GroupHeaderStyle` | MainWindow | 分组小标题 |
| `LesWindowStyle` | Design.xaml | 常规窗口基样式（WindowChrome 自绘标题栏） |
| `CaptionButtonStyle` / `CaptionCloseButtonStyle` | CaptionBar | 标题栏按钮（方形 46x40，无圆角；关闭 hover 红） |
| `MixerSliderStyle` | VolumeMixerWindow | 混音器滑杆（4px 轨道 + 14 圆点 Thumb；填充色取 `Slider.Foreground`，静音态 code-behind 换 `SliderMutedFillBrush`） |
| `TrayMenuStyle` / `TrayMenuItemStyle` / `TrayMenuSeparatorStyle` | Design.xaml | 托盘右键菜单（仅 TopLevelItem 简单形态，带 hover 过渡） |
| `CommandItemStyle` | Design.xaml | 命令清单行（命令面板搜索结果、托盘左键动作选择）：透明底 + hover `NavHoverBackground` 100/150ms，选中态由消费方把 `BorderBrush` 换 `NavAccent`，行内容（字形/名称/徽标）由消费方给 |

新显式样式须先在此登记。

## 例外白名单（允许硬编码颜色的地方）

1. SettingsPage 主题预览色块——必须固定展示某主题的样子。
2. 功能窗深色表面（遮罩 `#96000000`、控制条 `#E6101010`、录制红边框）。
3. 语义色按钮（保存/确认/复制全部的绿底、录制的暗红底）及功能窗按钮白字。
4. code-behind 按选中态直接设置 `Background`（OverlayWindow 工具条），本地值优先于隐式样式，机制兼容。

## 标题栏

见「自绘标题栏」章节。DWM 黑/白两态代码保留（对自绘窗口为无害空操作，功能窗备用）。
