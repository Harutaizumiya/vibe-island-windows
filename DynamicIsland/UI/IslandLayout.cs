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
    public static CornerRadius CollapsedShellCornerRadius => new(CollapsedShellRadius);

    // Top inverse-corner notch size.
    // Used by MainWindow.xaml.cs when building the custom clip for MainSurface.
    public static double TopInverseCornerRadius => 18;
    public static double BottomCornerRadius => 26;
}
