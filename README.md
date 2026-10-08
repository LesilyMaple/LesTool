# LesTool

仅 Windows 的开源效率工具：鼠标手势唤起的**悬浮轮盘** + 全局热键唤起的**悬浮多功能面板** + 常驻托盘的**设置页**。

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

## 发布

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

产物为 `src/LesTool/bin/Release/net10.0-windows/win-x64/publish/LesTool.exe` 单文件便携版。

## ⚠️ 本机已知环境问题（CET）

本开发机（Win10 19045，补丁停留在 2022-09）上，所有 .NET 10 原生宿主（apphost、
csc.exe、VBCSCompiler.exe）启动即报
`Fatal error. Your Windows doesn't fully support CET` 退出。这是 .NET 10 在
长期未更新的 Windows 10 上的 fail-fast 行为；`DOTNET_CETCompat=0` 与
runtimeconfig 均无法绕过。已验证的唯一启动路径是 `dotnet exec`（muxer）。

仓库内的应对（装好 Windows 更新后可全部移除）：

- `Directory.Build.props` + `tools/csc-shim/csc.cmd`：让编译走 `dotnet exec csc.dll`，绕过原生 csc.exe
- `scripts/run.ps1`：开发期用 `dotnet exec` 启动应用

**根治办法：安装全部 Windows 更新**（补齐 2024+ 用户态 CET 服务栈后检查即可通过，
发布出的单文件 exe 也将正常双击运行）。

## 许可

[MIT](LICENSE)
