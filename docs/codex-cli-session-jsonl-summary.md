# Codex CLI Session JSONL 状态读取方案总结

## 方案概述

目标是仿照当前项目对 Claude Code 的状态观察方式，不直接接入 Codex 桌面应用，而是读取 Codex CLI 在本地写出的 session JSONL 文件，增量推断当前 agent 的工作状态。

当前本机可观察到的关键文件：

- `C:\Users\Haruta\.codex\session_index.jsonl`
- `C:\Users\Haruta\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl`
- `C:\Users\Haruta\.codex\.codex-global-state.json`

其中真正适合做状态源的是 `rollout-*.jsonl`。`session_index.jsonl` 更适合做会话发现和排序，`.codex-global-state.json` 更偏全局 UI/环境信息，不适合实时状态判断。

## 已确认可用的 JSONL 信号

在实际 session 文件中已经看到了这些事件类型：

- `event_msg.task_started`
- `turn_context`
- `event_msg.user_message`
- `event_msg.agent_message`
- `response_item.message`
- `response_item.function_call`
- `response_item.function_call_output`
- `response_item.custom_tool_call`
- `response_item.custom_tool_call_output`
- `event_msg.task_complete`
- `event_msg.token_count`

这些信号足够支持一个基础状态机。

## 可行的实现方式

1. 启动时读取 `session_index.jsonl`，建立 session 列表。
2. 根据 session id 找到对应的 `rollout-*.jsonl` 文件。
3. 对每个活跃 session 保存一个 `lastOffset`。
4. 使用 `FileSystemWatcher` 监听 `~/.codex/sessions`。
5. 文件增长时只读取新增内容，并逐行解析 JSON。
6. 将事件映射为应用内部状态，再推送给 UI。

适合的内部状态枚举示例：

- `Idle`
- `Processing`
- `RunningTool`
- `Finishing`
- `Completed`
- `Stalled`
- `Unknown`

## 建议的状态映射

- 看到 `event_msg.task_started` -> `Processing`
- 看到 `response_item.function_call` 或 `response_item.custom_tool_call` -> `RunningTool`
- 看到 `response_item.function_call_output` 或 `response_item.custom_tool_call_output` -> 回到 `Processing`
- 看到 `event_msg.agent_message` 且 `phase == "commentary"` -> `Processing`
- 看到 `event_msg.agent_message` 且 `phase == "final_answer"` -> `Finishing`
- 看到 `event_msg.task_complete` -> `Completed` 或短暂 `IdleConfirmed`

## 空闲判断

不能把“JSONL 一段时间没有更新”直接当成空闲。

更稳的规则是：

- 只有看到 `event_msg.task_complete` 才进入确认空闲/完成态。
- 如果长时间没有新事件，但之前处于 `Processing` 或 `RunningTool`，应该进入 `Stalled`，而不是 `Idle`。
- 如果 watcher 失效、文件不可读、session 无法定位，应进入 `Unknown`。

## 方案优点

- 不依赖 Codex 桌面应用内部接口。
- 不需要修改 Codex CLI 本身。
- 可以直接利用本地已存在的数据。
- 能拿到比单纯进程监控更细的事件粒度。

## 方案缺点

- 本质上依赖非公开的本地文件格式，未来可能变化。
- 事件语义需要靠推断，不是正式状态 API。
- “无更新”无法区分真实空闲、网络问题、CLI 卡住、watcher 漏事件。
- 工具执行和 turn 完成之间可能存在短暂歧义，需要自己做状态机补偿。
- 需要处理增量读取、文件轮转、编码、容错和多 session 管理，工程成本不低。

## 结论

这个方案技术上可行，适合做实验性状态观察器，但不适合作为高可靠状态源。

如果目标只是做一个可视化 demo，它可以工作。
如果目标是做稳定产品，性价比偏低，因为：

- 信号不是官方接口
- 状态判断存在推断误差
- 为了弥补误判，还需要额外加入进程存活、超时、容错等辅助逻辑

当前判断：可以作为探索方案保留，但不建议作为主方案继续深挖。
