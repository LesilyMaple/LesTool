# LesTool

个人自用的 Windows 效率工具（不分发）：鼠标手势唤起的**悬浮轮盘** + 全局热键唤起的**悬浮多功能面板** + 常驻托盘的**设置页**。

- 目标框架：WPF / `net10.0-windows`
- UI：[WPF-UI](https://github.com/lepoco/wpfui)（Fluent 3 / Mica / 暗色）
- 架构：单进程 Generic Host；低级鼠标钩子跑在专属线程；轮盘窗口启动期预创建，手势触发只做定位 + Show
- 内存预算：working set < 100MB（release 构建）

当前状态：**项目骨架**。钩子/热键/托盘/手势/预创建轮盘通路已打通（含占位 UI），设置页与面板为占位窗口。

## 目录

```
src/LesTool/
  App.xaml(.cs)          # Generic Host 装配、全局异常、窗口调度
  Models/                # 设置模型（%APPDATA%\LesTool\settings.json）
  Services/
    MouseHookService     # WH_MOUSE_LL，专属线程 + 消息泵，回调纯计算
    GestureRecognizer    # 右键拖拽方向序列状态机
    RadialWindowController # 轮盘窗预创建/触发显示
    HotkeyService        # RegisterHotKey + message-only 窗口
    SettingsService      # JSON 设置读写
    TrayIconService      # 托盘图标与菜单
  Views/                 # 轮盘/设置/面板窗口
scripts/
  run.ps1                # 开发运行入口（见下）
  make-icon.ps1          # 重新生成 Assets/app.ico
tools/csc-shim/          # 编译器 shim（见下）
```

## 开发

```powershell
# 构建
dotnet build -c Release

# 运行（本机推荐方式，原因见下）
powershell -ExecutionPolicy Bypass -File scripts/run.ps1
```

## 发布（自用基本用不到）

日常形态就是 `dotnet build -c Release` + 桌面快捷方式。以下单文件命令保留备查，
但注意其产物在本机（见 CET 章节）无法启动：

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## ⚠️ 本机已知环境问题（CET）

本开发机（Win10 19045，补丁停留在 2022-09，**系统保持不更新**）上，所有 .NET 10
原生宿主（apphost、csc.exe、VBCSCompiler.exe）无法启动。已穷举验证：

- apphost 路径（即所有可直接双击的 .NET 10 exe）× 运行时 10.0.0 → 段错误；
  × 10.0.12 → `Fatal error. Your Windows doesn't fully support CET`。与运行时版本无关，不可修。
- `dotnet exec`（muxer 路径）× 任意运行时版本 → 完全正常。
- `DOTNET_CETCompat=0`、`COMPlus_CETCompat=0`、runtimeconfig `System.Runtime.CETCompat=false`
  均无效（自检发生在托管运行时接管之前）。

原因：CPU（Zen 3）支持 CET 硬件特性，但 Win10 需 2024 年前后的服务更新才补齐
用户态影子栈支持；.NET 10 原生宿主自检不通过即拒绝启动。

**对开发零影响**（构建走 shim、运行走 run.ps1，均已就位）。对本机的唯一影响：
发布出的单文件 exe 无法在本机双击启动。替代方案：桌面快捷方式走
`scripts/launch.vbs`（隐藏窗口启动 `dotnet exec`，体验与双击无异，实测可用）。
最终用户机器（正常更新的 Win10 2024+ 补丁 / Win11）不受影响。

注：经启动器运行时进程名为 `dotnet.exe`（muxer 托管），任务管理器中如此显示属正常。

## 许可

[MIT](LICENSE)
