using LinguaCue.Infrastructure;
using LinguaCue.Models;

namespace LinguaCue.Services;

public sealed class SubtitlePipeline(
    PortableLayout layout,
    RuntimeToolResolver toolResolver,
    FfmpegAudioExtractor audioExtractor,
    WhisperTranscriber transcriber,
    HyMtTranslator translator,
    SrtSubtitleService subtitleService)
{
    public async Task<PipelineResult> RunAsync(
        PipelineRequest request,
        IProgress<PipelineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new PipelineProgress(PipelineStage.Validating, 1, "正在检查输入与本地模型"));
        ValidateRequest(request);
        ValidateToolchain(request);
        Directory.CreateDirectory(request.OutputDirectory);

        var jobDirectory = layout.CreateJobDirectory();
        var audioPath = Path.Combine(jobDirectory, "audio.wav");
        var transcriptBasePath = Path.Combine(jobDirectory, "transcript");

        try
        {
            var extractionProgress = new InlineProgress<double>(value =>
                progress?.Report(new PipelineProgress(
                    PipelineStage.ExtractingAudio,
                    3 + value * 15,
                    "正在提取 16 kHz 单声道音频")));
            await audioExtractor.ExtractAsync(
                request.InputPath,
                audioPath,
                extractionProgress,
                detail => progress?.Report(new PipelineProgress(PipelineStage.ExtractingAudio, 10, "正在提取音频", detail)),
                cancellationToken);

            var transcriptionProgress = new InlineProgress<double>(value =>
                progress?.Report(new PipelineProgress(
                    PipelineStage.Transcribing,
                    18 + value * 42,
                    "whisper.cpp 正在生成带时间轴的原文")));
            var transcriptPath = await transcriber.TranscribeAsync(
                audioPath,
                transcriptBasePath,
                request.SourceLanguage,
                request.Acceleration,
                request.PerformanceProfile,
                request.Threads,
                transcriptionProgress,
                detail => progress?.Report(new PipelineProgress(PipelineStage.Transcribing, 35, "正在转录", detail)),
                cancellationToken);

            var cues = await subtitleService.ReadAsync(transcriptPath, cancellationToken);
            if (cues.Count == 0)
            {
                throw new InvalidDataException("Whisper 未生成可用字幕。请检查音轨、语言与模型。 ");
            }

            var safeBaseName = TaskOutputPathPlanner.SanitizeFileName(
                string.IsNullOrWhiteSpace(request.OutputBaseName)
                    ? Path.GetFileNameWithoutExtension(request.InputPath)
                    : request.OutputBaseName);
            var sourcePath = Path.Combine(request.OutputDirectory, $"{safeBaseName}.source.srt");
            progress?.Report(new PipelineProgress(PipelineStage.WritingSubtitles, 61, "正在写入原文字幕"));
            await subtitleService.WriteSourceAsync(sourcePath, cues, cancellationToken);

            string? translatedPath = null;
            string? bilingualPath = null;
            IReadOnlyList<SubtitleCue> finalCues = cues;

            if (request.Translate)
            {
                var translationProgress = new InlineProgress<double>(value =>
                    progress?.Report(new PipelineProgress(
                        PipelineStage.Translating,
                        62 + value * 32,
                        $"{request.TranslationModel.DisplayName} 正在翻译")));
                finalCues = await translator.TranslateAsync(
                    cues,
                    request.TargetLanguage,
                    request.TranslationModel,
                    request.Acceleration,
                    request.Threads,
                    translationProgress,
                    detail => progress?.Report(new PipelineProgress(PipelineStage.Translating, 72, "正在翻译字幕", detail)),
                    cancellationToken);

                translatedPath = Path.Combine(request.OutputDirectory, $"{safeBaseName}.{request.TargetLanguage.Code}.srt");
                progress?.Report(new PipelineProgress(PipelineStage.WritingSubtitles, 95, "正在写入译文字幕"));
                await subtitleService.WriteTranslatedAsync(translatedPath, finalCues, cancellationToken);

                if (request.GenerateBilingual)
                {
                    bilingualPath = Path.Combine(
                        request.OutputDirectory,
                        $"{safeBaseName}.bilingual.{request.TargetLanguage.Code}.srt");
                    await subtitleService.WriteBilingualAsync(bilingualPath, finalCues, cancellationToken);
                }
            }

            progress?.Report(new PipelineProgress(PipelineStage.Completed, 100, $"已生成 {finalCues.Count} 条字幕"));
            return new PipelineResult(finalCues, sourcePath, translatedPath, bilingualPath);
        }
        finally
        {
            TryDeleteJobDirectory(jobDirectory);
        }
    }

    private void ValidateToolchain(PipelineRequest request)
    {
        var snapshot = toolResolver.Inspect();
        if (!snapshot.IsTranscriptionReady)
        {
            var missing = new[] { snapshot.Ffmpeg, snapshot.Whisper, snapshot.WhisperModel }
                .Where(component => !component.IsReady)
                .Select(component => component.Name);
            throw new InvalidOperationException($"转录环境未就绪：{string.Join("、", missing)}。");
        }

        if (request.Translate)
        {
            if (!snapshot.Llama.IsReady)
            {
                throw new InvalidOperationException("翻译环境未就绪：缺少 llama.cpp。");
            }

            if (layout.FindTranslationModel(request.TranslationModel) is null)
            {
                throw new InvalidOperationException($"翻译环境未就绪：缺少 {request.TranslationModel.FileName}。");
            }
        }
    }

    private static void ValidateRequest(PipelineRequest request)
    {
        if (!File.Exists(request.InputPath))
        {
            throw new FileNotFoundException("输入媒体文件不存在。", request.InputPath);
        }

        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            throw new ArgumentException("请选择输出目录。", nameof(request));
        }

        if (request.Translate && request.SourceLanguage.Code == request.TargetLanguage.Code)
        {
            throw new ArgumentException("源语言与目标语言不能相同。", nameof(request));
        }
    }

    private void TryDeleteJobDirectory(string jobDirectory)
    {
        try
        {
            var resolvedTempRoot = Path.GetFullPath(layout.TempRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolvedJob = Path.GetFullPath(jobDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (resolvedJob.StartsWith(resolvedTempRoot, comparison) && Directory.Exists(jobDirectory))
            {
                Directory.Delete(jobDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temporary files can be reclaimed on the next startup.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the job directory when another process still owns a file.
        }
    }

}
