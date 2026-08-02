using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LinguaCue.Infrastructure;
using LinguaCue.Models;

namespace LinguaCue.Services;

public sealed partial class HyMtTranslator(
    IProcessRunner processRunner,
    RuntimeToolResolver toolResolver,
    PortableLayout layout)
{
    private const int BatchSize = 4;
    private const int SamplingSeed = 42;

    public async Task<IReadOnlyList<SubtitleCue>> TranslateAsync(
        IReadOnlyList<SubtitleCue> cues,
        LanguageOption targetLanguage,
        TranslationModelProfile profile,
        AccelerationMode acceleration = AccelerationMode.Auto,
        int threads = 0,
        IProgress<double>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var llamaServer = toolResolver.ResolveWithBackend(RuntimeTool.LlamaServer, acceleration);
        var llama = toolResolver.ResolveWithBackend(RuntimeTool.Llama, acceleration);
        if (llamaServer is null && llama is null)
        {
            throw new FileNotFoundException(
                "未找到 llama.cpp。请将 llama-server 或 llama-completion 放入 runtimes/<rid>/llama。",
                "llama-server");
        }

        var model = layout.FindTranslationModel(profile)
            ?? throw new FileNotFoundException($"未找到翻译模型 {profile.FileName}。");

        var selectedBackend = (llamaServer ?? llama)!.Backend;
        try
        {
            return await TranslateWithToolsAsync(llamaServer, llama);
        }
        catch (ToolExecutionException) when (selectedBackend != RuntimeBackend.Cpu)
        {
            var cpuServer = toolResolver.ResolveWithBackend(RuntimeTool.LlamaServer, AccelerationMode.Cpu);
            var cpuLlama = toolResolver.ResolveWithBackend(RuntimeTool.Llama, AccelerationMode.Cpu);
            if (cpuServer is null && cpuLlama is null)
            {
                throw;
            }

            log?.Invoke($"llama.cpp {selectedBackend} 后端执行失败，自动回退 CPU 并重新翻译当前任务。");
            return await TranslateWithToolsAsync(cpuServer, cpuLlama);
        }

        async Task<IReadOnlyList<SubtitleCue>> TranslateWithToolsAsync(
            ResolvedRuntimeTool? server,
            ResolvedRuntimeTool? completion)
        {
            if (server is not null)
            {
                await using var session = await LlamaServerSession.StartAsync(
                    server,
                    model,
                    threads,
                    log,
                    cancellationToken);
                return await TranslateCoreAsync(
                    cues,
                    targetLanguage,
                    session.CompleteAsync,
                    progress,
                    log,
                    cancellationToken);
            }

            return await TranslateCoreAsync(
                cues,
                targetLanguage,
                (prompt, maximumTokens, token) => RunCompletionAsync(
                    completion!,
                    model,
                    prompt,
                    maximumTokens,
                    threads,
                    log,
                    token),
                progress,
                log,
                cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<SubtitleCue>> TranslateCoreAsync(
        IReadOnlyList<SubtitleCue> cues,
        LanguageOption targetLanguage,
        Func<string, int, CancellationToken, Task<string>> complete,
        IProgress<double>? progress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var translated = new List<SubtitleCue>(cues.Count);
        var recoveredCueCount = 0;

        for (var offset = 0; offset < cues.Count; offset += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = cues.Skip(offset).Take(BatchSize).ToArray();
            var batchPrompt = BuildBatchPrompt(batch, targetLanguage.PromptName);
            var output = CleanupCompletion(await complete(
                batchPrompt,
                EstimateBatchTokenLimit(batch),
                cancellationToken));
            var translations = ParseMarkedTranslations(output, batch);
            var missing = batch.Where(cue => !translations.ContainsKey(cue.Index)).ToArray();
            if (missing.Length > 0)
            {
                recoveredCueCount += missing.Length;
                foreach (var cue in missing)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var prompt = BuildSinglePrompt(cue.SourceText, targetLanguage.PromptName);
                    var translation = CleanupCompletion(await complete(
                        prompt,
                        EstimateSingleTokenLimit(cue),
                        cancellationToken));
                    if (string.IsNullOrWhiteSpace(translation))
                    {
                        throw new InvalidDataException($"字幕 {cue.Index} 的补译结果为空。");
                    }

                    translations[cue.Index] = translation;
                }
            }

            translated.AddRange(batch.Select(cue => cue.WithTranslation(translations[cue.Index])));
            progress?.Report(Math.Clamp((offset + batch.Length) / (double)cues.Count, 0, 1));
        }

        if (recoveredCueCount > 0)
        {
            log?.Invoke($"批量翻译完成，已自动补译 {recoveredCueCount} 条字幕，输出完整。");
        }

        return translated;
    }

    private async Task<string> RunCompletionAsync(
        ResolvedRuntimeTool llama,
        string model,
        string prompt,
        int maximumTokens,
        int threads,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var effectiveThreads = threads > 0 ? threads : Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        var arguments = new List<string>
        {
            "--model", model,
            "-p", prompt,
            "--jinja",
            "-ngl", llama.Backend == RuntimeBackend.Cpu ? "0" : "999",
            "--threads", effectiveThreads.ToString(CultureInfo.InvariantCulture),
            "-n", maximumTokens.ToString(CultureInfo.InvariantCulture),
            "-st",
            "--temp", "0",
            "--seed", SamplingSeed.ToString(CultureInfo.InvariantCulture),
            "--no-display-prompt"
        };
        log?.Invoke($"llama.cpp 后端：{llama.Backend}，线程：{effectiveThreads}");
        log?.Invoke($"调用 llama.cpp：{CommandLineFormatter.Format(llama.Path, arguments)}");
        var result = await processRunner.RunAsync(
            new ProcessRequest(
                llama.Path,
                arguments),
            cancellationToken: cancellationToken,
            onStandardError: log);

        if (result.ExitCode != 0)
        {
            var diagnostic = LastUsefulLine(result.StandardError);
            if (diagnostic == "没有错误详情。")
            {
                diagnostic = LastUsefulLine(result.StandardOutput);
            }

            var exitCode = unchecked((uint)result.ExitCode);
            var crashHint = exitCode == 0xC0000005
                ? "llama.cpp 进程发生访问冲突（0xC0000005），通常是运行包与当前 CPU/系统不兼容。"
                : $"llama.cpp 退出码：{result.ExitCode}（0x{exitCode:X8}）。";
            throw new ToolExecutionException(
                $"Hy-MT2 翻译失败。{Environment.NewLine}{crashHint}{Environment.NewLine}可执行文件：{llama.Path}{Environment.NewLine}模型：{model}{Environment.NewLine}诊断：{diagnostic}",
                result);
        }

        return CleanupCompletion(result.StandardOutput);
    }

    private static string BuildBatchPrompt(IReadOnlyList<SubtitleCue> batch, string targetLanguage)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Please accurately translate the following subtitle segments into {targetLanguage}.");
        builder.AppendLine($"There are exactly {batch.Count} segments. You must return exactly {batch.Count} marker blocks in the same order.");
        builder.AppendLine("Preserve every marker exactly. Translate only the text after each marker.");
        builder.AppendLine("Do not merge, omit, stop early, explain, escape, or translate any marker. Do not output anything before the first marker or after the final translation.");
        foreach (var cue in batch)
        {
            builder.AppendLine(FormatMarker(cue.Index));
            builder.AppendLine(cue.SourceText.Replace("<<<LC_", "<LC_", StringComparison.Ordinal));
        }

        return builder.ToString();
    }

    private static Dictionary<int, string> ParseMarkedTranslations(
        string output,
        IReadOnlyList<SubtitleCue> expectedCues)
    {
        var matches = MarkerRegex().Matches(output);
        var translations = new Dictionary<int, string>();
        var expectedIds = expectedCues.Select(cue => cue.Index).ToHashSet();
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            if (!int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cueId))
            {
                continue;
            }

            if (!expectedIds.Contains(cueId))
            {
                continue;
            }

            var contentStart = match.Index + match.Length;
            var contentEnd = index + 1 < matches.Count ? matches[index + 1].Index : output.Length;
            var translation = output[contentStart..contentEnd].Trim();
            if (!string.IsNullOrWhiteSpace(translation))
            {
                translations[cueId] = translation;
            }
        }

        return translations;
    }

    private static string BuildSinglePrompt(string sourceText, string targetLanguage) =>
        $"Translate the following text into {targetLanguage}. " +
        $"Only output the translated result without any additional explanation:\n{sourceText}";

    private static int EstimateBatchTokenLimit(IReadOnlyList<SubtitleCue> batch) =>
        Math.Clamp(batch.Sum(cue => cue.SourceText.Length) * 2 + batch.Count * 32, 256, 1_200);

    private static int EstimateSingleTokenLimit(SubtitleCue cue) =>
        Math.Clamp(cue.SourceText.Length * 3 + 64, 128, 384);

    private static string FormatMarker(int index) => $"<<<LC_{index:000000}>>>";

    private static string CleanupCompletion(string output) =>
        output.Replace("<|im_end|>", string.Empty, StringComparison.Ordinal)
            .Replace("<|endoftext|>", string.Empty, StringComparison.Ordinal)
            .Replace("[end of text]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

    private static string LastUsefulLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "没有错误详情。";

    [GeneratedRegex(@"<<<LC_(?<id>\d{6})>>>", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex();
}
