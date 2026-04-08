using System.Windows;

namespace DynamicIsland.UI;

public static class IslandLayout
{
    // Fixed transparent host window size in MainWindow.xaml.
    public static double WindowWidth => 520;
    public static double WindowHeight => 300;

    // Host window distance from the top edge of the usable screen.
    // MainWindow.xaml.cs passes this into WindowPositionHelper.PositionTopCenter().
    public static double ScreenTopMargin => 0;

    // Main island shell size.
    // Collapsed width still cooperates with StatusViewModel.GetCollapsedWidth().
    public static double CollapsedWidth => 332;
    public static double ExpandedWidth => 492;
    public static double CollapsedHeight => 88;
    public static double ExpandedHeight => 188;

    // Header/content sizing.
    public static double StatusRowHeight => 56;
    public static GridLength StatusRowGridHeight => new(StatusRowHeight);
    public static double ExpandedRegionExpandedHeight => 144;
    public static double ActionPanelMinHeight => 118;

    // Spacing inside the shell.
    public static double TopMargin => 0;
    public static double ExpandedRegionTopSpacing => 8;

    // Outer shell radii used by MainWindow.xaml.cs.
    public static double CollapsedShellRadius => 32;
    public static double ExpandedShellRadius => 26;
    public static CornerRadius CollapsedShellCornerRadius => new(0, 0, CollapsedShellRadius, CollapsedShellRadius);
    public static CornerRadius ExpandedShellCornerRadius => new(0, 0, ExpandedShellRadius, ExpandedShellRadius);

    // Custom shell clip profile used by MainWindow.xaml.cs.
    // These match the user's reference shoulders: 28x12 with a small rounded transition.
    public static double TopShoulderWidth => 12;
    public static double TopShoulderHeight => 8;
    public static double TopShoulderRadius => 0;
    public static double BottomCornerRadius => 24;
}
