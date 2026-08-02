using System.Text;
using System.Text.RegularExpressions;
using LinguaCue.Infrastructure;
using LinguaCue.Models;
using LinguaCue.Services;

namespace LinguaCue.Tests;

public sealed class SubtitlePipelineTests
{
    [Fact]
    public async Task RunAsync_WithTranslation_WritesSourceTranslatedAndBilingualFiles()
    {
        var appRoot = CreateTemporaryDirectory();
        var dataRoot = CreateTemporaryDirectory();
        var outputRoot = CreateTemporaryDirectory();
        try
        {
            var layout = PortableLayout.Create(appRoot, dataRoot);
            var resolver = new RuntimeToolResolver(layout);
            CreateFakeToolchain(layout, resolver.RuntimeIdentifier);
            CreateFakeModels(appRoot);
            var inputPath = Path.Combine(appRoot, "demo.mp4");
            await File.WriteAllBytesAsync(inputPath, [0, 1, 2, 3]);
            var processRunner = new FakeProcessRunner { ReturnPartialBatchOnce = true };
            var subtitleService = new SrtSubtitleService();
            var outputLocation = TaskOutputPathPlanner.Reserve(outputRoot, inputPath);
            var pipeline = new SubtitlePipeline(
                layout,
                resolver,
                new FfmpegAudioExtractor(processRunner, resolver),
                new WhisperTranscriber(processRunner, resolver, layout),
                new HyMtTranslator(processRunner, resolver, layout),
                subtitleService);

            var result = await pipeline.RunAsync(new PipelineRequest(
                inputPath,
                outputLocation.TaskDirectory,
                ModelCatalog.SourceLanguages[0],
                ModelCatalog.TargetLanguages[0],
                Translate: true,
                GenerateBilingual: true,
                ModelCatalog.TranslationProfiles[0],
                OutputBaseName: outputLocation.OutputBaseName));

            Assert.Equal(2, result.Cues.Count);
            Assert.Equal("译文-1", result.Cues[0].TranslatedText);
            Assert.Equal("补译-2", result.Cues[1].TranslatedText);
            Assert.Contains("--seed", processRunner.Invocations.Last(request =>
                Path.GetFileNameWithoutExtension(request.FileName) == "llama-completion").Arguments);
            Assert.True(File.Exists(result.SourceSubtitlePath));
            Assert.True(File.Exists(result.TranslatedSubtitlePath));
            Assert.True(File.Exists(result.BilingualSubtitlePath));
            Assert.Equal(outputLocation.TaskDirectory, Path.GetDirectoryName(result.SourceSubtitlePath));
            Assert.Empty(Directory.EnumerateFiles(outputRoot));
            var bilingual = await File.ReadAllTextAsync(result.BilingualSubtitlePath!);
            Assert.Contains("Hello from LinguaCue", bilingual);
            Assert.Contains("译文-1", bilingual);
            Assert.Equal(5, processRunner.Invocations.Count);
        }
        finally
        {
            Directory.Delete(appRoot, recursive: true);
            Directory.Delete(dataRoot, recursive: true);
            Directory.Delete(outputRoot, recursive: true);
        }
    }

    private static void CreateFakeToolchain(PortableLayout layout, string runtimeIdentifier)
    {
        var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        CreateTool("ffmpeg", $"ffmpeg{suffix}");
        CreateTool("ffmpeg", $"ffprobe{suffix}");
        CreateTool("whisper", $"whisper-cli{suffix}");
        CreateTool("llama", $"llama-completion{suffix}");
        return;

        void CreateTool(string category, string fileName)
        {
            var directory = Path.Combine(layout.RuntimeRoot, runtimeIdentifier, category);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, fileName), "fake");
        }
    }

    private static void CreateFakeModels(string appRoot)
    {
        var speechDirectory = Path.Combine(appRoot, "models", "speech");
        var translationDirectory = Path.Combine(appRoot, "models", "translation");
        Directory.CreateDirectory(speechDirectory);
        Directory.CreateDirectory(translationDirectory);
        File.WriteAllText(Path.Combine(speechDirectory, ModelCatalog.WhisperModelFileName), "fake");
        File.WriteAllText(
            Path.Combine(translationDirectory, ModelCatalog.TranslationProfiles[0].FileName),
            "fake");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LinguaCue.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private int llamaInvocationCount;

        public List<ProcessRequest> Invocations { get; } = [];
        public bool ReturnPartialBatchOnce { get; init; }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            Action<string>? onStandardOutput = null,
            Action<string>? onStandardError = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add(request);
            var toolName = Path.GetFileNameWithoutExtension(request.FileName);
            return toolName switch
            {
                "ffprobe" => Task.FromResult(new ProcessResult(0, "12.5", string.Empty)),
                "ffmpeg" => Task.FromResult(RunFfmpeg(request, onStandardOutput)),
                "whisper-cli" => Task.FromResult(RunWhisper(request, onStandardError)),
                "llama-completion" => Task.FromResult(RunLlama(request)),
                _ => throw new InvalidOperationException($"Unexpected fake tool: {toolName}")
            };
        }

        private static ProcessResult RunFfmpeg(ProcessRequest request, Action<string>? progress)
        {
            File.WriteAllBytes(request.Arguments[^1], [1, 2, 3]);
            progress?.Invoke("out_time_us=12500000");
            return new ProcessResult(0, "out_time_us=12500000", string.Empty);
        }

        private static ProcessResult RunWhisper(ProcessRequest request, Action<string>? progress)
        {
            var outputBase = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "-of") + 1];
            File.WriteAllText(
                outputBase + ".srt",
                "1\n00:00:01,000 --> 00:00:03,000\nHello from LinguaCue\n\n" +
                "2\n00:00:03,500 --> 00:00:05,000\nEverything stays local\n",
                new UTF8Encoding(false));
            progress?.Invoke("progress = 100%");
            return new ProcessResult(0, string.Empty, "progress = 100%");
        }

        private ProcessResult RunLlama(ProcessRequest request)
        {
            llamaInvocationCount++;
            var prompt = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "-p") + 1];
            var markers = Regex.Matches(prompt, @"<<<LC_(?<id>\d{6})>>>");
            if (markers.Count == 0)
            {
                return new ProcessResult(0, "补译-2", string.Empty);
            }

            var output = new StringBuilder();
            var markerCount = ReturnPartialBatchOnce && llamaInvocationCount == 1 ? 1 : markers.Count;
            for (var index = 0; index < markerCount; index++)
            {
                output.AppendLine(markers[index].Value);
                output.AppendLine($"译文-{index + 1}");
            }

            return new ProcessResult(0, output.ToString(), string.Empty);
        }
    }
}
