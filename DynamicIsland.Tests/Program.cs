using System.Text.Json;
using DynamicIsland.Models;
using DynamicIsland.Services;
using DynamicIsland.ViewModels;

var failures = new List<string>();

Run(nameof(TaskLifecycleTransitions), TaskLifecycleTransitions);
Run(nameof(ThinkingSuspectedAfterProcessingDelay), ThinkingSuspectedAfterProcessingDelay);
Run(nameof(RunningToolLongAfterToolDelay), RunningToolLongAfterToolDelay);
Run(nameof(ThinkingSuspectedStallsAfterLongSilence), ThinkingSuspectedStallsAfterLongSilence);
Run(nameof(RunningToolLongStallsAfterLongSilence), RunningToolLongStallsAfterLongSilence);
Run(nameof(InterruptedSignalsWin), InterruptedSignalsWin);
Run(nameof(InterruptedCoolsDownToIdle), InterruptedCoolsDownToIdle);
Run(nameof(ApplyPatchTracksChangedFiles), ApplyPatchTracksChangedFiles);
Run(nameof(MalformedJsonIsIgnored), MalformedJsonIsIgnored);
Run(nameof(RunningToolLongBeatsNewerProcessing), RunningToolLongBeatsNewerProcessing);
Run(nameof(RunningToolBeatsThinkingSuspected), RunningToolBeatsThinkingSuspected);
Run(nameof(ActiveSessionWinsOverNewerStalledSession), ActiveSessionWinsOverNewerStalledSession);
Run(nameof(ServiceStartsBeforeBootAnimationCompletes), ServiceStartsBeforeBootAnimationCompletes);

if (failures.Count > 0)
{
    Console.Error.WriteLine("Test failures:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }

    return 1;
}

Console.WriteLine("All DynamicIsland state-machine tests passed.");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
    }
}

void TaskLifecycleTransitions()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f41336");
    machine.SetThreadName("Lifecycle Session");

    var startedAt = DateTimeOffset.Parse("2026-04-09T02:00:00Z");
    Apply(machine, EventMessage("task_started", startedAt));
    Expect(machine.BuildTask().Status, CodexSessionStatus.Processing, "task_started should enter Processing");

    Apply(machine, FunctionCall("shell_command", startedAt.AddSeconds(2)));
    Expect(machine.BuildTask().Status, CodexSessionStatus.RunningTool, "function_call should enter RunningTool");

    Apply(machine, FunctionCallOutput(startedAt.AddSeconds(4)));
    Expect(machine.BuildTask().Status, CodexSessionStatus.Processing, "function_call_output should return to Processing");

    Apply(machine, ResponseMessage("final_answer", "Final answer is being prepared.", startedAt.AddSeconds(6)));
    Expect(machine.BuildTask().Status, CodexSessionStatus.Finishing, "final_answer should enter Finishing");

    Apply(machine, TaskComplete(startedAt.AddSeconds(7)));
    Expect(machine.BuildTask().Status, CodexSessionStatus.Completed, "task_complete should enter Completed");

    machine.AdvanceClock(startedAt.AddSeconds(11));
    Expect(machine.BuildTask().Status, CodexSessionStatus.Idle, "completed state should cool down to Idle after 3 seconds");
}

void ThinkingSuspectedAfterProcessingDelay()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f41337");
    var startedAt = DateTimeOffset.Parse("2026-04-09T03:00:00Z");

    Apply(machine, EventMessage("task_started", startedAt));

    var immediate = machine.BuildSnapshot(startedAt.AddSeconds(7));
    Expect(immediate.Task.Status, CodexSessionStatus.Processing, "task_started should remain public Processing before 8 seconds");
    Expect(immediate.DerivedStatus, CodexCliDerivedStatus.Processing, "task_started should remain internal Processing before 8 seconds");

    var suspected = machine.BuildSnapshot(startedAt.AddSeconds(9));
    Expect(suspected.Task.Status, CodexSessionStatus.Processing, "thinking suspected should still map to public Processing");
    Expect(suspected.DerivedStatus, CodexCliDerivedStatus.ThinkingSuspected, "processing with no tool should become ThinkingSuspected after 8 seconds");
}

void RunningToolLongAfterToolDelay()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f41338");
    var startedAt = DateTimeOffset.Parse("2026-04-09T04:00:00Z");

    Apply(machine, EventMessage("task_started", startedAt));
    Apply(machine, FunctionCall("shell_command", startedAt.AddSeconds(1)));

    var shortRun = machine.BuildSnapshot(startedAt.AddSeconds(10));
    Expect(shortRun.Task.Status, CodexSessionStatus.RunningTool, "tool should remain public RunningTool before 10 seconds");
    Expect(shortRun.DerivedStatus, CodexCliDerivedStatus.RunningTool, "tool should remain internal RunningTool before 10 seconds");

    var longRun = machine.BuildSnapshot(startedAt.AddSeconds(12));
    Expect(longRun.Task.Status, CodexSessionStatus.RunningTool, "running tool long should still map to public RunningTool");
    Expect(longRun.DerivedStatus, CodexCliDerivedStatus.RunningToolLong, "tool should become RunningToolLong after 10 seconds");
}

void ThinkingSuspectedStallsAfterLongSilence()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f41339");
    var startedAt = DateTimeOffset.Parse("2026-04-09T04:10:00Z");

    Apply(machine, EventMessage("task_started", startedAt));
    var stalled = machine.BuildSnapshot(startedAt.AddSeconds(61));
    Expect(stalled.Task.Status, CodexSessionStatus.Stalled, "thinking suspected should eventually stall after 1 minute");
    Expect(stalled.DerivedStatus, CodexCliDerivedStatus.Stalled, "thinking suspected should transition to stalled internally");
}

void RunningToolLongStallsAfterLongSilence()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f4133a");
    var startedAt = DateTimeOffset.Parse("2026-04-09T04:20:00Z");

    Apply(machine, EventMessage("task_started", startedAt));
    Apply(machine, FunctionCall("shell_command", startedAt.AddSeconds(1)));
    var stalled = machine.BuildSnapshot(startedAt.AddSeconds(62));
    Expect(stalled.Task.Status, CodexSessionStatus.Stalled, "long-running tool should stall after 1 minute without events");
    Expect(stalled.DerivedStatus, CodexCliDerivedStatus.Stalled, "running tool long should transition to stalled internally");
}

void InterruptedSignalsWin()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f4133b");
    var startedAt = DateTimeOffset.Parse("2026-04-09T04:30:00Z");

    Apply(machine, EventMessage("task_started", startedAt));
    Apply(machine, TurnAborted(startedAt.AddSeconds(3)));
    Expect(machine.BuildTask().Status, CodexSessionStatus.Interrupted, "turn_aborted interrupted should enter Interrupted");

    Apply(machine, EventMessage("task_started", startedAt.AddSeconds(10)));
    Apply(machine, ThreadRolledBack(startedAt.AddSeconds(12)));
    Expect(machine.BuildTask().Status, CodexSessionStatus.Interrupted, "thread_rolled_back should stay Interrupted");
}

void InterruptedCoolsDownToIdle()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f4133c");
    var startedAt = DateTimeOffset.Parse("2026-04-09T04:40:00Z");

    Apply(machine, EventMessage("task_started", startedAt));
    Apply(machine, TurnAborted(startedAt.AddSeconds(2)));
    Expect(machine.BuildTask().Status, CodexSessionStatus.Interrupted, "turn_aborted interrupted should enter Interrupted");

    machine.AdvanceClock(startedAt.AddSeconds(8));
    Expect(machine.BuildTask().Status, CodexSessionStatus.Idle, "interrupted state should cool down to Idle after 5 seconds");
}

void ApplyPatchTracksChangedFiles()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f4133d");
    var startedAt = DateTimeOffset.Parse("2026-04-09T05:00:00Z");

    Apply(machine, EventMessage("task_started", startedAt));
    Apply(machine, ApplyPatchCall(
        """
        *** Begin Patch
        *** Update File: C:\Users\Haruta\Documents\code\APP\vibe-island-windows\DynamicIsland\MainWindow.xaml
        @@
        -old
        +new
        *** Add File: C:\Users\Haruta\Documents\code\APP\vibe-island-windows\DynamicIsland\Views\FilesPanel.xaml
        +content
        *** End Patch
        """,
        startedAt.AddSeconds(1)));

    var task = machine.BuildTask();
    Expect(task.ChangedFiles?.Count ?? 0, 2, "apply_patch input should track changed files");
    Expect(task.ChangedFiles![0], @"C:\Users\Haruta\Documents\code\APP\vibe-island-windows\DynamicIsland\Views\FilesPanel.xaml", "most recent patch path should be first");
    Expect(task.ChangedFiles![1], @"C:\Users\Haruta\Documents\code\APP\vibe-island-windows\DynamicIsland\MainWindow.xaml", "updated file should be retained");
}

void MalformedJsonIsIgnored()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f4133e");
    var success = machine.TryApplyLine("{not-json", out var error);

    if (success)
    {
        throw new InvalidOperationException("Malformed JSON should not parse successfully.");
    }

    if (string.IsNullOrWhiteSpace(error))
    {
        throw new InvalidOperationException("Malformed JSON should return a parse error.");
    }
}

void RunningToolLongBeatsNewerProcessing()
{
    var older = new Candidate(
        new CodexTask(
            CodexSessionStatus.RunningTool,
            "Older session",
            "Running shell_command for a while.",
            Array.Empty<string>(),
            DateTimeOffset.Parse("2026-04-09T02:41:24Z"),
            "older"),
        CodexCliDerivedStatus.RunningToolLong);

    var newer = new Candidate(
        new CodexTask(
            CodexSessionStatus.Processing,
            "Newer session",
            "Codex CLI is processing the current turn.",
            Array.Empty<string>(),
            DateTimeOffset.Parse("2026-04-09T02:42:24Z"),
            "newer"),
        CodexCliDerivedStatus.Processing);

    var selected = SelectCurrentTaskForTest(older, newer);
    Expect(selected.Task.SessionId ?? string.Empty, "older", "RunningToolLong should outrank a newer plain Processing session");
    Expect(selected.DerivedStatus, CodexCliDerivedStatus.RunningToolLong, "selection should preserve the higher active priority");
}

void RunningToolBeatsThinkingSuspected()
{
    var runningTool = new Candidate(
        new CodexTask(
            CodexSessionStatus.RunningTool,
            "Tool session",
            "Running shell_command.",
            Array.Empty<string>(),
            DateTimeOffset.Parse("2026-04-09T03:05:56.596Z"),
            "tool"),
        CodexCliDerivedStatus.RunningTool);

    var thinking = new Candidate(
        new CodexTask(
            CodexSessionStatus.Processing,
            "Thinking session",
            "Codex may be reasoning (no new events).",
            Array.Empty<string>(),
            DateTimeOffset.Parse("2026-04-09T03:05:57.282Z"),
            "thinking"),
        CodexCliDerivedStatus.ThinkingSuspected);

    var selected = SelectCurrentTaskForTest(runningTool, thinking);
    Expect(selected.Task.SessionId ?? string.Empty, "tool", "RunningTool should outrank ThinkingSuspected");
    Expect(selected.DerivedStatus, CodexCliDerivedStatus.RunningTool, "selection should preserve RunningTool");
}

void ActiveSessionWinsOverNewerStalledSession()
{
    var active = new Candidate(
        new CodexTask(
            CodexSessionStatus.Processing,
            "Active session",
            "Codex CLI is processing the current turn.",
            Array.Empty<string>(),
            DateTimeOffset.Parse("2026-04-09T03:05:56.596Z"),
            "active"),
        CodexCliDerivedStatus.Processing);

    var stalled = new Candidate(
        new CodexTask(
            CodexSessionStatus.Stalled,
            "Older session",
            "No new events; task may be stalled.",
            Array.Empty<string>(),
            DateTimeOffset.Parse("2026-04-09T03:05:57.282Z"),
            "stalled"),
        CodexCliDerivedStatus.Stalled);

    var selected = SelectCurrentTaskForTest(active, stalled);
    Expect(selected.Task.SessionId ?? string.Empty, "active", "active session should remain visible even if another session stalls later");
    Expect(selected.DerivedStatus, CodexCliDerivedStatus.Processing, "stalled sessions should not displace active work");
}

void ServiceStartsBeforeBootAnimationCompletes()
{
    var service = new ProbeStatusService();
    var viewModel = new StatusViewModel(service, new DynamicIsland.UI.IslandLayoutSettings());

    var initializeTask = viewModel.InitializeAsync();
    Thread.Sleep(150);

    if (service.StartCallCount != 1)
    {
        throw new InvalidOperationException("status service should start immediately instead of waiting for boot animation");
    }

    initializeTask.GetAwaiter().GetResult();
    viewModel.Dispose();
}

static void Apply(CodexCliSessionStateMachine machine, string jsonLine)
{
    if (!machine.TryApplyLine(jsonLine, out var error))
    {
        throw new InvalidOperationException($"Expected line to parse successfully, but received: {error}");
    }
}

static void Expect<T>(T actual, T expected, string message)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
    {
        throw new InvalidOperationException($"{message}. Expected {expected}, got {actual}.");
    }
}

static string EventMessage(string payloadType, DateTimeOffset timestamp)
{
    return JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new
        {
            type = payloadType
        }
    });
}

static string FunctionCall(string name, DateTimeOffset timestamp)
{
    return JsonSerializer.Serialize(new
    {
        timestamp,
        type = "response_item",
        payload = new
        {
            type = "function_call",
            name
        }
    });
}

static string FunctionCallOutput(DateTimeOffset timestamp)
{
    return JsonSerializer.Serialize(new
    {
        timestamp,
        type = "response_item",
        payload = new
        {
            type = "function_call_output"
        }
    });
}

static string ApplyPatchCall(string input, DateTimeOffset timestamp)
{
    return JsonSerializer.Serialize(new
    {
        timestamp,
        type = "response_item",
        payload = new
        {
            type = "custom_tool_call",
            name = "apply_patch",
            input
        }
    });
}

static string ResponseMessage(string phase, string text, DateTimeOffset timestamp)
{
    return JsonSerializer.Serialize(new
    {
        timestamp,
        type = "response_item",
        payload = new
        {
            type = "message",
            phase,
            role = "assistant",
            content = new[]
            {
                new
                {
                    type = "output_text",
                    text
                }
            }
        }
    });
}

static string TaskComplete(DateTimeOffset timestamp)
{
    return JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new
        {
            type = "task_complete"
        }
    });
}

static string TurnAborted(DateTimeOffset timestamp)
{
    return JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new
        {
            type = "turn_aborted",
            reason = "interrupted"
        }
    });
}

static string ThreadRolledBack(DateTimeOffset timestamp)
{
    return JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new
        {
            type = "thread_rolled_back",
            num_turns = 1
        }
    });
}

static Candidate SelectCurrentTaskForTest(params Candidate[] candidates)
{
    var activeCandidates = candidates
        .Where(candidate => IsActiveStatusForTest(candidate.DerivedStatus))
        .OrderBy(candidate => GetActivePriorityForTest(candidate.DerivedStatus))
        .ThenByDescending(candidate => candidate.Task.UpdatedAt)
        .ToList();

    if (activeCandidates.Count > 0)
    {
        return activeCandidates[0];
    }

    return candidates
        .OrderByDescending(candidate => candidate.Task.UpdatedAt)
        .ThenBy(candidate => GetPriorityForTest(candidate.DerivedStatus))
        .First();
}

static int GetPriorityForTest(CodexCliDerivedStatus status)
{
    return status switch
    {
        CodexCliDerivedStatus.Unknown => 0,
        CodexCliDerivedStatus.Interrupted => 1,
        CodexCliDerivedStatus.Stalled => 2,
        CodexCliDerivedStatus.Completed => 3,
        _ => 4
    };
}

static int GetActivePriorityForTest(CodexCliDerivedStatus status)
{
    return status switch
    {
        CodexCliDerivedStatus.RunningToolLong => 0,
        CodexCliDerivedStatus.RunningTool => 1,
        CodexCliDerivedStatus.Finishing => 2,
        CodexCliDerivedStatus.ThinkingSuspected => 3,
        CodexCliDerivedStatus.Processing => 4,
        _ => 5
    };
}

static bool IsActiveStatusForTest(CodexCliDerivedStatus status)
{
    return status is
        CodexCliDerivedStatus.Processing or
        CodexCliDerivedStatus.ThinkingSuspected or
        CodexCliDerivedStatus.RunningTool or
        CodexCliDerivedStatus.RunningToolLong or
        CodexCliDerivedStatus.Finishing;
}

sealed record Candidate(CodexTask Task, CodexCliDerivedStatus DerivedStatus);

sealed class ProbeStatusService : ICodexStatusService
{
    public event EventHandler<CodexTask>? TaskUpdated;

    public CodexTask? CurrentTask => null;

    public int StartCallCount { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartCallCount++;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}
