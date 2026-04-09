using System.Text.Json;
using DynamicIsland.Models;
using DynamicIsland.Services;

var failures = new List<string>();

Run(nameof(TaskLifecycleTransitions), TaskLifecycleTransitions);
Run(nameof(StalledAfterToolTimeout), StalledAfterToolTimeout);
Run(nameof(InterruptedSignalsWin), InterruptedSignalsWin);
Run(nameof(MalformedJsonIsIgnored), MalformedJsonIsIgnored);
Run(nameof(MostRecentSessionWinsOverOlderHigherPriorityState), MostRecentSessionWinsOverOlderHigherPriorityState);

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

void StalledAfterToolTimeout()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f41337");
    var startedAt = DateTimeOffset.Parse("2026-04-09T03:00:00Z");

    Apply(machine, EventMessage("task_started", startedAt));
    Apply(machine, FunctionCall("shell_command", startedAt.AddSeconds(1)));
    Expect(machine.BuildTask().Status, CodexSessionStatus.RunningTool, "function_call should enter RunningTool");

    machine.AdvanceClock(startedAt.AddSeconds(22));
    Expect(machine.BuildTask().Status, CodexSessionStatus.Stalled, "running tool with no events for 20 seconds should stall");
}

void InterruptedSignalsWin()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f41338");
    var startedAt = DateTimeOffset.Parse("2026-04-09T04:00:00Z");

    Apply(machine, EventMessage("task_started", startedAt));
    Apply(machine, TurnAborted(startedAt.AddSeconds(3)));
    Expect(machine.BuildTask().Status, CodexSessionStatus.Interrupted, "turn_aborted interrupted should enter Interrupted");

    Apply(machine, EventMessage("task_started", startedAt.AddSeconds(10)));
    Apply(machine, ThreadRolledBack(startedAt.AddSeconds(12)));
    Expect(machine.BuildTask().Status, CodexSessionStatus.Interrupted, "thread_rolled_back should stay Interrupted");
}

void MalformedJsonIsIgnored()
{
    var machine = new CodexCliSessionStateMachine("019d6ff3-687e-7cd1-a14f-a7fa77f41339");
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

void MostRecentSessionWinsOverOlderHigherPriorityState()
{
    var older = new CodexTask(
        CodexSessionStatus.Stalled,
        "Older session",
        "No new events arrived.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T02:41:24Z"),
        "older");

    var newer = new CodexTask(
        CodexSessionStatus.Processing,
        "Newer session",
        "Codex CLI is processing the current turn.",
        Array.Empty<string>(),
        DateTimeOffset.Parse("2026-04-09T02:42:24Z"),
        "newer");

    var selected = SelectCurrentTaskForTest(older, newer);
    Expect(selected.SessionId ?? string.Empty, "newer", "newer session activity should beat older stalled activity");
    Expect(selected.Status, CodexSessionStatus.Processing, "newer active session should remain visible");
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

static CodexTask SelectCurrentTaskForTest(params CodexTask[] tasks)
{
    return tasks
        .OrderByDescending(task => task.UpdatedAt)
        .ThenBy(task => GetPriorityForTest(task.Status))
        .First();
}

static int GetPriorityForTest(CodexSessionStatus status)
{
    return status switch
    {
        CodexSessionStatus.Unknown => 0,
        CodexSessionStatus.Interrupted => 1,
        CodexSessionStatus.Stalled => 2,
        CodexSessionStatus.RunningTool => 3,
        CodexSessionStatus.Processing => 4,
        CodexSessionStatus.Finishing => 5,
        CodexSessionStatus.Completed => 6,
        _ => 7
    };
}
