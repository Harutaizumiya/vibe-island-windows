using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Models;
using DynamicIsland.UI;
using DynamicIsland.Utils;
using DynamicIsland.ViewModels;
using Microsoft.Win32;

namespace DynamicIsland;

public partial class MainWindow : Window
{
    private const int WmDpiChanged = 0x02E0;

    private readonly StatusViewModel _viewModel;
    private readonly DispatcherTimer _hoverTimer;
    private HwndSource? _hwndSource;

    public MainWindow(StatusViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Deactivated += OnDeactivated;
        Closed += OnClosed;
        SizeChanged += OnSizeChanged;
        MainSurface.SizeChanged += OnMainSurfaceSizeChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _hoverTimer.Tick += OnHoverTimerTick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DiagnosticsLogger.Write("MainWindow loaded.");
        ApplyStatusPalette(_viewModel.CurrentStatus, animate: false);
        UpdateExpansionState(_viewModel.IsExpanded, animate: false);
        ApplyMainSurfaceClip();
        WindowPositionHelper.PositionTopCenter(this, topMargin: IslandLayout.ScreenTopMargin);
        DiagnosticsLogger.Write($"Window positioned at Left={Left}, Top={Top}, Width={Width}, Height={Height}.");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DiagnosticsLogger.Write("MainWindow closed.");
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        MainSurface.SizeChanged -= OnMainSurfaceSizeChanged;
        _hoverTimer.Tick -= OnHoverTimerTick;
        _hoverTimer.Stop();

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyMainSurfaceClip();
        WindowPositionHelper.PositionTopCenter(this, topMargin: IslandLayout.ScreenTopMargin);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ApplyMainSurfaceClip();
            WindowPositionHelper.PositionTopCenter(this, topMargin: IslandLayout.ScreenTopMargin);
        });
    }

    private void OnMainSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyMainSurfaceClip();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        DiagnosticsLogger.Write("MainWindow deactivated.");
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatusViewModel.IsExpanded))
        {
            DiagnosticsLogger.Write($"Window handling IsExpanded={_viewModel.IsExpanded}");
            UpdateExpansionState(_viewModel.IsExpanded, animate: true);
        }
        else if (e.PropertyName == nameof(StatusViewModel.CurrentStatus))
        {
            ApplyStatusPalette(_viewModel.CurrentStatus, animate: true);
            AnimationHelper.CreateStatusTransitionStoryboard(
                StatusTextPanel,
                GlyphContainer,
                StatusContentTranslateTransform).Begin();
        }
        else if (e.PropertyName == nameof(StatusViewModel.CollapsedWidth))
        {
            UpdateExpansionState(_viewModel.IsExpanded, animate: true);
        }
        else if (e.PropertyName == nameof(StatusViewModel.IsBouncing) && _viewModel.IsBouncing)
        {
            AnimationHelper.CreateBounceStoryboard(IslandScaleTransform).Begin();
        }
    }

    private void UpdateExpansionState(bool isExpanded, bool animate)
    {
        DiagnosticsLogger.Write($"UpdateExpansionState expanded={isExpanded}, animate={animate}, widthTarget={(isExpanded ? IslandLayout.ExpandedWidth : _viewModel.CollapsedWidth)}");
        var currentMainSurfaceCornerRadius = MainSurface.CornerRadius;
        // Collapsed width still comes from StatusViewModel.GetCollapsedWidth().
        var targetWidth = isExpanded ? IslandLayout.ExpandedWidth : _viewModel.CollapsedWidth;
        // Expanded/collapsed shell height is centralized in UI/IslandLayout.cs.
        var targetHeight = isExpanded ? IslandLayout.ExpandedHeight : IslandLayout.CollapsedHeight;
        // Approval/choice content reveal height is centralized in UI/IslandLayout.cs.
        var targetExpandedRegionHeight = isExpanded ? IslandLayout.ExpandedRegionExpandedHeight : 0.0;
        var targetPanelOpacity = isExpanded ? 1.0 : 0.0;
        var targetOffset = isExpanded ? 0.0 : -10.0;
        var targetMainSurfaceCornerRadius = new CornerRadius(
            isExpanded ? IslandLayout.ExpandedShellRadius : IslandLayout.CollapsedShellRadius);

        if (!animate)
        {
            MainSurface.Width = targetWidth;
            MainSurface.Height = targetHeight;
            ExpandedRegion.Height = targetExpandedRegionHeight;
            ExpandedRegionTranslateTransform.Y = targetOffset;
            ActionPanel.Opacity = targetPanelOpacity;
            ActionPanelHeader.Opacity = targetPanelOpacity;
            ActionButtonsPanel.Opacity = targetPanelOpacity;
            IslandScaleTransform.ScaleX = 1;
            IslandScaleTransform.ScaleY = 1;
            MainSurface.CornerRadius = targetMainSurfaceCornerRadius;
            ApplyMainSurfaceClip();
            return;
        }

        var storyboard = AnimationHelper.CreateExpandStoryboard(
            MainSurface,
            ExpandedRegion,
            ActionPanel,
            ActionPanelHeader,
            ActionButtonsPanel,
            ExpandedRegionTranslateTransform,
            IslandScaleTransform,
            currentMainSurfaceCornerRadius,
            targetMainSurfaceCornerRadius,
            targetWidth,
            targetHeight,
            targetExpandedRegionHeight,
            targetPanelOpacity,
            targetOffset,
            TimeSpan.FromMilliseconds(isExpanded ? 420 : 340),
            isExpanded);

        storyboard.Completed += (_, _) =>
        {
            MainSurface.CornerRadius = targetMainSurfaceCornerRadius;
            ExpandedRegion.Height = targetExpandedRegionHeight;
            ApplyMainSurfaceClip();
        };

        storyboard.Begin();
    }

    private void ApplyStatusPalette(ClaudecodeStatus status, bool animate)
    {
        var palette = status switch
        {
            ClaudecodeStatus.Working => new IslandPalette("#FF000000", "#FF181818", "#FFF4F4F4"),
            ClaudecodeStatus.NeedsApproval => new IslandPalette("#FF000000", "#FF202020", "#FFFFFFFF"),
            ClaudecodeStatus.NeedsChoice => new IslandPalette("#FF000000", "#FF1C1C1C", "#FFF0F0F0"),
            ClaudecodeStatus.Error => new IslandPalette("#FF000000", "#FF222222", "#FFE8E8E8"),
            _ => new IslandPalette("#FF000000", "#FF161616", "#FFF2F2F2")
        };

        AnimationHelper.TransitionBrush(MainSurfaceBackgroundBrush, palette.Background, animate, durationMs: 320);
        AnimationHelper.TransitionBrush(MainSurfaceBorderBrush, palette.Border, animate, durationMs: 320);
        AnimationHelper.TransitionBrush(GlyphBorderBrush, palette.Border, animate, durationMs: 280);
        AnimationHelper.TransitionBrush(StatusAccentEllipseBrush, palette.Accent, animate, durationMs: 240);

        var accentColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(palette.Accent)!;
        GlyphContainer.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 12,
            ShadowDepth = 0,
            Color = accentColor,
            Opacity = status is ClaudecodeStatus.Working or ClaudecodeStatus.NeedsApproval or ClaudecodeStatus.NeedsChoice ? 0.08 : 0.05
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDpiChanged)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ApplyMainSurfaceClip();
                WindowPositionHelper.PositionTopCenter(this, topMargin: IslandLayout.ScreenTopMargin);
            });
        }

        return IntPtr.Zero;
    }

    private void RootGrid_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == RootGrid)
        {
            _viewModel.Collapse();
            e.Handled = true;
        }
    }

    private void MainSurface_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        DiagnosticsLogger.Write("Mouse entered island.");
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void MainSurface_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        DiagnosticsLogger.Write("Mouse left island.");
        _hoverTimer.Stop();
        _viewModel.CollapseHover();
    }

    private void StatusCard_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dependencyObject &&
            FindAncestor<System.Windows.Controls.Button>(dependencyObject) is not null)
        {
            return;
        }

        DiagnosticsLogger.Write("Status card clicked.");
        _viewModel.ToggleExpandCommand.Execute(null);
        e.Handled = true;
    }

    private void OnHoverTimerTick(object? sender, EventArgs e)
    {
        DiagnosticsLogger.Write("Hover timer fired.");
        _hoverTimer.Stop();
        _viewModel.ExpandFromHover();
    }

    private void ApplyMainSurfaceClip()
    {
        var width = MainSurface.ActualWidth > 0 ? MainSurface.ActualWidth : MainSurface.Width;
        var height = MainSurface.ActualHeight > 0 ? MainSurface.ActualHeight : MainSurface.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var topInverseRadius = IslandLayout.TopInverseCornerRadius;
        var bottomRadius = IslandLayout.BottomCornerRadius;

        var geometry = new StreamGeometry();
        using var context = geometry.Open();

        context.BeginFigure(new System.Windows.Point(topInverseRadius, 0), isFilled: true, isClosed: true);
        context.LineTo(new System.Windows.Point(width - topInverseRadius, 0), isStroked: true, isSmoothJoin: false);

        context.QuadraticBezierTo(
            new System.Windows.Point(width, 0),
            new System.Windows.Point(width, topInverseRadius),
            isStroked: true,
            isSmoothJoin: true);

        context.LineTo(new System.Windows.Point(width, height - bottomRadius), isStroked: true, isSmoothJoin: false);
        context.QuadraticBezierTo(
            new System.Windows.Point(width, height),
            new System.Windows.Point(width - bottomRadius, height),
            isStroked: true,
            isSmoothJoin: true);

        context.LineTo(new System.Windows.Point(bottomRadius, height), isStroked: true, isSmoothJoin: false);
        context.QuadraticBezierTo(
            new System.Windows.Point(0, height),
            new System.Windows.Point(0, height - bottomRadius),
            isStroked: true,
            isSmoothJoin: true);

        context.LineTo(new System.Windows.Point(0, topInverseRadius), isStroked: true, isSmoothJoin: false);
        context.QuadraticBezierTo(
            new System.Windows.Point(0, 0),
            new System.Windows.Point(topInverseRadius, 0),
            isStroked: true,
            isSmoothJoin: true);

        geometry.Freeze();
        MainSurface.Clip = geometry;
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private readonly record struct IslandPalette(string Background, string Border, string Accent);
}
