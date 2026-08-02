using System.Globalization;
using System.Text.Json;
using LinguaCue.Infrastructure;
using LinguaCue.Models;

namespace LinguaCue.Services;

public sealed class SubtitleBurner(
    IProcessRunner processRunner,
    RuntimeToolResolver toolResolver,
    PortableLayout layout,
    SrtSubtitleService srtSubtitleService,
    AssSubtitleService assSubtitleService)
{
    public async Task<BurnResult> BurnAsync(
        BurnRequest request,
        IProgress<PipelineProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var ffmpeg = toolResolver.Resolve(RuntimeTool.Ffmpeg)
            ?? throw new FileNotFoundException("未找到 FFmpeg。请将它放入 runtimes/<rid>/ffmpeg。", "ffmpeg");
        var video = await ProbeVideoAsync(request.InputVideoPath, cancellationToken);
        var cues = await srtSubtitleService.ReadAsync(request.SubtitlePath, cancellationToken);
        var jobDirectory = layout.CreateJobDirectory();
        var assPath = Path.Combine(jobDirectory, "subtitles.ass");
        var temporaryOutput = Path.Combine(jobDirectory, "burning.mp4");

        try
        {
            progress?.Report(new PipelineProgress(PipelineStage.Validating, 1, "正在准备字幕烧录"));
            await assSubtitleService.WriteAsync(
                assPath,
                cues,
                video.Width,
                video.Height,
                request.Style,
                cancellationToken);

            var encoder = request.Encoder == BurnEncoderMode.Software
                ? "libx264"
                : await SelectEncoderAsync(ffmpeg, cancellationToken);
            progress?.Report(new PipelineProgress(
                PipelineStage.BurningSubtitles,
                2,
                $"正在使用 {encoder} 烧录字幕"));

            var result = await RunFfmpegAsync(encoder);
            if ((result.ExitCode != 0 || !File.Exists(temporaryOutput)) && encoder != "libx264")
            {
                log?.Invoke($"硬件编码器 {encoder} 执行失败，自动回退 libx264。");
                TryDelete(temporaryOutput);
                encoder = "libx264";
                result = await RunFfmpegAsync(encoder);
            }

            if (result.ExitCode != 0 || !File.Exists(temporaryOutput))
            {
                throw new ToolExecutionException(
                    $"FFmpeg 烧录字幕失败（编码器 {encoder}，退出码 {result.ExitCode}）。{Environment.NewLine}{LastUsefulLine(result.StandardError)}",
                    result);
            }

            var finalPath = FindAvailableOutputPath(request.OutputPath);
            var parent = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.Move(temporaryOutput, finalPath);
            progress?.Report(new PipelineProgress(PipelineStage.Completed, 100, "字幕已烧录到视频"));
            return new BurnResult(finalPath, encoder, video.Duration ?? TimeSpan.Zero);

            async Task<ProcessResult> RunFfmpegAsync(string selectedEncoder)
            {
                var arguments = BuildArguments(
                    request.InputVideoPath,
                    assPath,
                    temporaryOutput,
                    selectedEncoder,
                    layout.BundledFontRoot);
                log?.Invoke($"调用 FFmpeg 烧录：{CommandLineFormatter.Format(ffmpeg, arguments)}");
                return await processRunner.RunAsync(
                    new ProcessRequest(ffmpeg, arguments),
                    line =>
                    {
                        if (video.Duration is { TotalMilliseconds: > 0 } && TryParseProgressTime(line, out var elapsed))
                        {
                            var ratio = Math.Clamp(elapsed.TotalMilliseconds / video.Duration.Value.TotalMilliseconds, 0, 1);
                            progress?.Report(new PipelineProgress(
                                PipelineStage.BurningSubtitles,
                                2 + ratio * 97,
                                $"正在使用 {selectedEncoder} 烧录字幕"));
                        }
                    },
                    log,
                    cancellationToken);
            }
        }
        finally
        {
            TryDeleteJobDirectory(jobDirectory);
        }
    }

    public static IReadOnlyList<string> BuildArguments(
        string inputPath,
        string assPath,
        string outputPath,
        string encoder,
        string? fontDirectory)
    {
        var assFilter = $"ass=filename='{EscapeFilterPath(assPath)}'";
        if (!string.IsNullOrWhiteSpace(fontDirectory) && Directory.Exists(fontDirectory))
        {
            assFilter += $":fontsdir='{EscapeFilterPath(fontDirectory)}'";
        }

        var arguments = new List<string> { "-hide_banner", "-loglevel", "warning", "-y" };
        if (encoder == "h264_vaapi")
        {
            arguments.AddRange(["-vaapi_device", "/dev/dri/renderD128"]);
        }

        arguments.AddRange([
            "-i", inputPath,
            "-map", "0:v:0",
            "-map", "0:a?",
            "-vf", encoder == "h264_vaapi" ? $"{assFilter},format=nv12,hwupload" : assFilter,
            "-sn"
        ]);
        arguments.AddRange(BuildEncoderArguments(encoder));
        arguments.AddRange([
            "-c:a", "aac",
            "-b:a", "192k",
            "-movflags", "+faststart",
            "-progress", "pipe:1",
            "-nostats",
            outputPath
        ]);
        return arguments;
    }

    private async Task<VideoInfo> ProbeVideoAsync(string inputPath, CancellationToken cancellationToken)
    {
        var ffprobe = toolResolver.Resolve(RuntimeTool.Ffprobe)
            ?? throw new FileNotFoundException("烧录字幕需要 ffprobe。", "ffprobe");
        var arguments = new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height:format=duration",
            "-of", "json",
            inputPath
        };
        var result = await processRunner.RunAsync(
            new ProcessRequest(ffprobe, arguments),
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new ToolExecutionException("FFprobe 无法读取视频信息。", result);
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var stream = document.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault();
        if (stream.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("输入文件没有可用的视频轨道，无法烧录字幕。");
        }

        var width = stream.GetProperty("width").GetInt32();
        var height = stream.GetProperty("height").GetInt32();
        TimeSpan? duration = null;
        if (document.RootElement.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durationElement) &&
            double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            duration = TimeSpan.FromSeconds(seconds);
        }

        return new VideoInfo(width, height, duration);
    }

    private async Task<string> SelectEncoderAsync(string ffmpeg, CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new ProcessRequest(ffmpeg, ["-hide_banner", "-encoders"]),
            cancellationToken: cancellationToken);
        var encoders = result.StandardOutput + result.StandardError;
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "h264_nvenc", "h264_qsv", "h264_amf" }
            : OperatingSystem.IsMacOS()
                ? new[] { "h264_videotoolbox" }
                : File.Exists("/dev/dri/renderD128")
                    ? new[] { "h264_nvenc", "h264_vaapi" }
                    : new[] { "h264_nvenc" };
        return candidates.FirstOrDefault(candidate =>
                   encoders.Contains(candidate, StringComparison.OrdinalIgnoreCase))
               ?? "libx264";
    }

    private static IReadOnlyList<string> BuildEncoderArguments(string encoder) => encoder switch
    {
        "h264_nvenc" => ["-c:v", encoder, "-preset", "p5", "-cq", "22", "-b:v", "0", "-pix_fmt", "yuv420p"],
        "h264_qsv" => ["-c:v", encoder, "-preset", "medium", "-global_quality", "22", "-pix_fmt", "nv12"],
        "h264_amf" => ["-c:v", encoder, "-quality", "balanced", "-rc", "cqp", "-qp_i", "22", "-qp_p", "22", "-pix_fmt", "yuv420p"],
        "h264_videotoolbox" => ["-c:v", encoder, "-q:v", "65", "-pix_fmt", "yuv420p"],
        "h264_vaapi" => ["-c:v", encoder, "-global_quality", "22"],
        _ => ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p"]
    };

    private static string EscapeFilterPath(string path) =>
        Path.GetFullPath(path)
            .Replace('\\', '/')
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);

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

        return key == "out_time" && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out elapsed);
    }

    private static string FindAvailableOutputPath(string requestedPath)
    {
        var fullPath = Path.GetFullPath(requestedPath);
        if (!File.Exists(fullPath))
        {
            return fullPath;
        }

        var directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        for (var index = 2; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("无法为烧录视频分配不重名的输出文件。");
    }

    private static void ValidateRequest(BurnRequest request)
    {
        if (!File.Exists(request.InputVideoPath))
        {
            throw new FileNotFoundException("输入视频不存在。", request.InputVideoPath);
        }

        if (!File.Exists(request.SubtitlePath))
        {
            throw new FileNotFoundException("待烧录字幕不存在。", request.SubtitlePath);
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new ArgumentException("烧录输出路径不能为空。", nameof(request));
        }
    }

    private static string LastUsefulLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "没有错误详情。";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The job directory cleanup below will try again.
        }
    }

    private void TryDeleteJobDirectory(string jobDirectory)
    {
        try
        {
            var root = Path.GetFullPath(layout.TempRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var job = Path.GetFullPath(jobDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (job.StartsWith(root, comparison) && Directory.Exists(jobDirectory))
            {
                Directory.Delete(jobDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temporary files can be reclaimed on the next startup.
        }
    }

    private sealed record VideoInfo(int Width, int Height, TimeSpan? Duration);
}
