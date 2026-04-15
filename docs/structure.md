# 项目结构说明

## 项目概览

`vibe-island-windows` 是一个基于 WPF 的 Windows 桌面应用，用来把 Codex CLI 当前会话状态映射成一个常驻桌面的动态岛界面。项目目前包含主程序、测试程序、独立日志程序、文档与发布脚本几部分。

当前版本的核心目标有三件事：

- 读取 Codex CLI 的会话数据并实时推断任务状态。
- 用可展开的岛体界面展示当前状态、工具调用、Agent 输出与已更改文件。
- 提供 mock 数据源、调试模式与独立日志程序，方便本地调试与问题定位。

## 顶层目录

### `DynamicIsland/`

主程序目录，包含 WPF 界面、状态机、服务层与运行时配置。

- `App.xaml` / `App.xaml.cs`
  应用入口，负责启动窗口、选择数据源、初始化托盘与全局日志。
- `MainWindow.xaml` / `MainWindow.xaml.cs`
  动态岛主界面与窗口行为，包括收缩态、展开态、悬浮展开、自动折叠、动画和宿主窗口高度调整。
- `Models/`
  状态模型定义。
  - `CodexSessionStatus.cs`：公开状态枚举。
  - `CodexTask.cs`：UI 消费的任务快照，包含标题、消息、工具详情、已更改文件、调试来源等。
- `Services/`
  数据读取与状态推断核心。
  - `ICodexStatusService.cs`：状态服务接口。
  - `CodexCliStatusService.cs`：真实数据源，读取 `~/.codex/session_index.jsonl` 和 `~/.codex/sessions/**/rollout-*.jsonl`。
  - `CodexCliSessionStateMachine.cs`：会话状态机，负责把 rollout 事件映射成状态。
  - `MockCodexStatusService.cs`：模拟数据源，用于离线调试。
  - `TrayIconService.cs`：托盘图标与菜单。
- `ViewModels/`
  界面状态与交互逻辑。
  - `StatusViewModel.cs`：岛体文案、图标、展开内容、中文状态、自动折叠与调试模式逻辑。
  - `ObservableObject.cs` / `RelayCommand.cs` / `AsyncRelayCommand.cs`：基础 MVVM 支撑。
- `UI/`
  布局参数与岛体尺寸定义。
  - `IslandLayoutSettings.cs`
  - `IslandLayout.cs`
- `Utils/`
  通用工具类。
  - `AppRuntimeOptions.cs`：读取 `islandsettings.json` 与环境变量。
  - `DiagnosticsLogger.cs`：主程序日志写入。
  - `AnimationHelper.cs` / `CornerRadiusAnimation.cs`：动画辅助。
  - `WindowPositionHelper.cs`：窗口定位。
- `Styles/Themes.xaml`
  全局样式与主题资源。
- `Assets/codex-color.svg`
  空闲态使用的 Codex 图标。
- `islandsettings.json`
  运行时配置文件，当前支持数据源、调试模式与交互行为配置。

### `DynamicIsland.Tests/`

轻量测试程序。目前不是完整单元测试框架，而是一个控制台测试入口，用于覆盖状态机、服务聚合逻辑和 ViewModel 行为。

- `Program.cs`
  包含当前主要回归测试：
  - 状态机事件映射
  - `ThinkingSuspected` / `RunningToolLong` 推断逻辑
  - `Completed` / `Interrupted` 的冷却时间
  - 多 Session 聚合优先级
  - 调试模式文案与展开内容规则
  - 悬浮展开、点击展开与自动折叠行为

### `DynamicIsland.Logger/`

独立日志程序，用来把“会话读取详情”和“岛体最终状态”拆开记录，便于排查实时更新延迟、数据源问题和状态聚合问题。

- `Program.cs`
  运行后会分别输出 session 细节日志和岛体状态日志。

### `docs/`

项目文档目录。

- `codex-cli-session-jsonl-summary.md`
  Codex CLI session 文件格式摘要。
- `codex-state-machine.md`
  当前状态机规则说明。
- `structure.md`
  当前这份项目结构说明。
- `release/26H1.md`
  当前版本的阶段性发布说明。

### `scripts/`

发布与打包脚本目录。

- `package-release.ps1`
  用于生成发布产物和压缩包。

### 根目录其他文件

- `DynamicIsland.slnx`
  解决方案入口。
- `Directory.Build.props`
  统一构建配置。
- `NuGet.Config`
  NuGet 源配置。
- `.gitignore`
  Git 忽略规则。
- `CONTEXT.md`
  项目上下文说明。

## 当前主要能力

### 1. Codex 会话读取

主程序会从 Codex CLI 会话文件读取状态，并在真实数据源模式下持续跟踪：

- `~/.codex/session_index.jsonl`
- `~/.codex/sessions/**/rollout-*.jsonl`

当前支持 watcher、活跃轮询和周期性重扫三条链路，减少 Windows 文件通知不稳定带来的漏读问题。

### 2. 状态机与推断

目前对外公开的状态包括：

- `Idle`
- `Processing`
- `RunningTool`
- `Finishing`
- `Completed`
- `Stalled`
- `Interrupted`
- `Unknown`

内部还存在两个推断态：

- `ThinkingSuspected`
- `RunningToolLong`

这两个内部态不会直接扩展 UI 枚举，而是折叠回公开状态，用于更准确地处理超时、排序和提示文案。

### 3. 岛体界面

当前界面行为包括：

- 收缩态展示标题、左侧状态图标和右侧中文状态。
- 悬浮自动展开。
- 点击展开后维持更长的自动折叠时间。
- 展开态按当前状态切换内容：
  - `Processing` / `Finishing`：Agent 输出
  - `RunningTool`：工具调用详情和命令
  - `Completed`：已更改文件
- 展开高度会按内容动态调整，并同步更新宿主窗口高度，避免底部内容被裁切。

### 4. 调试与日志

当前已经具备两层调试能力：

- 主程序 `startup.log`
  记录应用启动、数据源选择、状态发布、窗口事件等。
- 独立 `DynamicIsland.Logger`
  将 session 细节和岛体最终状态拆分记录，适合定位“源文件在动但岛体不更新”这类问题。

### 5. 配置与 mock 模式

当前支持通过 `DynamicIsland/islandsettings.json` 控制：

- 数据源模式：`codex` / `mock`
- 调试模式开关
- 悬浮展开开关
- 悬浮自动折叠秒数
- 手动展开自动折叠秒数
- 展开区高度重算防抖时间

## 当前版本的实现边界

- 当前数据源仍然只覆盖 Codex CLI 会话文件，没有接入更上层的 API 或 IDE 扩展消息流。
- `ChangedFiles` 主要来自 `apply_patch` 事件，不覆盖所有 shell 级文件修改。
- 发布脚本已存在，但自包含发布依赖本机的 .NET runtime 包解析环境。
- 测试程序以控制台断言为主，还没有切换到正式测试框架。

## 建议阅读顺序

如果要快速理解项目，建议按这个顺序阅读：

1. `DynamicIsland/App.xaml.cs`
2. `DynamicIsland/Services/CodexCliStatusService.cs`
3. `DynamicIsland/Services/CodexCliSessionStateMachine.cs`
4. `DynamicIsland/ViewModels/StatusViewModel.cs`
5. `DynamicIsland/MainWindow.xaml`
6. `DynamicIsland/MainWindow.xaml.cs`
7. `docs/codex-state-machine.md`
