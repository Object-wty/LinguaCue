using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LinguaCue.Models;

namespace LinguaCue.Services;

public sealed partial class SrtSubtitleService
{
    public async Task<IReadOnlyList<SubtitleCue>> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        return Parse(content);
    }

    public IReadOnlyList<SubtitleCue> Parse(string content)
    {
        var normalized = content.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
        var blocks = BlankLineRegex().Split(normalized);
        var cues = new List<SubtitleCue>(blocks.Length);

        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            if (lines.Length < 2)
            {
                continue;
            }

            var timelineIndex = Array.FindIndex(lines, line => TimelineRegex().IsMatch(line));
            if (timelineIndex < 0)
            {
                continue;
            }

            var match = TimelineRegex().Match(lines[timelineIndex]);
            if (!TryParseTimestamp(match.Groups["start"].Value, out var start) ||
                !TryParseTimestamp(match.Groups["end"].Value, out var end))
            {
                continue;
            }

            var parsedIndex = timelineIndex > 0 && int.TryParse(lines[timelineIndex - 1], out var sourceIndex)
                ? sourceIndex
                : cues.Count + 1;
            var text = string.Join('\n', lines.Skip(timelineIndex + 1)).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            cues.Add(new SubtitleCue(parsedIndex, start, end, text));
        }

        return cues;
    }

    public Task WriteSourceAsync(
        string path,
        IEnumerable<SubtitleCue> cues,
        CancellationToken cancellationToken = default) =>
        WriteAsync(path, cues, cue => cue.SourceText, cancellationToken);

    public Task WriteTranslatedAsync(
        string path,
        IEnumerable<SubtitleCue> cues,
        CancellationToken cancellationToken = default) =>
        WriteAsync(path, cues, cue => cue.TranslatedText ?? cue.SourceText, cancellationToken);

    public Task WriteBilingualAsync(
        string path,
        IEnumerable<SubtitleCue> cues,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            path,
            cues,
            cue => string.IsNullOrWhiteSpace(cue.TranslatedText)
                ? cue.SourceText
                : $"{cue.SourceText}\n{cue.TranslatedText}",
            cancellationToken);

    private static async Task WriteAsync(
        string path,
        IEnumerable<SubtitleCue> cues,
        Func<SubtitleCue, string> textSelector,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var builder = new StringBuilder();
        var index = 1;
        foreach (var cue in cues.OrderBy(cue => cue.Start))
        {
            builder.AppendLine(index.ToString(CultureInfo.InvariantCulture));
            builder.Append(FormatTimestamp(cue.Start));
            builder.Append(" --> ");
            builder.AppendLine(FormatTimestamp(cue.End));
            builder.AppendLine(NormalizeSubtitleText(textSelector(cue)));
            builder.AppendLine();
            index++;
        }

        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(false), cancellationToken);
    }

    public static string FormatTimestamp(TimeSpan value)
    {
        var hours = (int)Math.Floor(value.TotalHours);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}");
    }

    private static bool TryParseTimestamp(string value, out TimeSpan result)
    {
        var normalized = value.Replace('.', ',');
        return TimeSpan.TryParseExact(
            normalized,
            ["h\\:mm\\:ss\\,fff", "hh\\:mm\\:ss\\,fff"],
            CultureInfo.InvariantCulture,
            out result);
    }

    private static string NormalizeSubtitleText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    [GeneratedRegex(@"\n\s*\n+", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLineRegex();

    [GeneratedRegex(
        @"(?<start>\d{1,3}:\d{2}:\d{2}[,.]\d{3})\s*-->\s*(?<end>\d{1,3}:\d{2}:\d{2}[,.]\d{3})",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimelineRegex();
}
