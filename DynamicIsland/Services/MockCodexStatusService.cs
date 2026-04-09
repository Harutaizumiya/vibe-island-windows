using System.Windows.Threading;
using DynamicIsland.Models;

namespace DynamicIsland.Services;

public sealed class MockCodexStatusService : ICodexStatusService
{
    private static readonly IReadOnlyList<string> InterruptedActions = ["Resume Demo", "Reset Demo"];
    private static readonly IReadOnlyList<string> EmptyActions = Array.Empty<string>();

    private readonly DispatcherTimer _timer;
    private DemoStage _stage = DemoStage.Startup;
    private bool _started;

    public MockCodexStatusService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _timer.Tick += OnTimerTick;
    }

    public event EventHandler<CodexTask>? TaskUpdated;

    public CodexTask? CurrentTask { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        Publish(CreateTask(
            CodexSessionStatus.Processing,
            "Mock Session",
            "Codex CLI is processing a demo turn.",
            EmptyActions));

        Schedule(DemoStage.RunningTool, seconds: 4);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _timer.Stop();
        _started = false;
        return Task.CompletedTask;
    }

    public Task ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default)
    {
        if (string.Equals(actionId, "Resume Demo", StringComparison.OrdinalIgnoreCase))
        {
            Publish(CreateTask(
                CodexSessionStatus.Processing,
                "Mock Session",
                "Resuming the demo turn after an interruption.",
                EmptyActions));
            Schedule(DemoStage.RunningTool, seconds: 3);
        }
        else if (string.Equals(actionId, "Reset Demo", StringComparison.OrdinalIgnoreCase))
        {
            Publish(CreateTask(
                CodexSessionStatus.Idle,
                "Codex CLI",
                "The demo feed was reset. Waiting to start again.",
                EmptyActions));
            Schedule(DemoStage.RestartingWork, seconds: 4);
        }

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
            case DemoStage.RunningTool:
                Publish(CreateTask(
                    CodexSessionStatus.RunningTool,
                    "Mock Session",
                    "Running a demo tool call.",
                    EmptyActions));
                Schedule(DemoStage.Finishing, seconds: 4);
                break;

            case DemoStage.Finishing:
                Publish(CreateTask(
                    CodexSessionStatus.Finishing,
                    "Mock Session",
                    "Preparing a demo final answer.",
                    EmptyActions));
                Schedule(DemoStage.Completed, seconds: 3);
                break;

            case DemoStage.Completed:
                Publish(CreateTask(
                    CodexSessionStatus.Completed,
                    "Mock Session",
                    "The demo turn completed successfully.",
                    EmptyActions));
                Schedule(DemoStage.Interrupted, seconds: 3);
                break;

            case DemoStage.Interrupted:
                Publish(CreateTask(
                    CodexSessionStatus.Interrupted,
                    "Mock Session",
                    "A later demo turn was interrupted.",
                    InterruptedActions));
                _stage = DemoStage.Interrupted;
                break;

            case DemoStage.RestartingWork:
                Publish(CreateTask(
                    CodexSessionStatus.Processing,
                    "Mock Session",
                    "Codex CLI is processing a fresh demo turn.",
                    EmptyActions));
                Schedule(DemoStage.RunningTool, seconds: 4);
                break;
        }
    }

    private void Schedule(DemoStage nextStage, int seconds)
    {
        _stage = nextStage;
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Start();
    }

    private void Publish(CodexTask task)
    {
        CurrentTask = task;
        TaskUpdated?.Invoke(this, task);
    }

    private static CodexTask CreateTask(
        CodexSessionStatus status,
        string title,
        string message,
        IReadOnlyList<string> actions)
    {
        return new CodexTask(status, title, message, actions, DateTimeOffset.Now, SessionId: "mock");
    }

    private enum DemoStage
    {
        Startup,
        RunningTool,
        Finishing,
        Completed,
        Interrupted,
        RestartingWork
    }
}
