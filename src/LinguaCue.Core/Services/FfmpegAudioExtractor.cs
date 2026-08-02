using System.Globalization;
using LinguaCue.Infrastructure;

namespace LinguaCue.Services;

public sealed class FfmpegAudioExtractor(IProcessRunner processRunner, RuntimeToolResolver toolResolver)
{
    public async Task ExtractAsync(
        string inputPath,
        string outputWavPath,
        IProgress<double>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = toolResolver.Resolve(RuntimeTool.Ffmpeg)
            ?? throw new FileNotFoundException("未找到 FFmpeg。请将它放入 runtimes/<rid>/ffmpeg。", "ffmpeg");
        var duration = await ProbeDurationAsync(inputPath, cancellationToken);

        var result = await processRunner.RunAsync(
            new ProcessRequest(
                ffmpeg,
                [
                    "-hide_banner",
                    "-loglevel", "error",
                    "-y",
                    "-i", inputPath,
                    "-vn",
                    "-ac", "1",
                    "-ar", "16000",
                    "-c:a", "pcm_s16le",
                    "-progress", "pipe:1",
                    "-nostats",
                    outputWavPath
                ]),
            line =>
            {
                if (duration is { TotalMilliseconds: > 0 } && TryParseProgressTime(line, out var elapsed))
                {
                    progress?.Report(Math.Clamp(elapsed.TotalMilliseconds / duration.Value.TotalMilliseconds, 0, 1));
                }
            },
            log,
            cancellationToken);

        if (result.ExitCode != 0 || !File.Exists(outputWavPath))
        {
            throw new ToolExecutionException(
                $"FFmpeg 提取音频失败。{Environment.NewLine}{LastUsefulLine(result.StandardError)}",
                result);
        }

        progress?.Report(1);
    }

    private async Task<TimeSpan?> ProbeDurationAsync(string inputPath, CancellationToken cancellationToken)
    {
        var ffprobe = toolResolver.Resolve(RuntimeTool.Ffprobe);
        if (ffprobe is null)
        {
            return null;
        }

        var result = await processRunner.RunAsync(
            new ProcessRequest(
                ffprobe,
                [
                    "-v", "error",
                    "-show_entries", "format=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    inputPath
                ]),
            cancellationToken: cancellationToken);

        return result.ExitCode == 0 &&
               double.TryParse(result.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    private static bool TryParseProgressTime(string line, out TimeSpan elapsed)
    {
        elapsed = TimeSpan.Zero;
        var separator = line.IndexOf('=');
        if (separator <= 0 || separator == line.Length - 1)
        {
            return false;
        }

        var key = line[..separator];
        var value = line[(separator + 1)..];
        if (key is "out_time_us" or "out_time_ms" &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
        {
            elapsed = TimeSpan.FromTicks(microseconds * 10);
            return true;
        }

        if (key == "out_time" && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out elapsed))
        {
            return true;
        }

        return false;
    }

    private static string LastUsefulLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "没有错误详情。";
}

