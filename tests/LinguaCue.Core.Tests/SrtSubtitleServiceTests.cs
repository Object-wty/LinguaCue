using System.Text;
using LinguaCue.Models;
using LinguaCue.Services;

namespace LinguaCue.Tests;

public sealed class SrtSubtitleServiceTests
{
    private readonly SrtSubtitleService _service = new();

    [Fact]
    public void Parse_AcceptsBomCommaAndDotMilliseconds()
    {
        const string content = "\uFEFF1\r\n00:00:01,250 --> 00:00:03,500\r\n你好\r\n\r\n" +
                               "2\n00:00:04.000 --> 00:00:06.125\nworld\nsecond line\n";

        var cues = _service.Parse(content);

        Assert.Equal(2, cues.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), cues[0].Start);
        Assert.Equal("你好", cues[0].SourceText);
        Assert.Equal(TimeSpan.FromMilliseconds(6125), cues[1].End);
        Assert.Equal("world\nsecond line", cues[1].SourceText);
    }

    [Fact]
    public async Task WriteBilingualAsync_PreservesTimelineAndUsesUtf8WithoutBom()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "bilingual.srt");
            var cues = new[]
            {
                new SubtitleCue(7, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4.5), "Hello", "你好")
            };

            await _service.WriteBilingualAsync(path, cues);

            var bytes = await File.ReadAllBytesAsync(path);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
            Assert.Contains(
                "1\n00:00:02,000 --> 00:00:04,500\nHello\n你好",
                text.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteSourceAsync_SortsAndRenumbersCues()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "source.srt");
            var cues = new[]
            {
                new SubtitleCue(20, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "second"),
                new SubtitleCue(10, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "first")
            };

            await _service.WriteSourceAsync(path, cues);
            var parsed = await _service.ReadAsync(path);

            Assert.Equal(["first", "second"], parsed.Select(cue => cue.SourceText));
            Assert.Equal([1, 2], parsed.Select(cue => cue.Index));
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
