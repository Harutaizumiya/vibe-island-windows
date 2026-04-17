using System.IO;
using System.Text.Json;

namespace DynamicIsland.Utils;

internal sealed class AppRuntimeOptions
{
    private const string ConfigFileName = "islandsettings.json";
    private static readonly Lazy<AppRuntimeOptions> CurrentOptions = new(Load);
    private const string ServiceModeEnvironmentVariable = "DYNAMIC_ISLAND_SERVICE_MODE";
    private const string DebugModeEnvironmentVariable = "DYNAMIC_ISLAND_DEBUG_MODE";

    public static AppRuntimeOptions Current => CurrentOptions.Value;

    public string ServiceMode { get; init; } = "codex";

    public bool DebugMode { get; init; }

    public bool ExpandOnHover { get; init; } = true;

    public int HoverAutoCollapseSeconds { get; init; } = 5;

    public int ManualAutoCollapseSeconds { get; init; } = 10;

    public int ExpandedLayoutRefreshDelayMilliseconds { get; init; } = 80;

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
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            var root = document.RootElement;

            var serviceMode = root.TryGetProperty("serviceMode", out var serviceModeElement)
                && serviceModeElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(serviceModeElement.GetString())
                ? serviceModeElement.GetString()!
                : "codex";

            var debugMode = root.TryGetProperty("debugMode", out var debugModeElement)
                && debugModeElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && debugModeElement.GetBoolean();

            var interactionElement = root.TryGetProperty("interaction", out var interactionValue)
                && interactionValue.ValueKind == JsonValueKind.Object
                ? interactionValue
                : default;

            return new AppRuntimeOptions
            {
                ServiceMode = serviceMode,
                DebugMode = debugMode,
                ExpandOnHover = ReadBoolean(interactionElement, "expandOnHover", fallback: true),
                HoverAutoCollapseSeconds = ReadPositiveInt(interactionElement, "hoverAutoCollapseSeconds", fallback: 5),
                ManualAutoCollapseSeconds = ReadPositiveInt(interactionElement, "manualAutoCollapseSeconds", fallback: 10),
                ExpandedLayoutRefreshDelayMilliseconds = ReadPositiveInt(interactionElement, "expandedLayoutRefreshDelayMilliseconds", fallback: 80)
            };
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.WriteError($"Failed to read {ConfigFileName}: {ex.Message}");
            return new AppRuntimeOptions();
        }
    }

    public static string ResolveServiceMode()
    {
        var value = Environment.GetEnvironmentVariable(ServiceModeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return Current.ServiceMode;
    }

    public static bool ResolveDebugMode()
    {
        var value = Environment.GetEnvironmentVariable(DebugModeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        return Current.DebugMode;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName, bool fallback)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
    }

    private static int ReadPositiveInt(JsonElement element, string propertyName, int fallback)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            && parsed > 0
            ? parsed
            : fallback;
    }
}
