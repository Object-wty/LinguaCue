using LinguaCue.Models;
using LinguaCue.Services;

namespace LinguaCue.Tests;

public sealed class AssSubtitleServiceTests
{
    [Fact]
    public async Task WriteAsync_UsesVideoCanvasUtf8StyleAndEscapesMultilineText()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "中文 字幕.ass");
            var cues = new[]
            {
                new SubtitleCue(
                    1,
                    TimeSpan.FromSeconds(1.25),
                    TimeSpan.FromSeconds(3.5),
                    "第一行\nsecond {line} \\N")
            };
            var style = new SubtitleBurnStyle(
                "Noto Sans CJK SC",
                52,
                "#12ABEF",
                "#010203",
                4,
                72);

            await new AssSubtitleService().WriteAsync(path, cues, 1920, 1080, style);
            var content = await File.ReadAllTextAsync(path);

            Assert.Contains("PlayResX: 1920", content);
            Assert.Contains("PlayResY: 1080", content);
            Assert.Contains("Noto Sans CJK SC,52,&H00EFAB12", content);
            Assert.Contains("&H00030201", content);
            Assert.Contains("1:25", content.Replace("0:00:01.25", "1:25"));
            Assert.Contains("第一行\\Nsecond \\{line\\} \\\\N", content);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("#FFFFFF", "&H00FFFFFF")]
    [InlineData("#123456", "&H00563412")]
    public void ToAssColor_ConvertsRgbToAssBgr(string input, string expected) =>
        Assert.Equal(expected, AssSubtitleService.ToAssColor(input));

    [Fact]
    public void BuildArguments_EscapesWindowsDriveAndKeepsInputAsSingleArgument()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fontDirectory = Path.Combine(root, "字体");
            Directory.CreateDirectory(fontDirectory);
            var arguments = SubtitleBurner.BuildArguments(
                "D:\\视频 文件\\demo (1).mp4",
                "D:\\临时 目录\\字幕.ass",
                "D:\\输出\\demo.subtitled.mp4",
                "h264_nvenc",
                fontDirectory);

            Assert.Contains("D:\\视频 文件\\demo (1).mp4", arguments);
            var filter = arguments[arguments.ToList().IndexOf("-vf") + 1];
            Assert.Contains("D\\:/临时 目录/字幕.ass", filter);
            var escapedFontDirectory = Path.GetFullPath(fontDirectory)
                .Replace('\\', '/')
                .Replace(":", "\\:", StringComparison.Ordinal);
            Assert.Contains($"fontsdir='{escapedFontDirectory}'", filter);
            Assert.Contains("h264_nvenc", arguments);
            Assert.Contains("+faststart", arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LinguaCue.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
