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
    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromMilliseconds(350);

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _threadNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _codexRoot;
    private readonly string _sessionsRoot;
    private readonly string _sessionIndexPath;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _activePollTimer;

    private FileSystemWatcher? _sessionsWatcher;
    private FileSystemWatcher? _sessionIndexWatcher;
    private bool _started;
    private bool _watchersHealthy = true;
    private string _lastCandidateSummary = string.Empty;

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
        _activePollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = ActivePollInterval
        };
        _activePollTimer.Tick += OnActivePollTick;
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
        InitializeWatchers();
        DiscoverSessionFiles();
        _clockTimer.Start();
        _activePollTimer.Start();
        PublishCurrentTask();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _clockTimer.Stop();
        _activePollTimer.Stop();
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
        _activePollTimer.Tick -= OnActivePollTick;
        _activePollTimer.Stop();
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

        foreach (var path in EnumerateBootstrapSessionFiles())
        {
            EnsureTracker(path, readFromStart: true);
        }
    }

    private IReadOnlyList<string> EnumerateBootstrapSessionFiles()
    {
        var cutoff = DateTime.UtcNow - ActiveSessionWindow;
        var recentFiles = Directory.EnumerateFiles(_sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        var bootstrapFiles = recentFiles
            .Where(file => file.LastWriteTimeUtc >= cutoff)
            .Take(8)
            .Select(file => file.FullName)
            .ToList();

        if (bootstrapFiles.Count > 0)
        {
            DiagnosticsLogger.Write($"Bootstrapping {bootstrapFiles.Count} recent session file(s).");
            return bootstrapFiles;
        }

        var latestFile = recentFiles.FirstOrDefault()?.FullName;
        if (latestFile is not null)
        {
            DiagnosticsLogger.Write("Bootstrapping the latest session file only.");
            return [latestFile];
        }

        return Array.Empty<string>();
    }

    private void HandleSessionFileEvent(string path)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            DiagnosticsLogger.Write($"Session file event: {path}");
            EnsureTracker(path, readFromStart: false);
            PublishCurrentTask();
        });
    }

    private void HandleSessionIndexEvent()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            DiagnosticsLogger.Write("Session index event received.");
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
            DiagnosticsLogger.Write($"Bootstrapping tracker for session {tracker.SessionId} from start.");
            ReadAppendedContent(tracker, resetOffset: true);
            return;
        }

        ReadAppendedContent(tracker, resetOffset: false);
    }

    private void ReadAppendedContent(SessionTracker tracker, bool resetOffset)
    {
        try
        {
            var appliedLines = 0;
            var ignoredLines = 0;
            var beforeTask = tracker.StateMachine.BuildTask();

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
                    ignoredLines++;
                    DiagnosticsLogger.Write($"Ignored malformed JSONL line for session {tracker.SessionId}: {error}");
                    continue;
                }

                appliedLines++;
            }

            var afterTask = tracker.StateMachine.BuildTask();
            if (appliedLines > 0 || ignoredLines > 0 || beforeTask.Status != afterTask.Status || beforeTask.Message != afterTask.Message)
            {
                DiagnosticsLogger.Write(
                    $"Session {tracker.SessionId} read result: applied={appliedLines}, ignored={ignoredLines}, " +
                    $"status {beforeTask.Status}->{afterTask.Status}, updatedAt={afterTask.UpdatedAt:O}, message={TruncateForLog(afterTask.Message)}");
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

    private void OnActivePollTick(object? sender, EventArgs e)
    {
        var pollCandidates = GetActivePollCandidates();
        if (pollCandidates.Count == 0)
        {
            return;
        }

        foreach (var tracker in pollCandidates)
        {
            ReadAppendedContent(tracker, resetOffset: false);
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
        DiagnosticsLogger.Write(
            $"PublishCurrentTask -> status={nextTask.Status}, session={nextTask.SessionId ?? "none"}, " +
            $"updatedAt={nextTask.UpdatedAt:O}, title={TruncateForLog(nextTask.Title)}, message={TruncateForLog(nextTask.Message)}");
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
            .ToList();

        if (candidates.Count > 0)
        {
            var summary = string.Join(
                " | ",
                candidates.Take(5).Select(task => $"{task.SessionId}:{task.Status}@{task.UpdatedAt:HH:mm:ss.fff}"));
            if (!string.Equals(_lastCandidateSummary, summary, StringComparison.Ordinal))
            {
                _lastCandidateSummary = summary;
                DiagnosticsLogger.Write($"BuildCurrentTask candidates: {summary}");
            }
        }
        else
        {
            _lastCandidateSummary = string.Empty;
        }

        if (candidates.Count == 0)
        {
            return new CodexTask(
                CodexSessionStatus.Idle,
                "Codex CLI",
                "Waiting for an active Codex CLI session.",
                Array.Empty<string>(),
                now);
        }

        var activeCandidates = candidates
            .Where(task => IsActiveStatus(task.Status))
            .OrderByDescending(task => task.UpdatedAt)
            .ToList();

        if (activeCandidates.Count > 0)
        {
            return activeCandidates[0];
        }

        return candidates
            .OrderByDescending(task => task.UpdatedAt)
            .ThenBy(task => GetPriority(task.Status))
            .First();
    }

    private IReadOnlyList<SessionTracker> GetActivePollCandidates()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            return _trackers.Values
                .Select(tracker => new
                {
                    Tracker = tracker,
                    Task = tracker.StateMachine.BuildTask()
                })
                .Where(entry =>
                    entry.Task.Status is CodexSessionStatus.Processing or CodexSessionStatus.RunningTool or CodexSessionStatus.Finishing
                    && now - entry.Task.UpdatedAt <= ActiveSessionWindow)
                .OrderByDescending(entry => entry.Task.UpdatedAt)
                .Take(2)
                .Select(entry => entry.Tracker)
                .ToList();
        }
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

    private static bool IsActiveStatus(CodexSessionStatus status)
    {
        return status is CodexSessionStatus.Processing or CodexSessionStatus.RunningTool or CodexSessionStatus.Finishing;
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

    private static string TruncateForLog(string value)
    {
        const int maxLength = 120;
        var singleLine = value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ');
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength] + "...";
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
