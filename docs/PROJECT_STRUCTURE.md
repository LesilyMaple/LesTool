# LesTool 项目结构说明

> 面向新加入的开发者；看完应能快速定位任意功能的代码位置。
> 基准日期：2026-10-15（P2/P3 插件迁移完成：截图与音量混音器已外置为插件）。项目演进较快，发现文档与代码不符时以代码为准、顺手更新本文档。
> 插件化设计与开发指南入口：`docs/PLUGIN_MIGRATION_PLAN.md`。

## 1. 项目概述

**做什么**：Windows 桌面效率工具。核心架构为"**平台 + 插件**"——宿主提供触发×命令矩阵（悬浮轮盘 / 命令面板 / 全局快捷键三类触发源，统一消费一张全局命令注册表 `ActionRegistry`），功能以**进程内插件**装载（plugins/ 目录，字节装载不锁文件）。2026-10-30 定案：所有动作统一走插件，用户脚本（.csx）体系退役。

**当前形态**：
- **平台（宿主）**：轮盘手势（Raw Input 旁路 + 钩子拦截回放）、命令面板、全局热键、剪贴板监听、主题、插件装载器（托盘常驻已于 2026-10-18 整体移除）
- **动作契约（2026-10-26/31 拍板）**：命令声明双属性——`scenarios`（可从哪些入口触发）+ 必填/选填上下文；平台按"入口能力表"门禁（轮盘/快捷键=全量，命令面板缺选中项）。热键配置在**各插件页的平台注入热键区**（无设置页的插件自动生成），面板键位在"命令面板"页；轮盘槽位配置留全局（跨插件编排）。自触发插件（空格预览）不走命令注册
- **插件**：**截图**（框选/窗口识别/标注/钉图/长截图/OCR/录屏，含 OCR 模型资产，ffmpeg 为可选资产）、**音量混音器**（EarTrumpet 式浮窗）、**剪切板历史**、**空格预览**（自触发）、**下载归档**、**智能搜索**、**主题切换**、**浏览器操作**（一插件多命令首例）
- **LesTool.Platform 契约程序集**：宿主与插件共同引用——命令模型、GDI 抓屏/窗口识别、RadialGuard/MouseEvents、Toast/剪贴板/应用图标服务、窗口镶边控件（CaptionBar/BorderlessWindow/CornerRadiusClip）

**技术栈**：

| 项 | 选型 | 备注 |
|---|---|---|
| 语言/框架 | C# / WPF / .NET 8（`net8.0-windows`，win-x64） | 刻意锁 8：开发机 Win10 无补丁，.NET 9+ 原生宿主因 CET 检查启动即崩 |
| SDK | 8.0.302（global.json 锁定，rollForward latestFeature） | |
| 插件契约 | `LesTool.Platform.dll` v1.3.0（ILesPlugin/ILesHost/IPluginAssets/IPluginSettings；RegisterCommand 带双属性 scenarios/requires/optional） | 插件只准引用 Platform，禁引宿主 |
| UI | Token 化体系（`design-tokens.json` 唯一数值源 → 生成 `Themes/tokens.xaml` + `Dark/Light.xaml`，`Design.xaml` 手写隐式样式） | 强约束成文于 `DESIGN.md`，**改 UI 前必读**；窗口镶边控件在 Platform |
| 日志 | Serilog → `%APPDATA%\LesTool\logs\`（Debug 级，按天滚动留 7 份） | 全项目用静态 `Serilog.Log` |
| OCR（截图插件内） | RapidOcrNet（PP-OCRv5） | 模型 = 插件资产 `plugins/screenshot/assets/models/v5/` |
| 系统 API | **手写 P/Invoke，不引钩子/热键/音频第三方库** | 鼠标键盘钩子、RegisterHotKey、GDI BitBlt、Core Audio COM、DWM |
| 录屏（截图插件内） | 外置 ffmpeg.exe（ddagrab 采集直喂硬件编码器） | **需自备**：放 `plugins/screenshot/assets/` 或程序目录（双路径兼容） |

**构建 / 运行 / 测试**：

```powershell
# 构建（日常工作流默认 Release；桌面快捷方式即指向该产物；插件随构建自动部署到宿主输出 plugins\）
dotnet build -c Release

# 运行
src\LesTool\bin\Release\net8.0-windows\win-x64\LesTool.exe

# 发布（自包含单文件；插件目录始终外置于 exe 旁，2026-10-14 拍板）
dotnet publish src/LesTool/LesTool.csproj -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

- **测试**：tests/LesTool.Tests 为无额外测试包的行为回归程序；运行 dotnet run --project tests/LesTool.Tests -c Release。发布脚本包含测试与 8 插件装载诊断；真实桌面功能仍需人工验收。
- **构建前注意**：先结束正在运行的 LesTool 实例，否则 exe 被锁导致构建失败。
- **运行期数据**（不在仓库）：`%APPDATA%\LesTool\`（settings.json、logs\、plugins.json、plugins\&lt;id&gt;\settings.json 每插件设置）；录屏临时段在 `%TEMP%\LesTool\rec\`；另存/长截图默认目录 `图片\LesTool`，录屏存 `视频\LesTool`（截图回车确认只写剪贴板，不落盘）。
- **插件卸载**：删宿主输出 `plugins\<id>\` 目录，重启生效（进程内程序集常驻）。

## 2. 目录树（注释）

```
LesTool/                          仓库根
├─ DESIGN.md                      UI 强约束规范：唯一数值源/字号四档/圆角两档/动画时长/窗口分级/显式样式登记表
├─ design-tokens.json             全部 UI 数值的唯一来源（色板/字号/间距/圆角/阴影 + dark/light 语义树 + 对比度门禁）
├─ design-skins.json              外置皮肤包来源（glass/aurora；同管线生成 + 对比度门禁）
├─ tools/emit_themes.py           从 design-tokens/skins.json 生成 Themes/Dark|Light.xaml、Skins/*.xaml 并后处理 tokens.xaml（勿手改生成物）
├─ README.md                      项目说明（内容滞后于现状）
├─ LICENSE                        MIT
├─ LesTool.sln                    解决方案（宿主 + Platform + 八个插件 + 行为回归工程）
├─ global.json                    锁 SDK 8.0.302
├─ scripts/make-icon.ps1          重新生成 Assets/app.ico（纯 ASCII，防 PS5.1 GBK 坑）
├─ docs/
│  ├─ PROJECT_STRUCTURE.md        本文档
│  └─ PLUGIN_MIGRATION_PLAN.md    插件化总体方案（含阶段进度）
├─ src/LesTool.Platform/          ★插件契约程序集（宿主与插件共同引用）
│  ├─ Capture/                    GdiCapture（GDI 抓屏/显示器/物理坐标摆窗）+ WindowDetector（悬停窗口识别）
│  ├─ Contracts/                  LesCommand/LesContext 命令模型 + ILesPlugin/ILesHost/IPluginAssets/IPluginSettings
│  ├─ Controls/                   窗口镶边三件套（CaptionBar / BorderlessWindow / CornerRadiusClip）+ 设置页三件套（SettingsSection / SettingsRow / CheckRow，PLUGIN_UI_SPEC）
│  ├─ Interaction/                RadialGuard（轮盘抑制作用域）+ MouseEvents（滚轮事件枢纽，UI 线程封送）
│  ├─ Services/                   ToastService / ClipboardService（平台横切服务）
│  └─ Shell/AppIcon.cs            进程图标提取缓存（音量插件的应用行在用）
├─ src/Plugins/
│  ├─ LesTool.Plugin.Screenshot/  截图插件：框选/标注/钉图/另存/OCR/长截图/录屏 + assets/models/v5 模型
│  │                              （构建自动部署到宿主输出 plugins\screenshot\，含 RapidOcrNet 及原生库）
│  ├─ LesTool.Plugin.Volume/      音量混音器插件：AudioMixerService + CoreAudioInterop + 浮窗
│  ├─ LesTool.Plugin.Preview/     空格预览插件：空格条件吞键 + 选中项 UIA + 三层瀑布渲染浮窗
│  └─ LesTool.Plugin.Clipboard/   剪切板历史插件：记录/去重/落盘（富文本 Html/Rtf 随行+徽标）+ 搜索浮窗（类别 Tab）+ 回贴（SendInput，Ctrl+Enter 纯文本）+ 多选合并；命名空间 ClipboardHistory 避开 Clipboard 撞名
└─ src/LesTool/                   宿主（平台服务 + 壳 UI）
   ├─ App.xaml(.cs)               组合根：模块表装配、全局异常（+Toast）、内置命令注册
   ├─ MainWindow.xaml(.cs)        主窗口：CaptionBar + 左侧分组导航（含动态"插件"分组）+ PageHost
   ├─ Services/                   平台侧服务（详见 §3）
   ├─ Themes/                     tokens.xaml（刻度，生成）+ Design.xaml（样式层，手写）+ Dark/Light.xaml（语义画刷，生成；ThemeManager 运行时换入换出）
   ├─ Skins/                      外置皮肤源（生成）→ 构建拷到输出 themes\（运行期扫描 + 热重载；GlassWindow.cs = 面板亚克力行为）
   └─ Views/                      PanelShellWindow / RadialWindow / IconPickerWindow（插件图标选择器）/ Pages/（含 PluginSettingsHeader 插件页头）
```

不说明的目录：`.git`、`obj`/`bin`（构建产物）、`.mimosa`（安全扫描工具产物）、`.zcode`（会话文件，含迁移前备份）。

## 3. src/LesTool/Services/ ——平台侧服务

| 文件 | 作用 | 备注 |
|---|---|---|
| `ModuleTable.cs` | **模块表**：初始化（登记序）/关停（逆序）显式有序清单，任一失败即抛 | 插件装载器是表中普通一员 |
| `Plugins/PluginHost.cs` | **插件装载器**：扫 `plugins/*/plugin.json` → 版本/原生冲突护栏 → 独立 PluginLoadContext + AssemblyDependencyResolver → 反射建入口 → 暂存提交 Initialize；单插件失败回滚 | 设置页经 PagesChanged 进主窗口导航"插件"分组 |
| `Plugins/PluginAssets.cs` / `PluginSettings.cs` | 插件资产（锚定 assets/，防越界）与独立设置（JSON 每插件一份） | 资产三原则成文于 ILesPlugin.cs 注释 |
| `ActionRegistry.cs` | **全局命令注册表**（模型在 Platform）：双属性声明、按入口出清单（ForScenario）、注册期矛盾检测、必填上下文运行时拦截 | 全部触发源的唯一动作清单；Register 即插件 API |
| `ScenarioCapabilities.cs` | 入口能力表：轮盘/快捷键=全量，命令面板=除选中项 | 绑定门禁与矛盾检测共用，勿另立规则 |
| `ActionContext.cs` | ContextProvider：按声明（必填\|选填合并）惰性取上下文；Focus 快照优先 | SelectedText 仅热键/轮盘路径取词 |
| `HotkeyService.cs` | 全局热键；状态按行索引记录，重复组合键后到者判冲突 | |
| `MouseHookService.cs` | 轮盘输入源：低级钩子（拦截+回放）+ Raw Input 旁路双通道 | 滚轮经 MouseEvents 枢纽；抑制查 RadialGuard |
| `RadialTrigger.cs` / `RadialWindowController.cs` / `PanelWindowController.cs` | 轮盘手势状态机与两壳窗口控制器 | 预创建 HWND 快显 |
| `AppSettings.cs` / `ThemeManager.cs` / `LogRetentionManager.cs` | 设置/主题/日志自动清理（保留周期切换即时重建 Logger） | |
| `KeyboardCaptureHook.cs` | 热键区捕获键帽的临时键盘钩子（拦截得到已被占用的组合） | 仅 HotkeySection 用 |

## 4. 入口与依赖

**入口 = `App.xaml.cs` OnStartup**：模块表按登记序初始化
`theme → window(主窗口) → radial → panel → mouse(钩子) → clipboard → hotkey → commands(内置命令注册) → plugins(插件装载)`；表外收尾：热键接线 + 默认 Alt+Space + ApplyAll + 冲突 Toast。退出 = 逆序关停（插件先于基础服务，保住录制中优雅停 ffmpeg）。

**依赖方向铁律**：插件 → Platform ← 宿主；插件禁引宿主。平台窗口样式（LesWindowStyle/Design.xaml/Icons.xaml）在宿主应用级资源，插件窗口运行期自动继承；插件 XAML 引用宿主程序集资源的 pack URI（`/LesTool;component/...`）合法。

## 5. 2026-11-01 架构加固落点

- Commands/CommandCatalog + CommandExecutor：唯一注册快照、入口能力复核、异步上下文、执行结果、重入策略。
- Context/TriggerEnvelope + ActionContext：触发前固定目标，各字段独立失败；Platform 的 BoundedQueryExecutor 以一个 MTA 线程限流 UIA。
- Plugins/PluginRuntime：暂存提交、失败回滚、停止门禁与资源逆序回收；PluginLoadContext 隔离私有托管图，PluginDependencyAudit 在初始化前拒绝原生冲突。
- Platform/Storage/JsonSettingsStore：同目录可靠替换、备份恢复、受保护读取、显式归档重置、异步保存确认。
- HotkeyBindingResolver + LauncherHotkeyDefaults：持久化配置与可用性分离，默认面板键初始化有独立持久化标记。
- ShutdownCoordinator：等待取消任务的清理、插件真实业务停止、资源回收与设置 Flush；IAsyncLesPlugin 用于下载/录制。
- build/Plugin.Build.targets + tools/package.py + scripts/package.ps1：逐插件替换开发输出、统一装配、文件哈希、8 插件装载诊断与真实初始化/Office/OCR 检查；.github/workflows/windows.yml 自动验证。
- 详细实施与边界见 ARCHITECTURE_HARDENING_PROGRESS.md；原先关于轮盘未接通、选中项未实现、占位导航的描述已过期。
