using System.Windows.Threading;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

public sealed class MockClaudecodeService : IClaudecodeService
{
    private readonly DispatcherTimer _timer;
    private DemoStage _stage = DemoStage.Startup;
    private bool _started;

    public MockClaudecodeService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _timer.Tick += OnTimerTick;
    }

    public event EventHandler<ClaudecodeTask>? TaskUpdated;

    public ClaudecodeTask? CurrentTask { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        Publish(CreateTask(
            ClaudecodeStatus.Working,
            "Working",
            "Claudecode is reviewing the active workspace.",
            Array.Empty<string>()));

        Schedule(DemoStage.AwaitingApproval, seconds: 5);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _timer.Stop();
        _started = false;
        return Task.CompletedTask;
    }

    public Task ApproveAsync(CancellationToken cancellationToken = default)
    {
        switch (_stage)
        {
            case DemoStage.AwaitingApprovalDecision:
                Publish(CreateTask(
                    ClaudecodeStatus.Working,
                    "Applying Approval",
                    "Approval received. Preparing the requested edits.",
                    Array.Empty<string>()));
                Schedule(DemoStage.AwaitingChoice, seconds: 3);
                break;

            case DemoStage.AwaitingChoiceDecision:
                Publish(CreateTask(
                    ClaudecodeStatus.Working,
                    "Publishing",
                    "Choice confirmed. Syncing the desktop response.",
                    Array.Empty<string>()));
                Schedule(DemoStage.CooldownToIdle, seconds: 3);
                break;
        }

        return Task.CompletedTask;
    }

    public Task RejectAsync(CancellationToken cancellationToken = default)
    {
        Publish(CreateTask(
            ClaudecodeStatus.Error,
            "Approval Rejected",
            "The current action was rejected. Waiting before restarting the queue.",
            Array.Empty<string>()));
        Schedule(DemoStage.RecoveringFromError, seconds: 4);
        return Task.CompletedTask;
    }

    public Task SnoozeAsync(CancellationToken cancellationToken = default)
    {
        Publish(CreateTask(
            ClaudecodeStatus.Idle,
            "Paused",
            "This task was snoozed. The mock feed will resume shortly.",
            Array.Empty<string>()));
        Schedule(DemoStage.RestartingWork, seconds: 6);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer.Tick -= OnTimerTick;
        _timer.Stop();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();

        switch (_stage)
        {
            case DemoStage.AwaitingApproval:
                Publish(CreateTask(
                    ClaudecodeStatus.NeedsApproval,
                    "Needs Approval",
                    "Write access is required before applying the next patch.",
                    new[] { "Approve", "Reject", "Later" }));
                _stage = DemoStage.AwaitingApprovalDecision;
                break;

            case DemoStage.AwaitingChoice:
                Publish(CreateTask(
                    ClaudecodeStatus.NeedsChoice,
                    "Needs Choice",
                    "Choose whether to publish the polished desktop overlay now.",
                    new[] { "Publish", "Discard", "Later" }));
                _stage = DemoStage.AwaitingChoiceDecision;
                break;

            case DemoStage.CooldownToIdle:
                Publish(CreateTask(
                    ClaudecodeStatus.Idle,
                    "All Clear",
                    "The overlay is idle and ready for the next Claudecode event.",
                    Array.Empty<string>()));
                Schedule(DemoStage.RestartingWork, seconds: 6);
                break;

            case DemoStage.RecoveringFromError:
                Publish(CreateTask(
                    ClaudecodeStatus.Idle,
                    "Recovered",
                    "The queue is stable again. A new task will arrive soon.",
                    Array.Empty<string>()));
                Schedule(DemoStage.RestartingWork, seconds: 5);
                break;

            case DemoStage.RestartingWork:
                Publish(CreateTask(
                    ClaudecodeStatus.Working,
                    "Working",
                    "Claudecode is scanning for the next user-facing update.",
                    Array.Empty<string>()));
                Schedule(DemoStage.AwaitingApproval, seconds: 5);
                break;
        }
    }

    private void Schedule(DemoStage nextStage, int seconds)
    {
        _stage = nextStage;
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Start();
    }

    private void Publish(ClaudecodeTask task)
    {
        CurrentTask = task;
        TaskUpdated?.Invoke(this, task);
    }

    private static ClaudecodeTask CreateTask(
        ClaudecodeStatus status,
        string title,
        string message,
        IReadOnlyList<string> actions)
    {
        return new ClaudecodeTask(status, title, message, actions, DateTimeOffset.Now);
    }

    private enum DemoStage
    {
        Startup,
        AwaitingApproval,
        AwaitingApprovalDecision,
        AwaitingChoice,
        AwaitingChoiceDecision,
        CooldownToIdle,
        RecoveringFromError,
        RestartingWork
    }
}
