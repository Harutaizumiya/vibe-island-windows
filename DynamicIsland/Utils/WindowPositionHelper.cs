using System.Windows;

namespace DynamicIsland.Utils;

public static class WindowPositionHelper
{
    public static void PositionTopCenter(Window window, double topMargin)
    {
        var workArea = SystemParameters.WorkArea;
        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;

        if (width <= 0)
        {
            return;
        }

        window.Left = workArea.Left + ((workArea.Width - width) / 2);
        window.Top = workArea.Top + topMargin;
    }
}
