using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using DynamicIsland.Models;
using DynamicIsland.Utils;

namespace DynamicIsland.Services;

public sealed class CodexCliStatusService : ICodexStatusService
{
    private static readonly Regex SessionIdRegex = new(@"(?<id>[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly TimeSpan ActiveSessionWindow = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _threadNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _codexRoot;
    private readonly string _sessionsRoot;
    private readonly string _sessionIndexPath;
    private readonly DispatcherTimer _clockTimer;

    private FileSystemWatcher? _sessionsWatcher;
    private FileSystemWatcher? _sessionIndexWatcher;
    private bool _started;
    private bool _watchersHealthy = true;

    public CodexCliStatusService()
    {
        _codexRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        _sessionsRoot = Path.Combine(_codexRoot, "sessions");
        _sessionIndexPath = Path.Combine(_codexRoot, "session_index.jsonl");
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += OnClockTick;
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
        LoadSessionIndex();
        DiscoverSessionFiles();
        InitializeWatchers();
        _clockTimer.Start();
        PublishCurrentTask();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _clockTimer.Stop();
        DisposeWatcher(_sessionsWatcher);
        _sessionsWatcher = null;
        DisposeWatcher(_sessionIndexWatcher);
        _sessionIndexWatcher = null;
        _started = false;
        return Task.CompletedTask;
    }

    public Task ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default)
    {
        DiagnosticsLogger.Write($"CodexCliStatusService ignored UI action '{actionId}'.");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _clockTimer.Tick -= OnClockTick;
        _clockTimer.Stop();
        DisposeWatcher(_sessionsWatcher);
        DisposeWatcher(_sessionIndexWatcher);
    }

    private void InitializeWatchers()
    {
        if (Directory.Exists(_sessionsRoot))
        {
            _sessionsWatcher = new FileSystemWatcher(_sessionsRoot, "rollout-*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _sessionsWatcher.Created += (_, e) => HandleSessionFileEvent(e.FullPath);
            _sessionsWatcher.Changed += (_, e) => HandleSessionFileEvent(e.FullPath);
            _sessionsWatcher.Renamed += (_, e) => HandleSessionFileEvent(e.FullPath);
            _sessionsWatcher.Error += (_, _) => HandleWatcherFailure("Codex CLI session watcher failed.");
            _sessionsWatcher.EnableRaisingEvents = true;
        }

        if (Directory.Exists(_codexRoot))
        {
            _sessionIndexWatcher = new FileSystemWatcher(_codexRoot, Path.GetFileName(_sessionIndexPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _sessionIndexWatcher.Created += (_, _) => HandleSessionIndexEvent();
            _sessionIndexWatcher.Changed += (_, _) => HandleSessionIndexEvent();
            _sessionIndexWatcher.Renamed += (_, _) => HandleSessionIndexEvent();
            _sessionIndexWatcher.Error += (_, _) => HandleWatcherFailure("Codex CLI session index watcher failed.");
            _sessionIndexWatcher.EnableRaisingEvents = true;
        }
    }

    private void DiscoverSessionFiles()
    {
        if (!Directory.Exists(_sessionsRoot))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories))
        {
            EnsureTracker(path, readFromStart: true);
        }
    }

    private void HandleSessionFileEvent(string path)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            EnsureTracker(path, readFromStart: false);
            PublishCurrentTask();
        });
    }

    private void HandleSessionIndexEvent()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LoadSessionIndex();
            PublishCurrentTask();
        });
    }

    private void HandleWatcherFailure(string message)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            DiagnosticsLogger.Write(message);
            _watchersHealthy = false;
            PublishCurrentTask(force: true);
        });
    }

    private void LoadSessionIndex()
    {
        if (!File.Exists(_sessionIndexPath))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(_sessionIndexPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                if (!document.RootElement.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var sessionId = idElement.GetString();
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    continue;
                }

                var threadName = document.RootElement.TryGetProperty("thread_name", out var threadNameElement)
                    && threadNameElement.ValueKind == JsonValueKind.String
                    ? threadNameElement.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(threadName))
                {
                    _threadNames[sessionId] = threadName!;
                }
            }

            foreach (var tracker in _trackers.Values)
            {
                if (_threadNames.TryGetValue(tracker.SessionId, out var threadName))
                {
                    tracker.StateMachine.SetThreadName(threadName);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write($"Failed to read session index: {ex.Message}");
        }
    }

    private void EnsureTracker(string path, bool readFromStart)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var sessionId = TryParseSessionId(path);
        if (sessionId is null)
        {
            DiagnosticsLogger.Write($"Skipped session file with unreadable session id: {path}");
            return;
        }

        SessionTracker tracker;
        lock (_gate)
        {
            if (!_trackers.TryGetValue(sessionId, out tracker!))
            {
                tracker = new SessionTracker(sessionId, path);
                if (_threadNames.TryGetValue(sessionId, out var threadName))
                {
                    tracker.StateMachine.SetThreadName(threadName);
                }

                _trackers[sessionId] = tracker;
            }
            else
            {
                tracker.FilePath = path;
            }
        }

        if (readFromStart && tracker.LastOffset == 0)
        {
            ReadAppendedContent(tracker, resetOffset: true);
            return;
        }

        ReadAppendedContent(tracker, resetOffset: false);
    }

    private void ReadAppendedContent(SessionTracker tracker, bool resetOffset)
    {
        try
        {
            using var stream = new FileStream(tracker.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (resetOffset || stream.Length < tracker.LastOffset)
            {
                tracker.LastOffset = 0;
                tracker.PendingLine = string.Empty;
            }

            stream.Seek(tracker.LastOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            var text = reader.ReadToEnd();
            tracker.LastOffset = stream.Position;

            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var buffer = tracker.PendingLine + text;
            var endsWithNewline = buffer.EndsWith('\n');
            var lines = buffer.Split('\n');
            tracker.PendingLine = endsWithNewline ? string.Empty : lines[^1];

            var lastIndex = endsWithNewline ? lines.Length : lines.Length - 1;
            for (var index = 0; index < lastIndex; index++)
            {
                var line = lines[index].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!tracker.StateMachine.TryApplyLine(line, out var error) && error is not null)
                {
                    DiagnosticsLogger.Write($"Ignored malformed JSONL line for session {tracker.SessionId}: {error}");
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write($"Failed to read session file '{tracker.FilePath}': {ex.Message}");
            tracker.StateMachine.MarkUnknown("Unable to read the Codex CLI session file.", DateTimeOffset.UtcNow);
        }
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        foreach (var tracker in _trackers.Values)
        {
            tracker.StateMachine.AdvanceClock(DateTimeOffset.UtcNow);
        }

        PublishCurrentTask();
    }

    private void PublishCurrentTask(bool force = false)
    {
        var nextTask = BuildCurrentTask();
        if (!force && AreEquivalent(CurrentTask, nextTask))
        {
            return;
        }

        CurrentTask = nextTask;
        TaskUpdated?.Invoke(this, nextTask);
    }

    private CodexTask BuildCurrentTask()
    {
        if (!_watchersHealthy)
        {
            return new CodexTask(
                CodexSessionStatus.Unknown,
                "Codex CLI",
                "The Codex CLI file watcher failed. Restart the island to recover live updates.",
                Array.Empty<string>(),
                DateTimeOffset.UtcNow);
        }

        var now = DateTimeOffset.UtcNow;
        var candidates = _trackers.Values
            .Select(tracker => tracker.StateMachine.BuildTask())
            .Where(task => task.Status != CodexSessionStatus.Idle && now - task.UpdatedAt <= ActiveSessionWindow)
            .OrderBy(task => GetPriority(task.Status))
            .ThenByDescending(task => task.UpdatedAt)
            .ToList();

        if (candidates.Count == 0)
        {
            return new CodexTask(
                CodexSessionStatus.Idle,
                "Codex CLI",
                "Waiting for an active Codex CLI session.",
                Array.Empty<string>(),
                now);
        }

        return candidates[0];
    }

    private static int GetPriority(CodexSessionStatus status)
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

    private static bool AreEquivalent(CodexTask? left, CodexTask right)
    {
        if (left is null)
        {
            return false;
        }

        return left.Status == right.Status
            && left.Title == right.Title
            && left.Message == right.Message
            && left.SessionId == right.SessionId
            && left.AvailableActions.SequenceEqual(right.AvailableActions);
    }

    private static string? TryParseSessionId(string path)
    {
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        var match = SessionIdRegex.Match(fileNameWithoutExtension);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static void DisposeWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    private sealed class SessionTracker
    {
        public SessionTracker(string sessionId, string filePath)
        {
            SessionId = sessionId;
            FilePath = filePath;
            StateMachine = new CodexCliSessionStateMachine(sessionId);
        }

        public string SessionId { get; }

        public string FilePath { get; set; }

        public CodexCliSessionStateMachine StateMachine { get; }

        public long LastOffset { get; set; }

        public string PendingLine { get; set; } = string.Empty;
    }
}
