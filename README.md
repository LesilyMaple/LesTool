# LesTool

Windows 桌面效率工具，**平台 + 插件**架构：宿主提供触发与调度（全局热键 / 命令面板 / 鼠标手势轮盘），功能以进程内插件装载。个人开发自用，代码公开供参考（MIT）。

## 功能一览

| 功能 | 形态 | 说明 |
|---|---|---|
| 截图套件 | 插件 | 框选 / 窗口识别 / 标注（矩形·椭圆·箭头·画笔·文字·马赛克·序号）/ 钉图 / 另存 / OCR（PP-OCRv5 中文） |
| 长截图 | 插件 | 滚轮驱动 + 静止收帧拼接（QQ 式交互） |
| 录屏 | 插件 | 外置 ffmpeg（ddagrab 采集直喂 GPU 编码器），暂停分段、无损拼接、播放器式预览 |
| 音量混音器 | 插件 | 每应用音量（EarTrumpet 式浮窗），手写 Core Audio 互操作 |
| 剪贴板历史 | 插件 | 分类检索、富文本回贴、图片与文件记录 |
| 空格预览 | 插件 | 资源管理器中按空格预览文档、图片和音视频 |
| 智能搜索 | 插件 | 选中文字识别与搜索，无选中时显示输入条 |
| 浏览器操作 | 插件 | 标签切换、刷新、恢复标签等 7 条命令 |
| 下载归档 | 插件 | 监视下载目录、规则分流、搬运账本 |
| 系统深浅色 | 插件 | 一键切换 Windows 深色/浅色模式（应用、任务栏、开始菜单一起切） |
| 命令面板 | 平台 | `Alt+Space` 呼出，搜索全部命令（内置/插件） |
| 轮盘 | 平台 | 右键按住拖动唤起（手势触发；Raw Input 旁路保证游戏内可用） |

## 架构一览

```
LesTool.exe            宿主：钩子/热键/面板/主题/插件装载器
LesTool.Platform.dll   契约程序集：ILesPlugin/ILesHost + 抓屏/事件/镶边控件等共享设施
plugins/<id>/          独立加载上下文（plugin.json + dll + .deps.json + assets/），重启生效
```

核心机制是**触发源 × 命令**矩阵：全部触发源消费同一张全局命令注册表，插件/内置命令同权接入（2026-10-30 定案：所有动作统一走插件，用户脚本体系已退役）。详见 `docs/PROJECT_STRUCTURE.md`（代码地图）与 `docs/PLUGIN_DEV.md`（插件开发指南）。

## 环境要求

- Windows 10 及以上；**.NET 8 SDK 8.0.302**（global.json 锁定）
  - 刻意锁 8：.NET 9+ 的原生宿主在缺 2024+ 补丁的 Win10 上因 CET 检查启动即崩；.NET 8 无此机制
- （可选）`ffmpeg.exe`：录屏需要。放置位置二选一：`plugins\screenshot\assets\`（推荐）或程序目录
- （可选）GPU：录屏自动探测硬件编码器（NVENC/QSV/AMF），无 GPU 回退 libx264

## 构建与运行

```powershell
dotnet build -c Release
# 插件随构建自动部署到宿主输出 plugins\ 下；直接运行：
src\LesTool\bin\Release\net8.0-windows\win-x64\LesTool.exe

# 完整发布：构建 + 回归 + 8 插件装配 + 装载/真实初始化/Office/OCR 检查 + ZIP
.\scripts\package.ps1
```

构建与测试还依赖 PATH 中的 Python 3.11+（UI 门禁与装配工具）。独立行为回归：

~~~powershell
dotnet run --project tests/LesTool.Tests -c Release
~~~

完整发行包位于 artifacts/releases；运行数据与发布产物分离。配置保存采用备份替换，损坏时尝试恢复备份；主备不可用时保留原件并进入保护模式，可在设置/插件管理页显式归档恢复。Clipboard 历史有自己的维护恢复入口。

## 快速上手

- **命令面板**：`Alt+Space` 呼出，打字搜命令，回车执行；
- **轮盘**：按住右键拖动超过 5px 唤起（支持槽位绑定与按应用覆盖）；
- **热键**：各插件页头直接设置，单命令点击改键，多命令展开配置；
- **插件管理**：查看装载状态；禁用或加载失败会保留原快捷键配置。

## 数据目录（不在仓库）

`%APPDATA%\LesTool\`：`settings.json`（配置）、`logs\`（Debug 级按天滚动留 7 份）、`plugins.json`（插件启用开关）、`plugins\<id>\settings.json`（每插件设置）。截图另存默认在 `图片\LesTool`，录屏保存在 `视频\LesTool`（确认截图只写剪贴板不落盘）。

## 文档

- `DESIGN.md` — UI 强约束规范（字号/圆角/间距/过渡/窗口分级），改 UI 前必读
- `docs/PROJECT_STRUCTURE.md` — 代码地图与依赖方向
- `docs/ARCHITECTURE_HARDENING_PLAN.md` — 架构加固设计方案（生命周期、配置、异步查询、依赖与发布）
- `docs/ARCHITECTURE_HARDENING_PROGRESS.md` — 本轮实施结果、验证命令与动态验收边界
- `docs/PLUGIN_DEV.md` — 插件开发指南（含可抄的工程模板）
- `docs/PLUGIN_MIGRATION_PLAN.md` — 插件化迁移方案与阶段记录

## 许可

[MIT](LICENSE)
