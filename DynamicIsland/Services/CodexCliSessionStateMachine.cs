using System.Text.Json;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

internal enum CodexCliDerivedStatus
{
    Idle,
    Processing,
    ThinkingSuspected,
    RunningTool,
    RunningToolLong,
    Finishing,
    Completed,
    Stalled,
    Interrupted,
    Unknown
}

internal sealed record CodexCliSessionSnapshot(CodexTask Task, CodexCliDerivedStatus DerivedStatus);

internal sealed class CodexCliSessionStateMachine
{
    private static readonly IReadOnlyList<string> EmptyActions = Array.Empty<string>();
    private static readonly IReadOnlyList<string> EmptyFiles = Array.Empty<string>();
    private static readonly TimeSpan CompletedDisplayDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InterruptedDisplayDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ThinkingSuspectedThreshold = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RunningToolLongThreshold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StalledThreshold = TimeSpan.FromMinutes(3);
    private static readonly StringComparer FileComparer = StringComparer.OrdinalIgnoreCase;

    private readonly List<string> _changedFiles = [];
    private SessionBaseState _baseState = SessionBaseState.Idle;
    private SessionEventKind _lastEventKind = SessionEventKind.None;
    private string? _threadName;
    private string? _lastMessage;
    private string? _lastToolName;
    private string? _lastToolDetail;
    private string? _currentOpenTool;
    private DateTimeOffset? _toolStartTime;
    private DateTimeOffset? _evaluationTime;

    public CodexCliSessionStateMachine(string sessionId)
    {
        SessionId = sessionId;
        UpdatedAt = DateTimeOffset.MinValue;
    }

    public string SessionId { get; }

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
        ClearOpenTool();
        return SetBaseState(SessionBaseState.Unknown, message, timestamp, SessionEventKind.Unknown, toolName: null);
    }

    public bool AdvanceClock(DateTimeOffset now)
    {
        var previous = BuildSnapshot(GetEvaluationTime());
        _evaluationTime = now;
        var current = BuildSnapshot(now);
        return !AreEquivalent(previous.Task, current.Task);
    }

    public CodexTask BuildTask()
    {
        return BuildSnapshot(GetEvaluationTime()).Task;
    }

    internal CodexCliSessionSnapshot BuildSnapshot(DateTimeOffset now)
    {
        var derivedStatus = GetDerivedStatus(now);
        var publicStatus = MapPublicStatus(derivedStatus);
        var title = !string.IsNullOrWhiteSpace(_threadName)
            ? _threadName!
            : "Codex";
        var message = BuildMessage(derivedStatus);

        return new CodexCliSessionSnapshot(
            new CodexTask(publicStatus, title, message, EmptyActions, UpdatedAt, SessionId, GetChangedFilesSnapshot(), BuildDebugSource(derivedStatus)),
            derivedStatus);
    }

    private bool TryApplyDocument(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var outerTypeElement))
        {
            return false;
        }

        var timestamp = TryReadTimestamp(root);
        AlignEvaluationTime(timestamp);

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
                    _changedFiles.Clear();
                    ClearOpenTool();
                    return SetBaseState(SessionBaseState.Processing, "Starting a new Codex CLI turn.", timestamp, SessionEventKind.TaskStarted, toolName: null);

                case ("response_item", "function_call"):
                case ("response_item", "custom_tool_call"):
                    TrackFilesFromToolCall(payload);
                    var toolName = ReadToolName(payload);
                    _lastToolDetail = ExtractToolDetail(payload, toolName);
                    _currentOpenTool = toolName;
                    _toolStartTime = timestamp;
                    return SetBaseState(SessionBaseState.RunningTool, BuildToolRunningMessage(toolName), timestamp, SessionEventKind.ToolStarted, toolName);

                case ("response_item", "function_call_output"):
                case ("response_item", "custom_tool_call_output"):
                    TrackFilesFromToolOutput(payload);
                    var completedToolName = _currentOpenTool ?? _lastToolName;
                    ClearOpenTool();
                    return SetBaseState(SessionBaseState.Processing, BuildPostToolMessage(completedToolName), timestamp, SessionEventKind.ToolOutput, completedToolName);

                case ("response_item", "reasoning"):
                    return ApplyReasoning(payload, timestamp);

                case ("response_item", "message"):
                    return ApplyResponseMessage(payload, timestamp);

                case ("event_msg", "agent_message"):
                    return ApplyAgentMessage(payload, timestamp);

                case ("event_msg", "task_complete"):
                    ClearOpenTool();
                    return SetBaseState(
                        SessionBaseState.Completed,
                        ReadOptionalString(payload, "last_agent_message") ?? "The current Codex CLI turn completed.",
                        timestamp,
                        SessionEventKind.Completed,
                        toolName: null);

                case ("event_msg", "turn_aborted"):
                    if (string.Equals(ReadOptionalString(payload, "reason"), "interrupted", StringComparison.OrdinalIgnoreCase))
                    {
                        ClearOpenTool();
                        return SetBaseState(SessionBaseState.Interrupted, "The current Codex CLI turn was interrupted.", timestamp, SessionEventKind.Interrupted, toolName: null);
                    }

                    return false;

                case ("event_msg", "thread_rolled_back"):
                    ClearOpenTool();
                    return SetBaseState(SessionBaseState.Interrupted, "Codex CLI rolled the thread back after an interruption.", timestamp, SessionEventKind.Rollback, toolName: null);
            }
        }

        return false;
    }

    private bool ApplyResponseMessage(JsonElement payload, DateTimeOffset timestamp)
    {
        var phase = ReadOptionalString(payload, "phase");
        if (string.Equals(phase, "commentary", StringComparison.OrdinalIgnoreCase))
        {
            return SetBaseState(
                SessionBaseState.Processing,
                ReadResponseText(payload) ?? "Codex CLI is processing the current turn.",
                timestamp,
                SessionEventKind.Commentary,
                toolName: null);
        }

        if (string.Equals(phase, "final_answer", StringComparison.OrdinalIgnoreCase))
        {
            ClearOpenTool();
            return SetBaseState(
                SessionBaseState.Finishing,
                ReadResponseText(payload) ?? "Codex CLI is preparing the final answer.",
                timestamp,
                SessionEventKind.FinalAnswer,
                toolName: null);
        }

        return false;
    }

    private bool ApplyReasoning(JsonElement payload, DateTimeOffset timestamp)
    {
        return SetBaseState(
            SessionBaseState.Processing,
            ReadReasoningText(payload) ?? "Codex is reasoning about the current turn.",
            timestamp,
            SessionEventKind.Reasoning,
            toolName: null);
    }

    private bool ApplyAgentMessage(JsonElement payload, DateTimeOffset timestamp)
    {
        var phase = ReadOptionalString(payload, "phase");
        if (string.Equals(phase, "commentary", StringComparison.OrdinalIgnoreCase))
        {
            return SetBaseState(
                SessionBaseState.Processing,
                ReadOptionalString(payload, "message") ?? "Codex CLI is processing the current turn.",
                timestamp,
                SessionEventKind.Commentary,
                toolName: null);
        }

        if (string.Equals(phase, "final_answer", StringComparison.OrdinalIgnoreCase))
        {
            ClearOpenTool();
            return SetBaseState(
                SessionBaseState.Finishing,
                ReadOptionalString(payload, "message") ?? "Codex CLI is preparing the final answer.",
                timestamp,
                SessionEventKind.FinalAnswer,
                toolName: null);
        }

        return false;
    }

    private bool SetBaseState(SessionBaseState baseState, string message, DateTimeOffset timestamp, SessionEventKind eventKind, string? toolName)
    {
        var previous = BuildSnapshot(GetEvaluationTime()).Task;

        _baseState = baseState;
        _lastEventKind = eventKind;
        _lastMessage = NormalizeMessage(message);
        _lastToolName = toolName;
        LastEventAt = timestamp;
        UpdatedAt = timestamp;
        AlignEvaluationTime(timestamp);

        var current = BuildSnapshot(GetEvaluationTime()).Task;
        return !AreEquivalent(previous, current);
    }

    private CodexCliDerivedStatus GetDerivedStatus(DateTimeOffset now)
    {
        if (_baseState == SessionBaseState.Completed && LastEventAt.HasValue && now - LastEventAt.Value >= CompletedDisplayDuration)
        {
            return CodexCliDerivedStatus.Idle;
        }

        if (_baseState == SessionBaseState.Interrupted && LastEventAt.HasValue && now - LastEventAt.Value >= InterruptedDisplayDuration)
        {
            return CodexCliDerivedStatus.Idle;
        }

        if (_baseState is SessionBaseState.Processing or SessionBaseState.RunningTool or SessionBaseState.Finishing
            && LastEventAt.HasValue
            && now - LastEventAt.Value >= StalledThreshold)
        {
            return CodexCliDerivedStatus.Stalled;
        }

        return _baseState switch
        {
            SessionBaseState.Processing when ShouldSuspectThinking(now) => CodexCliDerivedStatus.ThinkingSuspected,
            SessionBaseState.Processing => CodexCliDerivedStatus.Processing,
            SessionBaseState.RunningTool when ShouldMarkRunningToolLong(now) => CodexCliDerivedStatus.RunningToolLong,
            SessionBaseState.RunningTool => CodexCliDerivedStatus.RunningTool,
            SessionBaseState.Finishing => CodexCliDerivedStatus.Finishing,
            SessionBaseState.Completed => CodexCliDerivedStatus.Completed,
            SessionBaseState.Interrupted => CodexCliDerivedStatus.Interrupted,
            SessionBaseState.Unknown => CodexCliDerivedStatus.Unknown,
            _ => CodexCliDerivedStatus.Idle
        };
    }

    private bool ShouldSuspectThinking(DateTimeOffset now)
    {
        return _baseState == SessionBaseState.Processing
            && _currentOpenTool is null
            && LastEventAt.HasValue
            && now - LastEventAt.Value > ThinkingSuspectedThreshold
            && _lastEventKind is SessionEventKind.TaskStarted or SessionEventKind.Commentary or SessionEventKind.Reasoning;
    }

    private bool ShouldMarkRunningToolLong(DateTimeOffset now)
    {
        return _baseState == SessionBaseState.RunningTool
            && _currentOpenTool is not null
            && _toolStartTime.HasValue
            && now - _toolStartTime.Value > RunningToolLongThreshold;
    }

    private string BuildMessage(CodexCliDerivedStatus derivedStatus)
    {
        return derivedStatus switch
        {
            CodexCliDerivedStatus.Idle => "Waiting for an active Codex CLI session.",
            CodexCliDerivedStatus.ThinkingSuspected => "Codex may be reasoning (no new events).",
            CodexCliDerivedStatus.RunningToolLong => BuildRunningToolLongMessage(),
            CodexCliDerivedStatus.Stalled => "No new events; task may be stalled.",
            _ => _lastMessage ?? BuildFallbackMessage(derivedStatus)
        };
    }

    private string BuildRunningToolLongMessage()
    {
        var toolName = _currentOpenTool ?? _lastToolName;
        return toolName is not null
            ? $"Running {toolName} for a while."
            : "Running a tool for a while.";
    }

    private IReadOnlyList<string> GetChangedFilesSnapshot()
    {
        return _changedFiles.Count == 0 ? EmptyFiles : _changedFiles.ToArray();
    }

    private void TrackFilesFromToolCall(JsonElement payload)
    {
        if (!string.Equals(ReadToolName(payload), "apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var input = ReadOptionalString(payload, "input");
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        foreach (var path in ExtractFilesFromApplyPatch(input))
        {
            AddChangedFile(path);
        }
    }

    private void TrackFilesFromToolOutput(JsonElement payload)
    {
        if (!string.Equals(_currentOpenTool ?? _lastToolName, "apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var output = ReadOptionalString(payload, "output");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        foreach (var path in ExtractFilesFromToolOutput(output))
        {
            AddChangedFile(path);
        }
    }

    private void AddChangedFile(string path)
    {
        var normalizedPath = NormalizeFilePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        _changedFiles.RemoveAll(existing => FileComparer.Equals(existing, normalizedPath));
        _changedFiles.Insert(0, normalizedPath);

        const int maxFiles = 8;
        if (_changedFiles.Count > maxFiles)
        {
            _changedFiles.RemoveRange(maxFiles, _changedFiles.Count - maxFiles);
        }
    }

    private void ClearOpenTool()
    {
        _currentOpenTool = null;
        _toolStartTime = null;
    }

    private void AlignEvaluationTime(DateTimeOffset timestamp)
    {
        if (!_evaluationTime.HasValue || timestamp > _evaluationTime.Value)
        {
            _evaluationTime = timestamp;
        }
    }

    private DateTimeOffset GetEvaluationTime()
    {
        return _evaluationTime ?? LastEventAt ?? DateTimeOffset.UtcNow;
    }

    private static IEnumerable<string> ExtractFilesFromApplyPatch(string input)
    {
        var lines = input.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var line in lines)
        {
            if (TryReadFileDirective(line, "*** Update File: ", out var updatedPath)
                || TryReadFileDirective(line, "*** Add File: ", out updatedPath)
                || TryReadFileDirective(line, "*** Delete File: ", out updatedPath)
                || TryReadFileDirective(line, "*** Move to: ", out updatedPath))
            {
                yield return updatedPath!;
            }
        }
    }

    private static IEnumerable<string> ExtractFilesFromToolOutput(string output)
    {
        var text = TryReadNestedToolOutput(output) ?? output;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length <= 2 || !char.IsLetter(line[0]) || !char.IsWhiteSpace(line[1]))
            {
                continue;
            }

            yield return line[2..].Trim();
        }
    }

    private static bool TryReadFileDirective(string line, string prefix, out string? path)
    {
        if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            path = line[prefix.Length..].Trim();
            return !string.IsNullOrWhiteSpace(path);
        }

        path = null;
        return false;
    }

    private static string? TryReadNestedToolOutput(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.TryGetProperty("output", out var outputElement) && outputElement.ValueKind == JsonValueKind.String
                ? outputElement.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeFilePath(string path)
    {
        return path.Trim().Trim('"').Replace('/', System.IO.Path.DirectorySeparatorChar);
    }

    private static string? ReadToolName(JsonElement payload)
    {
        return ReadOptionalString(payload, "name")
            ?? ReadOptionalString(payload, "tool_name")
            ?? ReadOptionalString(payload, "call_name");
    }

    private static string? ExtractToolDetail(JsonElement payload, string? toolName)
    {
        if (string.Equals(toolName, "shell_command", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractShellCommandDetail(payload);
        }

        if (string.Equals(toolName, "apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            var input = ReadOptionalString(payload, "input");
            if (!string.IsNullOrWhiteSpace(input))
            {
                var paths = ExtractFilesFromApplyPatch(input)
                    .Take(3)
                    .Select(path => System.IO.Path.GetFileName(NormalizeFilePath(path)))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToArray();
                if (paths.Length > 0)
                {
                    return $"apply_patch -> {string.Join(", ", paths)}";
                }

                return TruncateSingleLine(input, 220);
            }
        }

        var arguments = ReadOptionalString(payload, "arguments");
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            return TruncateSingleLine(arguments, 220);
        }

        var rawInput = ReadOptionalString(payload, "input");
        if (!string.IsNullOrWhiteSpace(rawInput))
        {
            return TruncateSingleLine(rawInput, 220);
        }

        return null;
    }

    private static string? ExtractShellCommandDetail(JsonElement payload)
    {
        if (payload.TryGetProperty("arguments", out var argumentsElement))
        {
            if (argumentsElement.ValueKind == JsonValueKind.String)
            {
                var rawArguments = argumentsElement.GetString();
                if (!string.IsNullOrWhiteSpace(rawArguments))
                {
                    return TryExtractShellCommandFromJson(rawArguments) ?? TruncateSingleLine(rawArguments, 320);
                }
            }

            if (argumentsElement.ValueKind == JsonValueKind.Object)
            {
                return ExtractShellCommandFromObject(argumentsElement);
            }
        }

        return ReadOptionalString(payload, "input");
    }

    private static string? TryExtractShellCommandFromJson(string rawArguments)
    {
        try
        {
            using var document = JsonDocument.Parse(rawArguments);
            return ExtractShellCommandFromObject(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractShellCommandFromObject(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (arguments.TryGetProperty("command", out var commandElement))
        {
            var command = ReadCommandValue(commandElement);
            if (!string.IsNullOrWhiteSpace(command))
            {
                return command;
            }
        }

        if (arguments.TryGetProperty("cmd", out var cmdElement))
        {
            var command = ReadCommandValue(cmdElement);
            if (!string.IsNullOrWhiteSpace(command))
            {
                return command;
            }
        }

        return TruncateSingleLine(arguments.ToString(), 220);
    }

    private static string? ReadCommandValue(JsonElement commandElement)
    {
        if (commandElement.ValueKind == JsonValueKind.String)
        {
            return TruncateSingleLine(commandElement.GetString(), 320);
        }

        if (commandElement.ValueKind == JsonValueKind.Array)
        {
            var parts = commandElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToArray();
            if (parts.Length > 0)
            {
                return TruncateSingleLine(string.Join(" ", parts), 320);
            }
        }

        return null;
    }

    private static string TruncateSingleLine(string? value, int maxLength)
    {
        var normalized = NormalizeMessage(value ?? string.Empty).Replace('\n', ' ');
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..(maxLength - 1)] + "…";
    }

    private static string BuildToolRunningMessage(string? toolName)
    {
        return toolName is not null
            ? $"Running {toolName}."
            : "Running a Codex CLI tool.";
    }

    private static string BuildPostToolMessage(string? toolName)
    {
        return toolName is not null
            ? $"Finished {toolName}. Continuing the current turn."
            : "Tool output received. Continuing the current turn.";
    }

    private static CodexSessionStatus MapPublicStatus(CodexCliDerivedStatus derivedStatus)
    {
        return derivedStatus switch
        {
            CodexCliDerivedStatus.ThinkingSuspected => CodexSessionStatus.Processing,
            CodexCliDerivedStatus.RunningToolLong => CodexSessionStatus.RunningTool,
            CodexCliDerivedStatus.Processing => CodexSessionStatus.Processing,
            CodexCliDerivedStatus.RunningTool => CodexSessionStatus.RunningTool,
            CodexCliDerivedStatus.Finishing => CodexSessionStatus.Finishing,
            CodexCliDerivedStatus.Completed => CodexSessionStatus.Completed,
            CodexCliDerivedStatus.Stalled => CodexSessionStatus.Stalled,
            CodexCliDerivedStatus.Interrupted => CodexSessionStatus.Interrupted,
            CodexCliDerivedStatus.Unknown => CodexSessionStatus.Unknown,
            _ => CodexSessionStatus.Idle
        };
    }

    private static string BuildFallbackMessage(CodexCliDerivedStatus derivedStatus)
    {
        return derivedStatus switch
        {
            CodexCliDerivedStatus.Processing => "Codex CLI is processing the current turn.",
            CodexCliDerivedStatus.ThinkingSuspected => "Codex may be reasoning (no new events).",
            CodexCliDerivedStatus.RunningTool => "Running a Codex CLI tool.",
            CodexCliDerivedStatus.RunningToolLong => "Running a tool for a while.",
            CodexCliDerivedStatus.Finishing => "Codex CLI is preparing the final answer.",
            CodexCliDerivedStatus.Completed => "The current Codex CLI turn completed.",
            CodexCliDerivedStatus.Stalled => "No new events; task may be stalled.",
            CodexCliDerivedStatus.Interrupted => "The current Codex CLI turn was interrupted.",
            CodexCliDerivedStatus.Unknown => "Unable to read Codex CLI status.",
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

    private static string? ReadReasoningText(JsonElement payload)
    {
        if (!payload.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in summary.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            if (item.ValueKind == JsonValueKind.Object)
            {
                var text = ReadOptionalString(item, "text")
                    ?? ReadOptionalString(item, "summary")
                    ?? ReadOptionalString(item, "content");
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

    private string BuildDebugSource(CodexCliDerivedStatus derivedStatus)
    {
        var lines = new List<string>
        {
            $"Derived: {derivedStatus}",
            $"Public: {MapPublicStatus(derivedStatus)}",
            $"Event: {FormatEventKind(_lastEventKind)}"
        };

        if (!string.IsNullOrWhiteSpace(_currentOpenTool ?? _lastToolName))
        {
            lines.Add($"Tool: {_currentOpenTool ?? _lastToolName}");
        }

        if (!string.IsNullOrWhiteSpace(_lastToolDetail))
        {
            lines.Add($"Command: {_lastToolDetail}");
        }

        if (LastEventAt.HasValue)
        {
            lines.Add($"EventTime: {LastEventAt.Value:O}");
        }

        lines.Add($"Session: {SessionId}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatEventKind(SessionEventKind eventKind)
    {
        return eventKind switch
        {
            SessionEventKind.TaskStarted => "task_started",
            SessionEventKind.Commentary => "commentary",
            SessionEventKind.Reasoning => "reasoning",
            SessionEventKind.ToolStarted => "tool_started",
            SessionEventKind.ToolOutput => "tool_output",
            SessionEventKind.FinalAnswer => "final_answer",
            SessionEventKind.Completed => "task_complete",
            SessionEventKind.Interrupted => "turn_aborted",
            SessionEventKind.Rollback => "thread_rolled_back",
            SessionEventKind.Unknown => "unknown",
            _ => "none"
        };
    }

    private static bool AreEquivalent(CodexTask left, CodexTask right)
    {
        return left.Status == right.Status
            && left.Title == right.Title
            && left.Message == right.Message
            && left.SessionId == right.SessionId
            && left.AvailableActions.SequenceEqual(right.AvailableActions)
            && (left.ChangedFiles ?? Array.Empty<string>()).SequenceEqual(right.ChangedFiles ?? Array.Empty<string>())
            && left.DebugSource == right.DebugSource;
    }

    private enum SessionBaseState
    {
        Idle,
        Processing,
        RunningTool,
        Finishing,
        Completed,
        Interrupted,
        Unknown
    }

    private enum SessionEventKind
    {
        None,
        TaskStarted,
        Commentary,
        Reasoning,
        ToolStarted,
        ToolOutput,
        FinalAnswer,
        Completed,
        Interrupted,
        Rollback,
        Unknown
    }
}
