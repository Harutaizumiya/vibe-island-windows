using DynamicIsland.Models;
using DynamicIsland.Services;
using DynamicIsland.UI;
using DynamicIsland.Utils;
using System.Windows.Threading;

namespace DynamicIsland.ViewModels;

public sealed class StatusViewModel : ObservableObject, IDisposable
{
    private readonly IClaudecodeService _service;
    private readonly IslandLayoutSettings _layoutSettings;
    private readonly DispatcherTimer _glyphTimer;
    private readonly string[] _workingGlyphFrames = ["|", "/", "-", "\\"];

    private ClaudecodeStatus _currentStatus = ClaudecodeStatus.Idle;
    private string _statusText = "Booting";
    private string _statusMessage = "Preparing the island services.";
    private string _compactStatusMessage = "Preparing the island services.";
    private string _statusGlyph = "o";
    private string _activityBadgeText = "BOOT";
    private bool _isExpanded;
    private bool _isBusy;
    private bool _isActionRequired;
    private bool _isBouncing;
    private bool _isManualExpanded;
    private bool _isHoverExpanded;
    private double _collapsedWidth;
    private string _primaryActionText = "Approve";
    private string _secondaryActionText = "Reject";
    private string _tertiaryActionText = "Later";
    private string _panelHintText = "Waiting for the next actionable state.";
    private bool _isPrimaryActionVisible;
    private bool _isSecondaryActionVisible;
    private bool _isTertiaryActionVisible;
    private int _glyphFrameIndex;

    public StatusViewModel(IClaudecodeService service, IslandLayoutSettings layoutSettings)
    {
        _service = service;
        _layoutSettings = layoutSettings;
        _service.TaskUpdated += OnTaskUpdated;
        _layoutSettings.PropertyChanged += OnLayoutSettingsPropertyChanged;
        _glyphTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _glyphTimer.Tick += OnGlyphTimerTick;
        _collapsedWidth = _layoutSettings.GetCollapsedWidth(_currentStatus);

        ToggleExpandCommand = new RelayCommand(ToggleExpand);
        ApproveCommand = new AsyncRelayCommand(ApproveAsync, () => IsPrimaryActionVisible);
        RejectCommand = new AsyncRelayCommand(RejectAsync, () => IsSecondaryActionVisible);
        SnoozeCommand = new AsyncRelayCommand(SnoozeAsync, () => IsTertiaryActionVisible);
    }

    public ClaudecodeStatus CurrentStatus
    {
        get => _currentStatus;
        private set => SetProperty(ref _currentStatus, value);
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

    public string ExpansionHint => IsExpanded
        ? "TAP TO CLOSE"
        : IsActionRequired
            ? "ACTION"
            : "TAP / HOVER";

    public RelayCommand ToggleExpandCommand { get; }

    public AsyncRelayCommand ApproveCommand { get; }

    public AsyncRelayCommand RejectCommand { get; }

    public AsyncRelayCommand SnoozeCommand { get; }

    public async Task InitializeAsync()
    {
        if (_service.CurrentTask is not null)
        {
            ApplyTask(_service.CurrentTask);
        }

        await RunBootAnimationAsync();
        await _service.StartAsync();
    }

    public void Collapse()
    {
        _isManualExpanded = false;
        _isHoverExpanded = false;
        IsExpanded = false;
    }

    public void ExpandFromHover()
    {
        if (IsActionRequired || _isManualExpanded || IsExpanded)
        {
            return;
        }

        _isHoverExpanded = true;
        IsExpanded = true;
    }

    public void CollapseHover()
    {
        if (!_isHoverExpanded || IsActionRequired)
        {
            return;
        }

        _isHoverExpanded = false;
        IsExpanded = false;
    }

    public void Dispose()
    {
        _service.TaskUpdated -= OnTaskUpdated;
        _layoutSettings.PropertyChanged -= OnLayoutSettingsPropertyChanged;
        _glyphTimer.Tick -= OnGlyphTimerTick;
        _glyphTimer.Stop();
    }

    private async Task ApproveAsync()
    {
        await _service.ApproveAsync();
        RefreshCommands();
    }

    private async Task RejectAsync()
    {
        await _service.RejectAsync();
        RefreshCommands();
    }

    private async Task SnoozeAsync()
    {
        await _service.SnoozeAsync();
        RefreshCommands();
    }

    private void ToggleExpand()
    {
        _isHoverExpanded = false;

        if (IsExpanded && _isManualExpanded)
        {
            _isManualExpanded = false;
            IsExpanded = false;
            return;
        }

        _isManualExpanded = !IsExpanded;
        IsExpanded = !IsExpanded;
    }

    private void OnTaskUpdated(object? sender, ClaudecodeTask task)
    {
        ApplyTask(task);
    }

    private void ApplyTask(ClaudecodeTask task)
    {
        DiagnosticsLogger.Write($"Task update: status={task.Status}, title={task.Title}, actions={task.AvailableActions.Count}");
        CurrentStatus = task.Status;
        StatusText = task.Title;
        StatusMessage = task.Message;
        CompactStatusMessage = BuildCompactStatusMessage(task);
        IsBusy = task.Status == ClaudecodeStatus.Working;
        IsActionRequired = task.Status is ClaudecodeStatus.NeedsApproval or ClaudecodeStatus.NeedsChoice;

        PrimaryActionText = task.AvailableActions.ElementAtOrDefault(0) ?? "Approve";
        SecondaryActionText = task.AvailableActions.ElementAtOrDefault(1) ?? "Reject";
        TertiaryActionText = task.AvailableActions.ElementAtOrDefault(2) ?? "Later";

        IsPrimaryActionVisible = task.AvailableActions.Count > 0;
        IsSecondaryActionVisible = task.AvailableActions.Count > 1;
        IsTertiaryActionVisible = task.AvailableActions.Count > 2;

        PanelHintText = BuildPanelHint(task);
        ActivityBadgeText = BuildActivityBadge(task.Status);

        if (IsActionRequired)
        {
            _isHoverExpanded = false;
            IsExpanded = true;
            _ = TriggerBounceAsync();
        }
        else if (!_isManualExpanded)
        {
            _isHoverExpanded = false;
            IsExpanded = false;
        }

        CollapsedWidth = _layoutSettings.GetCollapsedWidth(task.Status);
        UpdateStaticGlyph(task.Status);

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

    private string BuildPanelHint(ClaudecodeTask task)
    {
        return task.Status switch
        {
            ClaudecodeStatus.Working => "The island is live and will expand again when an action is needed.",
            ClaudecodeStatus.NeedsApproval => "Review the request below and choose how Claudecode should proceed.",
            ClaudecodeStatus.NeedsChoice => "This step needs a user decision before the mock workflow can continue.",
            ClaudecodeStatus.Error => "The current flow was interrupted. It will recover automatically after a short pause.",
            _ => "No action is required right now. Tap the island to inspect the latest status message."
        };
    }

    private string BuildCompactStatusMessage(ClaudecodeTask task)
    {
        return task.Status switch
        {
            ClaudecodeStatus.Working => "Applying background progress",
            ClaudecodeStatus.NeedsApproval => "Approval needed",
            ClaudecodeStatus.NeedsChoice => "Decision needed",
            ClaudecodeStatus.Error => "Needs recovery",
            _ => "Standing by"
        };
    }

    private static string BuildActivityBadge(ClaudecodeStatus status)
    {
        return status switch
        {
            ClaudecodeStatus.Working => "LIVE",
            ClaudecodeStatus.NeedsApproval => "REVIEW",
            ClaudecodeStatus.NeedsChoice => "CHOOSE",
            ClaudecodeStatus.Error => "RETRY",
            _ => "READY"
        };
    }

    private void UpdateGlyphAnimation()
    {
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

    private void UpdateStaticGlyph(ClaudecodeStatus status)
    {
        StatusGlyph = status switch
        {
            ClaudecodeStatus.NeedsApproval => "!",
            ClaudecodeStatus.NeedsChoice => "?",
            ClaudecodeStatus.Error => "x",
            ClaudecodeStatus.Idle => "o",
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

    private void OnLayoutSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IslandLayoutSettings.IdleCollapsedWidth)
            or nameof(IslandLayoutSettings.WorkingCollapsedWidth)
            or nameof(IslandLayoutSettings.ApprovalCollapsedWidth)
            or nameof(IslandLayoutSettings.ChoiceCollapsedWidth)
            or nameof(IslandLayoutSettings.ErrorCollapsedWidth))
        {
            CollapsedWidth = _layoutSettings.GetCollapsedWidth(CurrentStatus);
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
        ApproveCommand.RaiseCanExecuteChanged();
        RejectCommand.RaiseCanExecuteChanged();
        SnoozeCommand.RaiseCanExecuteChanged();
    }
}
