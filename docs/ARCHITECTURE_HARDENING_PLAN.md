# LesTool 架构加固设计方案

日期：2026-11-01。状态：设计基线；核心实现与验证结果见 ARCHITECTURE_HARDENING_PROGRESS.md。适用基线：当前 .NET 8 / WPF、Platform 1.2.0、8 个同仓插件。

## 1. 目标与边界

保留“宿主 + Platform + 进程内插件”和“触发源 × 命令”两条主线。近期完成可恢复的生命周期、用户配置保护、受控上下文查询、完整交付；独立插件依赖隔离作为有进入条件的后续阶段。

本文保留最初设计目标。后续已实施核心加固；接口、目录与验证边界以当前源码和实施记录为准，性能预算仍须在目标机器实测后定案。

必须维持的行为：

- Alt+Space 命令面板、右键轮盘、现有快捷键与插件入口继续工作。
- 一命令一键位；快捷键仍在插件页头配置；轮盘仍承担跨插件编排。
- 智能搜索的 SelectedText/Cursor 为选填；无选中文字时继续降级为输入条。
- 命令面板仍不提供选中文本/文件；本次不扩大入口能力。
- 空格预览仍为自触发插件；OCR/ffmpeg 等资产缺失仍按子功能降级。
- 用户偏好、插件开关与插件包作者清单分别存储；禁用和删除插件不自动清除用户配置。
- 插件仍重启后增删/启停生效；本期不增加热卸载、插件市场或通用远程执行框架。

完成标准：一次插件失败不留下可调用的半初始化对象；一次插件禁用不丢失绑定；一次配置写失败不破坏上一份有效配置；慢目标应用不冻结 LesTool UI；一次干净装配能产生完整可检查的程序目录。

## 2. 设计依据与不变量

| 当前证据 | 缺口 | 加固后的不变量 |
|---|---|---|
| PluginHost.LoadOne 先 Initialize，后进入 _plugins；注册直接写全局集合 | 失败后命令、页面、订阅没有整体回收 | 只有 Active 插件可见、可派发；失败资源可追溯回收 |
| CleanupOrphanHotkeys 按当前注册表删配置 | 禁用/失败被当作卸载 | 期望配置和本次可用状态分离 |
| AppSettings/PluginSettings/PluginState 直接 WriteAllText | 中断损坏、失败不可确认 | 最后有效文件保留，保存状态可观察 |
| ActionContext 外层一个 try/catch；UIA 与轮询同步等待 | 单字段失败影响后续字段，主线程阻塞 | 每字段独立失败；慢查询有准入和等待期限 |
| AssemblyResolve 在全部目录按简单名取第一个 DLL | 依赖选择缺少归属约束 | 依赖来源确定；歧义拒绝加载 |
| 各插件 AfterBuild 复制到已存在的宿主 bin | 干净构建、发布装配依赖隐含条件 | 打包顺序显式，发布输出来自全新 staging |

上述位置分别位于 src/LesTool/Services/Plugins/、Services/ActionRegistry.cs、Services/ActionContext.cs、App.xaml.cs 与各插件 csproj。审查证据来自当前源码；运行性能、真实故障概率与 WPF 自定义加载上下文兼容性尚待验证。

## 3. 保持三层结构，按职责建立接缝

目标依赖示意：

~~~text
热键 / 面板 / 轮盘
        ↓ 捕获 TriggerEnvelope
CommandExecutor → ContextProvider → UIA 执行器 / 剪贴板协调器
        ↓                        （返回普通数据快照）
CommandCatalog 中 Active 的命令 → 插件回调

App 组合根 → PluginRuntime → 插件状态、注册批次、资源作用域
            SettingsStore → 原子文件写入与恢复
            Package 编排 → 宿主 + 插件 + 清单 + 依赖 + 资产
~~~

先在宿主中建立内部类型，不先拆一批新程序集。App 负责构造实例和接线；现有静态入口可暂时作为转发门面，但不再持有第二份状态。测试可以直接创建独立 Catalog/Executor/Store，避免跨用例污染。

Platform 已是 WPF 桌面平台，当前不强行追求跨平台。它的公开契约与系统实现先按命名空间/目录区分，稳定数据模型不引用具体窗口。若未来出现独立 SDK 发布需求，再安排 Contracts 与 Windows/WPF 实现的程序集拆分和二进制迁移。

## 4. 插件生命周期与注册事务

### 4.1 状态与所有权

内部运行状态：Discovered → Validated → Initializing → Starting → Active → Stopping → Stopped；校验、初始化、启动任一失败进入 Failed，禁用直接进入 Disabled。各状态包含 PluginId、运行代次 Epoch、阶段、异常摘要和耗时。

每个插件独占一个 PluginRuntime，持有实例、RegistrationBatch、ResourceScope、停止令牌。命令/页面同时记录 OwnerId 与 Epoch；命令字符串不再承担唯一的归属证明。

规则：

1. PluginId 以规范化小写标识保存，比较使用 OrdinalIgnoreCase；现有 8 个 id 不改名。
2. 插件 ID、命令 ID 均须唯一；命令要求插件前缀，宿主命令归属保留的 host 所有者。重复 ID 返回注册错误，禁止覆盖先前注册。
3. 清单 ID 限制为字母数字、点、下划线和连字符，并拒绝点目录、分隔符与 Windows 保留设备名。入口程序集必须解析到插件目录内。
4. 资产路径用 GetRelativePath/完整目录边界校验，不能只判断字符串 StartsWith；例如 assets-other 不属于 assets。资产辅助 API 不被当作进程内插件的权限沙箱。

### 4.2 初始化、提交与回滚

加载步骤：

1. 一次性发现清单并校验重复 ID、版本、路径；这一步不执行插件代码。
2. 创建 Runtime 与暂存注册上下文。RegisterCommand/RegisterSettingsPage 只写 RegistrationBatch，不发布 PagesChanged，不让主导航观察到半成品。
3. 在 UI 线程调用 Initialize。资源在创建后、任何后续可能失败的操作前，立即交给 ResourceScope。
4. 执行已登记的启动回调，检查命令/页面批次、命令前缀和上下文声明。受宿主管理的事件回调在 Active 以前不派发。
5. 构造新的 Catalog 快照，在 UI 线程一个不含 await 的提交段中切换命令/页面与 Active 状态。注册冲突必须在切换前验证完毕。
6. 提交后统一通知 UI。通知订阅者异常逐一记录，不回滚已成功提交的插件，也不阻止其他订阅者。

任何失败：先关闭回调闸、撤销该代次的发布项、取消停止令牌；对已创建实例尽力调用 Shutdown，finally 中继续逆序释放 ResourceScope。每个清理项独立捕获异常；某个窗口关闭失败不能阻断事件退订与定时器释放。最终记录原始失败和清理失败，并继续加载其他插件。

注册“事务”只覆盖宿主管理的注册和明确登记的资源，不承诺撤销插件任意文件操作。现有 8 个插件必须逐一迁移；宿主无法自动发现未登记的静态事件、线程和句柄。

### 4.3 最小增量能力接口

冻结当前 ILesHost/ILesPlugin 的必需成员。新增可选能力接口，插件通过能力探测使用，不继续给旧接口增加必需方法：

~~~csharp
public interface IPluginLifetimeHost
{
    IPluginLifetime Lifetime { get; }
}

public interface IPluginLifetime
{
    bool IsActive { get; }
    CancellationToken Stopping { get; }
    T Own<T>(T resource) where T : IDisposable;
    void OnStopping(Action stop);
    void OnStarted(Action start);
}
~~~

Own 接收幂等 IDisposable；窗口、事件与本机句柄通过小型适配器登记。内部事件订阅辅助方法返回 IDisposable，并用 IsActive/Epoch 过滤回调。同步键盘过滤器保持 volatile 快路径，不能在钩子线程引入锁等待。

OnStarted 表示 Starting 阶段的启动钩子，不表示已经可对外派发；启动钩子订阅的事件须经过上述门禁。OnStopping 先停止生产新工作，Dispose 再释放资源。插件 Shutdown 负责业务收尾，资源释放由作用域兜底，避免两个路径各自维护一份不一致清单。

迁移重点：Clipboard 的监听/保存计时器，Preview 的 FilterKey 与轮询，DownloadRouter 的 watcher/巡检/账本，Screenshot 的会话/ffmpeg，Volume 的 COM 对象与刷新计时器。

### 4.4 应用启动与退出

保留 ModuleTable 的显式顺序。宿主模块初始化失败时，回收已完成模块，并释放当前失败模块已登记的部分资源，然后退出；启动异常不能被通用 UI 异常处理器吞掉后继续运行半初始化应用。给 HotkeyService 增加显式 Stop，保证注销热键、拆消息钩子与释放 HwndSource。

异步任务引入后，以 ShutdownCoordinator 处理正常关闭：Closing 暂缓真正关闭 → 停止接收触发 → 关闭插件派发闸/取消等待任务 → 业务停止与配置 Flush → 逆序释放 → Application.Shutdown。流程幂等，重复关闭复用同一任务。UI 线程不能 Wait/Result 等待需要回到 UI 的任务。

现有同步 Shutdown 先保留；对确有异步收尾的下载搬运/录制，另增可选异步停止能力，同一实例只走一种业务停止入口。OnExit 保留同步兜底。系统强制结束不承诺完成异步收尾，依靠已持久化数据和下次恢复。Flush 使用独立的收尾期限令牌，不能复用已经取消的插件 Stopping 令牌；停止超时不等于后台操作已经结束，仍在飞的操作所需资源不能被提前释放。

## 5. 热键：配置意图与可用状态分离

Hotkeys 仍保存 Action、Keys、Enabled，第一阶段不改文件格式。运行时新增解析结果：Registered、Conflict、DisabledByUser、PluginDisabled、PluginFailed、MissingPlugin、MissingCommand、UnsupportedScenario、InvalidKeys。可用性从插件发现清单 + Catalog 计算，不回写覆盖用户的 Enabled。

具体变更：

- 将 CleanupOrphanHotkeys 替换为非破坏性的 ResolveBindings，所有暂不可用绑定保留。
- ApplyAll 只注册 Enabled 且命令 Active、支持 Hotkey 的绑定；其他项不调用 RegisterHotKey。
- 无效格式、系统占用与当前不可用分开显示和记录。找不到来源的旧绑定统一标记“命令暂不可用”，不猜测卸载。
- 非活动绑定不占实际快捷键，也不阻止活动插件配置相同键位。插件恢复时统一重新算冲突，冲突行保留配置并标记，禁止静默改键。按现有绑定顺序先到先得，后者标记 Conflict，不依赖插件目录扫描顺序，也不重排持久化配置。
- 键位比较先解析成 modifiers + virtual key，避免大小写/修饰键顺序形成不同字符串。兼容期对非规范 ID/键串的迁移先备份，碰撞不自动合并。
- 同一命令的旧多绑定只在用户编辑该命令时按现行一命令一键位规则收敛，不在启动时批量改写。
- 显式卸载仅改变插件文件/可用性；若未来增加“同时清除配置”，它必须是独立、明确的操作。

默认面板键位只在新建用户配置时初始化一次。旧配置中没有 panel.launcher 绑定时保留原样，避免把用户主动清空的键位在下次启动重新补回来。迁移后记录默认初始化已完成；不把“当前无绑定”等同于“从未初始化”。

验收关键路径：配键 → 禁用 → 重启 → 启用 → 重启，原键位恢复；模拟插件加载失败后恢复，键位仍在；主动清空面板键位后重启不复活。

## 6. 配置持久化：原子替换、恢复与可确认保存

### 6.1 共用存储实现，保持数据归属

新增内部 AtomicJsonFile 与 SettingsStore：前者负责文件读写/备份，后者负责值快照、版本与保存状态。AppSettings、PluginState、PluginSettings 复用这一实现，但文件、键空间与生命周期仍彼此独立。由 SettingsStoreFactory 保证同一路径只存在一个写入协调器。

记录内存 Revision 与 PersistedRevision；只有确认落盘的版本才能推进后者。后台写入按文件串行，不能让旧快照覆盖新快照；合并写入时，等待某版本的调用可由更新版本成功落盘满足。业务对象必须在所属线程生成不可变/深复制快照，再交给文件线程。

### 6.2 写入步骤

1. 序列化并验证待写 JSON；创建同目录唯一临时文件，使用 CreateNew，禁止占用现有文件。
2. 写入完整字节并 Flush(flushToDisk: true)，关闭临时流。
3. 目标存在时通过 File.Replace(temp, target, backup) 替换并保留上一份文件；首次创建时在同目录 Move 到目标，不覆盖竞争创建的文件。
4. 替换完成后确认保存成功；失败保留原目标、记录失败与 dirty 状态。清理仅针对本次拥有的临时文件。
5. 不允许失败后降级为“先删原文件再复制”。异常中断恢复时验证主文件、备份与临时文件，临时文件不因时间较新就自动胜出。

临时文件、主文件和备份必须同卷；微软 File.Replace 文档明确了卷约束和备份行为。Flush(true) 用于刷出中间文件缓冲，但整体方案不承诺所有硬件掉电场景下的绝对持久性。[File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace?view=net-8.0)、[FileStream.Flush](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-8.0)

### 6.3 读取与恢复

ReadResult 包含 Valid、NewFile、RecoveredFromBackup、ProtectedDefaults、UnsupportedSchema，以及可展示的原因。

- 主文件有效：正常加载，校验关键值范围；不因为可选字段缺失而丢弃整份设置。
- 主文件损坏、备份有效：先把损坏原件复制归档为带时间戳的 corrupt 文件，归档成功后恢复备份；恢复路径也使用可靠写入。恢复时保留已验证的良好备份，不能让替换步骤把损坏主文件写进同一个 .bak，覆盖仅剩的良好副本。只提示一次，不把全部错误当成首次启动。
- 主备都无效：默认值只用于保护性运行，禁止自动写回该文件；依赖损坏配置的自动后台动作暂停。用户通过现有设置/插件管理界面的恢复操作明确重置后才重新开放写入。plugins.json 无法恢复时只启动宿主与插件管理，不按“全部启用”加载未知偏好的插件；恢复后重启应用。
- 文件版本高于可读版本：保留原文件、禁止降级写回；显示版本不兼容。
- 无文件：才允许新配置默认初始化；读取权限失败不等于无文件。

宿主/插件状态 DTO 使用扩展数据保存未知字段；插件键值字典保留未识别键。第一阶段只升级写入实现，不加 envelope、不搬目录。确需改变结构时，再增加 SchemaVersion（插件字典使用保留键 $schemaVersion），按 v0 → v1 的纯转换函数迁移；迁移原件另存不可变备份。

### 6.4 失败语义与兼容入口

拟议内部结果：

~~~csharp
public sealed record SaveResult(
    bool Succeeded, long PersistedRevision,
    string? ErrorCode, string? ErrorMessage);
~~~

现有同步 IPluginSettings.Set 保持同步保存语义，失败抛出明确的 SettingsWriteException，并由统一设置操作边界显示“当前修改尚未保存”。不能继续只记日志并正常返回，也不能悄悄把旧 Set 改成无确认的异步队列。

对较大数据和频繁写入新增可选 IAsyncPluginSettings：SetAsync/FlushAsync 返回可等待结果；现有 Get/Set 保留以兼容简单插件。自有设置页逐步改为 await 的保存路径，内存即时生效，保存失败显示未保存状态、保留 dirty 并有界退避重试。事件处理器负责捕获和呈现结果，业务层不使用 async void。

MoveLedger 必须在“对应版本或更新版本已成功持久化”后才视为已保存。BeginInvoke 的排队成功不能充当写入成功；写入失败或新账到来都保留 dirty。应用退出时 await Flush，不能仅投递一个可能来不及执行的 UI 回调。P1 一旦引入后台写入，就同时交付 ShutdownCoordinator 的最小 Flush 等待路径，不等到 P3 才补退出保障。

剪贴板历史自有 JSON 也复用文件写入器；图片独立 PNG 继续保持现有布局，索引引用的图片应先完成落盘，再提交索引，孤立图片清理由独立维护步骤执行。

## 7. 上下文与命令执行的异步化

### 7.1 在等待之前固定目标

引入内部 TriggerEnvelope，不立即改现有 LesContext 的字段/构造函数。内容包括 ExecutionId、Source、TargetHwnd、目标进程身份、Cursor、捕获时刻和触发会话代次。

- 面板：弹出抢焦点前捕获 TargetHwnd，执行时沿用该快照。
- 热键：收到 WM_HOTKEY 后，在任何 await 之前捕获。
- 轮盘：手势显示前捕获原目标，执行仍指向同一会话目标。
- HWND 必须配合窗口仍存在/进程身份校验，防句柄复用；不能只存标题。
- 后台查询不能在几百毫秒后重新读取全局 FocusedElement 并把新应用内容当作原目标。UIA 元素必须验证属于目标；选中文本/文件在查询期间仍可能变化，返回数据记录采集时间，超出会话有效期就丢弃。

入口能力表保持当前规则。获取需求为 (Requires | Optional) 与当前入口能力的交集，快照可提供的字段沿用既定能力规则；不向不支持的入口偷偷获取选中项。每个字段单独返回 Ready、Empty、TimedOut、Failed、Stale、NotRequested；某个字段异常只影响自身。最终映射成现有 LesContext，缺失必填仍由执行器统一拦截，选填保留原降级。

### 7.2 有界 UIA 执行器

统一 SelectionText 与 ExplorerSelection 的 UIA 查询，使用无窗口的专用 MTA 工作线程，线程内创建、使用并释放 AutomationElement，只向调用方返回文本/路径/矩形 DTO。微软对桌面 UIA 客户端建议独立线程；跨线程传递 COM 自动化对象还存在 apartment 生命周期问题。[UIA 线程说明](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading)

初始策略（待实测调整）：

| 项目 | 初始预算/约束 |
|---|---|
| UIA 同时执行 | 1 个，不为每次超时创建新线程 |
| 排队 | 上限 8；过期请求直接丢弃，预览跟随只保留最新请求 |
| 选中文字查询/轮盘探测 | 调用方最多等待 150ms（含排队） |
| 文件选中项查询 | 调用方最多等待 250ms（含排队） |
| 挂起提供方 | 当前调用超时后停止接纳新 UIA 工作，直到原调用真实返回 |
| 健康恢复 | 原调用返回后释放占用；丢弃迟到结果，经退避再接纳请求 |

必须区分“调用方超时”和“底层 COM 调用结束”：CancellationToken 不能强制终止挂住的 UIA。不能 Thread.Abort，也不能持续新建替代线程。工作线程永久挂起时，本次进程中的 UIA 功能降级；需要重启恢复。若未来要求无需重启即可恢复，应单独评估进程外 UIA 代理，本期不引入。

轮盘展示不再同步等 ProbeSelection：先显示其他槽位，需要选中文本的槽位暂空；探测返回且会话仍有效后再恢复相应槽位，不挪动布局。Optional SelectedText（智能搜索）不因无选区被隐藏。

预览的 300ms 跟随轮询不叠加 UIA 请求：在飞时仅记录最新意图；新选中项结果携带窗口/内容 generation，旧结果不能替换新预览。

### 7.3 剪贴板兜底作为有串行约束的操作

保留 UIA 优先、Ctrl+C 兜底，但将轮询改成异步等待剪贴板更新与超时，不在 UI 线程 Sleep。必须用 STA 的剪贴板调用仍在 UI/专用 STA 上短时执行，不与 UIA MTA 混用。

一次兜底流程：

1. 进入平台内部剪贴板操作协调器，防本程序的取词、回贴、恢复相互交错。
2. 快照可恢复的格式与序列号；空剪贴板也有明确恢复动作。不支持或无法可靠快照时直接降级，不把它视为空剪贴板。
3. 注入前再次确认前台仍是原目标、且非控制台/自家窗口，否则取消兜底；不强行切换到另一个目标。
4. 注入 Ctrl+C，异步等待预期剪贴板更新，初始上限 300ms；查询期间会话取消则停止等待。
5. 读取结果后，仅在当前序列仍对应本次拷贝、且没有观察到第三方新写入时尝试恢复。外部更新已到来时保留外部新内容，禁止无条件覆盖。
6. 自发写标记带操作 ID、序列关联与过期时间，替代可能长期残留的纯次数计数；超时分支也处理迟到更新。

外部进程仍可能在检查和恢复之间竞争，不能宣称完全消除系统剪贴板竞态。对无法确定归属的更新宁可跳过恢复/兜底，记录受限降级；对用户可见的恢复失败给出明确提示。协调器不能长时间持有 OpenClipboard 锁跨越 await。

### 7.4 执行器与异步回调

Catalog 负责注册/查询，CommandExecutor 负责执行；保留 ActionRegistry 静态转发门面作为迁移接缝。新内部签名：

~~~csharp
Task<CommandResult> ExecuteAsync(
    string commandId, TriggerEnvelope trigger,
    CancellationToken cancellationToken);
~~~

执行步骤：查询 Active 命令 → 校验 Source/Scenarios/入口能力 → 捕获 Owner/Epoch → 按声明异步获取上下文 → 检查会话与插件仍有效 → 必填拦截 → 在 UI 线程进入插件处理器 → await 处理器 → 返回结果。场景校验在执行边界再次完成，不能只依赖配置 UI 的筛选。

CommandResult 区分 Succeeded、Unavailable、MissingContext、Cancelled、Busy、Failed、StaleTrigger。热键等事件入口统一调用一个捕获异常的入口适配器，避免无观察的 Task；异常日志和 Toast 由执行边界负责一次，不层层重复提示。

现有同步 Action<LesContext> 转换为调用后返回 CompletedTask；现有回调仍从 UI 线程进入。新增可选 IAsyncCommandHost，允许注册 Func<LesContext, CancellationToken, Task>，元数据使用独立 CommandDescriptor。内部 CommandEntry 持有 Descriptor、OwnerId、Epoch 和异步处理器；旧 LesCommand 类型与注册签名在兼容窗口内保留，转换后交同一执行器，UI 不直接调用 Callback。

取消只表示停止等待/协作取消，不宣称中断任意同步处理器。保持同步 toggle 类命令现有行为；异步长命令按声明选择拒绝重入或接纳并发，不给所有命令套一把全局锁。尚未进入处理器的同会话过期请求可被新请求取代；已经开始的文件搬移不因 UI 换焦点自动回滚。

所有 SendInput 类处理器在真正注入前验证目标窗口；目标不再匹配时返回 StaleTrigger，不能把按键发给刚切换到的其他应用。长任务完成后的 UI 更新同时校验内容 generation 与插件停止令牌。

## 8. 插件依赖与契约兼容

### 8.1 近期采用同仓、同包版本策略

当前 8 个插件仍与宿主一起构建/交付。近期先做确定性校验，不把自定义加载上下文作为前四阶段的先决条件：

- 修复现有全目录探针：维护 Assembly → OwnerPlugin 的归属，优先且只解析请求插件的依赖；递归依赖同样登记归属。来源不明时仅允许明确共享清单，不扫描其他插件目录碰运气。
- 同包检查全部依赖的程序集标识、版本与内容哈希；共享解析路径遇到冲突直接报告插件/依赖来源，禁止按目录顺序决定胜者。当前策略宁可拒绝不兼容插件，也不加载未知版本。
- Platform 契约、WPF/.NET 框架与 Serilog 为明确共享项；日志共享的目的是保持同一 Logger 配置。插件不能携带第二份契约类型参与转换。
- 原生库使用确定的绝对路径，记录来源。近期同名原生库要求整包一致；若不能验证运行时并存，就拒绝该组合，不能把托管隔离当作原生隔离已经成立。
- 全局 AssemblyResolve 订阅与 AddDllDirectory 返回的目录句柄由宿主生命周期管理，退出时回收。加载失败的插件不再成为后续插件的依赖探测来源。

### 8.2 版本规则

本轮公开能力按兼容新增规划为 Platform 1.3.x，现有接口必需成员与已有构造函数冻结。IPluginLifetimeHost、IAsyncPluginSettings、IAsyncCommandHost 等采用独立可选接口，能力不满足时拒绝对应新插件或按明确约定降级。

plugin.json 新增 apiContract 主版本字段，minPlatform 继续表达最低实现版本。检查顺序为：字段合法 → 支持的契约主版本 → 最低实现版本 → 入口引用和类型可赋值性 → 初始化。非法版本字符串必须失败，不能跳过校验。

同一契约主版本只做向后兼容新增；删除成员、修改签名或改变基本语义需要新的契约主版本和明确迁移。包版本、契约主版本、程序集绑定身份分别处理；若未来冻结 AssemblyVersion，minPlatform 必须改读显式 API 版本元数据，不能继续误读冻结后的程序集版本。

本轮交付更新 8 个清单并同批重编；至少把所有清单的最低支持基线纠正到实际使用的接口版本。1.2 以前已有破坏性接口演进，不能仅凭同为 1.x 就承诺历史二进制可用。缺 apiContract 的旧插件标记 Legacy，不在未验证时自动认定兼容；用户数据和绑定仍保留。

兼容矩阵至少验证：新宿主 + 全部新插件、新宿主 + 已声明支持的上一版插件、旧宿主拒绝要求新版能力的插件。尚未通过矩阵的组合必须明确要求重编，不能称为已兼容。

### 8.3 独立依赖隔离的进入条件

实施更新：用户已要求继续实施 P5；现已启用独立 ALC。下列进入条件保留为设计基线，实际证据及边界见 ARCHITECTURE_HARDENING_PROGRESS.md。

出现“独立发布插件”或“必须同时使用冲突依赖”需求后，先做加载器原型：每插件一个非 collectible PluginLoadContext + AssemblyDependencyResolver；依赖按该插件 .deps.json 解析，Platform/框架/共享日志走宿主同一实例。保留重启卸载语义，暂不追求运行时回收整个 WPF 插件。[官方插件加载方案](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support)

原型通过以下用例才替换生产加载器：两插件各用不同版本的同名托管依赖；全部插件 XAML/BAML、pack URI、隐式样式、主题切换正常；OCR/Skia/ONNX 原生库及 Office 预览正常；本地化与资源程序集正确；缺依赖时只标记对应插件失败。

托管加载上下文不承诺原生库同名多版本并存，也不形成进程故障隔离。若原型在 WPF/原生库上不成立，保留经过校验的同包统一依赖策略；迁移仅回退加载器和整包二进制，不改用户配置。

## 9. 构建、装配与诊断

### 9.1 唯一发布入口

新增 scripts/package.ps1，复用现有 .NET 8 SDK 锁定和 Python UI 门禁。将各插件重复的输出/依赖复制规则集中在 build/Plugin.Build.targets；PluginId、入口工程与清单一致性在打包时校验。

构建工程不再负责隐式修改另一个项目的输出。先保留开发期 DeployToHost 作为兼容入口，但由解决方案构建完成后的显式装配步骤调用，去掉 Exists(HostOutput) 决定是否悄悄跳过的行为。旧路径的收缩仅在新入口验收后实施。

发布过程：

1. 校验工具版本、清单与输入目录；读取 src/Plugins 中的插件源清单形成预期集合，不能仅扫描已有宿主输出。
2. 创建本次独有的 artifacts/staging/<build-id>；不使用现有 bin/publish 中的旧文件补齐缺项。
3. 构建解决方案并运行相关测试/UiGate；发布宿主为现行 win-x64 自包含模式。
4. 分别发布全部插件至各自 staging 子目录，收集完整运行期依赖、.deps.json、runtimes/、资源程序集、plugin.json、资产；不能只复制根目录 *.dll。
5. 按明确共享清单去除多余契约/共享库，验证剩余依赖能解析。OCR 模型是否必需由子功能资产策略定义；缺 ffmpeg 记录录屏不可用，不能误判整个截图插件损坏。
6. 生成 package-manifest.json，包含源版本/构建标识、SDK/RID、宿主/API/插件版本、文件相对路径与哈希、可选资产状态。不把用户设置、日志、缓存打包进去。
7. 执行目录清单、版本、依赖与装载探测；源清单中的插件数/ID 集合必须与装配产物一致，当前基线是 8 个。
8. 验证成功后在同卷将 staging 提升为一个新的、带版本的完整发行目录，生成归档。失败保持上一发行目录与用户正在运行的程序不变。

回滚通过选择上一份完整发行目录完成，不覆盖运行中的 exe，也不把新旧插件文件混装。清理 staging 仅允许删除本次创建且已经验证位于 artifacts/staging 下的目录；包脚本不递归操作程序安装目录或 APPDATA。

### 9.2 验证模式与日志

增加开发诊断入口，只在独立子进程中扫描清单、校验依赖、装载程序集和检查入口类型；不调用真实插件 Initialize，不安装全局钩子、不启动下载 watcher。它证明包可装载，不等同于功能启动验收。

生命周期故障使用测试插件与假宿主适配器验证。真实输入、UIA、原生 OCR 与录屏仍需 Windows 运行验收。新增 AppPaths 统一数据路径注入，测试使用临时根，保证不接触实际 APPDATA；诊断进程使用独立互斥体标识，避免唤起用户现有实例。

运行日志补充 PluginId、Epoch、ExecutionId、TriggerSource、阶段、耗时、上下文字段状态、保存 Revision。UIA 排队/超时、插件失败、配置未保存均有可定位摘要；通常不记录选中文本和剪贴板全文。性能日志用于验证预算，不用模糊的“卡了一下”作为唯一证据。

## 10. 分阶段实施、进入条件与回退

各阶段都是可单独审查的完整改动；每阶段通过相关构建/测试后才能进入依赖阶段。新增能力对外统一以完整发行包交付；不在一次改动中重写所有页面和业务实现。

| 阶段 | 主要改动 | 验收出口 | 回退方式 |
|---|---|---|---|
| P0 配置意图保护与最小测试基线 | 去掉启动删绑定，解析运行状态，修默认面板键复活，建立测试工程 | 禁用/失败/缺失/清空键位后重启配置不丢；旧 JSON 可读 | 代码回退；没有结构迁移，不恢复旧数据覆盖新配置 |
| P1 可靠存储 | AtomicJsonFile、读恢复/保护模式、保存结果、账本确认、退出 Flush 接缝 | 临时文件写入/替换失败时上一有效配置可读；账本失败保持 dirty | 原格式和旧读取器保留；必要时从迁移备份显式恢复 |
| P2 生命周期 | 注册批次、资源作用域、所有权、失败回滚、显式停止模块 | 初始化任一点失败无残留命令/订阅；其余插件可用；停两次安全 | 整包回退；用户数据不因加载器切换变更 |
| P3 异步查询与执行 | 快照目标、有界 UIA、异步剪贴板、命令结果、预览代次、异步关闭 | 挂起 UIA 不阻塞主 UI、不增长线程；旧结果不污染新会话 | 各子链路保留短期内部适配器，按提交回退；不加用户可见模式开关 |
| P4 完整装配与兼容 | package 脚本、公共 targets、依赖校验、诊断、版本矩阵 | 空 bin/obj 的隔离工作副本中生成完整包，装载集合等于源集合 | 保留上一发行目录，切回整包；不混拷 DLL |
| P5 条件阶段 | 独立 ALC 原型并按用例评估 | 托管冲突依赖与 8 插件 WPF/原生验收全部通过 | 原型未通过则保留 P4 的统一依赖策略 |

依赖关系：P0 立即先做；P1 提供后续迁移的数据保护；P2 后再迁移异步生命周期；P4 的脚本与依赖清单可以提前编写，但最终装配验收必须包含 P1–P3 的最终产物；P5 仅在进入条件成立后排期。

每个阶段开始前保存工作区基线与用户数据迁移备份，不覆盖用户未提交的改动。代码恢复与数据恢复分别处理：代码回退不自动覆盖用户在新版中做出的配置；只有发生不兼容 schema 变化时才使用对应备份，并先保留新版原件。

## 11. 验证矩阵

新增一个 Windows 测试工程（例如 tests/LesTool.Tests），先覆盖核心状态与文件行为；真实 UI/COM 集成用例单独标记，CI 使用 Windows/.NET 8。依赖仅在需要控制时钟、文件失败点、系统调用或进程适配器时抽象；不为每个类创建接口。

| 范围 | 必须验证的行为 |
|---|---|
| 插件事务 | 在命令注册后、页面注册后、订阅后、启动钩子中注入失败；Catalog 无该插件项、作用域逆序清理、Shutdown 异常不阻断清理、重复 Stop 安全 |
| 所有权 | 重复插件/命令 ID 拒绝；失败插件不能覆盖宿主命令；旧 Epoch 的回调与查询结果不再生效 |
| 热键持久化 | Disabled/Failed/Missing 插件绑定保留；恢复后能注册；相同键恢复时显式冲突；面板主动解绑后不自动添加 |
| 配置文件 | 在写临时文件、刷盘、替换前后分别注入失败；主文件为有效旧版或有效新版；备份可恢复；主备损坏进入保护模式；未知字段保留 |
| 保存并发 | r1/r2 写入交错不会以旧覆盖新；r1 成功但 r2 尚未写入仍 dirty；写失败上层可确认，MoveLedger 不清脏 |
| 上下文 | 一个字段异常不妨碍其他字段；目标变化/句柄失效返回 Stale；必填拦截、选填降级、Palette 能力限制保持 |
| UIA 限流 | 用假提供方模拟永久阻塞/迟到返回；等待方超时，线程数有界，队列不增长，迟到结果不执行旧命令；真实挂起场景另做集成验收 |
| 剪贴板 | 空值恢复、富文本/文件/图片快照、无法快照、迟到复制、第三方更新、取消、恢复失败；后续正常复制不被残留自发标记长期吞掉 |
| 业务纯逻辑 | 轮盘几何/场景覆盖、命令筛选、下载规则匹配；保留可复现边界用例，避免只测试实现细节 |
| 发布 | 隔离副本干净构建；缺插件/依赖/必须文件时失败；可选资产缺失按预期降级；上一发行目录不变；包中不含实际用户数据 |

性能验收用相同开发机/数据集分别记录改前改后：钩子回调不执行 IO/COM/同步 UI 等待；普通命令的新增调度开销目标 p95 <20ms（不含插件业务与慢上下文）；UIA 挂起时 UI 心跳、窗口移动和其他命令仍响应；轮盘首显不等待慢查询。数值是设计目标，不是当前测量结论，未达标必须说明数据和原因。

运行验收覆盖 8 插件的主要路径，特别是多显示器/DPI、空格预览跟随、截图与轮盘抑制、录制中退出、下载搬运中退出、无 ffmpeg/无 OCR 模型。尽量复用现有人工验收，不另建高成本全量 UI 回放系统。

## 12. 文件影响地图

以下新路径是实施建议，本轮尚未创建这些代码文件：

| 路径 | 责任 |
|---|---|
| src/LesTool/Services/Plugins/PluginHost.cs | 拆分发现/校验/初始化/提交；保留启动入口门面 |
| Services/Plugins/PluginRuntime.cs、RegistrationBatch.cs、PluginResourceScope.cs | 状态、所有权、提交与失败回收 |
| src/LesTool/Services/ActionRegistry.cs | 兼容转发与 Catalog 查询，逐步移走执行责任 |
| Services/Commands/CommandCatalog.cs、CommandExecutor.cs | 注册快照与统一异步执行 |
| Services/Context/TriggerEnvelope.cs、ContextProvider.cs、UiaQueryExecutor.cs、ClipboardOperationCoordinator.cs | 目标快照、字段结果、限流与兜底协调 |
| Services/Settings/AtomicJsonFile.cs、SettingsStore.cs、SettingsStoreFactory.cs | 共用可靠存储；AppSettings/PluginState/PluginSettings 转适配器 |
| Services/HotkeyBindingResolver.cs、Views/Pages/HotkeyOps.cs | 期望配置解析、重复键判定、状态呈现 |
| Services/ShutdownCoordinator.cs、App.xaml.cs、MainWindow.xaml.cs、HotkeyService.cs | 有序关闭、启动失败回收、显式基础服务停止 |
| src/LesTool.Platform/Contracts/ | 可选能力接口、结果/元数据契约；现有必需接口冻结 |
| src/LesTool.Platform/Interaction/SelectionText.cs、Shell/ExplorerSelection.cs | 将慢查询迁移到受控执行器，保留按需适配入口 |
| src/Plugins/* | 资源登记、异步调用/保存与目标校验；各插件业务边界不重画 |
| build/Plugin.Build.targets、scripts/package.ps1 | 统一构建约定和完整发布装配 |
| tests/LesTool.Tests/、可选的测试插件目录 | 故障注入、配置、规则、装配验证 |

表中 Services/、Views/ 的省略根路径均为 src/LesTool/。新 Store/Executor 的内部拆分随实施复杂度调整，不为了对齐此表机械创建空壳类型。

完成对应阶段时同步 README、PROJECT_STRUCTURE、PLUGIN_DEV；涉及设置页交互再同步 PLUGIN_UI_SPEC，涉及 UI 规则再同步 DESIGN 与 check_ui.py。旧方案保留为历史记录，增加指向新规则的说明，避免把互相矛盾的两套规则都当成当前约束。

本方案最终交付的是：能继续沿现有架构增加插件，同时对失败、恢复、保存、执行和发布有明确、可验证约束的桌面应用。实施前仍需确认的技术事实限于真实 UIA 提供方行为、目标机器性能预算、WPF/原生库加载原型；这些不阻止先完成 P0–P2。
