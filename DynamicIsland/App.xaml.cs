using System.Windows;
using DynamicIsland.Services;
using DynamicIsland.UI;
using DynamicIsland.Utils;
using DynamicIsland.ViewModels;

namespace DynamicIsland;

public partial class App : System.Windows.Application
{
    private ICodexStatusService? _service;
    private StatusViewModel? _viewModel;
    private TrayIconService? _trayIconService;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DiagnosticsLogger.ConfigureVerboseLogging(AppRuntimeOptions.ResolveDebugMode());
        DiagnosticsLogger.WriteInfo("App startup begin.");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        _service = CreateStatusService();
        var layoutSettings = (IslandLayoutSettings)Resources["IslandLayoutConfig"];
        _viewModel = new StatusViewModel(_service, layoutSettings);
        DiagnosticsLogger.WriteInfo("Service and view model created.");

        var mainWindow = new MainWindow(_viewModel, layoutSettings);
        MainWindow = mainWindow;
        DiagnosticsLogger.WriteInfo("MainWindow constructed.");
        _trayIconService = new TrayIconService(ShowMainWindow, ExitApplication);
        mainWindow.Show();
        DiagnosticsLogger.WriteInfo("MainWindow.Show completed.");

        await _viewModel.InitializeAsync();
        DiagnosticsLogger.WriteInfo("ViewModel initialization completed.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticsLogger.WriteInfo($"App exit. Code={e.ApplicationExitCode}");
        _trayIconService?.Dispose();
        _viewModel?.Dispose();
        _service?.Dispose();
        DiagnosticsLogger.Shutdown();
        base.OnExit(e);
    }

    private static ICodexStatusService CreateStatusService()
    {
        var mode = AppRuntimeOptions.ResolveServiceMode();
        DiagnosticsLogger.WriteInfo($"Configured service mode: {mode}");
        if (string.Equals(mode, "mock", StringComparison.OrdinalIgnoreCase))
        {
            DiagnosticsLogger.WriteInfo("Using mock Codex status service.");
            return new MockCodexStatusService();
        }

        DiagnosticsLogger.WriteInfo("Using Codex CLI status service.");
        return new CodexCliStatusService();
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticsLogger.WriteError($"Dispatcher exception: {e.Exception}");
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        DiagnosticsLogger.WriteError($"Unhandled exception: {e.ExceptionObject}");
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
