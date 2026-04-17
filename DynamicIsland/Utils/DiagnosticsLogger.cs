using System.IO;
using System.Text;

namespace DynamicIsland.Utils;

public static class DiagnosticsLogger
{
    private static readonly string LogDirectory = AppContext.BaseDirectory;
    private static readonly string LogPath = Path.Combine(LogDirectory, "startup.log");
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static bool _verboseEnabled;
    private static bool _shutdownRequested;

    public static string CurrentLogPath => LogPath;

    public static void ConfigureVerboseLogging(bool enabled)
    {
        _verboseEnabled = enabled;
    }

    public static void Write(string message)
    {
        WriteInfo(message);
    }

    public static void WriteInfo(string message)
    {
        WriteCore(message);
    }

    public static void WriteVerbose(string message)
    {
        if (!_verboseEnabled)
        {
            return;
        }

        WriteCore(message);
    }

    public static void WriteError(string message)
    {
        WriteCore(message);
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            _shutdownRequested = true;
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static void WriteCore(string message)
    {
        try
        {
            lock (Gate)
            {
                if (_shutdownRequested)
                {
                    return;
                }

                Directory.CreateDirectory(LogDirectory);
                _writer ??= CreateWriter();
                _writer.Write('[');
                _writer.Write(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                _writer.Write("] ");
                _writer.WriteLine(message);
                _writer.Flush();
            }
        }
        catch
        {
        }
    }

    private static StreamWriter CreateWriter()
    {
        var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, Encoding.UTF8)
        {
            AutoFlush = true
        };
    }
}
