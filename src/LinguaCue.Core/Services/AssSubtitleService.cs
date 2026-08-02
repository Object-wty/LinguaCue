using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LinguaCue.Models;

namespace LinguaCue.Services;

public sealed partial class AssSubtitleService
{
    public async Task WriteAsync(
        string path,
        IReadOnlyList<SubtitleCue> cues,
        int videoWidth,
        int videoHeight,
        SubtitleBurnStyle style,
        CancellationToken cancellationToken = default)
    {
        if (cues.Count == 0)
        {
            throw new InvalidDataException("字幕文件中没有可烧录的字幕条目。");
        }

        ValidateStyle(style);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var builder = new StringBuilder();
        builder.AppendLine("[Script Info]");
        builder.AppendLine("ScriptType: v4.00+");
        builder.AppendLine($"PlayResX: {Math.Max(videoWidth, 16)}");
        builder.AppendLine($"PlayResY: {Math.Max(videoHeight, 16)}");
        builder.AppendLine("WrapStyle: 0");
        builder.AppendLine("ScaledBorderAndShadow: yes");
        builder.AppendLine("YCbCr Matrix: TV.709");
        builder.AppendLine();
        builder.AppendLine("[V4+ Styles]");
        builder.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        builder.Append("Style: Default,");
        builder.Append(EscapeStyleValue(style.FontName));
        builder.Append(',').Append(style.FontSize.ToString("0.##", CultureInfo.InvariantCulture));
        builder.Append(',').Append(ToAssColor(style.PrimaryColor));
        builder.Append(",&H000000FF,").Append(ToAssColor(style.OutlineColor));
        builder.Append(",&H80000000,0,0,0,0,100,100,0,0,1,");
        builder.Append(style.OutlineWidth.ToString("0.##", CultureInfo.InvariantCulture));
        builder.Append(",0,2,40,40,").Append(Math.Max(style.MarginBottom, 0)).AppendLine(",1");
        builder.AppendLine();
        builder.AppendLine("[Events]");
        builder.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        foreach (var cue in cues.OrderBy(cue => cue.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append("Dialogue: 0,");
            builder.Append(FormatTimestamp(cue.Start)).Append(',');
            builder.Append(FormatTimestamp(cue.End)).Append(",Default,,0,0,0,,");
            builder.AppendLine(EscapeDialogueText(cue.SourceText));
        }

        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(false), cancellationToken);
    }

    public static string ToAssColor(string color)
    {
        var match = HexColorRegex().Match(color.Trim());
        if (!match.Success)
        {
            throw new ArgumentException($"无效颜色：{color}。请使用 #RRGGBB。", nameof(color));
        }

        var value = match.Groups["rgb"].Value;
        return $"&H00{value[4..6]}{value[2..4]}{value[0..2]}".ToUpperInvariant();
    }

    public static string EscapeDialogueText(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("{", "\\{", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal)
            .Replace("\r\n", "\\N", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\\N", StringComparison.Ordinal)
            .Trim();

    private static string EscapeStyleValue(string value) =>
        value.Replace(',', ' ').Trim();

    private static string FormatTimestamp(TimeSpan value)
    {
        var clamped = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        var hours = (int)Math.Floor(clamped.TotalHours);
        var centiseconds = clamped.Milliseconds / 10;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours}:{clamped.Minutes:00}:{clamped.Seconds:00}.{centiseconds:00}");
    }

    private static void ValidateStyle(SubtitleBurnStyle style)
    {
        if (string.IsNullOrWhiteSpace(style.FontName))
        {
            throw new ArgumentException("字幕字体不能为空。", nameof(style));
        }

        if (style.FontSize is < 8 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(style), "字幕字号必须在 8–240 之间。");
        }

        if (style.OutlineWidth is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(style), "字幕描边必须在 0–20 之间。");
        }

        _ = ToAssColor(style.PrimaryColor);
        _ = ToAssColor(style.OutlineColor);
    }

    [GeneratedRegex("^#?(?<rgb>[0-9a-fA-F]{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColorRegex();
}
