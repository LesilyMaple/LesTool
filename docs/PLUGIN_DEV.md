# LesTool 插件开发指南

> 面向插件作者。规范成文于 2026-10-15（P4）；与代码不符时以代码为准、顺手更新本文档。
> 最小骨架见第 3 节工程模板（模板即全文，直接复制）；完整范例：截图插件、音量混音器插件。

## 1. 心智模型：平台 + 插件

```
LesTool.exe（宿主：钩子/热键/面板/主题/插件装载器）
   └─ 引用 → LesTool.Platform.dll（契约：接口 + 共享设施，版本即兼容承诺）
                 ↑
   插件（只准引用 Platform，禁止引用宿主）
   plugins/<id>/{plugin.json, *.dll, assets/**}   ← 构建自动部署；删目录重启即卸载
```

插件是**进程内程序集**：宿主用字节数组 `Assembly.Load` 装载（不锁文件），调用是普通 .NET 直调，零跨进程开销。触发×命令矩阵是消费面：插件注册的命令自动进入**热键、命令面板**两类触发源（轮盘槽位开发中）。

## 2. 五分钟起步

照第 3 节建三样东西：

1. `.csproj`：整段复制工程模板，改 `AssemblyName` / `RootNamespace` / `PluginId`；
2. `plugin.json`：按字段表填写全部字段；
3. 入口类：实现 `ILesPlugin`（命名空间 + `Id` 属性与清单一致）。

构建解决方案，产物自动落到宿主输出 `plugins\<你的Id>\`，启动即装载。

## 3. 工程模板

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>LesTool.Plugin.YourName</RootNamespace>
    <AssemblyName>LesTool.Plugin.YourName</AssemblyName>
    <Version>1.0.0</Version>
    <PluginId>yourname</PluginId>
    <!-- 带原生依赖的包（如 OCR）才需要：RID 让原生库平铺输出 + 强制包程序集落 bin -->
    <!-- <RuntimeIdentifier>win-x64</RuntimeIdentifier> -->
    <!-- <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies> -->
    <HostOutput>$(MSBuildProjectDirectory)\..\..\LesTool\bin\$(Configuration)\net8.0-windows\win-x64</HostOutput>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\LesTool.Platform\LesTool.Platform.csproj" />
  </ItemGroup>
  <Target Name="DeployToHost" AfterTargets="Build" Condition="Exists('$(HostOutput)')">
    <ItemGroup>
      <PluginDlls Include="$(TargetDir)*.dll" Exclude="$(TargetDir)LesTool.Platform.dll;$(TargetDir)Serilog.dll" />
      <PluginAssets Include="$(MSBuildProjectDirectory)\assets\**\*.*" />
    </ItemGroup>
    <Copy SourceFiles="@(PluginDlls)" DestinationFolder="$(HostOutput)\plugins\$(PluginId)\" SkipUnchangedFiles="true" />
    <Copy SourceFiles="@(PluginAssets)"
          DestinationFiles="@(PluginAssets->'$(HostOutput)\plugins\$(PluginId)\assets\%(RecursiveDir)%(Filename)%(Extension)')"
          SkipUnchangedFiles="true" />
    <Copy SourceFiles="$(MSBuildProjectDirectory)\plugin.json"
          DestinationFiles="$(HostOutput)\plugins\$(PluginId)\plugin.json" SkipUnchangedFiles="true" />
  </Target>
</Project>
```

`plugin.json` 字段（**小驼峰**，宿主按大小写不敏感解析）：

| 字段 | 说明 |
|---|---|
| `id` | 插件 Id：目录名、设置目录名、命令 Id 前缀约定共用 |
| `name` / `version` | 显示名与版本（日志/提示用） |
| `minPlatform` | 最低 Platform 版本；宿主较旧时拒载并提示（比装载后 MissingMethod 温和） |
| `entryAssembly` | 入口 dll 文件名 |
| `entryType` | 入口类全名（实现 `ILesPlugin`） |

## 4. 契约 API

```csharp
public interface ILesPlugin
{
    string Id { get; }
    void Initialize(ILesHost host);  // UI 线程；注册命令/设置页/订阅。禁止访问磁盘资产
    void Shutdown();                 // UI 线程；停进程、退订阅、放重资源。必须幂等
}

public interface ILesHost
{
    void RegisterCommand(string id, string name, string glyph, Action<LesContext> callback,
        string keywords = "", ContextKind requires = ContextKind.None,
        CommandScenarios scenarios = CommandScenarios.All, ContextKind optional = ContextKind.None);
    void RegisterSettingsPage(Func<FrameworkElement> factory); // 可选；只给纯内容，页框宿主管
    IPluginAssets Assets { get; }      // 解析 plugins/<id>/assets/ 下的相对路径（防越界）
    IPluginSettings Settings { get; }  // 独立键值存储，%APPDATA%\LesTool\plugins\<id>\settings.json
    void Toast(string message, int durationMs = 2000);
}
```

**Platform 公共设施**（`LesTool.Platform` 命名空间，插件直接用）：

| 设施 | 用途 |
|---|---|
| `GdiCapture` | 抓屏 / 显示器信息 / 物理坐标摆窗（跨 DPI 定位唯一可靠方式） |
| `WindowDetector` | 悬停点顶层窗口识别 |
| `MouseEvents` | 滚轮等鼠标事件（UI 线程回调，封送已做） |
| `RadialGuard` | 全屏会话抑制轮盘手势（`BeginSuppress()` 返回 IDisposable） |
| `ToastService` / `ClipboardService` / `AppIcon` | 提示 / 剪贴板元数据与自发标记 / 进程图标缓存 |
| `Platform.Controls` | 窗口镶边三件套（CaptionBar / BorderlessWindow / CornerRadiusClip）+ 设置页三件套（SettingsSection / SettingsRow / CheckRow，见第 8 节） |

## 5. 命令

- Id 约定 `<插件Id>.<动词>`（如 `screenshot.region`、`volume.mixer`）；重复 Id 顶替先前注册；
- `glyph` 是 Segoe MDL2 码位；`keywords` 空格分隔供面板搜索；
- **双属性声明（2026-10-26/31 拍板，Platform 1.1.0 起）**：
  - `scenarios`：可从哪些入口触发（Hotkey/Palette/Radial，缺省三源全开）。声明与能力表矛盾（如面板+必填选中文件）注册期即 Warning；
  - `requires`：**必填**上下文——缺了动作无意义。绑定期按入口能力表门禁（给不出的入口不列该命令），运行时取空由平台统一拦截+Toast，插件不必自己判空；
  - `optional`：**选填**上下文——缺了降级不失效。空字段照常派发，动作自判（要自定义"缺上下文"提示的走这里）；
- **入口能力表**（平台定死，声明只做减法）：轮盘/快捷键=全量；命令面板=除选中文件/选中文本（弹出即抢焦点，结构性取不到）；
- **设置页与页头自动装配（2026-10-31 起，Platform 1.2.0）**：`RegisterSettingsPage(factory)`
  只交"纯内容"（一串 `SettingsSection`）；宿主统一包页框——页头（图标可点击换 + 清单简介 +
  **键位小部件**：单动作直改、多动作汇总"已绑 N/M"+下拉逐条配，2026-10-31 二改热键配置上页头）
  + 配置区。注册过命令的插件必有键位配置入口，无设置页则自动生成"仅页头"页
  ——作者零成本，勿自己写热键 UI 或页头；
- **自带触发条件的插件不走命令注册**（如空格预览的空格监听），触发方式的配置放插件自己的设置页；
- 回调在 UI 线程；自己 try/catch 或依赖注册表的统一捕获（ERROR 日志）。

## 6. 资产三原则（强制）

1. **装载不依赖资产**：`Initialize` 不得读任何资产文件——零资产时插件核心命令必须可用；
2. **按子功能独立降级**：用到资产的功能在使用时点自查 `host.Assets.Resolve(...)`，缺失则隐藏入口或点击时提示，不牵连其他子功能；
3. **降级必须可解释**：文案写明具体路径，统一格式「缺少 {资产}，请放置到 plugins/{id}/assets/{相对路径}」。

大件资产（ffmpeg.exe、模型文件）**不入仓库**，由用户放置；读取方做多路径搜索（插件资产目录 → 程序目录，向后兼容）。

## 7. 生命周期与时序

- 装载发生在宿主模块表的 `plugins` 步骤：**先于热键恢复**（插件命令可被绑定）；
- 关停按逆序：**插件先于基础服务**——进行中的重活（录屏 ffmpeg）要能优雅收尾；
- 单插件装载失败只 ERROR + Toast，不拖累宿主与其他插件；`Shutdown` 可能被多次触达，必须幂等。

## 8. UI 纪律

- `DESIGN.md` 强约束对插件窗口同样生效：**全局隐式样式跨程序集自动继承**（宿主 App 级合并字典），插件窗口不写样式也合规；
- **设置页布局走 PLUGIN_UI_SPEC**（docs/PLUGIN_UI_SPEC.md，机器门禁强制）：纯内容只能是
  `SettingsSection` 堆叠；行用 `SettingsRow`（标签列 120）或 `CheckRow`（自说明开关），一行一项；
  区序 总开关→常用→规则→危险；即时生效无确定按钮。样板：下载归档页（表单行）、智能搜索页（列表块）；
- 引用宿主程序集内资源（如图标库）用 pack URI：`/LesTool;component/Assets/Icons.xaml`；
- 窗口镶边用 `Platform.Controls`（XAML 引用需 `;assembly=LesTool.Platform` 后缀）；
- 物理坐标窗口（置顶浮层/遮罩类）一律 `GdiCapture.MoveWindowPhys` 直摆，不要用 DIP Left/Top。

## 9. 常见坑速查

| 症状 | 原因与解法 |
|---|---|
| NuGet 包的 dll 没进 plugins 目录 | 库工程默认不拷包程序集——csproj 加 `CopyLocalLockFileAssemblies=true` |
| 原生库（onnxruntime 等）加载失败 | RID 设 `win-x64` 使原生库平铺；宿主装载器已对插件目录调 `AddDllDirectory`，平铺即可解析 |
| XAML 里引用宿主控件报"命名空间不存在" | 跨程序集要加 `assembly=LesTool` / `assembly=LesTool.Platform` 限定 |
| 装载后行为与旧版不同 | `plugin.json` 字段是小驼峰；宿主解析大小写不敏感，但序列化别的 JSON 时记得自己开 |
| 改了代码没生效 | 插件部署靠构建拷贝（SkipUnchangedFiles）；确认构建真的跑了，或删掉宿主输出 plugins\<id> 再构建 |

## 10. 参考实现

| 插件 | 看点 |
|---|---|
| `LesTool.Plugin.Screenshot` | 重型插件：多窗口/原生库/模型资产/多路径 ffmpeg/会话生命周期 |
| `LesTool.Plugin.Volume` | COM 互操作 + 轮询型数据层 + 浮窗窗口 |


## 2026-11-01：Platform 1.3 加固规则（优先于本文早期模板）

- 插件清单必须包含 apiContract=1 与 minPlatform=1.3.0；本轮 8 个插件与宿主同批重编。历史二进制未经过兼容矩阵，不因同属 1.x 就自动认定兼容。
- 同仓工程统一导入 build/Plugin.Build.targets。DeployToHost 由宿主构建完成后显式调用，宿主对插件的 ProjectReference 为 ReferenceOutputAssembly=false 的构建依赖。上文独立 AfterBuild 拷贝模板属于历史写法。
- RegisterCommand/RegisterSettingsPage 在 Initialize 时暂存；启动回调成功、命令无冲突后才统一发布。重复 ID、越界前缀与上下文声明矛盾拒绝整个批次，不覆盖原命令。
- IPluginLifetimeHost 是可选能力；资源创建后立即 Own/OwnCleanup 登记。OnStopping 在业务 Shutdown 前执行；事件须检查 IsActive，异步完成须检查停止令牌/代次。Shutdown 与 Dispose 必须幂等。
- IAsyncCommandHost.RegisterAsyncCommand 使用 CommandDescriptor，处理器返回 Task；CommandConcurrency 可选择 Allow / RejectReentry。同步处理器仍从 UI 线程调用，不在回调中同步等待 UIA。
- IAsyncLesPlugin.ShutdownAsync 返回时必须已经停止在飞业务。调用方取消等待不等于真实工作结束；下载/录制按此接口收尾，之后才释放资源与 Flush 设置。
- IAsyncPluginSettings 提供 SetAsync/FlushAsync 与保存结果；原 Set 保持同步语义，失败抛 SettingsWriteException。保存失败不得清除业务 dirty；损坏文件受保护时只能通过明确恢复动作重置。
- 每插件一个非 collectible PluginLoadContext，按 .deps.json 解析私有托管依赖，同名不同版本可并存。Platform/Serilog/WPF 共享宿主实例；System.* 名称的 NuGet 库仍优先按私有依赖处理。缺失私有库不借用其他插件。原生 DLL 仍在初始化前做同包内容一致性检查；更新与卸载须重启。
- 对外分发使用 scripts/package.ps1。包必须含入口 DLL、.deps.json、运行依赖、清单及资产。--diagnose-plugins 只查装载/类型；发布还执行 --verify-plugin-startup，使用临时数据初始化全部插件、创建深浅主题设置页、检查 Office 预览及原生 OCR，不开启全局输入钩子或下载监听。完整输入/硬件场景另行验收。
- 业务数据使用 (host.Settings as IPluginDataDirectory)?.DataDirectory；已有剪贴板目录布局不变，测试时宿主可注入临时根。
