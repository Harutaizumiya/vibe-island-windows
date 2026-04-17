using System.Text.Json;
using DynamicIsland.Models;
using DynamicIsland.Services;
using DynamicIsland.ViewModels;

var failures = new List<string>();

Run(nameof(TaskLifecycleTransitions), TaskLifecycleTransitions);
Run(nameof(ThinkingSuspectedAfterProcessingDelay), ThinkingSuspectedAfterProcessingDelay);
Run(nameof(ReasoningRefreshesProcessingActivity), ReasoningRefreshesProcessingActivity);
Run(nameof(RunningToolLongAfterToolDelay), RunningToolLongAfterToolDelay);
Run(nameof(ThinkingSuspectedStallsAfterLongSilence), ThinkingSuspectedStallsAfterLongSilence);
Run(nameof(RunningToolLongStallsAfterLongSilence), RunningToolLongStallsAfterLongSilence);
Run(nameof(InterruptedSignalsWin), InterruptedSignalsWin);
Run(nameof(InterruptedCoolsDownToIdle), InterruptedCoolsDownToIdle);
Run(nameof(ApplyPatchTracksChangedFiles), ApplyPatchTracksChangedFiles);
Run(nameof(TrackerRetentionCapsToFourSessions), TrackerRetentionCapsToFourSessions);
Run(nameof(SelectedAndActivePollSessionsAreRetained), SelectedAndActivePollSessionsAreRetained);
Run(nameof(ThreadNameRetentionKeepsTrackedSessions), ThreadNameRetentionKeepsTrackedSessions);
Run(nameof(MalformedJsonIsIgnored), MalformedJsonIsIgnored);
Run(nameof(RunningToolLongBeatsNewerProcessing), RunningToolLongBeatsNewerProcessing);
Run(nameof(RunningToolBeatsThinkingSuspected), RunningToolBeatsThinkingSuspected);
Run(nameof(ActiveSessionWinsOverNewerStalledSession), ActiveSessionWinsOverNewerStalledSession);
Run(nameof(ServiceStartsBeforeBootAnimationCompletes), ServiceStartsBeforeBootAnimationCompletes);
Run(nameof(ExpandedContentFollowsProcessingAndToolStates), ExpandedContentFollowsProcessingAndToolStates);
Run(nameof(FallbackExpandedMessagesStayDebugOnly), FallbackExpandedMessagesStayDebugOnly);
Run(nameof(ShellCommandDetailsAreCapturedFromFunctionCall), ShellCommandDetailsAreCapturedFromFunctionCall);
Run(nameof(ExpandedContentShowsChangedFilesOnlyAfterCompletion), ExpandedContentShowsChangedFilesOnlyAfterCompletion);
Run(nameof(BuildTaskOmitsDebugSourceOutsideDebugMode), BuildTaskOmitsDebugSourceOutsideDebugMode);
Run(nameof(BuildSnapshotReturnsCachedInstanceWhenStateIsUnchanged), BuildSnapshotReturnsCachedInstanceWhenStateIsUnchanged);
Run(nameof(IdleUsesCodexIcon), IdleUsesCodexIcon);
Run(nameof(IdleShowsCodexTitle), IdleShowsCodexTitle);
Run(nameof(CompactStatusUsesChineseLabels), CompactStatusUsesChineseLabels);
Run(nameof(CompletedUsesCheckGlyph), CompletedUsesCheckGlyph);
Run(nameof(CompletedStaysVisibleForOneMinute), CompletedStaysVisibleForOneMinute);
Run(nameof(CompletedAutoExpandsThenAutoCollapses), CompletedAutoExpandsThenAutoCollapses);
Run(nameof(HoverExpansionAutoCollapsesAfterFocusLoss), HoverExpansionAutoCollapsesAfterFocusLoss);
Run(nameof(ManualExpansionAutoCollapsesAfterFocusLoss), ManualExpansionAutoCollapsesAfterFocusLoss);

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
    Expect(machine.BuildTask().Status, CodexSessionStatus.Completed, "completed state should remain visible during the one-minute cooldown");
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

void ReasoningRefreshesProcessingActivity()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f4133f");
    var startedAt = DateTimeOffset.Parse("2026-04-09T03:30:00Z");

    Apply(machine, EventMessage("task_started", startedAt));
    Apply(machine, Reasoning(startedAt.AddSeconds(7), "Inspecting the latest session activity."));

    var refreshed = machine.BuildSnapshot(startedAt.AddSeconds(14));
    Expect(refreshed.Task.Status, CodexSessionStatus.Processing, "reasoning should keep the public status in Processing");
    Expect(refreshed.DerivedStatus, CodexCliDerivedStatus.Processing, "reasoning should refresh activity and avoid early ThinkingSuspected");
    Expect(refreshed.Task.UpdatedAt, startedAt.AddSeconds(7), "reasoning should refresh UpdatedAt so the island can follow progress");

    var suspectedLater = machine.BuildSnapshot(startedAt.AddSeconds(16));
    Expect(suspectedLater.DerivedStatus, CodexCliDerivedStatus.ThinkingSuspected, "reasoning should still age into ThinkingSuspected after the threshold");
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
    var stalled = machine.BuildSnapshot(startedAt.AddSeconds(181));
    Expect(stalled.Task.Status, CodexSessionStatus.Stalled, "thinking suspected should eventually stall after 3 minutes");
    Expect(stalled.DerivedStatus, CodexCliDerivedStatus.Stalled, "thinking suspected should transition to stalled internally");
}

void RunningToolLongStallsAfterLongSilence()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f4133a");
    var startedAt = DateTimeOffset.Parse("2026-04-09T04:20:00Z");

    Apply(machine, EventMessage("task_started", startedAt));
    Apply(machine, FunctionCall("shell_command", startedAt.AddSeconds(1)));
    var stalled = machine.BuildSnapshot(startedAt.AddSeconds(182));
    Expect(stalled.Task.Status, CodexSessionStatus.Stalled, "long-running tool should stall after 3 minutes without events");
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

void TrackerRetentionCapsToFourSessions()
{
    var retained = CodexCliStatusService.SelectRetainedSessionIdsForTest(
        [
            new CodexCliRetentionCandidate("session-1", CodexCliDerivedStatus.Processing, DateTimeOffset.Parse("2026-04-09T05:00:00Z"), DateTimeOffset.Parse("2026-04-09T05:00:00Z"), DateTimeOffset.MinValue, DateTimeOffset.Parse("2026-04-09T05:00:00Z"), false, false),
            new CodexCliRetentionCandidate("session-2", CodexCliDerivedStatus.RunningTool, DateTimeOffset.Parse("2026-04-09T05:00:05Z"), DateTimeOffset.Parse("2026-04-09T05:00:05Z"), DateTimeOffset.MinValue, DateTimeOffset.Parse("2026-04-09T05:00:05Z"), false, false),
            new CodexCliRetentionCandidate("session-3", CodexCliDerivedStatus.Completed, DateTimeOffset.Parse("2026-04-09T05:00:06Z"), DateTimeOffset.Parse("2026-04-09T05:00:06Z"), DateTimeOffset.MinValue, DateTimeOffset.Parse("2026-04-09T05:00:06Z"), false, false),
            new CodexCliRetentionCandidate("session-4", CodexCliDerivedStatus.Processing, DateTimeOffset.Parse("2026-04-09T05:00:07Z"), DateTimeOffset.Parse("2026-04-09T05:00:07Z"), DateTimeOffset.MinValue, DateTimeOffset.Parse("2026-04-09T05:00:07Z"), false, false),
            new CodexCliRetentionCandidate("session-5", CodexCliDerivedStatus.Stalled, DateTimeOffset.Parse("2026-04-09T05:00:08Z"), DateTimeOffset.Parse("2026-04-09T05:00:08Z"), DateTimeOffset.MinValue, DateTimeOffset.Parse("2026-04-09T05:00:08Z"), false, false)
        ]);

    Expect(retained.Count, 4, "tracker retention should cap the service cache at four sessions");
    Expect(retained.Contains("session-2"), true, "active running-tool sessions should be retained");
    Expect(retained.Contains("session-4"), true, "newer active processing sessions should be retained");
}

void SelectedAndActivePollSessionsAreRetained()
{
    var retained = CodexCliStatusService.SelectRetainedSessionIdsForTest(
        [
            new CodexCliRetentionCandidate("selected", CodexCliDerivedStatus.Completed, DateTimeOffset.Parse("2026-04-09T05:10:00Z"), DateTimeOffset.Parse("2026-04-09T05:10:00Z"), DateTimeOffset.Parse("2026-04-09T05:10:00Z"), DateTimeOffset.Parse("2026-04-09T05:10:00Z"), true, false),
            new CodexCliRetentionCandidate("active-poll", CodexCliDerivedStatus.Processing, DateTimeOffset.Parse("2026-04-09T05:10:01Z"), DateTimeOffset.Parse("2026-04-09T05:10:01Z"), DateTimeOffset.MinValue, DateTimeOffset.Parse("2026-04-09T05:10:01Z"), false, true),
            new CodexCliRetentionCandidate("session-3", CodexCliDerivedStatus.RunningToolLong, DateTimeOffset.Parse("2026-04-09T05:10:02Z"), DateTimeOffset.Parse("2026-04-09T05:10:02Z"), DateTimeOffset.MinValue, DateTimeOffset.Parse("2026-04-09T05:10:02Z"), false, false),
            new CodexCliRetentionCandidate("session-4", CodexCliDerivedStatus.Stalled, DateTimeOffset.Parse("2026-04-09T05:10:03Z"), DateTimeOffset.Parse("2026-04-09T05:10:03Z"), DateTimeOffset.MinValue, DateTimeOffset.Parse("2026-04-09T05:10:03Z"), false, false),
            new CodexCliRetentionCandidate("session-5", CodexCliDerivedStatus.Interrupted, DateTimeOffset.Parse("2026-04-09T05:10:04Z"), DateTimeOffset.Parse("2026-04-09T05:10:04Z"), DateTimeOffset.MinValue, DateTimeOffset.Parse("2026-04-09T05:10:04Z"), false, false)
        ]);

    Expect(retained.Contains("selected"), true, "the currently published session should never be trimmed");
    Expect(retained.Contains("active-poll"), true, "active-poll sessions should never be trimmed");
}

void ThreadNameRetentionKeepsTrackedSessions()
{
    var candidates = new List<CodexCliThreadNameCandidate>
    {
        new("tracked-old", DateTimeOffset.Parse("2026-04-09T05:20:00Z"), true)
    };

    for (var index = 0; index < 19; index++)
    {
        candidates.Add(new CodexCliThreadNameCandidate(
            $"session-{index}",
            DateTimeOffset.Parse("2026-04-09T05:20:00Z").AddMinutes(index + 1),
            false));
    }

    var retained = CodexCliStatusService.SelectRetainedThreadNamesForTest(candidates);
    Expect(retained.Count, 16, "thread-name retention should cap the cache at sixteen entries");
    Expect(retained.Contains("tracked-old"), true, "tracked sessions should keep their thread names even if they are older");
    Expect(retained.Contains("session-0"), false, "old untracked thread names should be eligible for trimming");
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

void ExpandedContentFollowsProcessingAndToolStates()
{
    var service = new ControllableStatusService();
    var viewModel = new StatusViewModel(service, new DynamicIsland.UI.IslandLayoutSettings());

    service.Publish(new CodexTask(
        CodexSessionStatus.Processing,
        "Session",
        "Streaming commentary from the agent.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T06:00:00Z"),
        "session-1"));

    Expect(viewModel.ExpandedSectionTitle, "AGENT OUTPUT", "processing should show the agent output section");
    Expect(viewModel.ExpandedDetailText, "Streaming commentary from the agent.", "processing should show the latest agent output");
    Expect(viewModel.IsExpandedTextVisible, true, "processing should show text details");
    Expect(viewModel.IsChangedFilesVisible, false, "processing should not show changed files");

    service.Publish(new CodexTask(
        CodexSessionStatus.RunningTool,
        "Session",
        "Running shell_command.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T06:00:10Z"),
        "session-1",
        DebugSource: "Derived: RunningTool\nPublic: RunningTool\nEvent: tool_started\nTool: shell_command\nCommand: dotnet build DynamicIsland/DynamicIsland.csproj -c Release\nEventTime: 2026-04-09T06:00:10.0000000+00:00"));

    Expect(viewModel.ExpandedSectionTitle, "TOOL DETAILS", "running tool should show tool details");
    Expect(viewModel.ExpandedDetailText.Contains("Running shell_command.", StringComparison.Ordinal), true, "tool details should include the tool message");
    Expect(viewModel.ExpandedDetailText.Contains("Tool: shell_command", StringComparison.Ordinal), true, "tool details should include the tool name");
    Expect(viewModel.ExpandedDetailText.Contains("Command: dotnet build DynamicIsland/DynamicIsland.csproj -c Release", StringComparison.Ordinal), true, "tool details should include the command");
    Expect(viewModel.IsExpandedTextVisible, true, "running tool should keep text details visible");
    Expect(viewModel.IsChangedFilesVisible, false, "running tool should not show changed files");

    viewModel.Dispose();
}

void FallbackExpandedMessagesStayDebugOnly()
{
    var service = new ControllableStatusService();
    var viewModel = new StatusViewModel(service, new DynamicIsland.UI.IslandLayoutSettings());

    service.Publish(new CodexTask(
        CodexSessionStatus.Processing,
        "Session",
        "Codex is reasoning about the current turn.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T06:00:05Z"),
        "session-fallback"));

    Expect(viewModel.ExpandedDetailText, string.Empty, "fallback reasoning text should be hidden outside debug mode");
    Expect(viewModel.IsExpandedTextVisible, false, "fallback reasoning text should not keep the expanded text panel visible");

    viewModel.Dispose();
}

void ShellCommandDetailsAreCapturedFromFunctionCall()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f41341");
    var startedAt = DateTimeOffset.Parse("2026-04-09T06:05:00Z");

    Apply(machine, JsonSerializer.Serialize(new
    {
        timestamp = startedAt,
        type = "response_item",
        payload = new
        {
            type = "function_call",
            name = "shell_command",
            arguments = "{\"command\":\"dotnet test DynamicIsland.Tests/DynamicIsland.Tests.csproj -c Release\",\"workdir\":\"C:\\\\Users\\\\Haruta\\\\Documents\\\\code\\\\APP\\\\vibe-island-windows\"}"
        }
    }));

    var task = machine.BuildTask();
    Expect(task.Status, CodexSessionStatus.RunningTool, "shell command call should enter RunningTool");
    Expect(task.Message.Contains("Command: dotnet test DynamicIsland.Tests/DynamicIsland.Tests.csproj -c Release", StringComparison.Ordinal), true, "tool messages should include the parsed shell command even outside debug mode");
}

void ExpandedContentShowsChangedFilesOnlyAfterCompletion()
{
    var service = new ControllableStatusService();
    var viewModel = new StatusViewModel(service, new DynamicIsland.UI.IslandLayoutSettings());

    service.Publish(new CodexTask(
        CodexSessionStatus.RunningTool,
        "Session",
        "Running apply_patch.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T06:10:00Z"),
        "session-2",
        ChangedFiles: new[]
        {
            @"C:\Users\Haruta\Documents\code\APP\vibe-island-windows\DynamicIsland\MainWindow.xaml"
        }));

    Expect(viewModel.IsChangedFilesVisible, false, "changed files should stay hidden before completion");
    Expect(viewModel.ChangedFiles.Count, 0, "changed files list should remain empty before completion");

    service.Publish(new CodexTask(
        CodexSessionStatus.Completed,
        "Session",
        "Completed turn.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T06:10:20Z"),
        "session-2",
        ChangedFiles: new[]
        {
            @"C:\Users\Haruta\Documents\code\APP\vibe-island-windows\DynamicIsland\MainWindow.xaml",
            @"C:\Users\Haruta\Documents\code\APP\vibe-island-windows\DynamicIsland\ViewModels\StatusViewModel.cs"
        }));

    Expect(viewModel.ExpandedSectionTitle, "CHANGED FILES", "completed turns should switch to changed files");
    Expect(viewModel.IsChangedFilesVisible, true, "completed turns should show changed files");
    Expect(viewModel.IsExpandedTextVisible, false, "completed turns with changed files should hide the text panel");
    Expect(viewModel.ChangedFiles.Count, 2, "completed turns should expose tracked changed files");

    viewModel.Dispose();
}

void BuildTaskOmitsDebugSourceOutsideDebugMode()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f41342");
    var startedAt = DateTimeOffset.Parse("2026-04-09T06:15:00Z");

    Apply(machine, EventMessage("task_started", startedAt));
    var task = machine.BuildTask();
    Expect(string.IsNullOrWhiteSpace(task.DebugSource), true, "non-debug mode should not generate debug source strings");
}

void BuildSnapshotReturnsCachedInstanceWhenStateIsUnchanged()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f41343");
    var startedAt = DateTimeOffset.Parse("2026-04-09T06:16:00Z");

    Apply(machine, EventMessage("task_started", startedAt));
    var first = machine.BuildSnapshot(startedAt.AddSeconds(1));
    var second = machine.BuildSnapshot(startedAt.AddSeconds(1));

    if (!ReferenceEquals(first, second))
    {
        throw new InvalidOperationException("unchanged snapshots should reuse the cached snapshot instance");
    }
}

void IdleUsesCodexIcon()
{
    var service = new ControllableStatusService();
    var viewModel = new StatusViewModel(service, new DynamicIsland.UI.IslandLayoutSettings());

    service.Publish(new CodexTask(
        CodexSessionStatus.Idle,
        "Codex CLI",
        "Waiting for an active Codex CLI session.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T06:20:00Z"),
        "session-idle"));

    Expect(viewModel.IsIdleCodexIconVisible, true, "idle state should show the Codex icon");

    service.Publish(new CodexTask(
        CodexSessionStatus.Processing,
        "Session",
        "Agent is processing.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T06:20:05Z"),
        "session-idle"));

    Expect(viewModel.IsIdleCodexIconVisible, false, "non-idle states should hide the Codex icon");

    viewModel.Dispose();
}

void IdleShowsCodexTitle()
{
    var service = new ControllableStatusService();
    var viewModel = new StatusViewModel(service, new DynamicIsland.UI.IslandLayoutSettings());

    service.Publish(new CodexTask(
        CodexSessionStatus.Idle,
        "Codex CLI",
        "Waiting for an active Codex CLI session.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T06:25:00Z"),
        "session-idle"));

    Expect(viewModel.StatusText, "Codex", "idle state should display Codex instead of Codex CLI");

    service.Publish(new CodexTask(
        CodexSessionStatus.Processing,
        "Mock Session",
        "Agent is processing.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T06:25:05Z"),
        "session-idle"));

    Expect(viewModel.StatusText, "Mock Session", "non-idle states should keep their original title");

    viewModel.Dispose();
}

void CompactStatusUsesChineseLabels()
{
    var service = new ControllableStatusService();
    var viewModel = new StatusViewModel(service, new DynamicIsland.UI.IslandLayoutSettings());

    service.Publish(new CodexTask(
        CodexSessionStatus.Idle,
        "Codex CLI",
        "Waiting for an active Codex CLI session.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T06:27:00Z"),
        "session-idle"));
    Expect(viewModel.CompactStatusText, "空闲", "idle state should use the Chinese compact label");

    service.Publish(new CodexTask(
        CodexSessionStatus.Processing,
        "Mock Session",
        "Agent is processing.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T06:27:05Z"),
        "session-processing"));
    Expect(viewModel.CompactStatusText, "思考中", "processing state should use the Chinese compact label");

    service.Publish(new CodexTask(
        CodexSessionStatus.RunningTool,
        "Mock Session",
        "Running shell_command.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T06:27:10Z"),
        "session-tool"));
    Expect(viewModel.CompactStatusText, "调用工具", "running tool should use the Chinese compact label");

    viewModel.Dispose();
}

void CompletedUsesCheckGlyph()
{
    var service = new ControllableStatusService();
    var viewModel = new StatusViewModel(service, new DynamicIsland.UI.IslandLayoutSettings());

    service.Publish(new CodexTask(
        CodexSessionStatus.Completed,
        "Session",
        "Completed turn.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T06:28:00Z"),
        "session-completed"));

    Expect(viewModel.StatusGlyph, "√", "completed state should use the check glyph");

    viewModel.Dispose();
}

void CompletedStaysVisibleForOneMinute()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f41340");
    var startedAt = DateTimeOffset.Parse("2026-04-09T06:30:00Z");

    Apply(machine, EventMessage("task_started", startedAt));
    Apply(machine, TaskComplete(startedAt.AddSeconds(5)));

    machine.AdvanceClock(startedAt.AddSeconds(64));
    Expect(machine.BuildTask().Status, CodexSessionStatus.Completed, "completed state should still be visible before one minute elapses");

    machine.AdvanceClock(startedAt.AddSeconds(66));
    Expect(machine.BuildTask().Status, CodexSessionStatus.Idle, "completed state should cool down to Idle after one minute");
}

void CompletedAutoExpandsThenAutoCollapses()
{
    var service = new ControllableStatusService();
    var viewModel = new StatusViewModel(service, new DynamicIsland.UI.IslandLayoutSettings());

    service.Publish(new CodexTask(
        CodexSessionStatus.Completed,
        "Session",
        "Completed turn.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T06:40:00Z"),
        "session-completed",
        ChangedFiles: new[]
        {
            @"C:\Users\Haruta\Documents\code\APP\vibe-island-windows\DynamicIsland\MainWindow.xaml"
        }));

    Expect(viewModel.IsExpanded, true, "completed state should trigger one automatic expansion");

    var method = typeof(StatusViewModel).GetMethod("OnCompletedAutoCollapseTimerTick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    if (method is null)
    {
        throw new InvalidOperationException("Expected completed auto-collapse handler to exist.");
    }

    method.Invoke(viewModel, [null, EventArgs.Empty]);
    Expect(viewModel.IsExpanded, false, "completed state should auto-collapse after the timer fires");

    viewModel.Dispose();
}

void HoverExpansionAutoCollapsesAfterFocusLoss()
{
    var service = new ControllableStatusService();
    var viewModel = new StatusViewModel(service, new DynamicIsland.UI.IslandLayoutSettings());

    viewModel.ExpandFromHover();
    Expect(viewModel.IsExpanded, true, "hover should expand the island immediately");

    viewModel.ScheduleCollapseAfterFocusLoss();
    InvokeNonPublic(viewModel, "OnFocusLossAutoCollapseTimerTick");
    Expect(viewModel.IsExpanded, false, "hover expansion should auto-collapse after focus loss");

    viewModel.Dispose();
}

void ManualExpansionAutoCollapsesAfterFocusLoss()
{
    var service = new ControllableStatusService();
    var viewModel = new StatusViewModel(service, new DynamicIsland.UI.IslandLayoutSettings());

    viewModel.ToggleExpandCommand.Execute(null);
    Expect(viewModel.IsExpanded, true, "manual toggle should expand the island");

    viewModel.ScheduleCollapseAfterFocusLoss();
    InvokeNonPublic(viewModel, "OnFocusLossAutoCollapseTimerTick");
    Expect(viewModel.IsExpanded, false, "manual expansion should auto-collapse after focus loss");

    viewModel.Dispose();
}

static void InvokeNonPublic(object target, string methodName)
{
    var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    if (method is null)
    {
        throw new InvalidOperationException($"Expected non-public method '{methodName}' to exist.");
    }

    method.Invoke(target, [null, EventArgs.Empty]);
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

static string Reasoning(DateTimeOffset timestamp, string summary)
{
    return JsonSerializer.Serialize(new
    {
        timestamp,
        type = "response_item",
        payload = new
        {
            type = "reasoning",
            summary = new[]
            {
                new
                {
                    text = summary
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

sealed class ControllableStatusService : ICodexStatusService
{
    public event EventHandler<CodexTask>? TaskUpdated;

    public CodexTask? CurrentTask { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
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

    public void Publish(CodexTask task)
    {
        CurrentTask = task;
        TaskUpdated?.Invoke(this, task);
    }

    public void Dispose()
    {
    }
}
