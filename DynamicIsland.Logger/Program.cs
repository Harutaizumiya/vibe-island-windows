using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicIsland.Models;
using DynamicIsland.Services;

var runner = new SessionLogRunner();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    runner.RequestStop();
};

await runner.RunAsync();

internal sealed class SessionLogRunner
{
    private static readonly Regex SessionIdRegex = new(@"(?<id>[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly TimeSpan ActiveSessionWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly string _codexRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    private readonly string _sessionIndexPath;
    private readonly string _sessionsRoot;
    private readonly string _logDirectory;
    private readonly string _sessionLogPath;
    private readonly string _islandLogPath;
    private readonly Dictionary<string, SessionTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _threadNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly PeriodicTimer _timer = new(TickInterval);
    private readonly FileSystemWatcher? _sessionWatcher;
    private readonly FileSystemWatcher? _sessionIndexWatcher;

    private volatile bool _stopRequested;
    private CodexTask? _currentTask;

    public SessionLogRunner()
    {
        _sessionsRoot = Path.Combine(_codexRoot, "sessions");
        _sessionIndexPath = Path.Combine(_codexRoot, "session_index.jsonl");
        _logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        _sessionLogPath = Path.Combine(_logDirectory, "session-details.log");
        _islandLogPath = Path.Combine(_logDirectory, "island-status.log");

        Directory.CreateDirectory(_logDirectory);

        if (Directory.Exists(_sessionsRoot))
        {
            _sessionWatcher = new FileSystemWatcher(_sessionsRoot, "rollout-*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
            };
            _sessionWatcher.Created += (_, e) => HandleSessionFileEvent(e.FullPath);
            _sessionWatcher.Changed += (_, e) => HandleSessionFileEvent(e.FullPath);
            _sessionWatcher.Renamed += (_, e) => HandleSessionFileEvent(e.FullPath);
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
        }
    }

    public async Task RunAsync()
    {
        WriteSessionLog("Logger starting.");
        LoadSessionIndex();
        BootstrapTrackers();

        if (_sessionWatcher is not null)
        {
            _sessionWatcher.EnableRaisingEvents = true;
        }

        if (_sessionIndexWatcher is not null)
        {
            _sessionIndexWatcher.EnableRaisingEvents = true;
        }

        PublishCurrentTask(force: true);
        WriteSessionLog($"Watching session data under '{_codexRoot}'. Press Ctrl+C to stop.");

        try
        {
            while (!_stopRequested && await _timer.WaitForNextTickAsync())
            {
                Tick();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _sessionWatcher?.Dispose();
            _sessionIndexWatcher?.Dispose();
            _timer.Dispose();
            WriteSessionLog("Logger stopped.");
        }
    }

    public void RequestStop()
    {
        _stopRequested = true;
    }

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            foreach (var tracker in _trackers.Values)
            {
                var before = tracker.StateMachine.BuildSnapshot(now);
                var changed = tracker.StateMachine.AdvanceClock(now);
                var after = tracker.StateMachine.BuildSnapshot(now);
                if (changed)
                {
                    WriteSessionLog(
                        $"clock session={tracker.SessionId} derived {before.DerivedStatus}->{after.DerivedStatus} public {before.Task.Status}->{after.Task.Status} message={Sanitize(after.Task.Message)}");
                }
            }
        }

        PublishCurrentTask();
    }

    private void HandleSessionFileEvent(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        lock (_gate)
        {
            WriteSessionLog($"session-file-event path={path}");
            EnsureTracker(path, readFromStart: false);
            PublishCurrentTask();
        }
    }

    private void HandleSessionIndexEvent()
    {
        lock (_gate)
        {
            WriteSessionLog("session-index-event");
            LoadSessionIndex();
            PublishCurrentTask();
        }
    }

    private void BootstrapTrackers()
    {
        if (!Directory.Exists(_sessionsRoot))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - ActiveSessionWindow;
        var bootstrapFiles = Directory.EnumerateFiles(_sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && file.LastWriteTimeUtc >= cutoff)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(8)
            .Select(file => file.FullName)
            .ToList();

        WriteSessionLog($"bootstrap count={bootstrapFiles.Count}");
        foreach (var path in bootstrapFiles)
        {
            EnsureTracker(path, readFromStart: true);
        }
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
            WriteSessionLog($"session-index-error message={Sanitize(ex.Message)}");
        }
    }

    private void EnsureTracker(string path, bool readFromStart)
    {
        var sessionId = TryParseSessionId(path);
        if (sessionId is null)
        {
            WriteSessionLog($"skip-unreadable-session path={path}");
            return;
        }

        if (!_trackers.TryGetValue(sessionId, out var tracker))
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

        ReadAppendedContent(tracker, resetOffset: readFromStart && tracker.LastOffset == 0);
    }

    private void ReadAppendedContent(SessionTracker tracker, bool resetOffset)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var before = tracker.StateMachine.BuildSnapshot(now);
            var appliedLines = 0;
            var ignoredLines = 0;

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
                    WriteSessionLog($"session={tracker.SessionId} malformed-json error={Sanitize(error)}");
                    continue;
                }

                appliedLines++;
            }

            var after = tracker.StateMachine.BuildSnapshot(DateTimeOffset.UtcNow);
            WriteSessionLog(
                $"session={tracker.SessionId} read applied={appliedLines} ignored={ignoredLines} derived {before.DerivedStatus}->{after.DerivedStatus} public {before.Task.Status}->{after.Task.Status} updatedAt={after.Task.UpdatedAt:O} title={Sanitize(after.Task.Title)} message={Sanitize(after.Task.Message)}");
        }
        catch (Exception ex)
        {
            tracker.StateMachine.MarkUnknown("Unable to read the Codex CLI session file.", DateTimeOffset.UtcNow);
            WriteSessionLog($"session={tracker.SessionId} read-error path={tracker.FilePath} message={Sanitize(ex.Message)}");
        }
    }

    private void PublishCurrentTask(bool force = false)
    {
        var next = BuildCurrentTask();
        if (!force && AreEquivalent(_currentTask, next))
        {
            return;
        }

        _currentTask = next;
        WriteIslandLog(
            $"status={next.Status} session={next.SessionId ?? "none"} updatedAt={next.UpdatedAt:O} title={Sanitize(next.Title)} message={Sanitize(next.Message)} changedFiles={FormatChangedFiles(next.ChangedFiles)}");
    }

    private CodexTask BuildCurrentTask()
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = _trackers.Values
            .Select(tracker => tracker.StateMachine.BuildSnapshot(now))
            .Where(snapshot => snapshot.Task.Status != CodexSessionStatus.Idle && now - snapshot.Task.UpdatedAt <= ActiveSessionWindow)
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

        var activeCandidates = candidates
            .Where(snapshot => IsActiveStatus(snapshot.DerivedStatus))
            .OrderBy(snapshot => GetActivePriority(snapshot.DerivedStatus))
            .ThenByDescending(snapshot => snapshot.Task.UpdatedAt)
            .ToList();

        if (activeCandidates.Count > 0)
        {
            return activeCandidates[0].Task;
        }

        return candidates
            .OrderByDescending(snapshot => snapshot.Task.UpdatedAt)
            .ThenBy(snapshot => GetInactivePriority(snapshot.DerivedStatus))
            .Select(snapshot => snapshot.Task)
            .First();
    }

    private void WriteSessionLog(string message)
    {
        WriteLine(_sessionLogPath, message);
    }

    private void WriteIslandLog(string message)
    {
        WriteLine(_islandLogPath, message);
    }

    private static void WriteLine(string path, string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
        File.AppendAllText(path, line);
        Console.Write(line);
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

    private static int GetInactivePriority(CodexCliDerivedStatus status)
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
            && (left.ChangedFiles ?? Array.Empty<string>()).SequenceEqual(right.ChangedFiles ?? Array.Empty<string>());
    }

    private static string? TryParseSessionId(string path)
    {
        var match = SessionIdRegex.Match(Path.GetFileNameWithoutExtension(path));
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
    }

    private static string FormatChangedFiles(IReadOnlyList<string>? files)
    {
        if (files is null || files.Count == 0)
        {
            return "-";
        }

        return string.Join(",", files);
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
