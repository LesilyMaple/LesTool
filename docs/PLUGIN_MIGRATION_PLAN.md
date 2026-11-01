# 截图与音量混音器插件化 + 架构整改方案

> 基准日期：2026-10-14。架构问题取自当日架构评审结论（失败路径债务优先修，结构性欠账随迁移顺手偿还）。
> 本方案为设计定稿文档：**只出方案，不改代码**；实施按第六节阶段推进，每阶段有人工验收关卡（依项目铁律，动态验证由用户执行）。

## 1. 目标与已定决策

把截图与音量混音器拆成进程内插件，同时偿还架构评审发现的债务，且资产机制要成为后续插件的公共范本。

**已定决策（约束，不再讨论）**：

| # | 决策 |
|---|---|
| D1 | 插件仅两个：**截图**（标注/钉图/OCR/长截图/录屏等子功能整体随迁，不再细拆）、**音量混音器** |
| D2 | 其余一律为平台服务：轮盘、热键、鼠标钩子、剪贴板、主题、托盘图标、命令面板、主窗口 |
| D3 | 托盘图标属平台（应用身份）；拆出去的是混音器功能本身 |
| D4 | 插件**自带资产**（OCR 模型、ffmpeg 等）；资产机制做成通用能力，禁止截图私有写法 |
| D5 | 性能纪律不回退：启动预热、懒加载保持；首次触发不劣化 |
| D6 | 架构整改与插件化编排在同一条迁移线上（先修地基再搬家），不另开战场 |

## 2. 目标形态总览

```
LesTool.sln
├─ src/LesTool                     宿主（平台服务 + 壳 UI）
├─ src/LesTool.Platform            契约与共享工具程序集（宿主与插件共同引用）
├─ src/Plugins/LesTool.Plugin.Screenshot
│    assets/ocr/**                 ← 模型资产随插件工程
│    └─（构建后拷至宿主输出）plugins/screenshot/
│         plugin.json  LesTool.Plugin.Screenshot.dll  assets/**
└─ src/Plugins/LesTool.Plugin.VolumeMixer → plugins/volume/

宿主输出目录：
  LesTool.exe、LesTool.Platform.dll、settings 等平台文件
  plugins/<id>/{plugin.json, *.dll, assets/**}      ← 运行期扫描装载
  ffmpeg.exe                                          ← 兼容旧约定的回退搜索点
```

数据目录：`%APPDATA%\LesTool\` 下 `settings.json`（平台，仅 Theme/Hotkeys/Tray）、`plugins\<id>\settings.json`（每插件独立）、`Scripts\`、`ScriptCache\`（脚本体系不动）。

## 3. 第一部分：平台层先行（架构整改）

### 3.1 共享工具上收（评审问题 4）

新建 `LesTool.Platform` 类库（`net8.0-windows`，`UseWPF=true`，SDK 版本随 global.json）。迁入：

| 迁入物 | 现位置 | 去向 |
|---|---|---|
| `GdiCapture` | `Services/Screenshot/GdiCapture.cs` | `Platform/Capture/`（外部 8 处消费者改引平台命名空间） |
| `WindowDetector` | `Services/Screenshot/WindowDetector.cs` | `Platform/Capture/` |
| `LesContext` / `ContextKind` / `LesCommand` 模型 | `Services/ActionContext.cs`、`ActionRegistry.cs` | `Platform/Contracts/`（插件注册命令需要这些类型；`ActionRegistry` 实现留在宿主） |

原 `Services/Screenshot↔Recording` 的双向 using 随之消失。**验收**：Release 构建 0 警告 0 错误；全库 grep 无平台代码引用 `LesTool.Services.Screenshot`。

### 3.2 失败路径修复（评审问题 1 + 2，最高优先，先于一切搬迁）

- `ScreenshotService.End()` 幂等化（重复调用安全），全部会话事件处理器（`OnConfirmed/OnPinned/OnSaveRequested/OnRecordRequested/OnLongShotRequested/OnOcrRequested/OnCancelled`）统一 try/finally 收摊；`OnConfirmed`、`OnSaveRequested` 改为与 `OnPinned` 相同的"**先 End 后干活**"次序（证据：`ScreenshotService.cs:115-126` 现在把 `SavePng` 放在 `End()` 之前）。
- `App.DispatcherUnhandledException`（`App.xaml.cs:38-42`）：保留 `Handled=true` 不崩进程，但**追加 Toast「某功能出错，详见日志」**——不再静默。
- **验收（用户执行）**：把图片保存目录置为只读 → 触发截图确认 → 期望 Toast 报错、ESC/再次热键可正常发起新会话、轮盘全程可用、日志仅一条 ERROR。

### 3.3 轮盘抑制与鼠标事件收口（评审问题 2）

- `MouseHookService.SuppressRadialTrigger`（`MouseHookService.cs:24`）→ 平台 `RadialGuard`：`BeginSuppress()` 返回 `IDisposable` 作用域；钩子回调只读 `RadialGuard.IsSuppressed` 静态 volatile（钩子线程零委托调用，维持 LowLevelHooksTimeout 纪律）。`ScreenshotService` 的三处直写改为 using 作用域。
- `WheelObserved` 静态事件（`MouseHookService.cs:28`）→ 平台 `MouseEvents` 事件枢纽，**枢纽内完成 UI 线程封送**（订阅方免自封送；`LongScreenshot.cs:81` 的自封送删除）。滚轮事件低频，封送开销可忽略。

### 3.4 模块生命周期契约（评审问题 3）

宿主内建有序模块表，替换 `App.OnStartup/OnExit` 的散装调用（现证据：`App.xaml.cs:61-88` 装配 15+ 项、`:84/158-159` 硬编码 Warmup/Shutdown、`:108` 的 `_radial!`）：

```csharp
var modules = new ModuleTable();
modules.Add("theme",   ThemeManager.Initialize,    applyCaptionCleanup: null);
modules.Add("radial",  initRadial,                 shutdownRadial);
modules.Add("hotkey",  HotkeyService.Start,        null);
modules.Add("scripts", ScriptEngine.Initialize,    null);
modules.Add("plugins", PluginHost.Load,            PluginHost.ShutdownAll); // Phase 1 加入
modules.Add("mixer",   initTray,                   disposeTray);
modules.Run(order documented); // 每步前置条件断言（缺依赖抛带说明的异常，替代 null 宽容静默 no-op）
```

退出序固定：**插件先于基础服务**（保住"录制中退出优雅停 ffmpeg"的现语义，`ScreenRecorder.Shutdown`）。此表即将来插件的宿主运行器。

### 3.5 顺手小修（与搬迁同线，不另开 PR 批次）

| 项 | 证据 | 修法 |
|---|---|---|
| 面板动作页绕过命令注册表 | `PanelActionsPage.xaml.cs:17` 直调 | 改 `ActionRegistry.Execute("screenshot.region")`，删复制的 try/catch+MessageBox |
| 热键状态键冲突 | `HotkeyService.cs:103/107` 同键互覆盖 | `ApplyAll` 对重复组合键：后条直接判 Conflict 跳过注册，状态键改绑定索引 |
| 错误反馈双轨 | `ScreenRecorder.cs:70` 等 6 处 MessageBox（部分无 owner） | 统一走平台 `Feedback`（Toast 为主；确需模态的用带 owner 的样式化对话框） |
| 死依赖 | `LesTool.csproj` CommunityToolkit.Mvvm 引而未用 | 移除 |
| 截图保存路径重复 | `ScreenshotService.cs:223/283` | 常量合一（搬迁时顺手） |

## 4. 第二部分：插件机制（公共范本）

### 4.1 形态与构建

插件 = 解决方案内普通 csproj，引用**且仅引用** `LesTool.Platform`（禁止引用宿主 exe）。构建后由 AfterBuild 目标把插件输出整体拷到宿主输出 `plugins/<id>/`。装载用 `Assembly.Load(字节数组)`（不锁文件，沿 `ScriptEngine.cs:338` 验证过的纪律）。

### 4.2 契约（Platform/Contracts/，签名即 API 冻结点）

```csharp
public interface ILesPlugin
{
    string Id { get; }                    // "screenshot" / "volume"
    void Initialize(ILesHost host);       // UI 线程：注册命令/订阅事件/声明资产；禁止访问磁盘资产
    void Shutdown();                      // UI 线程：收摊（停 ffmpeg、放 OCR 引擎），幂等
}

public interface ILesHost
{
    void RegisterCommand(string id, string name, string glyph, Action<LesContext> callback,
                         string keywords = "", ContextKind requires = ContextKind.None);
    IFeedback Feedback { get; }           // Toast / 带 owner 对话框
    MouseEvents Mouse { get; }            // 滚轮等（已封送 UI 线程）
    RadialGuard Radial { get; }           // 抑制作用域
    IPluginAssets Assets { get; }         // 资产解析（本插件目录）
    IPluginSettings Settings { get; }     // 每插件独立设置
    void RegisterSettingsPage(string title, string glyph, Func<FrameworkElement> factory); // 可选
}
```

`CommandKind` 增加 `Plugin`；命令面板徽标显示「插件」（`PanelShellWindow.xaml.cs:201` 的 switch 增一档）。`ActionRegistry.Register` 注释中"将来的公共插件 API"（`ActionRegistry.cs:62`）就此兑现。

### 4.3 装载流程与失败隔离

模块表 `plugins` 步骤：扫描 `plugins/*/plugin.json`（字段：id/name/version/entry/minPlatform）→ 版本护栏（Platform 程序集版本 < minPlatform 则跳过并 ERROR+Toast）→ 字节装载 → 反射建入口一次 → `Initialize`。**单插件失败只 Toast + ERROR 日志，不阻断其余插件与宿主**（隔离纪律同脚本引擎）。装载时机：**启动即装载，不做懒加载**（见第七节性能论证）。

### 4.4 设置：每插件独立存储

`IPluginSettings`：Get/Set 键值，落 `%APPDATA%\LesTool\plugins\<id>\settings.json`。宿主 `AppSettingsData` 维持三节不动（Tray.LeftClickAction 存的是命令 Id，天然与插件解耦）。

### 4.5 UI 挂载

- 命令：即注册表，四触发源（热键/面板/托盘/轮盘槽位）自动可用，无需插件额外接线。
- 设置页：`RegisterSettingsPage` 注册的页进主窗口导航"插件"分组；`MainWindow.xaml.cs:31-42` 的硬编码 switch 改为"平台页 + 插件页"两段构建。
- 缺席降级：热键行/托盘命令指向不存在命令时——热键页行内标注「命令不存在（插件未装？）」；托盘回退链：配置命令 → `volume.mixer` → `panel.launcher`（永远存在）+ 一次性 Toast 提示。

### 4.6 资产机制（范本核心）

目录约定：`plugins/<id>/assets/**`；`IPluginAssets.Resolve("models/v5/det.onnx")` 返回存在与否及全路径，`List(dir)`/`TotalBytes()` 供诊断。

**三条范本原则（对所有后续插件生效）**：

1. **资产永不阻塞装载**——`Initialize` 不得依赖任何资产存在；插件核心命令必须在零资产时可用。
2. **按子功能独立降级**——每个用到资产的子功能在使用时点自查资产，缺失则隐藏入口或点击时 Toast（写明应放置的**具体相对路径**），不牵连其他子功能。例：截图插件零资产时框选/标注/钉图照常，OCR 与录屏按钮给出指引。
3. **降级必须可解释**——Toast + 日志文案统一格式：「缺少 {资产}，请放置到 plugins/{id}/assets/{相对路径}」。

构建接线：模型等仓库内资产放插件工程 `assets/` 目录随构建拷出；ffmpeg 等仓库外大件不入库，`FindFfmpeg` 搜索序改为 `plugins/screenshot/assets/ffmpeg.exe → 程序目录\ffmpeg.exe`（`ScreenRecorder.cs` 现仅搜程序目录，双路径向后兼容老约定）。

### 4.7 与脚本引擎的关系

复用其**纪律**（字节装载不锁文件、失败隔离、预热习惯），不复用其**编译管线**（插件是解决方案内预编译 DLL，无需 Roslyn/指纹/IL 缓存；版本护栏用 minPlatform 而非宿主指纹）。脚本体系本身不动。

## 5. 第三部分：两个插件的迁移

### 5.1 截图插件（LesTool.Plugin.Screenshot）

随迁文件（整块，不改逻辑）：`ScreenshotService`、`OverlayWindow(.xaml)`、`SelectionSession`、`Annotation*`、`OcrPanelState`、`ScreenshotOcr`、`LongScreenshot`、`VerticalStitcher`、`Long{Bar,Frame,Preview}Window`、`PinWindow`、`ScreenRecorder`、`Record{Bar,Frame,Preview}Window`。**迁移 diff 只允许三类变化：using/命名空间/访问修饰符**，其余逐行审（OverlayWindow 1144 行是最大回归面）。

- `Initialize`：注册 `screenshot.region` 命令；后台跑编码器预热（等价迁移现 `App.xaml.cs:84` 的 `ScreenRecorder.Warmup()`）；订阅 `MouseEvents`；OCR 引擎惰性纪律保持（会话起用、会话终释放，`ScreenshotService.cs:246` 语义不变）。
- `Shutdown`：停 ffmpeg 保段、放 OCR、收计时器（承接现 `App.xaml.cs:158-159` 两个硬编码退出钩子）。
- 宿主侧删除：App 中对 Screenshot/ScreenRecorder/LongScreenshot 的全部直接引用与 using；`PanelActionsPage` 按钮改为按命令存在性显示（Phase 0.5 已改派发，此处只加缺席隐藏）。
- OCR 模型：`Assets/ocr` 的 csproj 拷贝项从宿主工程移入插件工程，目标 `assets/models/v5/`。
- 资产缺席行为：见 4.6 三原则。

### 5.2 音量混音器插件（LesTool.Plugin.VolumeMixer)

随迁：`AudioMixerService`、`CoreAudioInterop`、`AppIconCache`（同文件内，`AudioMixerService.cs:15`）、`VolumeMixerWindow`。

- `Initialize`：自建 `AudioMixerService`（本机总音量 E_NOINTERFACE 的降级语义保持）+ 注册 `volume.mixer` 命令开关浮窗；预创建窗口（现 `PrepareForFastShow` 纪律）。
- `Shutdown`：停 250ms 轮询、藏窗。
- 平台侧 `VolumeMixerController` 瘦身为**纯托盘**：TaskbarIcon + 右键菜单 + 左键命令派发（回退链 Phase 1 已就位）+ 托盘外观配置应用（`TrayPage`、`TrayIconRenderer` 均留平台）。插件缺席时托盘照常工作，左键回退命令面板并提示。

## 6. 分阶段实施计划（每阶段一道验收关卡）

| 阶段 | 内容 | 完成标准（可验证） |
|---|---|---|
| **P0 平台地基** | 3.1–3.5 全部（不含任何插件工程） | ✅ 2026-10-14 完成，Release 0 警告（动态验收随 P1 一并跑） |
| **P1 插件骨架** | Platform 契约 + 装载器 + plugin.json + 徽标/设置页注册/每插件设置 + 托盘回退链 + **最小示例插件**（一条命令+一页设置+一个资产文件，即"模范"的可运行样例与作者模板） | ✅ 2026-10-15 完成，构建绿；动态验收单见项目沟通 |
| **P2 截图迁移** | 5.1 | ✅ 2026-10-15 完成，构建绿；附带：Toast/Clipboard/窗口镶边控件/AppIcon 因跨程序集需要顺势上收 Platform（属契约面扩容，已在 PROJECT_STRUCTURE.md 记录） |
| **P3 混音器迁移** | 5.2 | ✅ 2026-10-15 完成，构建绿；托盘瘦身为纯平台服务，回退链就位 |
| **P4 成文收尾** | `docs/PLUGIN_DEV.md` 插件开发指南（目录/清单/契约/资产三原则/生命周期/日志纪律）；PROJECT_STRUCTURE.md、README、DESIGN.md 更新；死代码清扫 | PROJECT_STRUCTURE.md 已随 P2/P3 更新；其余待做 |

顺序依据：3.2 必须先于 P2（不在坏地基上搬家）；P1 的示例插件先行验证范本，P2/P3 只是往范本里填实现。

## 7. 性能保障（D5 兑现）

- **启动即装载**（非懒加载）：两插件字节读取+反射各一次，毫秒级；换来的是**首次触发与今日完全等价**——会话本就每次现建遮罩窗口（`ScreenshotService.cs:65-73`），真正的重活（冻结全屏 BitBlt、ffmpeg 探测）位置与成本不变。启动新增预算 <50ms，超标则改"启动后台延迟装载"预案（仅装载时机后移，不引入懒加载语义）。
- **稳态零编组**：进程内插件即跨程序集直调，纳秒级；无 IPC。脚本引擎已用同机制验证（`ScriptEngine.cs:336-343` 一次性反射 + 缓存委托）。
- **钩子线程零插件接触**：抑制检查保持平台静态 volatile 读（3.3），滚轮封送在枢纽不在钩子线程。
- **热路径清单核对**：框选鼠标移动/放大镜取色、长截图 30ms 静止采样、录屏（重活全在 ffmpeg 进程）——全部进程内直调，不受程序集边界影响。

## 8. 风险清单

| # | 风险 | 缓解 |
|---|---|---|
| R1 | OverlayWindow/大文件搬迁引入回归 | 迁移 diff 三类变化白名单（using/命名空间/访问修饰符），逐行审；搬迁与重构严格分批 |
| R2 | internal→public 的 API 面扩散 | Platform 公共面走契约评审；工具类最小公开 |
| R3 | 插件 XAML 跨程序集的主题/样式继承 | 插件窗口吃应用级合并字典与 Design.xaml 隐式样式（理论自动继承）；P2 验收单含暗/亮主题 + 150% DPI 专项 |
| R4 | 热键/托盘指向已卸载插件的命令 | 回退链 + 页面状态标注（P1） |
| R5 | 老用户 ffmpeg 在程序目录 | 双路径搜索兼容（4.6） |
| R6 | 单文件发布形态与 plugins 目录 | 见待确认 #2，P4 前定案 |
| R7 | 退出时序打破"录制中退出保段"语义 | 模块表固定插件先于基础服务关停（3.4），P2 验收含录制中退出用例 |

## 9. 决策点（2026-10-14 已拍板两项）

1. **脚本引用面**（已定：接受）：插件装载后其程序集进入 AppDomain，`ScriptEngine.BuildReferences`（`ScriptEngine.cs:350`）会把插件类型自动暴露给用户脚本 using——接受此能力增强，在 PLUGIN_DEV.md 写明。
2. **发布形态**（已定：按建议）：自包含单文件发布时，插件目录始终外置于 exe 旁（单文件仅包宿主），发布文档写明。
3. **PanelActionsPage 去留**（P4 评审，三选一）：
   - a) 原样保留——平台页只剩一个随插件在缺席时隐藏的按钮，未来每个插件的入口都要手动加进平台页（重新耦合，不推荐）；
   - b) 删除——截图入口由命令面板/热键覆盖，导航去掉"面板动作"项；
   - c) 改造为「命令总览」页——列 `ActionRegistry.All` 全量命令（内置/脚本/插件徽标 + 直接执行 + 已绑热键），页面有独立价值且只依赖命令骨干，与插件零耦合（推荐）。
