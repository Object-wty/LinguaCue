using LinguaCue.Infrastructure;
using LinguaCue.Models;
using LinguaCue.Services;

namespace LinguaCue.Tests;

public sealed class UserSettingsServiceTests
{
    [Theory]
    [InlineData(42)]
    [InlineData(48)]
    public void Load_CompleteLegacyBurnDefaults_MigratesToCurrentDefaults(double legacyFontSize)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            WriteSettings(root, legacyFontSize, marginBottom: 60);

            var settings = CreateService(root).Load();

            Assert.Equal(SubtitleBurnStyle.Default, settings.BurnStyle);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_CustomBurnStyleThatResemblesLegacyDefaults_PreservesCustomization()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            WriteSettings(root, fontSize: 42, marginBottom: 35);

            var settings = CreateService(root).Load();

            Assert.Equal(42, settings.BurnStyle.FontSize);
            Assert.Equal(35, settings.BurnStyle.MarginBottom);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static UserSettingsService CreateService(string root) =>
        new(PortableLayout.Create(root, root));

    private static void WriteSettings(string root, double fontSize, int marginBottom) =>
        File.WriteAllText(
            Path.Combine(root, "settings.json"),
            $$"""
              {
                "maxConcurrentTasks": 2,
                "performanceProfile": "balanced",
                "burnStyle": {
                  "fontName": "Noto Sans SC",
                  "fontSize": {{fontSize}},
                  "primaryColor": "#FFFFFF",
                  "outlineColor": "#000000",
                  "outlineWidth": 3,
                  "marginBottom": {{marginBottom}}
                }
              }
              """);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LinguaCue.Settings.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
