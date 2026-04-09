using System.Windows;
using DynamicIsland.Services;
using DynamicIsland.UI;
using DynamicIsland.Utils;
using DynamicIsland.ViewModels;

namespace DynamicIsland;

public partial class App : System.Windows.Application
{
    private IClaudecodeService? _service;
    private StatusViewModel? _viewModel;
    private TrayIconService? _trayIconService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DiagnosticsLogger.Write("App startup begin.");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        _service = new MockClaudecodeService();
        var layoutSettings = (IslandLayoutSettings)Resources["IslandLayoutConfig"];
        _viewModel = new StatusViewModel(_service, layoutSettings);
        DiagnosticsLogger.Write("Service and view model created.");

        var mainWindow = new MainWindow(_viewModel, layoutSettings);
        MainWindow = mainWindow;
        DiagnosticsLogger.Write("MainWindow constructed.");
        _trayIconService = new TrayIconService(ShowMainWindow, ExitApplication);
        mainWindow.Show();
        DiagnosticsLogger.Write("MainWindow.Show completed.");

        await _viewModel.InitializeAsync();
        DiagnosticsLogger.Write("ViewModel initialization completed.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticsLogger.Write($"App exit. Code={e.ApplicationExitCode}");
        _trayIconService?.Dispose();
        _viewModel?.Dispose();
        _service?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticsLogger.Write($"Dispatcher exception: {e.Exception}");
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        DiagnosticsLogger.Write($"Unhandled exception: {e.ExceptionObject}");
    }

    private void ShowMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Dispatcher.Invoke(() =>
        {
            if (!MainWindow.IsVisible)
            {
                MainWindow.Show();
            }

            if (MainWindow.WindowState == WindowState.Minimized)
            {
                MainWindow.WindowState = WindowState.Normal;
            }

            MainWindow.Topmost = true;
            MainWindow.Activate();
        });
    }

    private void ExitApplication()
    {
        Dispatcher.Invoke(Shutdown);
    }
}
