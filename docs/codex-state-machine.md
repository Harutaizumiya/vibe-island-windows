# Codex CLI 状态机说明

本文档对应当前实现：

- [CodexCliSessionStateMachine.cs](/C:/Users/Haruta/Documents/code/APP/vibe-island-windows/DynamicIsland/Services/CodexCliSessionStateMachine.cs)
- [CodexCliStatusService.cs](/C:/Users/Haruta/Documents/code/APP/vibe-island-windows/DynamicIsland/Services/CodexCliStatusService.cs)

## 1. 数据来源

状态服务当前从下面两类文件读取状态：

- `~/.codex/session_index.jsonl`
  - 用于建立 `session id -> thread_name` 映射
- `~/.codex/sessions/**/rollout-*.jsonl`
  - 用于读取每个 session 的事件流

服务启动后会：

1. 读取 `session_index.jsonl`
2. 初始化 `rollout-*.jsonl` 的 watcher
3. bootstrap 最近活跃的 session 文件
4. 启动 1 秒时钟和 350ms 活跃轮询

## 2. Session 级状态机

每个 session 对应一个 `CodexCliSessionStateMachine`，初始状态为 `Idle`。

### 2.1 事件到状态的映射

| rollout 事件 | 条件 | 状态 | 说明 |
| --- | --- | --- | --- |
| `event_msg.task_started` | 无 | `Processing` | 开始一轮新任务，同时清空本轮已改文件列表 |
| `response_item.function_call` | 无 | `RunningTool` | 工具调用开始 |
| `response_item.custom_tool_call` | 无 | `RunningTool` | 自定义工具调用开始 |
| `response_item.function_call_output` | 无 | `Processing` | 工具执行结束，回到处理中 |
| `response_item.custom_tool_call_output` | 无 | `Processing` | 自定义工具结束，回到处理中 |
| `response_item.message` | `phase=commentary` | `Processing` | assistant commentary 输出 |
| `response_item.message` | `phase=final_answer` | `Finishing` | assistant final 输出阶段 |
| `event_msg.agent_message` | `phase=commentary` | `Processing` | agent commentary 输出 |
| `event_msg.agent_message` | `phase=final_answer` | `Finishing` | agent final 输出阶段 |
| `event_msg.task_complete` | 无 | `Completed` | 当前 turn 完成 |
| `event_msg.turn_aborted` | `reason=interrupted` | `Interrupted` | 用户或外部中断 |
| `event_msg.thread_rolled_back` | 无 | `Interrupted` | 线程回滚 |

无法解析的 JSON 行不会改变状态；文件读取失败时会把该 session 标记为 `Unknown`。

### 2.2 时间驱动的自动迁移

除了直接由 rollout 事件驱动外，还有 3 个时间规则：

| 当前状态 | 条件 | 下一个状态 |
| --- | --- | --- |
| `Completed` | 3 秒没有新事件 | `Idle` |
| `Interrupted` | 5 秒没有新事件 | `Idle` |
| `Processing` / `RunningTool` / `Finishing` | 20 秒没有新事件 | `Stalled` |

说明：

- `Interrupted` 被设计成短暂提示，不应长期占据岛体
- `Stalled` 只表示活跃状态长时间没有新事件，不代表任务真的结束

## 3. Message 与 Title 的生成

每个 session 输出成 `CodexTask` 时：

- `Title`
  - 优先用 `session_index.jsonl` 中的 `thread_name`
  - 否则回退为 `Codex CLI`
- `Message`
  - `Idle` 固定为 `Waiting for an active Codex CLI session.`
  - 其他状态优先使用最近一次事件消息
  - 没有可用事件消息时使用状态默认文案

默认文案如下：

| 状态 | 默认文案 |
| --- | --- |
| `Processing` | `Codex CLI is processing the current turn.` |
| `RunningTool` | `Running {toolName}.` 或 `Running a Codex CLI tool.` |
| `Finishing` | `Codex CLI is preparing the final answer.` |
| `Completed` | `The current Codex CLI turn completed.` |
| `Stalled` | `No new events arrived for 20 seconds.` |
| `Interrupted` | `The current Codex CLI turn was interrupted.` |
| `Unknown` | `Unable to read Codex CLI status.` |
| `Idle` | `Waiting for an active Codex CLI session.` |

## 4. 已改文件提取逻辑

当前 session 还会维护一份 `ChangedFiles`，用于展开岛展示。

### 4.1 何时清空

- 收到 `task_started` 时清空，表示开始一轮新的 turn

### 4.2 何时记录

- `function_call` / `custom_tool_call`
  - 当工具名是 `apply_patch` 时，从补丁输入中提取文件
- `function_call_output` / `custom_tool_call_output`
  - 仅当上一条工具名是 `apply_patch` 时，从工具输出中补充文件

### 4.3 提取规则

从 `apply_patch` 输入里识别这些指令：

- `*** Update File: ...`
- `*** Add File: ...`
- `*** Delete File: ...`
- `*** Move to: ...`

额外规则：

- 路径按不区分大小写去重
- 新文件排在最前
- 最多保留 8 个文件

## 5. 服务级最终选态逻辑

`CodexCliStatusService` 不直接展示“最后一个读到的 session”，而是先对所有 tracker 做一轮聚合。

### 5.1 候选集过滤

只有满足下面条件的 session 才会进入候选集：

- `task.Status != Idle`
- `now - task.UpdatedAt <= 30 分钟`

如果 watcher 故障，则直接输出：

- 状态：`Unknown`
- 文案：`The Codex CLI file watcher failed. Restart the island to recover live updates.`

### 5.2 最终选择规则

选择顺序如下：

1. 如果存在活跃状态 `Processing / RunningTool / Finishing`
   - 选 `UpdatedAt` 最新的那个
2. 否则
   - 先按 `UpdatedAt` 倒序
   - 再按优先级排序

非活跃状态的优先级数值越小越优先：

| 状态 | 优先级 |
| --- | --- |
| `Unknown` | 0 |
| `Interrupted` | 1 |
| `Stalled` | 2 |
| `RunningTool` | 3 |
| `Processing` | 4 |
| `Finishing` | 5 |
| `Completed` | 6 |
| `Idle` | 7 |

注意：

- 只要存在活跃 session，旧的 `Interrupted` 或 `Stalled` 都不会抢占显示
- 当没有活跃 session 时，较新的 `Interrupted` 会短暂显示 5 秒，然后自动退回 `Idle`

## 6. 更新触发机制

状态更新有 3 个来源：

- `FileSystemWatcher`
  - 监听 `rollout-*.jsonl` 和 `session_index.jsonl`
- `Clock Tick`
  - 每 1 秒推进时间驱动状态，例如 `Completed -> Idle`
- `Active Poll`
  - 每 350ms 主动轮询最近 2 个活跃 session
  - 用来兜底 watcher 延迟

## 7. 当前 UI 输出含义

当前展开岛主要依赖 `CodexTask.ChangedFiles`：

- 有已改文件时，展示最新提取到的文件列表
- 没有已改文件时，展开区只剩说明性提示

折叠岛仍然由这些字段驱动：

- `Status`
- `Title`
- `Message`
- `UpdatedAt`

## 8. 当前实现的边界

当前状态机能识别的是“Codex CLI 已经写进 rollout 的外显事件”，不能识别模型内部但尚未写盘的思考阶段。

已改文件目前主要依赖 `apply_patch`：

- 如果修改是通过其他工具完成，且 rollout 中没有明确文件路径，展开岛可能拿不到文件列表
- 目前不会从普通 `shell_command` 输出里做宽松文件猜测，以避免误判
