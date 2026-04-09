using System.Text.Json;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

internal sealed class CodexCliSessionStateMachine
{
    private static readonly IReadOnlyList<string> EmptyActions = Array.Empty<string>();
    private static readonly TimeSpan CompletedDisplayDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StalledThreshold = TimeSpan.FromSeconds(20);

    private string? _threadName;
    private string? _lastMessage;
    private string? _lastToolName;

    public CodexCliSessionStateMachine(string sessionId)
    {
        SessionId = sessionId;
        Status = CodexSessionStatus.Idle;
        UpdatedAt = DateTimeOffset.MinValue;
    }

    public string SessionId { get; }

    public CodexSessionStatus Status { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? LastEventAt { get; private set; }

    public void SetThreadName(string? threadName)
    {
        if (!string.IsNullOrWhiteSpace(threadName))
        {
            _threadName = threadName;
        }
    }

    public bool TryApplyLine(string line, out string? error)
    {
        error = null;

        try
        {
            using var document = JsonDocument.Parse(line);
            return TryApplyDocument(document.RootElement);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool MarkUnknown(string message, DateTimeOffset timestamp)
    {
        return SetStatus(CodexSessionStatus.Unknown, message, timestamp, toolName: _lastToolName);
    }

    public bool AdvanceClock(DateTimeOffset now)
    {
        if (Status == CodexSessionStatus.Completed && LastEventAt.HasValue && now - LastEventAt.Value >= CompletedDisplayDuration)
        {
            return SetStatus(CodexSessionStatus.Idle, "Waiting for an active Codex CLI session.", now, toolName: null);
        }

        if (Status is CodexSessionStatus.Processing or CodexSessionStatus.RunningTool or CodexSessionStatus.Finishing
            && LastEventAt.HasValue
            && now - LastEventAt.Value >= StalledThreshold)
        {
            var detail = _lastToolName is not null
                ? $"No new events arrived for 20 seconds while running {_lastToolName}."
                : "No new events arrived for 20 seconds.";
            return SetStatus(CodexSessionStatus.Stalled, detail, now, toolName: _lastToolName);
        }

        return false;
    }

    public CodexTask BuildTask()
    {
        var title = !string.IsNullOrWhiteSpace(_threadName)
            ? _threadName!
            : "Codex CLI";

        var message = Status == CodexSessionStatus.Idle
            ? "Waiting for an active Codex CLI session."
            : _lastMessage ?? BuildFallbackMessage(Status, _lastToolName);

        return new CodexTask(Status, title, message, EmptyActions, UpdatedAt, SessionId);
    }

    private bool TryApplyDocument(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var outerTypeElement))
        {
            return false;
        }

        var timestamp = TryReadTimestamp(root);

        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (payload.TryGetProperty("type", out var payloadTypeElement) && payloadTypeElement.ValueKind == JsonValueKind.String)
        {
            var payloadType = payloadTypeElement.GetString();
            switch (outerTypeElement.GetString(), payloadType)
            {
                case ("event_msg", "task_started"):
                    return SetStatus(CodexSessionStatus.Processing, "Starting a new Codex CLI turn.", timestamp, toolName: null);

                case ("response_item", "function_call"):
                case ("response_item", "custom_tool_call"):
                    return SetStatus(
                        CodexSessionStatus.RunningTool,
                        BuildToolRunningMessage(payload),
                        timestamp,
                        ReadToolName(payload));

                case ("response_item", "function_call_output"):
                case ("response_item", "custom_tool_call_output"):
                    return SetStatus(
                        CodexSessionStatus.Processing,
                        BuildPostToolMessage(),
                        timestamp,
                        toolName: null);

                case ("response_item", "message"):
                    return ApplyResponseMessage(payload, timestamp);

                case ("event_msg", "agent_message"):
                    return ApplyAgentMessage(payload, timestamp);

                case ("event_msg", "task_complete"):
                    return SetStatus(
                        CodexSessionStatus.Completed,
                        ReadOptionalString(payload, "last_agent_message") ?? "The current Codex CLI turn completed.",
                        timestamp,
                        toolName: null);

                case ("event_msg", "turn_aborted"):
                    if (string.Equals(ReadOptionalString(payload, "reason"), "interrupted", StringComparison.OrdinalIgnoreCase))
                    {
                        return SetStatus(CodexSessionStatus.Interrupted, "The current Codex CLI turn was interrupted.", timestamp, toolName: null);
                    }

                    return false;

                case ("event_msg", "thread_rolled_back"):
                    return SetStatus(CodexSessionStatus.Interrupted, "Codex CLI rolled the thread back after an interruption.", timestamp, toolName: null);
            }
        }

        return false;
    }

    private bool ApplyResponseMessage(JsonElement payload, DateTimeOffset timestamp)
    {
        var phase = ReadOptionalString(payload, "phase");
        if (string.Equals(phase, "commentary", StringComparison.OrdinalIgnoreCase))
        {
            return SetStatus(
                CodexSessionStatus.Processing,
                ReadResponseText(payload) ?? "Codex CLI is processing the current turn.",
                timestamp,
                toolName: null);
        }

        if (string.Equals(phase, "final_answer", StringComparison.OrdinalIgnoreCase))
        {
            return SetStatus(
                CodexSessionStatus.Finishing,
                ReadResponseText(payload) ?? "Codex CLI is preparing the final answer.",
                timestamp,
                toolName: null);
        }

        return false;
    }

    private bool ApplyAgentMessage(JsonElement payload, DateTimeOffset timestamp)
    {
        var phase = ReadOptionalString(payload, "phase");
        if (string.Equals(phase, "commentary", StringComparison.OrdinalIgnoreCase))
        {
            return SetStatus(
                CodexSessionStatus.Processing,
                ReadOptionalString(payload, "message") ?? "Codex CLI is processing the current turn.",
                timestamp,
                toolName: null);
        }

        if (string.Equals(phase, "final_answer", StringComparison.OrdinalIgnoreCase))
        {
            return SetStatus(
                CodexSessionStatus.Finishing,
                ReadOptionalString(payload, "message") ?? "Codex CLI is preparing the final answer.",
                timestamp,
                toolName: null);
        }

        return false;
    }

    private bool SetStatus(CodexSessionStatus status, string message, DateTimeOffset timestamp, string? toolName)
    {
        var normalizedMessage = NormalizeMessage(message);
        var changed = status != Status || !string.Equals(_lastMessage, normalizedMessage, StringComparison.Ordinal);

        Status = status;
        _lastMessage = normalizedMessage;
        _lastToolName = toolName;
        LastEventAt = timestamp;
        UpdatedAt = timestamp;
        return changed;
    }

    private static string? ReadToolName(JsonElement payload)
    {
        return ReadOptionalString(payload, "name")
            ?? ReadOptionalString(payload, "tool_name")
            ?? ReadOptionalString(payload, "call_name");
    }

    private static string BuildToolRunningMessage(JsonElement payload)
    {
        var toolName = ReadToolName(payload);
        return toolName is not null
            ? $"Running {toolName}."
            : "Running a Codex CLI tool.";
    }

    private string BuildPostToolMessage()
    {
        return _lastToolName is not null
            ? $"Finished {_lastToolName}. Continuing the current turn."
            : "Tool output received. Continuing the current turn.";
    }

    private static string BuildFallbackMessage(CodexSessionStatus status, string? toolName)
    {
        return status switch
        {
            CodexSessionStatus.Processing => "Codex CLI is processing the current turn.",
            CodexSessionStatus.RunningTool => toolName is not null ? $"Running {toolName}." : "Running a Codex CLI tool.",
            CodexSessionStatus.Finishing => "Codex CLI is preparing the final answer.",
            CodexSessionStatus.Completed => "The current Codex CLI turn completed.",
            CodexSessionStatus.Stalled => "No new events arrived for 20 seconds.",
            CodexSessionStatus.Interrupted => "The current Codex CLI turn was interrupted.",
            CodexSessionStatus.Unknown => "Unable to read Codex CLI status.",
            _ => "Waiting for an active Codex CLI session."
        };
    }

    private static string? ReadResponseText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (ReadOptionalString(item, "type") == "output_text")
            {
                var text = ReadOptionalString(item, "text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset TryReadTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var timestampElement)
            && timestampElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(timestampElement.GetString(), out var timestamp))
        {
            return timestamp;
        }

        return DateTimeOffset.UtcNow;
    }

    private static string NormalizeMessage(string message)
    {
        var trimmed = message.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? "Codex CLI updated the current session."
            : trimmed.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
