using System.Windows;
using DynamicIsland.Models;
using DynamicIsland.ViewModels;

namespace DynamicIsland.UI;

public sealed class IslandLayoutSettings : ObservableObject
{
    private double _windowWidth = 520;
    private double _windowHeight = 300;
    private double _screenTopMargin;
    private double _idleCollapsedWidth = 332;
    private double _workingCollapsedWidth = 372;
    private double _approvalCollapsedWidth = 364;
    private double _choiceCollapsedWidth = 364;
    private double _errorCollapsedWidth = 348;
    private double _expandedWidth = 492;
    private double _collapsedHeight = 88;
    private double _expandedHeight = 188;
    private double _statusRowHeight = 56;
    private double _expandedRegionExpandedHeight = 144;
    private double _actionPanelMinHeight = 118;
    private double _topMargin;
    private double _expandedRegionTopSpacing = 8;
    private double _collapsedShellRadius = 32;
    private double _expandedShellRadius = 26;
    private double _bottomCornerRadius = 24;

    public double WindowWidth
    {
        get => _windowWidth;
        set => SetProperty(ref _windowWidth, value);
    }

    public double WindowHeight
    {
        get => _windowHeight;
        set => SetProperty(ref _windowHeight, value);
    }

    public double ScreenTopMargin
    {
        get => _screenTopMargin;
        set => SetProperty(ref _screenTopMargin, value);
    }

    public double IdleCollapsedWidth
    {
        get => _idleCollapsedWidth;
        set => SetProperty(ref _idleCollapsedWidth, value);
    }

    public double WorkingCollapsedWidth
    {
        get => _workingCollapsedWidth;
        set => SetProperty(ref _workingCollapsedWidth, value);
    }

    public double ApprovalCollapsedWidth
    {
        get => _approvalCollapsedWidth;
        set => SetProperty(ref _approvalCollapsedWidth, value);
    }

    public double ChoiceCollapsedWidth
    {
        get => _choiceCollapsedWidth;
        set => SetProperty(ref _choiceCollapsedWidth, value);
    }

    public double ErrorCollapsedWidth
    {
        get => _errorCollapsedWidth;
        set => SetProperty(ref _errorCollapsedWidth, value);
    }

    public double ExpandedWidth
    {
        get => _expandedWidth;
        set => SetProperty(ref _expandedWidth, value);
    }

    public double CollapsedHeight
    {
        get => _collapsedHeight;
        set => SetProperty(ref _collapsedHeight, value);
    }

    public double ExpandedHeight
    {
        get => _expandedHeight;
        set => SetProperty(ref _expandedHeight, value);
    }

    public double StatusRowHeight
    {
        get => _statusRowHeight;
        set
        {
            if (!SetProperty(ref _statusRowHeight, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StatusRowGridHeight));
        }
    }

    public GridLength StatusRowGridHeight => new(StatusRowHeight);

    public double ExpandedRegionExpandedHeight
    {
        get => _expandedRegionExpandedHeight;
        set => SetProperty(ref _expandedRegionExpandedHeight, value);
    }

    public double ActionPanelMinHeight
    {
        get => _actionPanelMinHeight;
        set => SetProperty(ref _actionPanelMinHeight, value);
    }

    public double TopMargin
    {
        get => _topMargin;
        set
        {
            if (!SetProperty(ref _topMargin, value))
            {
                return;
            }

            OnPropertyChanged(nameof(TopMarginThickness));
        }
    }

    public Thickness TopMarginThickness => new(0, TopMargin, 0, 0);

    public double ExpandedRegionTopSpacing
    {
        get => _expandedRegionTopSpacing;
        set
        {
            if (!SetProperty(ref _expandedRegionTopSpacing, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ExpandedRegionTopSpacingThickness));
        }
    }

    public Thickness ExpandedRegionTopSpacingThickness => new(0, ExpandedRegionTopSpacing, 0, 0);

    public double CollapsedShellRadius
    {
        get => _collapsedShellRadius;
        set
        {
            if (!SetProperty(ref _collapsedShellRadius, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CollapsedShellCornerRadius));
        }
    }

    public double ExpandedShellRadius
    {
        get => _expandedShellRadius;
        set
        {
            if (!SetProperty(ref _expandedShellRadius, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ExpandedShellCornerRadius));
        }
    }

    public CornerRadius CollapsedShellCornerRadius => new(0, 0, CollapsedShellRadius, CollapsedShellRadius);

    public CornerRadius ExpandedShellCornerRadius => new(0, 0, ExpandedShellRadius, ExpandedShellRadius);

    public double BottomCornerRadius
    {
        get => _bottomCornerRadius;
        set => SetProperty(ref _bottomCornerRadius, value);
    }

    public double GetCollapsedWidth(ClaudecodeStatus status)
    {
        return status switch
        {
            ClaudecodeStatus.Working => WorkingCollapsedWidth,
            ClaudecodeStatus.NeedsApproval => ApprovalCollapsedWidth,
            ClaudecodeStatus.NeedsChoice => ChoiceCollapsedWidth,
            ClaudecodeStatus.Error => ErrorCollapsedWidth,
            _ => IdleCollapsedWidth
        };
    }
}
