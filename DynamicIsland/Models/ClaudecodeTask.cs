namespace DynamicIsland.Models;

public sealed record ClaudecodeTask(
    ClaudecodeStatus Status,
    string Title,
    string Message,
    IReadOnlyList<string> AvailableActions,
    DateTimeOffset UpdatedAt);
