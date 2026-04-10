using DynamicIsland.Models;
using DynamicIsland.Services;
using DynamicIsland.UI;
using DynamicIsland.Utils;
using System.Windows.Threading;

namespace DynamicIsland.ViewModels;

public sealed class StatusViewModel : ObservableObject, IDisposable
{
    private static readonly HashSet<string> DebugOnlyExpandedMessages = new(StringComparer.Ordinal)
    {
        "Codex is reasoning about the current turn.",
        "Codex is processing the current turn.",
        "Codex CLI is processing the current turn.",
        "Codex CLI is preparing the final answer.",
        "Running a Codex CLI tool.",
        "No new events; task may be stalled.",
        "The current turn was interrupted.",
        "The live watcher could not determine the current session state.",
        "Waiting for an active Codex CLI session."
    };

    private readonly ICodexStatusService _service;
    private readonly IslandLayoutSettings _layoutSettings;
    private readonly DispatcherTimer _glyphTimer;
    private readonly DispatcherTimer _approvalFeedbackTimer;
    private readonly DispatcherTimer _completedAutoCollapseTimer;
    private readonly DispatcherTimer _focusLossAutoCollapseTimer;
    private readonly AppRuntimeOptions _runtimeOptions;
    private readonly bool _isDebugMode;
    private readonly string[] _workingGlyphFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private const string ApprovalGlyph = "✓";

    private CodexSessionStatus _currentStatus = CodexSessionStatus.Idle;
    private string _statusText = "Booting";
    private string _statusMessage = "Preparing the island services.";
    private string _compactStatusMessage = "Preparing the island services.";
    private string _compactStatusText = "启动中";
    private string _statusGlyph = "o";
    private string _activityBadgeText = "BOOT";
    private bool _isExpanded;
    private bool _isBusy;
    private bool _isActionRequired;
    private bool _isBouncing;
    private bool _isManualExpanded;
    private bool _isHoverExpanded;
    private double _collapsedWidth;
    private string _primaryActionText = string.Empty;
    private string _secondaryActionText = string.Empty;
    private string _tertiaryActionText = string.Empty;
    private string _panelHintText = "Waiting for the next actionable state.";
    private IReadOnlyList<string> _changedFiles = Array.Empty<string>();
    private string _expandedSectionTitle = "STATUS DETAILS";
    private string _expandedDetailText = "Waiting for the next actionable state.";
    private bool _isPrimaryActionVisible;
    private bool _isSecondaryActionVisible;
    private bool _isTertiaryActionVisible;
    private bool _isApprovalFeedbackVisible;
    private int _glyphFrameIndex;
    private string _debugStatusText = "Idle";
    private string _debugSourceText = "Debug mode disabled.";

    public StatusViewModel(ICodexStatusService service, IslandLayoutSettings layoutSettings)
    {
        _service = service;
        _layoutSettings = layoutSettings;
        _runtimeOptions = AppRuntimeOptions.Current;
        _isDebugMode = AppRuntimeOptions.ResolveDebugMode();
        _service.TaskUpdated += OnTaskUpdated;
        _layoutSettings.PropertyChanged += OnLayoutSettingsPropertyChanged;
        _glyphTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _glyphTimer.Tick += OnGlyphTimerTick;
        _approvalFeedbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1600)
        };
        _approvalFeedbackTimer.Tick += OnApprovalFeedbackTimerTick;
        _completedAutoCollapseTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _completedAutoCollapseTimer.Tick += OnCompletedAutoCollapseTimerTick;
        _focusLossAutoCollapseTimer = new DispatcherTimer(DispatcherPriority.Background);
        _focusLossAutoCollapseTimer.Tick += OnFocusLossAutoCollapseTimerTick;
        _collapsedWidth = _layoutSettings.GetCollapsedWidth(_currentStatus);

        ToggleExpandCommand = new RelayCommand(ToggleExpand);
        PrimaryActionCommand = new AsyncRelayCommand(ExecutePrimaryActionAsync, () => IsPrimaryActionVisible);
        SecondaryActionCommand = new AsyncRelayCommand(ExecuteSecondaryActionAsync, () => IsSecondaryActionVisible);
        TertiaryActionCommand = new AsyncRelayCommand(ExecuteTertiaryActionAsync, () => IsTertiaryActionVisible);
    }

    public bool IsDebugMode => _isDebugMode;

    public bool ExpandOnHover => _runtimeOptions.ExpandOnHover;

    public bool IsIdleCodexIconVisible => CurrentStatus == CodexSessionStatus.Idle && !IsApprovalFeedbackVisible;

    public CodexSessionStatus CurrentStatus
    {
        get => _currentStatus;
        private set
        {
            if (SetProperty(ref _currentStatus, value))
            {
                OnPropertyChanged(nameof(IsIdleCodexIconVisible));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string CompactStatusMessage
    {
        get => _compactStatusMessage;
        private set => SetProperty(ref _compactStatusMessage, value);
    }

    public string CompactStatusText
    {
        get => _compactStatusText;
        private set => SetProperty(ref _compactStatusText, value);
    }

    public string StatusGlyph
    {
        get => _statusGlyph;
        private set => SetProperty(ref _statusGlyph, value);
    }

    public string ActivityBadgeText
    {
        get => _activityBadgeText;
        private set => SetProperty(ref _activityBadgeText, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        private set => SetExpanded(value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                UpdateGlyphAnimation();
            }
        }
    }

    public bool IsActionRequired
    {
        get => _isActionRequired;
        private set
        {
            if (SetProperty(ref _isActionRequired, value))
            {
                OnPropertyChanged(nameof(ExpansionHint));
            }
        }
    }

    public bool IsBouncing
    {
        get => _isBouncing;
        private set => SetProperty(ref _isBouncing, value);
    }

    public double CollapsedWidth
    {
        get => _collapsedWidth;
        private set => SetProperty(ref _collapsedWidth, value);
    }

    public string PrimaryActionText
    {
        get => _primaryActionText;
        private set => SetProperty(ref _primaryActionText, value);
    }

    public string SecondaryActionText
    {
        get => _secondaryActionText;
        private set => SetProperty(ref _secondaryActionText, value);
    }

    public string TertiaryActionText
    {
        get => _tertiaryActionText;
        private set => SetProperty(ref _tertiaryActionText, value);
    }

    public string PanelHintText
    {
        get => _panelHintText;
        private set => SetProperty(ref _panelHintText, value);
    }

    public IReadOnlyList<string> ChangedFiles
    {
        get => _changedFiles;
        private set => SetProperty(ref _changedFiles, value);
    }

    public string ExpandedSectionTitle
    {
        get => _expandedSectionTitle;
        private set => SetProperty(ref _expandedSectionTitle, value);
    }

    public string ExpandedDetailText
    {
        get => _expandedDetailText;
        private set => SetProperty(ref _expandedDetailText, value);
    }

    public bool IsPrimaryActionVisible
    {
        get => _isPrimaryActionVisible;
        private set => SetProperty(ref _isPrimaryActionVisible, value);
    }

    public bool IsSecondaryActionVisible
    {
        get => _isSecondaryActionVisible;
        private set => SetProperty(ref _isSecondaryActionVisible, value);
    }

    public bool IsTertiaryActionVisible
    {
        get => _isTertiaryActionVisible;
        private set => SetProperty(ref _isTertiaryActionVisible, value);
    }

    public bool IsApprovalFeedbackVisible
    {
        get => _isApprovalFeedbackVisible;
        private set
        {
            if (SetProperty(ref _isApprovalFeedbackVisible, value))
            {
                OnPropertyChanged(nameof(IsIdleCodexIconVisible));
            }
        }
    }

    public string DebugStatusText
    {
        get => _debugStatusText;
        private set => SetProperty(ref _debugStatusText, value);
    }

    public string DebugSourceText
    {
        get => _debugSourceText;
        private set => SetProperty(ref _debugSourceText, value);
    }

    public string ExpansionHint => IsExpanded
        ? "TAP TO CLOSE"
        : IsActionRequired
            ? "ACTION"
            : "TAP / HOVER";

    public bool IsIdleExpandedState => CurrentStatus == CodexSessionStatus.Idle;

    public bool IsDetailPanelVisible => !IsIdleExpandedState;

    public bool IsExpandedTextVisible => !IsIdleExpandedState
        && !string.IsNullOrWhiteSpace(ExpandedDetailText)
        && (CurrentStatus != CodexSessionStatus.Completed || ChangedFiles.Count == 0);

    public bool IsChangedFilesVisible => CurrentStatus == CodexSessionStatus.Completed && ChangedFiles.Count > 0;

    public double ExpandedPanelMinHeight => 0;

    public double ExpandedRegionBaseHeight => 0;

    public RelayCommand ToggleExpandCommand { get; }

    public AsyncRelayCommand PrimaryActionCommand { get; }

    public AsyncRelayCommand SecondaryActionCommand { get; }

    public AsyncRelayCommand TertiaryActionCommand { get; }

    public async Task InitializeAsync()
    {
        if (_service.CurrentTask is not null)
        {
            ApplyTask(_service.CurrentTask);
        }

        var startTask = _service.StartAsync();
        await RunBootAnimationAsync();
        await startTask;
    }

    public void Collapse()
    {
        _focusLossAutoCollapseTimer.Stop();
        _isManualExpanded = false;
        _isHoverExpanded = false;
        IsExpanded = false;
    }

    public void ExpandFromHover()
    {
        _focusLossAutoCollapseTimer.Stop();

        if (IsActionRequired || _isManualExpanded)
        {
            return;
        }

        _isHoverExpanded = true;
        IsExpanded = true;
    }

    public void ScheduleCollapseAfterFocusLoss()
    {
        if (IsActionRequired || !IsExpanded)
        {
            return;
        }

        if (_isManualExpanded)
        {
            _focusLossAutoCollapseTimer.Interval = TimeSpan.FromSeconds(_runtimeOptions.ManualAutoCollapseSeconds);
        }
        else if (_isHoverExpanded)
        {
            _focusLossAutoCollapseTimer.Interval = TimeSpan.FromSeconds(_runtimeOptions.HoverAutoCollapseSeconds);
        }
        else
        {
            return;
        }

        _focusLossAutoCollapseTimer.Stop();
        _focusLossAutoCollapseTimer.Start();
    }

    public void CancelScheduledCollapse()
    {
        _focusLossAutoCollapseTimer.Stop();
    }

    public void Dispose()
    {
        _service.TaskUpdated -= OnTaskUpdated;
        _layoutSettings.PropertyChanged -= OnLayoutSettingsPropertyChanged;
        _glyphTimer.Tick -= OnGlyphTimerTick;
        _approvalFeedbackTimer.Tick -= OnApprovalFeedbackTimerTick;
        _completedAutoCollapseTimer.Tick -= OnCompletedAutoCollapseTimerTick;
        _focusLossAutoCollapseTimer.Tick -= OnFocusLossAutoCollapseTimerTick;
        _glyphTimer.Stop();
        _approvalFeedbackTimer.Stop();
        _completedAutoCollapseTimer.Stop();
        _focusLossAutoCollapseTimer.Stop();
    }

    private async Task ExecutePrimaryActionAsync()
    {
        await ExecuteActionAsync(PrimaryActionText);
    }

    private async Task ExecuteSecondaryActionAsync()
    {
        await ExecuteActionAsync(SecondaryActionText);
    }

    private async Task ExecuteTertiaryActionAsync()
    {
        await ExecuteActionAsync(TertiaryActionText);
    }

    private async Task ExecuteActionAsync(string actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId))
        {
            return;
        }

        if (IsApprovalAction(actionId))
        {
            ShowApprovalFeedback();
        }

        await _service.ExecuteActionAsync(actionId);
        RefreshCommands();
    }

    private void ToggleExpand()
    {
        _isHoverExpanded = false;
        _focusLossAutoCollapseTimer.Stop();

        if (IsExpanded && _isManualExpanded)
        {
            _isManualExpanded = false;
            IsExpanded = false;
            return;
        }

        _isManualExpanded = !IsExpanded;
        IsExpanded = !IsExpanded;
    }

    private void OnTaskUpdated(object? sender, CodexTask task)
    {
        ApplyTask(task);
    }

    private void ApplyTask(CodexTask task)
    {
        ClearApprovalFeedback();
        var previousStatus = CurrentStatus;
        DiagnosticsLogger.Write($"Task update: status={task.Status}, title={task.Title}, actions={task.AvailableActions.Count}");
        CurrentStatus = task.Status;
        StatusText = BuildDisplayTitle(task);
        StatusMessage = task.Message;
        CompactStatusMessage = BuildCompactStatusMessage(task);
        CompactStatusText = BuildCompactStatusText(task.Status);
        DebugStatusText = task.Status.ToString();
        DebugSourceText = string.IsNullOrWhiteSpace(task.DebugSource)
            ? "No source details available."
            : task.DebugSource;
        ChangedFiles = BuildChangedFiles(task);
        ExpandedSectionTitle = BuildExpandedSectionTitle(task);
        ExpandedDetailText = FilterExpandedDetailText(BuildExpandedDetailText(task));
        IsBusy = task.Status is CodexSessionStatus.Processing or CodexSessionStatus.RunningTool or CodexSessionStatus.Finishing;
        IsActionRequired = task.AvailableActions.Count > 0;

        PrimaryActionText = task.AvailableActions.ElementAtOrDefault(0) ?? string.Empty;
        SecondaryActionText = task.AvailableActions.ElementAtOrDefault(1) ?? string.Empty;
        TertiaryActionText = task.AvailableActions.ElementAtOrDefault(2) ?? string.Empty;

        IsPrimaryActionVisible = task.AvailableActions.Count > 0;
        IsSecondaryActionVisible = task.AvailableActions.Count > 1;
        IsTertiaryActionVisible = task.AvailableActions.Count > 2;

        PanelHintText = BuildPanelHint(task);
        ActivityBadgeText = BuildActivityBadge(task.Status);

        if (task.Status == CodexSessionStatus.Completed)
        {
            ShowCompletedState(previousStatus);
        }
        else if (IsActionRequired)
        {
            _completedAutoCollapseTimer.Stop();
            _isHoverExpanded = false;
            _focusLossAutoCollapseTimer.Stop();
            IsExpanded = true;
            _ = TriggerBounceAsync();
        }
        else if (!_isManualExpanded)
        {
            _completedAutoCollapseTimer.Stop();
            _isHoverExpanded = false;
            _focusLossAutoCollapseTimer.Stop();
            IsExpanded = false;
        }

        CollapsedWidth = _layoutSettings.GetCollapsedWidth(task.Status);
        UpdateStaticGlyph(task.Status);
        RaiseExpandedPanelStateChanged();

        RefreshCommands();
    }

    private async Task RunBootAnimationAsync()
    {
        IsExpanded = true;
        await Task.Delay(900);

        if (!IsActionRequired && !_isManualExpanded)
        {
            IsExpanded = false;
        }
    }

    private string BuildPanelHint(CodexTask task)
    {
        if (task.Status == CodexSessionStatus.Completed && (task.ChangedFiles?.Count ?? 0) > 0)
        {
            return "The island is showing the latest files changed in this Codex session.";
        }

        return task.Status switch
        {
            CodexSessionStatus.Processing => "Codex CLI is still thinking. The island will keep following the current turn.",
            CodexSessionStatus.RunningTool => "A tool call is in flight. If it keeps running, the island will continue following it.",
            CodexSessionStatus.Finishing => "Codex CLI is preparing the final answer for the current turn.",
            CodexSessionStatus.Completed => "This turn completed. The island will stay visible for about one minute before returning to idle.",
            CodexSessionStatus.Stalled => "No meaningful session progress arrived for about three minutes; the task may be stalled.",
            CodexSessionStatus.Interrupted => "The current turn was interrupted or rolled back before completion.",
            CodexSessionStatus.Unknown => "The live watcher could not determine the current session state.",
            _ => "No action is required right now. Tap the island to inspect the latest status message."
        };
    }

    private static IReadOnlyList<string> BuildChangedFiles(CodexTask task)
    {
        if (task.Status != CodexSessionStatus.Completed || task.ChangedFiles is null || task.ChangedFiles.Count == 0)
        {
            return Array.Empty<string>();
        }

        return task.ChangedFiles
            .Select(FormatFileLabel)
            .ToArray();
    }

    private static string FormatFileLabel(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2)
        {
            return $"{segments[^2]}/{segments[^1]}";
        }

        return normalized;
    }

    private static string BuildExpandedSectionTitle(CodexTask task)
    {
        return task.Status switch
        {
            CodexSessionStatus.Processing => "AGENT OUTPUT",
            CodexSessionStatus.RunningTool => "TOOL DETAILS",
            CodexSessionStatus.Finishing => "AGENT OUTPUT",
            CodexSessionStatus.Completed => "CHANGED FILES",
            _ => "STATUS DETAILS"
        };
    }

    private static string BuildExpandedDetailText(CodexTask task)
    {
        return task.Status switch
        {
            CodexSessionStatus.Processing => NormalizeExpandedDetail(task.Message, "Codex is processing the current turn."),
            CodexSessionStatus.RunningTool => BuildToolDetailText(task),
            CodexSessionStatus.Finishing => NormalizeExpandedDetail(task.Message, "Codex is preparing the final answer."),
            CodexSessionStatus.Completed => (task.ChangedFiles?.Count ?? 0) > 0
                ? "The island is showing the files changed in the completed turn."
                : "This turn completed without any tracked file changes.",
            CodexSessionStatus.Stalled => NormalizeExpandedDetail(task.Message, "No new events; task may be stalled."),
            CodexSessionStatus.Interrupted => NormalizeExpandedDetail(task.Message, "The current turn was interrupted."),
            CodexSessionStatus.Unknown => NormalizeExpandedDetail(task.Message, "The live watcher could not determine the current session state."),
            _ => NormalizeExpandedDetail(task.Message, "Waiting for an active Codex CLI session.")
        };
    }

    private string FilterExpandedDetailText(string detailText)
    {
        if (IsDebugMode || string.IsNullOrWhiteSpace(detailText))
        {
            return detailText;
        }

        return DebugOnlyExpandedMessages.Contains(detailText)
            ? string.Empty
            : detailText;
    }

    private static string BuildToolDetailText(CodexTask task)
    {
        var details = new List<string>
        {
            NormalizeExpandedDetail(task.Message, "Running a Codex CLI tool.")
        };

        AppendDebugLine(details, task.DebugSource, "Tool:");
        AppendDebugLine(details, task.DebugSource, "Command:");
        AppendDebugLine(details, task.DebugSource, "EventTime:");
        return string.Join(Environment.NewLine, details.Distinct(StringComparer.Ordinal));
    }

    private static void AppendDebugLine(List<string> details, string? debugSource, string prefix)
    {
        if (string.IsNullOrWhiteSpace(debugSource))
        {
            return;
        }

        var line = debugSource
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(line))
        {
            details.Add(line);
        }
    }

    private static string NormalizeExpandedDetail(string? message, string fallback)
    {
        return string.IsNullOrWhiteSpace(message) ? fallback : message.Trim();
    }

    private static string BuildDisplayTitle(CodexTask task)
    {
        if (task.Status == CodexSessionStatus.Idle)
        {
            return "Codex";
        }

        return task.Title;
    }

    private string BuildCompactStatusMessage(CodexTask task)
    {
        return task.Status switch
        {
            CodexSessionStatus.Processing => "Thinking",
            CodexSessionStatus.RunningTool => "Tool running",
            CodexSessionStatus.Finishing => "Finishing",
            CodexSessionStatus.Completed => "Turn complete",
            CodexSessionStatus.Stalled => "No new events",
            CodexSessionStatus.Interrupted => "Turn interrupted",
            CodexSessionStatus.Unknown => "Watcher issue",
            _ => "Waiting for Codex CLI"
        };
    }

    private static string BuildCompactStatusText(CodexSessionStatus status)
    {
        return status switch
        {
            CodexSessionStatus.Idle => "空闲",
            CodexSessionStatus.Processing => "思考中",
            CodexSessionStatus.RunningTool => "调用工具",
            CodexSessionStatus.Finishing => "整理结果",
            CodexSessionStatus.Completed => "已完成",
            CodexSessionStatus.Stalled => "可能卡住",
            CodexSessionStatus.Interrupted => "已中断",
            CodexSessionStatus.Unknown => "未知",
            _ => "空闲"
        };
    }

    private static string BuildActivityBadge(CodexSessionStatus status)
    {
        return status switch
        {
            CodexSessionStatus.Processing => "LIVE",
            CodexSessionStatus.RunningTool => "TOOL",
            CodexSessionStatus.Finishing => "DONE",
            CodexSessionStatus.Completed => "OK",
            CodexSessionStatus.Stalled => "WAIT",
            CodexSessionStatus.Interrupted => "STOP",
            CodexSessionStatus.Unknown => "WARN",
            _ => "READY"
        };
    }

    private void UpdateGlyphAnimation()
    {
        if (IsApprovalFeedbackVisible)
        {
            _glyphTimer.Stop();
            StatusGlyph = ApprovalGlyph;
            return;
        }

        if (IsBusy)
        {
            if (!_glyphTimer.IsEnabled)
            {
                _glyphTimer.Start();
            }

            return;
        }

        _glyphTimer.Stop();
        UpdateStaticGlyph(CurrentStatus);
    }

    private void UpdateStaticGlyph(CodexSessionStatus status)
    {
        if (IsApprovalFeedbackVisible)
        {
            StatusGlyph = ApprovalGlyph;
            return;
        }

        StatusGlyph = status switch
        {
            CodexSessionStatus.Completed => "√",
            CodexSessionStatus.Stalled => "!",
            CodexSessionStatus.Interrupted => "x",
            CodexSessionStatus.Unknown => "?",
            CodexSessionStatus.Idle => "o",
            _ => _workingGlyphFrames[_glyphFrameIndex % _workingGlyphFrames.Length]
        };
    }

    private async Task TriggerBounceAsync()
    {
        IsBouncing = true;
        await Task.Delay(180);
        IsBouncing = false;
    }

    private void OnGlyphTimerTick(object? sender, EventArgs e)
    {
        _glyphFrameIndex = (_glyphFrameIndex + 1) % _workingGlyphFrames.Length;
        StatusGlyph = _workingGlyphFrames[_glyphFrameIndex];
    }

    private void ShowApprovalFeedback()
    {
        _approvalFeedbackTimer.Stop();
        _isManualExpanded = false;
        _isHoverExpanded = false;
        _focusLossAutoCollapseTimer.Stop();
        IsExpanded = false;
        IsApprovalFeedbackVisible = true;
        _glyphTimer.Stop();
        StatusGlyph = ApprovalGlyph;
        _approvalFeedbackTimer.Start();
    }

    private void ClearApprovalFeedback()
    {
        if (!IsApprovalFeedbackVisible)
        {
            return;
        }

        _approvalFeedbackTimer.Stop();
        IsApprovalFeedbackVisible = false;
        UpdateGlyphAnimation();
    }

    private void OnApprovalFeedbackTimerTick(object? sender, EventArgs e)
    {
        ClearApprovalFeedback();
    }

    private void OnCompletedAutoCollapseTimerTick(object? sender, EventArgs e)
    {
        _completedAutoCollapseTimer.Stop();
        if (CurrentStatus != CodexSessionStatus.Completed || _isManualExpanded || IsActionRequired)
        {
            return;
        }

        _isHoverExpanded = false;
        IsExpanded = false;
    }

    private void OnFocusLossAutoCollapseTimerTick(object? sender, EventArgs e)
    {
        _focusLossAutoCollapseTimer.Stop();
        if (IsActionRequired || !IsExpanded)
        {
            return;
        }

        if (_isManualExpanded || _isHoverExpanded)
        {
            _isManualExpanded = false;
            _isHoverExpanded = false;
            IsExpanded = false;
        }
    }

    private static bool IsApprovalAction(string actionId)
    {
        return actionId.Contains("approve", StringComparison.OrdinalIgnoreCase)
            || actionId.Contains("approval", StringComparison.OrdinalIgnoreCase)
            || actionId.Contains("confirm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actionId, "allow", StringComparison.OrdinalIgnoreCase);
    }

    private void OnLayoutSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IslandLayoutSettings.IdleCollapsedWidth)
            or nameof(IslandLayoutSettings.BusyCollapsedWidth)
            or nameof(IslandLayoutSettings.AttentionCollapsedWidth))
        {
            CollapsedWidth = _layoutSettings.GetCollapsedWidth(CurrentStatus);
        }

        if (e.PropertyName is nameof(IslandLayoutSettings.ActionPanelMinHeight)
            or nameof(IslandLayoutSettings.ExpandedRegionExpandedHeight))
        {
            RaiseExpandedPanelStateChanged();
        }
    }

    private bool SetExpanded(bool value)
    {
        if (!SetProperty(ref _isExpanded, value, nameof(IsExpanded)))
        {
            return false;
        }

        DiagnosticsLogger.Write($"IsExpanded -> {value}");
        OnPropertyChanged(nameof(ExpansionHint));
        return true;
    }

    private void RefreshCommands()
    {
        PrimaryActionCommand.RaiseCanExecuteChanged();
        SecondaryActionCommand.RaiseCanExecuteChanged();
        TertiaryActionCommand.RaiseCanExecuteChanged();
    }

    private void ShowCompletedState(CodexSessionStatus previousStatus)
    {
        _completedAutoCollapseTimer.Stop();

        if (previousStatus != CodexSessionStatus.Completed)
        {
            _isManualExpanded = false;
            _isHoverExpanded = false;
            IsExpanded = true;
        }

        _completedAutoCollapseTimer.Start();
    }

    private void RaiseExpandedPanelStateChanged()
    {
        OnPropertyChanged(nameof(IsIdleExpandedState));
        OnPropertyChanged(nameof(IsDetailPanelVisible));
        OnPropertyChanged(nameof(IsExpandedTextVisible));
        OnPropertyChanged(nameof(IsChangedFilesVisible));
        OnPropertyChanged(nameof(ExpandedPanelMinHeight));
        OnPropertyChanged(nameof(ExpandedRegionBaseHeight));
    }
}
