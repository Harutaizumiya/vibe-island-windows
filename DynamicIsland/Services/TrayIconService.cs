using System.Drawing;
using DynamicIsland.Utils;
using Forms = System.Windows.Forms;

namespace DynamicIsland.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Action _showAction;
    private readonly Action _exitAction;

    public TrayIconService(Action showAction, Action exitAction)
    {
        _showAction = showAction;
        _exitAction = exitAction;

        var contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.Add("Show Island", null, (_, _) => _showAction());
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => _exitAction());

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Dynamic Island",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = contextMenu
        };

        _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;
        DiagnosticsLogger.Write("Tray icon initialized.");
    }

    public void Dispose()
    {
        _notifyIcon.DoubleClick -= OnNotifyIconDoubleClick;

        if (_notifyIcon.ContextMenuStrip is not null)
        {
            _notifyIcon.ContextMenuStrip.Dispose();
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        DiagnosticsLogger.Write("Tray icon disposed.");
    }

    private void OnNotifyIconDoubleClick(object? sender, EventArgs e)
    {
        _showAction();
    }
}
