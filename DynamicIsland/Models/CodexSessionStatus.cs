namespace DynamicIsland.Models;

public enum CodexSessionStatus
{
    Idle,
    Processing,
    RunningTool,
    Finishing,
    Completed,
    Stalled,
    Interrupted,
    Unknown
}
