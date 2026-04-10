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
            CodexSessionStatus.Idle,
            "Mock Session",
            "Mock feed ready. Waiting to start a demo turn.",
            EmptyActions,
            debugSource: "Source: Mock feed\nStage: idle"));

        Schedule(DemoStage.RestartingWork, seconds: 2);
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
                EmptyActions,
                debugSource: "Source: Mock feed\nStage: resume"));
            Schedule(DemoStage.RunningTool, seconds: 3);
        }
        else if (string.Equals(actionId, "Reset Demo", StringComparison.OrdinalIgnoreCase))
        {
            Publish(CreateTask(
                CodexSessionStatus.Idle,
                "Codex CLI",
                "The demo feed was reset. Waiting to start again.",
                EmptyActions,
                debugSource: "Source: Mock feed\nStage: reset"));
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
                    "Running shell_command.",
                    EmptyActions,
                    debugSource: $"Source: Mock feed\nStage: running_tool\nTool: shell_command\nEventTime: {DateTimeOffset.UtcNow:O}"));
                Schedule(DemoStage.Finishing, seconds: 4);
                break;

            case DemoStage.Finishing:
                Publish(CreateTask(
                    CodexSessionStatus.Finishing,
                    "Mock Session",
                    "Preparing a demo final answer.",
                    EmptyActions,
                    debugSource: "Source: Mock feed\nStage: finishing"));
                Schedule(DemoStage.Completed, seconds: 3);
                break;

            case DemoStage.Completed:
                Publish(CreateTask(
                    CodexSessionStatus.Completed,
                    "Mock Session",
                    "The demo turn completed successfully.",
                    EmptyActions,
                    changedFiles:
                    [
                        @"C:\Users\Haruta\Documents\code\APP\vibe-island-windows\DynamicIsland\MainWindow.xaml",
                        @"C:\Users\Haruta\Documents\code\APP\vibe-island-windows\DynamicIsland\ViewModels\StatusViewModel.cs"
                    ],
                    debugSource: "Source: Mock feed\nStage: completed"));
                Schedule(DemoStage.Interrupted, seconds: 3);
                break;

            case DemoStage.Interrupted:
                Publish(CreateTask(
                    CodexSessionStatus.Interrupted,
                    "Mock Session",
                    "A later demo turn was interrupted.",
                    InterruptedActions,
                    debugSource: "Source: Mock feed\nStage: interrupted"));
                _stage = DemoStage.Interrupted;
                break;

            case DemoStage.RestartingWork:
                Publish(CreateTask(
                    CodexSessionStatus.Processing,
                    "Mock Session",
                    "Codex CLI is processing a fresh demo turn.",
                    EmptyActions,
                    debugSource: "Source: Mock feed\nStage: processing"));
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
        IReadOnlyList<string> actions,
        IReadOnlyList<string>? changedFiles = null,
        string? debugSource = null)
    {
        return new CodexTask(status, title, message, actions, DateTimeOffset.Now, SessionId: "mock", ChangedFiles: changedFiles, DebugSource: debugSource);
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
