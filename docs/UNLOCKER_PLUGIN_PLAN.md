# Unlocker 插件方案（v1 定稿，2026-10-31）

强制删除插件：删除选中的文件/文件夹；删不掉时查明占用进程、给出人话原因与建议，
可一键结束占用进程并自动重试。市场调研结论（2026-10-31，对齐 PowerToys File Locksmith
的官方姿势）已并入本方案。

## 1. 范围定案

**做**：
- 单命令 `unlocker.delete`（Requires = SelectedFiles），轮盘/快捷键/命令面板全触发源可用
- 默认删进回收站，确认窗可勾选永久删除（LockHunter 同款安全卖点）
- 失败诊断：Restart Manager 查占用进程（微软官方 API，File Locksmith 同源）
- 处置阶梯：剥只读重试 → `\\?\` 长路径重试 → 查无占用者等 2 秒自动重试 →
  结束占用进程（用户主动点，系统进程黑名单保护）→ 自动重试删除闭环

**不做（v1 明确砍掉，勿再提）**：
- 强制抢关句柄（DuplicateHandle 远程关句柄）——连微软都只给 End task；易崩占用方
- 重启后删除（MoveFileEx DELAY_UNTIL_REBOOT）——需管理员权限，本应用普通权限运行
- 解锁后移动/改名/复制（Unlocker 系组合动作）→ backlog
- 自动修 ACL 权限（EMCO 特色）→ backlog
- 删除失败自动弹窗助手（UnlockerAssistant 式全局监听）→ backlog
- 内核驱动（LockHunter/Unlocker 的 SYSTEM 级锁能力）——永不

## 2. 交互流程（一个窗口两态）

```
资源管理器选中 → 触发 unlocker.delete
  ├─ 无选中/非资源管理器前台 → Toast「请先在资源管理器中选中要删除的文件」
  └─ 有选中 → UnlockerWindow【确认态】
        ├─ 列表：每项图标+名称；N 项 · 总大小（递归统计）
        ├─ 删除方式：移到回收站（默认）/ 永久删除
        └─ [取消] [删除（危险色）]
  → 执行阶梯删除（见 §3）
  ├─ 全成功 → Toast「已删除 N 项」+ 关窗
  └─ 有失败 → 同窗切【诊断态】
```

诊断态布局：
- 每失败项一行：名称 + 原因短句；展开区显示细节
  - 被占用：占用进程列表（图标+进程名+PID），每行按钮
    「结束 <进程名> 并重试」；黑名单进程按钮置灰并注明「系统关键进程」
  - 占用者不明：「可能是杀毒或搜索索引正在扫描，通常数秒后自行释放」+ 已自动等待重试说明
  - 拒绝访问：「已尝试去除只读属性。仍失败：权限不足或受系统保护，不建议强行删除系统文件」
  - 路径过长：已用长路径前缀重试过，仍失败原样报错误码
  - 其他：Win32 错误码 + FormatMessage 文本
- 底部：[重试全部] [打开文件位置]（SHOpenFolderAndSelectItems 原语，SmartSearch 样板）[关闭]
- 非提权查不全时诚实文案：「部分系统进程需管理员权限才能看到」

窗口形态照 QrResultWindow 样板：物理坐标直摆、黑角 Effect+Margin 承载阴影、命名避开
`Delete`/`Clipboard` 等系统名。

## 3. 删除引擎（阶梯）

对每个选中项（文件夹递归逐项删除，失败精确到子项，避免"删一半无声中断"）：

1. 常规删除：SHFileOperation FO_DELETE（带 FOF_ALLOWUNDO 即回收站；永久则去 UNDO），
   带 FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
2. 失败 + 只读属性 → 剥只读 → 重试
3. 失败 + 路径超长 → `\\?\` 前缀走 File/Directory.Delete（永久删，UI 注明）
4. 仍失败 → 进诊断态；RM 查无占用者 → 等 2 秒自动重试一次（偷师 Locksmith --wait）
5. 用户点「结束并重试」→ Process.Kill（托管 API，不经 Process.Start，绕开 Mimosa 红线）
   → WaitForExit(3s) → 自动重试删除 → 结果刷新该行

进程黑名单（按钮置灰）：csrss、smss、wininit、winlogon、services、lsass、svchost、dwm、
System、Registry、Memory Compression。explorer 允许结束，文案警告「桌面将重新加载」。

## 4. 诊断引擎（Restart Manager）

- RmStartSession → RmRegisterResources(失败项路径) → RmGetList → PID 列表 →
  Process.GetProcessById 取名称，Platform.AppIcon 取图标（失败回落默认图标）
- 文件直接注册；目录注册目录路径查目录句柄占用者（cmd cd 驻留等场景），查不到再报占用者不明
- RM 查不到 ≠ 无人占用：非提权盲区、杀软/索引瞬时扫描均可能，统一走「占用者不明」文案兜底
- 一次会话批量注册全部失败项（RM 会话有开销，不逐项开会话）

## 5. 技术要点对照红线

| 事项 | 处理 |
|---|---|
| Mimosa Process.Start 红线 | 不用 Process.Start；杀进程走托管 Process.Kill |
| 打开文件位置 | SHOpenFolderAndSelectItems 原语（SmartSearch 有样板） |
| COM 手写 | RM 为普通 P/Invoke 声明，无 vtable；SHFileOperation 同 |
| 插件架构 | 纯新增项目，不动现有模块；启动装一次常驻至退出 |
| 日志约定 | 触发/删除成功/结束进程 INFO；RM 明细/错误码/重试 DEBUG；删除失败 ERROR（原因+路径） |
| 图标 | 命令 glyph 用 Segoe MDL2（\uE74D）；插件图标走 Lucide SVG 流水线，勿自寻图片 |

## 6. 插件结构

```
src/Plugins/LesTool.Plugin.Unlocker/
  LesTool.Plugin.Unlocker.csproj   // 照 Browser 插件
  plugin.json                      // id=unlocker，minPlatform=1.0.0
  UnlockerPlugin.cs                // 入口：注册 unlocker.delete，Requires=SelectedFiles
  UnlockerWindow.xaml(.cs)         // 确认态+诊断态两态窗口
  DeleteEngine.cs                  // 阶梯删除、失败收集、递归统计大小
  LockDiagnoser.cs                 // RM 封装、原因分诊、错误码人话映射
  ProcessGuard.cs                  // 黑名单、结束进程+重试闭环
```

## 7. 验收清单（用户动态验收）

1. 记事本打开 txt → 删除 → 诊断列出 Notepad →「结束并重试」→ 删除成功
2. Word 打开 docx → 同上，按钮文案明示未保存风险
3. 只读文件 → 自动剥只读删除成功
4. 多选 3 项（2 可删 + 1 被占用）→ 部分成功，诊断态只列 1 项
5. 桌面选中快捷方式 → 可删（ExplorerSelection 含 Desktop 形态）
6. 文件夹内含被占用子文件 → 递归删除，失败项精确到该子文件
7. 非资源管理器前台触发 → Toast 提示，不开窗
8. svchost 等系统进程占用场景 → 按钮置灰
9. 回收站模式删除的文件可在回收站还原
10. 日志关键词：`强制删除`（INFO）/`占用诊断`（DEBUG）/`删除失败`（ERROR）

## 8. Backlog（按优先级）

1. 设置页：默认删除方式、等待重试时长
2. 解锁后移动/改名（Unlock & Move/Rename 组合动作）
3. 自动修权限（ACL）场景支持
4. 提权会话（以管理员重启宿主）解锁 RM 盲区与重启后删除
