using System.IO;
using System.Text.Json;

namespace DynamicIsland.Utils;

internal sealed class AppRuntimeOptions
{
    private const string ConfigFileName = "islandsettings.json";
    private static readonly Lazy<AppRuntimeOptions> CurrentOptions = new(Load);

    public static AppRuntimeOptions Current => CurrentOptions.Value;

    public string ServiceMode { get; init; } = "codex";

    public bool DebugMode { get; init; }

    private static AppRuntimeOptions Load()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
        if (!File.Exists(configPath))
        {
            return new AppRuntimeOptions();
        }

        try
        {
            var json = File.ReadAllText(configPath);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var serviceMode = root.TryGetProperty("serviceMode", out var serviceModeElement)
                && serviceModeElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(serviceModeElement.GetString())
                ? serviceModeElement.GetString()!
                : "codex";

            var debugMode = root.TryGetProperty("debugMode", out var debugModeElement)
                && debugModeElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && debugModeElement.GetBoolean();

            return new AppRuntimeOptions
            {
                ServiceMode = serviceMode,
                DebugMode = debugMode
            };
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Write($"Failed to read {ConfigFileName}: {ex.Message}");
            return new AppRuntimeOptions();
        }
    }

    public static string ResolveServiceMode()
    {
        var value = Environment.GetEnvironmentVariable("DYNAMIC_ISLAND_SERVICE_MODE");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return Current.ServiceMode;
    }

    public static bool ResolveDebugMode()
    {
        var value = Environment.GetEnvironmentVariable("DYNAMIC_ISLAND_DEBUG_MODE");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        return Current.DebugMode;
    }
}
