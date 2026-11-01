# LesTool 项目结构说明

> 面向新加入的开发者；看完应能快速定位任意功能的代码位置。
> 基准日期：2026-10-15（P2/P3 插件迁移完成：截图与音量混音器已外置为插件）。项目演进较快，发现文档与代码不符时以代码为准、顺手更新本文档。
> 插件化设计与开发指南入口：`docs/PLUGIN_MIGRATION_PLAN.md`。

## 1. 项目概述

**做什么**：Windows 桌面效率工具。核心架构为"**平台 + 插件**"——宿主提供触发×命令矩阵（悬浮轮盘 / 命令面板 / 全局快捷键 / 托盘点击四类触发源，统一消费一张全局命令注册表 `ActionRegistry`），功能以**进程内插件**装载（plugins/ 目录，字节装载不锁文件）；用户脚本（.csx）是与插件同权的动态命令来源。

**当前形态**：
- **平台（宿主）**：轮盘手势（Raw Input 旁路 + 钩子拦截回放）、命令面板、全局热键、剪贴板监听、主题、托盘、用户脚本引擎、插件装载器
- **插件**：**截图**（框选/窗口识别/标注/钉图/长截图/OCR/录屏，整体一个插件，含 OCR 模型资产；ffmpeg 为可选资产）、**音量混音器**（EarTrumpet 式浮窗）、**示例插件**（作者模板）
- **LesTool.Platform 契约程序集**：宿主与插件共同引用——命令模型、GDI 抓屏/窗口识别、RadialGuard/MouseEvents、Toast/剪贴板/应用图标服务、窗口镶边控件（CaptionBar/BorderlessWindow/CornerRadiusClip）

**技术栈**：

| 项 | 选型 | 备注 |
|---|---|---|
| 语言/框架 | C# / WPF / .NET 8（`net8.0-windows`，win-x64） | 刻意锁 8：开发机 Win10 无补丁，.NET 9+ 原生宿主因 CET 检查启动即崩 |
| SDK | 8.0.302（global.json 锁定，rollForward latestFeature） | |
| 插件契约 | `LesTool.Platform.dll` v1.0.0（ILesPlugin/ILesHost/IPluginAssets/IPluginSettings） | 插件只准引用 Platform，禁引宿主 |
| UI | 自建 Fluent 体系（`Themes/Design.xaml` 隐式样式 + Dark/Light 双主题字典） | 强约束成文于 `DESIGN.md`，**改 UI 前必读**；窗口镶边控件在 Platform |
| 日志 | Serilog → `%APPDATA%\LesTool\logs\`（Debug 级，按天滚动留 7 份） | 全项目用静态 `Serilog.Log` |
| 托盘 | Hardcodet.NotifyIcon.Wpf（仅宿主托盘） | |
| 脚本引擎 | Microsoft.CodeAnalysis.CSharp（Roslyn，仅宿主） | |
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

- **测试**：无自动化测试。验证流程 = Release 构建 0 警告 0 错误后人工运行验收。
- **构建前注意**：先结束正在运行的 LesTool 实例，否则 exe 被锁导致构建失败。
- **运行期数据**（不在仓库）：`%APPDATA%\LesTool\`（settings.json、logs\、Scripts\、ScriptCache\、plugins\&lt;id&gt;\settings.json 每插件设置）；录屏临时段在 `%TEMP%\LesTool\rec\`；另存/长截图默认目录 `图片\LesTool`，录屏存 `视频\LesTool`（截图回车确认只写剪贴板，不落盘）。
- **插件卸载**：删宿主输出 `plugins\<id>\` 目录，重启生效（进程内程序集常驻）。

## 2. 目录树（注释）

```
LesTool/                          仓库根
├─ DESIGN.md                      UI 强约束规范：字号四档/圆角两档/动画时长/窗口分级/显式样式登记表
├─ README.md                      项目说明（内容滞后于现状）
├─ LICENSE                        MIT
├─ LesTool.sln                    解决方案（宿主 + Platform + 三个插件）
├─ global.json                    锁 SDK 8.0.302
├─ scripts/make-icon.ps1          重新生成 Assets/app.ico（纯 ASCII，防 PS5.1 GBK 坑）
├─ docs/
│  ├─ PROJECT_STRUCTURE.md        本文档
│  └─ PLUGIN_MIGRATION_PLAN.md    插件化总体方案（含阶段进度）
├─ src/LesTool.Platform/          ★插件契约程序集（宿主与插件共同引用）
│  ├─ Capture/                    GdiCapture（GDI 抓屏/显示器/物理坐标摆窗）+ WindowDetector（悬停窗口识别）
│  ├─ Contracts/                  LesCommand/LesContext 命令模型 + ILesPlugin/ILesHost/IPluginAssets/IPluginSettings
│  ├─ Controls/                   窗口镶边三件套：CaptionBar / BorderlessWindow / CornerRadiusClip
│  ├─ Interaction/                RadialGuard（轮盘抑制作用域）+ MouseEvents（滚轮事件枢纽，UI 线程封送）
│  ├─ Services/                   ToastService / ClipboardService（平台横切服务）
│  └─ Shell/AppIcon.cs            进程图标提取缓存（托盘与音量插件共用）
├─ src/Plugins/
│  ├─ LesTool.Plugin.Sample/      示例插件（作者模板）：命令+设置页+资产三原则演示
│  ├─ LesTool.Plugin.Screenshot/  截图插件：框选/标注/钉图/另存/OCR/长截图/录屏 + assets/models/v5 模型
│  │                              （构建自动部署到宿主输出 plugins\screenshot\，含 RapidOcrNet 及原生库）
│  └─ LesTool.Plugin.Volume/      音量混音器插件：AudioMixerService + CoreAudioInterop + 浮窗
└─ src/LesTool/                   宿主（平台服务 + 壳 UI）
   ├─ App.xaml(.cs)               组合根：模块表装配、全局异常（+Toast）、内置命令注册
   ├─ MainWindow.xaml(.cs)        主窗口：CaptionBar + 左侧分组导航（含动态"插件"分组）+ PageHost
   ├─ Services/                   平台侧服务（详见 §3）
   ├─ Themes/                     Design.xaml（常驻）+ Dark/Light.xaml（ThemeManager 运行时换入换出）
   └─ Views/                      PanelShellWindow / RadialWindow / Pages/
```

不说明的目录：`.git`、`obj`/`bin`（构建产物）、`.mimosa`（安全扫描工具产物）、`.zcode`（会话文件，含迁移前备份）。

## 3. src/LesTool/Services/ ——平台侧服务

| 文件 | 作用 | 备注 |
|---|---|---|
| `ModuleTable.cs` | **模块表**：初始化（登记序）/关停（逆序）显式有序清单，任一失败即抛 | 插件装载器是表中普通一员 |
| `Plugins/PluginHost.cs` | **插件装载器**：扫 `plugins/*/plugin.json` → minPlatform 版本护栏 → 字节数组 Assembly.Load → 反射建入口 → Initialize；单插件失败只 Toast；AssemblyResolve 兜底探针 + AddDllDirectory 原生路径注册 | 设置页经 PagesChanged 进主窗口导航"插件"分组 |
| `Plugins/PluginAssets.cs` / `PluginSettings.cs` | 插件资产（锚定 assets/，防越界）与独立设置（JSON 每插件一份） | 资产三原则成文于 ILesPlugin.cs 注释 |
| `ActionRegistry.cs` | **全局命令注册表**（模型在 Platform） | 四触发源唯一动作清单；Register 即插件 API |
| `ActionContext.cs` | ContextProvider：按 ContextKind 惰性取上下文 | SelectedText/SelectedFiles 未实现 |
| `HotkeyService.cs` | 全局热键；状态按行索引记录，重复组合键后到者判冲突 | |
| `MouseHookService.cs` | 轮盘输入源：低级钩子（拦截+回放）+ Raw Input 旁路双通道 | 滚轮经 MouseEvents 枢纽；抑制查 RadialGuard |
| `RadialTrigger.cs` / `RadialWindowController.cs` / `PanelWindowController.cs` | 轮盘手势状态机与两壳窗口控制器 | 预创建 HWND 快显 |
| `AppSettings.cs` / `ThemeManager.cs` / `TrayIconRenderer.cs` | 设置/主题/托盘图标绘制 | TrayPage 与控制器共用 |
| `VolumeMixerController.cs` | **纯托盘**（平台，应用身份）：左键命令派发（回退链 配置→volume.mixer→panel.launcher） | 混音器本体在音量插件 |
| `KeyboardCaptureHook.cs` | 热键页捕获键帽的临时键盘钩子 | 仅 HotkeyRow 用 |
| `Scripting/` | 用户脚本引擎（.csx → Roslyn → script:* 命令）+ LesApi 门面 | 与插件同权的命令来源 |

## 4. 入口与依赖

**入口 = `App.xaml.cs` OnStartup**：模块表按登记序初始化
`theme → window(主窗口) → radial → panel → mouse(钩子) → clipboard → hotkey → commands(内置命令注册) → scripts → plugins(插件装载) → mixer(托盘)`；表外收尾：热键接线 + 默认 Alt+Space + ApplyAll + 冲突 Toast。退出 = 逆序关停（插件先于基础服务，保住录制中优雅停 ffmpeg）。

**依赖方向铁律**：插件 → Platform ← 宿主；插件禁引宿主。平台窗口样式（LesWindowStyle/Design.xaml/Icons.xaml）在宿主应用级资源，插件窗口运行期自动继承；插件 XAML 引用宿主程序集资源的 pack URI（`/LesTool;component/...`）合法。

## 5. 【待确认】清单

1. **README.md 内容滞后**：需按插件化新形态重写（开源前必做）。
2. **ffmpeg.exe 分发方式**：仓库无该文件；插件资产目录或程序目录二选一放置（README/PLUGIN_DEV 需写明）。
3. **PanelActionsPage 去留**（P4 评审三选一，倾向改「命令总览」页）：见 PLUGIN_MIGRATION_PLAN.md §9。
4. **轮盘扇形命中/槽位**仍为原型（RadialWindow TODO）。
5. 三个占位导航页（contextmenu/event/bgaction）规划中未开工。
