namespace DynamicIsland.Models;

public sealed record CodexTask(
    CodexSessionStatus Status,
    string Title,
    string Message,
    IReadOnlyList<string> AvailableActions,
    DateTimeOffset UpdatedAt,
    string? SessionId = null,
    IReadOnlyList<string>? ChangedFiles = null);
