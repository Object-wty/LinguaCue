using System.Text.Json;
using System.Text.Json.Serialization;
using LinguaCue.Infrastructure;
using LinguaCue.Models;

namespace LinguaCue.Services;

public sealed record LinguaCueUserSettings(
    int MaxConcurrentTasks,
    PerformanceProfile PerformanceProfile,
    SubtitleBurnStyle BurnStyle)
{
    public static LinguaCueUserSettings Default { get; } = new(
        2,
        PerformanceProfile.Balanced,
        SubtitleBurnStyle.Default);
}

public sealed class UserSettingsService(PortableLayout layout)
{
    private readonly string settingsPath = Path.Combine(layout.DataRoot, "settings.json");
    private readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public LinguaCueUserSettings Load()
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return LinguaCueUserSettings.Default;
            }

            var settings = JsonSerializer.Deserialize<LinguaCueUserSettings>(
                File.ReadAllText(settingsPath),
                serializerOptions);
            if (settings is null)
            {
                return LinguaCueUserSettings.Default;
            }

            var burnStyle = settings.BurnStyle.FontName == "Noto Sans CJK SC"
                ? settings.BurnStyle with { FontName = SubtitleBurnStyle.Default.FontName }
                : settings.BurnStyle;
            if (IsLegacyDefaultBurnStyle(burnStyle))
            {
                burnStyle = SubtitleBurnStyle.Default;
            }

            return settings with
            {
                MaxConcurrentTasks = Math.Clamp(settings.MaxConcurrentTasks, 1, 4),
                BurnStyle = burnStyle
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return LinguaCueUserSettings.Default;
        }
    }

    private static bool IsLegacyDefaultBurnStyle(SubtitleBurnStyle style) => style is
    {
        FontName: "Noto Sans SC",
        FontSize: 42 or 48,
        PrimaryColor: "#FFFFFF",
        OutlineColor: "#000000",
        OutlineWidth: 3,
        MarginBottom: 60
    };

    public void Save(LinguaCueUserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var temporaryPath = settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, serializerOptions));
            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Settings persistence must never interrupt a media task.
        }
    }
}
