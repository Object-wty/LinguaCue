using System.Globalization;
using System.Text.RegularExpressions;
using LinguaCue.Infrastructure;
using LinguaCue.Models;

namespace LinguaCue.Services;

public sealed partial class WhisperTranscriber(
    IProcessRunner processRunner,
    RuntimeToolResolver toolResolver,
    PortableLayout layout)
{
    public async Task<string> TranscribeAsync(
        string wavPath,
        string outputBasePath,
        LanguageOption sourceLanguage,
        AccelerationMode acceleration = AccelerationMode.Auto,
        PerformanceProfile performanceProfile = PerformanceProfile.Balanced,
        int threads = 0,
        IProgress<double>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var selected = toolResolver.ResolveWithBackend(RuntimeTool.Whisper, acceleration)
            ?? throw new FileNotFoundException("未找到 whisper.cpp。请将 whisper-cli 放入 runtimes/<rid>/whisper。", "whisper-cli");
        var model = layout.FindSpeechModel()
            ?? throw new FileNotFoundException($"未找到 Whisper 模型 {ModelCatalog.WhisperModelFileName}。");
        var outputPath = outputBasePath + ".srt";

        var result = await RunAsync(selected);
        if ((result.ExitCode != 0 || !File.Exists(outputPath)) && selected.Backend != RuntimeBackend.Cpu)
        {
            var cpu = toolResolver.ResolveWithBackend(RuntimeTool.Whisper, AccelerationMode.Cpu);
            if (cpu is not null && !string.Equals(cpu.Path, selected.Path, StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"Whisper {selected.Backend} 后端执行失败，自动回退 CPU。");
                TryDelete(outputPath);
                result = await RunAsync(cpu);
                selected = cpu;
            }
        }

        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new ToolExecutionException(
                $"whisper.cpp 转录失败（{selected.Backend}，退出码 {result.ExitCode}）。{Environment.NewLine}期望输出：{outputPath}{Environment.NewLine}{LastUsefulLine(result)}",
                result);
        }

        progress?.Report(1);
        return outputPath;

        async Task<ProcessResult> RunAsync(ResolvedRuntimeTool tool)
        {
            var searchSize = performanceProfile switch
            {
                PerformanceProfile.Fast => 1,
                PerformanceProfile.Quality => 5,
                _ => 3
            };
            var effectiveThreads = threads > 0
                ? threads
                : Math.Clamp(Environment.ProcessorCount, 1, 12);
            var arguments = new List<string>
            {
                "-m", model,
                "-f", wavPath,
                "-l", sourceLanguage.Code,
                "-t", effectiveThreads.ToString(CultureInfo.InvariantCulture),
                "-bs", searchSize.ToString(CultureInfo.InvariantCulture),
                "-bo", searchSize.ToString(CultureInfo.InvariantCulture),
                "-osrt",
                "-of", outputBasePath
            };
            if (tool.Backend == RuntimeBackend.Cpu)
            {
                arguments.Insert(arguments.Count - 3, "-ng");
            }

            log?.Invoke($"Whisper 后端：{tool.Backend}，线程：{effectiveThreads}，搜索：{searchSize}/{searchSize}");
            log?.Invoke($"调用 whisper.cpp：{CommandLineFormatter.Format(tool.Path, arguments)}");
            return await processRunner.RunAsync(
                new ProcessRequest(tool.Path, arguments),
                log,
                line =>
                {
                    log?.Invoke(line);
                    var match = ProgressRegex().Match(line);
                    if (match.Success && double.TryParse(match.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                    {
                        progress?.Report(Math.Clamp(value / 100d, 0, 1));
                    }
                },
                cancellationToken);
        }
    }

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
            // A second attempt will report the final tool error with diagnostics.
        }
    }

    [GeneratedRegex(@"(?:progress\s*=\s*|\[)(?<value>\d{1,3}(?:\.\d+)?)%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgressRegex();

    private static string LastUsefulLine(ProcessResult result)
    {
        var error = result.StandardError
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        if (!string.IsNullOrWhiteSpace(error))
        {
            return error;
        }

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? "没有错误详情。";
    }
}
