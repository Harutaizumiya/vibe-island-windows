<div align="center">

<img src="DynamicIsland/Assets/codex-color.svg" alt="Codex Icon" width="84" height="84" />

# vibe-island-windows

一个基于 WPF 的 Windows 动态岛应用，用来实时跟踪 Codex CLI 当前会话，把 Agent 的工作状态、工具调用和任务结果映射成桌面上的~~轻量~~反馈界面。

<p>
  <img src="https://img.shields.io/badge/Windows-Desktop-111111?style=flat-square" alt="Windows Desktop" />
  <img src="https://img.shields.io/badge/.NET-10-111111?style=flat-square" alt=".NET 10" />
  <img src="https://img.shields.io/badge/WPF-UI-111111?style=flat-square" alt="WPF UI" />
  <img src="https://img.shields.io/badge/Codex-CLI%20Tracking-111111?style=flat-square" alt="Codex CLI Tracking" />
</p>

</div>

---

## 简介

**让你的Windows也可以拥有刘海**

让 Codex CLI 不再只存在于终端里，而是被压缩成一个常驻桌面的灵动岛。

当前版本已经覆盖三类核心能力：

- 读取真实 Codex CLI session 文件，并持续跟踪当前活跃会话。
- 用动态岛 UI 展示思考中、调用工具、完成、卡住、中断等状态。
- 提供 mock 数据源、调试模式和独立日志程序，方便定位更新延迟和状态误判。

## 当前特性

| 能力 | 当前实现 |
| --- | --- |
| 数据源 | 支持 `codex` 与 `mock` 两种模式 |
| 真实状态读取 | 读取 `session_index.jsonl` 与 `rollout-*.jsonl` |
| 状态推断 | 支持 `Idle / Processing / RunningTool / Finishing / Completed / Stalled / Interrupted / Unknown` |
| 内部推断态 | 支持 `ThinkingSuspected`、`RunningToolLong` |
| 展开态内容 | 按状态切换为 Agent 输出、工具详情或已更改文件 |
| 交互行为 | 悬浮展开、点击展开、失焦自动折叠 |
| 调试能力 | 调试模式可显示状态和数据来源 |
| 日志能力 | 主程序日志 + 独立 logger 程序 |
| 完成反馈 | 完成时自动展开，随后自动折叠 |
| 视觉细节 | 空闲态 Codex 图标、中文状态标签、完成态 |

## 工作方式

### 1. 数据进入

主程序会从用户本地的 Codex CLI 会话文件读取数据：

- `~/.codex/session_index.jsonl`
- `~/.codex/sessions/**/rollout-*.jsonl`

为了降低延迟和漏读风险，当前实现不是只依赖单一路径，而是组合了三条链路：

- `FileSystemWatcher`
- 活跃 session 轮询
- 周期性补扫最近 session

### 2. 状态机推断

rollout 事件会进入内部状态机，再映射成 UI 公开状态。当前公开状态包括：

- `Idle`
- `Processing`
- `RunningTool`
- `Finishing`
- `Completed`
- `Stalled`
- `Interrupted`
- `Unknown`

内部还维护两个更细的推断态：

- `ThinkingSuspected`
- `RunningToolLong`

它们不会直接暴露成新的 UI 枚举，而是用于排序、超时和提示语义。

### 3. 岛体展示

当前展开区的展示逻辑已经按状态分流：

- `Processing` / `Finishing`：展示 Agent 输出
- `RunningTool`：展示工具调用详情和命令
- `Completed`：展示已更改文件

同时，展开区高度会根据内容动态调整，宿主窗口也会同步放大，避免底部内容被裁切。

## 快速开始

### 环境要求

- Windows
- .NET 10 SDK
- 可选：本地安装并使用 Codex CLI

### 本地运行

```powershell
dotnet build DynamicIsland\DynamicIsland.csproj -c Debug
dotnet run --project DynamicIsland\DynamicIsland.csproj -c Debug
```

### 运行测试

```powershell
dotnet run --project DynamicIsland.Tests\DynamicIsland.Tests.csproj -c Release
```

### 运行独立日志程序

```powershell
dotnet run --project DynamicIsland.Logger\DynamicIsland.Logger.csproj -c Release
```

## 配置方式

运行时配置位于：

- [`DynamicIsland/islandsettings.json`](DynamicIsland/islandsettings.json)

当前支持的主要配置项：

```jsonc
{
  "serviceMode": "codex",
  "debugMode": false,
  "interaction": {
    "expandOnHover": true,
    "hoverAutoCollapseSeconds": 1,
    "manualAutoCollapseSeconds": 3,
    "expandedLayoutRefreshDelayMilliseconds": 40
  }
}
```

说明：

- `serviceMode`
  - `codex`：读取真实 Codex CLI 会话
  - `mock`：使用模拟数据
- `debugMode`
  - 打开后显示更多调试信息和数据来源
- `interaction`
  - 控制悬浮展开、自动折叠和布局刷新节奏

环境变量仍可覆盖部分配置：

- `DYNAMIC_ISLAND_SERVICE_MODE`
- `DYNAMIC_ISLAND_DEBUG_MODE`

## 项目结构

```text
DynamicIsland/         主程序
DynamicIsland.Tests/   测试入口
DynamicIsland.Logger/  独立日志程序
docs/                  文档
scripts/               打包与发布脚本
```

更完整的结构说明见：

- [docs/structure.md](docs/structure.md)

## 文档索引

- [docs/structure.md](docs/structure.md)
- [docs/codex-state-machine.md](docs/codex-state-machine.md)
- [docs/codex-cli-session-jsonl-summary.md](docs/codex-cli-session-jsonl-summary.md)
- [docs/release/26H1.md](docs/release/26H1.md)



## 后续方向

- 优化内存占用
- 优化展开区的长文本、多工具和多文件展示。
- 优化顶部衔接圆角
- 改良为开箱即用

## License

本项目采用 [MIT License](LICENSE)。
