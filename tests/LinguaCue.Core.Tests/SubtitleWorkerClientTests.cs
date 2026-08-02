using LinguaCue.Infrastructure;
using LinguaCue.Models;
using LinguaCue.Services;

namespace LinguaCue.Tests;

public sealed class SubtitleWorkerClientTests
{
    [Fact]
    public async Task RunAsync_WorkerError_PropagatesStructuredUtf8Message()
    {
        var dataRoot = CreateTemporaryDirectory();
        try
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                ?? throw new InvalidOperationException("无法确定测试配置目录。");
            var solutionRoot = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                ".."));
            var workerRoot = Path.Combine(
                solutionRoot,
                "src",
                "LinguaCue.Cli",
                "bin",
                configuration,
                "net8.0");
            var layout = PortableLayout.Create(workerRoot, dataRoot);
            var client = new SubtitleWorkerClient(layout);
            var status = client.Inspect();
            var logs = new List<string>();
            var request = new PipelineRequest(
                Path.Combine(dataRoot, "不存在.mp4"),
                dataRoot,
                ModelCatalog.SourceLanguages[0],
                ModelCatalog.TargetLanguages[0],
                Translate: false,
                GenerateBilingual: false,
                ModelCatalog.TranslationProfiles[0]);

            var exception = await Assert.ThrowsAsync<SubtitleWorkerException>(() =>
                client.RunAsync(request, log: logs.Add));

            Assert.Equal("输入媒体文件不存在。", exception.Message);
            Assert.Contains("FileNotFoundException", exception.Detail);
            Assert.True(status.IsReady);
            Assert.NotNull(status.Path);
            Assert.Contains(logs, message =>
                message.StartsWith("调用字幕控制台：", StringComparison.Ordinal) &&
                message.Contains("--input", StringComparison.Ordinal) &&
                message.Contains("不存在.mp4", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LinguaCue.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
