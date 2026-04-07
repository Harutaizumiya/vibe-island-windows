using System.IO;

namespace DynamicIsland.Utils;

public static class DiagnosticsLogger
{
    private static readonly string LogDirectory = AppContext.BaseDirectory;

    private static readonly string LogPath = Path.Combine(LogDirectory, "startup.log");

    public static string CurrentLogPath => LogPath;

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(
                LogPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
