using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using DynamicIsland.Models;
using DynamicIsland.Utils;

namespace DynamicIsland.Services;

internal readonly record struct CodexCliRetentionCandidate(
    string SessionId,
    CodexCliDerivedStatus DerivedStatus,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastObservedAt,
    DateTimeOffset LastPublishedAt,
    DateTimeOffset LastFileWriteAtUtc,
    bool IsSelected,
    bool IsActivePoll);

internal readonly record struct CodexCliThreadNameCandidate(
    string SessionId,
    DateTimeOffset LastAccessedAt,
    bool IsTracked);

public sealed class CodexCliStatusService : ICodexStatusService
{
    private const int MaxRetainedTrackers = 4;
    private const int MaxRetainedThreadNames = 16;

    private static readonly Regex SessionIdRegex = new(@"(?<id>[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly TimeSpan ActiveSessionWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan SessionDiscoveryInterval = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _threadNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _threadNameLastAccess = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _codexRoot;
    private readonly string _sessionsRoot;
    private readonly string _sessionIndexPath;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _activePollTimer;
    private readonly bool _debugMode;

    private FileSystemWatcher? _sessionsWatcher;
    private FileSystemWatcher? _sessionIndexWatcher;
    private bool _started;
    private bool _watchersHealthy = true;
    private string _lastCandidateSummary = string.Empty;
    private DateTimeOffset _lastSessionDiscoveryAt = DateTimeOffset.MinValue;
    private string? _lastSelectedSessionId;

    public CodexCliStatusService()
    {
        _codexRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        _sessionsRoot = Path.Combine(_codexRoot, "sessions");
        _sessionIndexPath = Path.Combine(_codexRoot, "session_index.jsonl");
        _debugMode = AppRuntimeOptions.ResolveDebugMode();
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
        TrimTrackers(Array.Empty<string>(), DateTimeOffset.UtcNow);
        _lastSessionDiscoveryAt = DateTimeOffset.UtcNow;
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
        DiagnosticsLogger.WriteVerbose($"CodexCliStatusService ignored UI action '{actionId}'.");
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

    internal static IReadOnlyList<string> SelectRetainedSessionIdsForTest(IEnumerable<CodexCliRetentionCandidate> candidates, int maxRetained = MaxRetainedTrackers)
    {
        var candidateList = candidates.ToList();
        var retained = new List<string>();

        foreach (var protectedCandidate in candidateList
                     .Where(candidate => candidate.IsSelected || candidate.IsActivePoll)
                     .OrderByDescending(candidate => candidate.IsSelected)
                     .ThenByDescending(candidate => candidate.IsActivePoll)
                     .ThenByDescending(candidate => candidate.UpdatedAt))
        {
            if (!retained.Contains(protectedCandidate.SessionId, StringComparer.OrdinalIgnoreCase))
            {
                retained.Add(protectedCandidate.SessionId);
            }
        }

        foreach (var candidate in candidateList
                     .OrderByDescending(candidate => IsActiveStatus(candidate.DerivedStatus))
                     .ThenByDescending(candidate => candidate.UpdatedAt)
                     .ThenByDescending(candidate => candidate.LastFileWriteAtUtc)
                     .ThenByDescending(candidate => candidate.LastObservedAt)
                     .ThenByDescending(candidate => candidate.LastPublishedAt))
        {
            if (retained.Count >= maxRetained)
            {
                break;
            }

            if (!retained.Contains(candidate.SessionId, StringComparer.OrdinalIgnoreCase))
            {
                retained.Add(candidate.SessionId);
            }
        }

        return retained;
    }

    internal static IReadOnlyList<string> SelectRetainedThreadNamesForTest(IEnumerable<CodexCliThreadNameCandidate> candidates, int maxRetained = MaxRetainedThreadNames)
    {
        var retained = new List<string>();
        foreach (var candidate in candidates.Where(candidate => candidate.IsTracked).OrderByDescending(candidate => candidate.LastAccessedAt))
        {
            if (!retained.Contains(candidate.SessionId, StringComparer.OrdinalIgnoreCase))
            {
                retained.Add(candidate.SessionId);
            }
        }

        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.LastAccessedAt))
        {
            if (retained.Count >= maxRetained)
            {
                break;
            }

            if (!retained.Contains(candidate.SessionId, StringComparer.OrdinalIgnoreCase))
            {
                retained.Add(candidate.SessionId);
            }
        }

        return retained;
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
            DiagnosticsLogger.WriteVerbose($"Bootstrapping {bootstrapFiles.Count} recent session file(s).");
            return bootstrapFiles;
        }

        var latestFile = recentFiles.FirstOrDefault()?.FullName;
        if (latestFile is not null)
        {
            DiagnosticsLogger.WriteVerbose("Bootstrapping the latest session file only.");
            return [latestFile];
        }

        return Array.Empty<string>();
    }

    private void HandleSessionFileEvent(string path)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            DiagnosticsLogger.WriteVerbose($"Session file event: {path}");
            EnsureTracker(path, readFromStart: false);
            PublishCurrentTask();
        });
    }

    private void HandleSessionIndexEvent()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            DiagnosticsLogger.WriteVerbose("Session index event received.");
            LoadSessionIndex();
            PublishCurrentTask();
        });
    }

    private void HandleWatcherFailure(string message)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            DiagnosticsLogger.WriteError(message);
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
            var now = DateTimeOffset.UtcNow;
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
                    TouchThreadName(sessionId, now);
                }
            }

            foreach (var tracker in _trackers.Values)
            {
                if (_threadNames.TryGetValue(tracker.SessionId, out var threadName))
                {
                    tracker.StateMachine.SetThreadName(threadName);
                    TouchThreadName(tracker.SessionId, now);
                }
            }

            TrimThreadNames();
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.WriteError($"Failed to read session index: {ex.Message}");
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
            DiagnosticsLogger.WriteVerbose($"Skipped session file with unreadable session id: {path}");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        SessionTracker tracker;
        lock (_gate)
        {
            if (!_trackers.TryGetValue(sessionId, out tracker!))
            {
                tracker = new SessionTracker(sessionId, path);
                _trackers[sessionId] = tracker;
            }
            else
            {
                tracker.FilePath = path;
            }
        }

        tracker.LastObservedAt = now;
        tracker.LastFileWriteAtUtc = TryGetFileWriteTimeUtc(path, now);

        if (!_threadNames.TryGetValue(sessionId, out var threadName))
        {
            threadName = TryLoadThreadName(sessionId);
        }

        if (!string.IsNullOrWhiteSpace(threadName))
        {
            tracker.StateMachine.SetThreadName(threadName);
            TouchThreadName(sessionId, now);
        }

        if (readFromStart && tracker.LastOffset == 0)
        {
            DiagnosticsLogger.WriteVerbose($"Bootstrapping tracker for session {tracker.SessionId} from start.");
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
            var beforeInfo = tracker.StateMachine.GetStatusInfo(DateTimeOffset.UtcNow);

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
            tracker.LastObservedAt = DateTimeOffset.UtcNow;
            tracker.LastFileWriteAtUtc = TryGetFileWriteTimeUtc(tracker.FilePath, tracker.LastObservedAt);

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
                    DiagnosticsLogger.WriteVerbose($"Ignored malformed JSONL line for session {tracker.SessionId}: {error}");
                    continue;
                }

                appliedLines++;
            }

            var afterSnapshot = tracker.StateMachine.BuildSnapshot(DateTimeOffset.UtcNow);
            if (appliedLines > 0
                || ignoredLines > 0
                || beforeInfo.DerivedStatus != afterSnapshot.DerivedStatus
                || beforeInfo.UpdatedAt != afterSnapshot.Task.UpdatedAt)
            {
                DiagnosticsLogger.WriteVerbose(
                    $"Session {tracker.SessionId} read result: applied={appliedLines}, ignored={ignoredLines}, " +
                    $"status {beforeInfo.DerivedStatus}->{afterSnapshot.DerivedStatus}, updatedAt={afterSnapshot.Task.UpdatedAt:O}, message={TruncateForLog(afterSnapshot.Task.Message)}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.WriteError($"Failed to read session file '{tracker.FilePath}': {ex.Message}");
            tracker.StateMachine.MarkUnknown("Unable to read the Codex CLI session file.", DateTimeOffset.UtcNow);
        }
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        TryDiscoverRecentSessionFiles();

        foreach (var tracker in _trackers.Values)
        {
            tracker.StateMachine.AdvanceClock(DateTimeOffset.UtcNow);
        }

        PublishCurrentTask();
        TrimTrackers(GetActivePollCandidateIds(DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
    }

    private void TryDiscoverRecentSessionFiles()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSessionDiscoveryAt < SessionDiscoveryInterval)
        {
            return;
        }

        _lastSessionDiscoveryAt = now;

        if (!Directory.Exists(_sessionsRoot))
        {
            return;
        }

        var discoveredCount = 0;
        foreach (var path in EnumerateBootstrapSessionFiles())
        {
            var sessionId = TryParseSessionId(path);
            if (sessionId is null)
            {
                continue;
            }

            lock (_gate)
            {
                if (_trackers.ContainsKey(sessionId))
                {
                    continue;
                }
            }

            DiagnosticsLogger.WriteVerbose($"Periodic session discovery picked up session file: {path}");
            EnsureTracker(path, readFromStart: true);
            discoveredCount++;
        }

        if (discoveredCount > 0)
        {
            DiagnosticsLogger.WriteVerbose($"Periodic session discovery added {discoveredCount} tracker(s).");
        }

        TrimTrackers(Array.Empty<string>(), now);
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
            tracker.LastObservedAt = DateTimeOffset.UtcNow;
            ReadAppendedContent(tracker, resetOffset: false);
        }

        PublishCurrentTask();
    }

    private void PublishCurrentTask(bool force = false)
    {
        var selection = BuildCurrentSelection();
        var nextTask = selection.Task;
        if (!force && AreEquivalent(CurrentTask, nextTask))
        {
            return;
        }

        CurrentTask = nextTask;
        if (selection.Tracker is not null)
        {
            selection.Tracker.LastPublishedAt = DateTimeOffset.UtcNow;
        }

        DiagnosticsLogger.WriteVerbose(
            $"PublishCurrentTask -> status={nextTask.Status}, session={nextTask.SessionId ?? "none"}, " +
            $"updatedAt={nextTask.UpdatedAt:O}, title={TruncateForLog(nextTask.Title)}, message={TruncateForLog(nextTask.Message)}");
        TaskUpdated?.Invoke(this, nextTask);
    }

    private CurrentTaskSelection BuildCurrentSelection()
    {
        if (!_watchersHealthy)
        {
            _lastSelectedSessionId = null;
            return new CurrentTaskSelection(
                null,
                new CodexTask(
                    CodexSessionStatus.Unknown,
                    "Codex CLI",
                    "The Codex CLI file watcher failed. Restart the island to recover live updates.",
                    Array.Empty<string>(),
                    DateTimeOffset.UtcNow,
                    DebugSource: _debugMode ? "Source: FileSystemWatcher error" : null));
        }

        var now = DateTimeOffset.UtcNow;
        var candidates = GetCandidateStates(now);

        if (candidates.Count > 0)
        {
            var summary = string.Join(
                " | ",
                candidates.Take(5).Select(entry => $"{entry.Tracker.SessionId}:{entry.Info.DerivedStatus}@{entry.Info.UpdatedAt:HH:mm:ss.fff}"));
            if (!string.Equals(_lastCandidateSummary, summary, StringComparison.Ordinal))
            {
                _lastCandidateSummary = summary;
                DiagnosticsLogger.WriteVerbose($"BuildCurrentTask candidates: {summary}");
            }
        }
        else
        {
            _lastCandidateSummary = string.Empty;
        }

        if (candidates.Count == 0)
        {
            _lastSelectedSessionId = null;
            return new CurrentTaskSelection(
                null,
                new CodexTask(
                    CodexSessionStatus.Idle,
                    "Codex CLI",
                    "Waiting for an active Codex CLI session.",
                    Array.Empty<string>(),
                    now,
                    DebugSource: _debugMode ? $"Source root: {_sessionsRoot}" : null));
        }

        var activeCandidate = candidates
            .Where(entry => IsActiveStatus(entry.Info.DerivedStatus))
            .OrderBy(entry => GetActivePriority(entry.Info.DerivedStatus))
            .ThenByDescending(entry => entry.Info.UpdatedAt)
            .FirstOrDefault();

        if (activeCandidate.Tracker is not null)
        {
            _lastSelectedSessionId = activeCandidate.Tracker.SessionId;
            activeCandidate.Tracker.LastObservedAt = now;
            return new CurrentTaskSelection(activeCandidate.Tracker, AttachDebugSource(activeCandidate.Tracker, activeCandidate.Tracker.StateMachine.BuildTask()));
        }

        var selected = candidates
            .OrderByDescending(entry => entry.Info.UpdatedAt)
            .ThenBy(entry => GetPriority(entry.Info.DerivedStatus))
            .First();
        _lastSelectedSessionId = selected.Tracker.SessionId;
        selected.Tracker.LastObservedAt = now;
        return new CurrentTaskSelection(selected.Tracker, AttachDebugSource(selected.Tracker, selected.Tracker.StateMachine.BuildTask()));
    }

    private IReadOnlyList<CandidateState> GetCandidateStates(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _trackers.Values
                .Select(tracker => new CandidateState(tracker, tracker.StateMachine.GetStatusInfo(now)))
                .Where(entry => entry.Info.PublicStatus != CodexSessionStatus.Idle && now - entry.Info.UpdatedAt <= ActiveSessionWindow)
                .ToList();
        }
    }

    private IReadOnlyList<SessionTracker> GetActivePollCandidates()
    {
        var now = DateTimeOffset.UtcNow;
        return GetCandidateStates(now)
            .Where(entry => IsActiveStatus(entry.Info.DerivedStatus))
            .OrderBy(entry => GetActivePriority(entry.Info.DerivedStatus))
            .ThenByDescending(entry => entry.Info.UpdatedAt)
            .Take(2)
            .Select(entry => entry.Tracker)
            .ToList();
    }

    private IReadOnlyList<string> GetActivePollCandidateIds(DateTimeOffset now)
    {
        return GetCandidateStates(now)
            .Where(entry => IsActiveStatus(entry.Info.DerivedStatus))
            .OrderBy(entry => GetActivePriority(entry.Info.DerivedStatus))
            .ThenByDescending(entry => entry.Info.UpdatedAt)
            .Take(2)
            .Select(entry => entry.Tracker.SessionId)
            .ToList();
    }

    private void TrimTrackers(IReadOnlyCollection<string> activePollSessionIds, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_trackers.Count <= MaxRetainedTrackers)
            {
                return;
            }

            var retentionCandidates = _trackers.Values
                .Select(tracker =>
                {
                    var info = tracker.StateMachine.GetStatusInfo(now);
                    return new CodexCliRetentionCandidate(
                        tracker.SessionId,
                        info.DerivedStatus,
                        info.UpdatedAt,
                        tracker.LastObservedAt,
                        tracker.LastPublishedAt,
                        tracker.LastFileWriteAtUtc,
                        string.Equals(tracker.SessionId, _lastSelectedSessionId, StringComparison.OrdinalIgnoreCase),
                        activePollSessionIds.Contains(tracker.SessionId, StringComparer.OrdinalIgnoreCase));
                })
                .ToList();

            var retainedIds = SelectRetainedSessionIdsForTest(retentionCandidates);
            foreach (var sessionId in _trackers.Keys.ToList())
            {
                if (retainedIds.Contains(sessionId, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                _trackers.Remove(sessionId);
            }
        }

        TrimThreadNames();
    }

    private void TrimThreadNames()
    {
        lock (_gate)
        {
            if (_threadNames.Count <= MaxRetainedThreadNames)
            {
                return;
            }

            var retainedIds = SelectRetainedThreadNamesForTest(
                _threadNames.Keys.Select(sessionId => new CodexCliThreadNameCandidate(
                    sessionId,
                    _threadNameLastAccess.TryGetValue(sessionId, out var lastAccess) ? lastAccess : DateTimeOffset.MinValue,
                    _trackers.ContainsKey(sessionId))));

            foreach (var sessionId in _threadNames.Keys.ToList())
            {
                if (retainedIds.Contains(sessionId, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                _threadNames.Remove(sessionId);
                _threadNameLastAccess.Remove(sessionId);
            }
        }
    }

    private void TouchThreadName(string sessionId, DateTimeOffset timestamp)
    {
        _threadNameLastAccess[sessionId] = timestamp;
    }

    private string? TryLoadThreadName(string sessionId)
    {
        if (!File.Exists(_sessionIndexPath))
        {
            return null;
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
                if (!document.RootElement.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String
                    || !string.Equals(idElement.GetString(), sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var threadName = document.RootElement.TryGetProperty("thread_name", out var threadNameElement)
                    && threadNameElement.ValueKind == JsonValueKind.String
                    ? threadNameElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(threadName))
                {
                    return null;
                }

                _threadNames[sessionId] = threadName!;
                TouchThreadName(sessionId, DateTimeOffset.UtcNow);
                TrimThreadNames();
                return threadName;
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.WriteError($"Failed to reload thread name for session '{sessionId}': {ex.Message}");
        }

        return null;
    }

    private CodexTask AttachDebugSource(SessionTracker tracker, CodexTask task)
    {
        if (!_debugMode)
        {
            return task;
        }

        var path = tracker.FilePath;
        var debugSource = string.IsNullOrWhiteSpace(task.DebugSource)
            ? $"File: {path}"
            : $"{task.DebugSource}{Environment.NewLine}File: {path}";

        return task with { DebugSource = debugSource };
    }

    private static int GetPriority(CodexCliDerivedStatus status)
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

    private static int GetActivePriority(CodexCliDerivedStatus status)
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

    private static bool IsActiveStatus(CodexCliDerivedStatus status)
    {
        return status is
            CodexCliDerivedStatus.Processing or
            CodexCliDerivedStatus.ThinkingSuspected or
            CodexCliDerivedStatus.RunningTool or
            CodexCliDerivedStatus.RunningToolLong or
            CodexCliDerivedStatus.Finishing;
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
            && left.AvailableActions.SequenceEqual(right.AvailableActions)
            && (left.ChangedFiles ?? Array.Empty<string>()).SequenceEqual(right.ChangedFiles ?? Array.Empty<string>())
            && left.DebugSource == right.DebugSource;
    }

    private static string? TryParseSessionId(string path)
    {
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        var match = SessionIdRegex.Match(fileNameWithoutExtension);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static DateTimeOffset TryGetFileWriteTimeUtc(string path, DateTimeOffset fallback)
    {
        try
        {
            return File.Exists(path)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
                : fallback;
        }
        catch
        {
            return fallback;
        }
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
            LastObservedAt = DateTimeOffset.UtcNow;
            LastPublishedAt = DateTimeOffset.MinValue;
            LastFileWriteAtUtc = DateTimeOffset.MinValue;
        }

        public string SessionId { get; }

        public string FilePath { get; set; }

        public CodexCliSessionStateMachine StateMachine { get; }

        public long LastOffset { get; set; }

        public string PendingLine { get; set; } = string.Empty;

        public DateTimeOffset LastObservedAt { get; set; }

        public DateTimeOffset LastPublishedAt { get; set; }

        public DateTimeOffset LastFileWriteAtUtc { get; set; }
    }

    private readonly record struct CandidateState(SessionTracker Tracker, CodexCliSessionStatusInfo Info);

    private readonly record struct CurrentTaskSelection(SessionTracker? Tracker, CodexTask Task);
}
