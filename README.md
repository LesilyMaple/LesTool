# LesTool

个人自用的 Windows 效率工具（不分发）：鼠标手势唤起的**悬浮轮盘** + 常驻托盘。当前进度：右键按住拖动超 5px 显示轮盘、松开隐藏。

- 目标框架：WPF / `net8.0-windows`（**刻意选 8**：本机系统永久不更新，.NET 9+ 的原生宿主在缺 2024+ 补丁的 Win10 上因 CET 检查启动即崩；.NET 8 无此机制，exe 可直接双击）
- UI：[WPF-UI](https://github.com/lepoco/wpfui)（Fluent）
- SDK：8.0.302（global.json 锁定）；.NET 8 补丁只修不加坏，越新越好，2026-11-10 的最后一次更新是"定格版"

## 目录

```
src/LesTool/
  App.xaml(.cs)          # 装配 + 全局异常 + 轮盘接线
  MainWindow             # 占位窗口（关闭即退出程序）
  Services/
    MouseHookService     # WH_MOUSE_LL，专属线程 + 消息泵，回调纯计算不吞事件
    RadialTrigger        # 右键按住拖动 >5px 触发显示 / 松开隐藏
    RadialWindowController # 轮盘窗预创建 / DPI 换算定位 / 显隐
  Views/RadialWindow     # 无边框全透明置顶不抢焦点窗口
scripts/make-icon.ps1    # 重新生成 Assets/app.ico
```

## 开发

```powershell
dotnet build -c Release
# 双击桌面快捷方式，或直接运行
src\LesTool\bin\Release\net8.0-windows\win-x64\LesTool.exe
```

## 日志

`%APPDATA%\LesTool\logs\`（Debug 级，含首现异常全记录——排障用）

## 许可

[MIT](LICENSE)
