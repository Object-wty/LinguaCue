using Avalonia;
using Avalonia.Headless;
using LinguaCue;
using LinguaCue.Infrastructure;
using LinguaCue.Models;
using LinguaCue.Services;
using LinguaCue.ViewModels;
using LinguaCue.Views;

var darkMode = args.Contains("--dark", StringComparer.OrdinalIgnoreCase);
var withSampleCues = args.Contains("--sample", StringComparer.OrdinalIgnoreCase);
var outputPath = Path.GetFullPath(
    args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal))
    ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "ui", "main-window.png"));
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var dataRoot = Path.Combine(Path.GetTempPath(), $"LinguaCue-Snapshot-{Guid.NewGuid():N}");
try
{
    var layout = PortableLayout.Create(AppContext.BaseDirectory, dataRoot);
    var toolResolver = new RuntimeToolResolver(layout);
    var subtitleService = new SrtSubtitleService();
    var workerClient = new SubtitleWorkerClient(layout);
    var settingsService = new UserSettingsService(layout);
    var viewModel = new MainWindowViewModel(
        new SnapshotStorageService(),
        new AvaloniaThemeService(Application.Current!),
        new ModelImportService(layout),
        toolResolver,
        subtitleService,
        workerClient,
        new TaskQueueScheduler(2),
        settingsService);
    viewModel.Initialize();
    viewModel.IsDarkMode = darkMode;
    if (withSampleCues)
    {
        var running = CreateSampleTask(
            "Async Await 性能解析.mp4",
            SubtitleTaskState.Running,
            62,
            "Hy-MT2 正在翻译字幕",
            "离线翻译");
        var queued = CreateSampleTask(
            "Avalonia 跨平台教程.mp4",
            SubtitleTaskState.Queued,
            0,
            "排队中",
            "等待可用处理槽位");
        var completed = CreateSampleTask(
            "LinguaCue 功能演示.mp4",
            SubtitleTaskState.Ready,
            100,
            "字幕转换完成",
            "已生成 3 个 SRT · 3 条字幕");
        completed.Cues.Add(new SubtitleCueViewModel(new SubtitleCue(
            1,
            TimeSpan.FromSeconds(1.2),
            TimeSpan.FromSeconds(4.8),
            "Welcome to LinguaCue. Everything stays on your computer.",
            "欢迎使用 LinguaCue，所有内容都只保留在你的电脑上。")));
        completed.Cues.Add(new SubtitleCueViewModel(new SubtitleCue(
            2,
            TimeSpan.FromSeconds(5.1),
            TimeSpan.FromSeconds(8.4),
            "You can review the original and translated subtitles side by side.",
            "你可以在这里对照校对原文和译文字幕。")));
        completed.Cues.Add(new SubtitleCueViewModel(new SubtitleCue(
            3,
            TimeSpan.FromSeconds(9),
            TimeSpan.FromSeconds(12.25),
            "Export source, translated, or bilingual SRT files when ready.",
            "校对完成后，可导出原文、译文或双语 SRT。")));
        viewModel.Tasks.Add(running);
        viewModel.Tasks.Add(queued);
        viewModel.Tasks.Add(completed);
        viewModel.SelectedTask = completed;

        SubtitleTaskViewModel CreateSampleTask(
            string fileName,
            SubtitleTaskState state,
            double progress,
            string status,
            string stage)
        {
            var task = new SubtitleTaskViewModel(
                new PipelineRequest(
                    Path.Combine(dataRoot, fileName),
                    dataRoot,
                    ModelCatalog.SourceLanguages[0],
                    ModelCatalog.TargetLanguages[0],
                    true,
                    true,
                    ModelCatalog.TranslationProfiles[0],
                    OutputBaseName: Path.GetFileNameWithoutExtension(fileName)),
                workerClient,
                SubtitleBurnStyle.Default,
                _ => { },
                _ => { },
                _ => { });
            task.State = state;
            task.ProgressValue = progress;
            task.StatusText = status;
            task.StageText = stage;
            return task;
        }
    }

    var window = new MainWindow { DataContext = viewModel };
    window.Show();

    var frame = window.CaptureRenderedFrame()
        ?? throw new InvalidOperationException("Avalonia headless renderer did not return a frame.");
    frame.Save(outputPath);
    window.Close();
    Console.WriteLine(outputPath);
}
finally
{
    if (Directory.Exists(dataRoot))
    {
        Directory.Delete(dataRoot, recursive: true);
    }
}

internal sealed class SnapshotStorageService : IStorageService
{
    public Task<IReadOnlyList<string>> PickMediaFilesAsync() =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> PickOutputDirectoryAsync() => Task.FromResult<string?>(null);

    public Task<string?> PickModelFileAsync(string modelType) => Task.FromResult<string?>(null);

    public Task<string?> PickSubtitleSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);
}
