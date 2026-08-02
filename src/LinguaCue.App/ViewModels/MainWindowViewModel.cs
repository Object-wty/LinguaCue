using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaCue.Infrastructure;
using LinguaCue.Models;
using LinguaCue.Services;

namespace LinguaCue.ViewModels;

public sealed record PerformanceProfileOption(
    PerformanceProfile Value,
    string DisplayName,
    string Description)
{
    public override string ToString() => DisplayName;
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IStorageService storageService;
    private readonly IThemeService themeService;
    private readonly ModelImportService modelImportService;
    private readonly RuntimeToolResolver toolResolver;
    private readonly SrtSubtitleService subtitleService;
    private readonly SubtitleWorkerClient workerClient;
    private readonly TaskQueueScheduler scheduler;
    private readonly UserSettingsService settingsService;
    private readonly DispatcherTimer elapsedTimer;
    private LinguaCueUserSettings settings;
    private CancellationTokenSource? importCancellation;

    public MainWindowViewModel(
        IStorageService storageService,
        IThemeService themeService,
        ModelImportService modelImportService,
        RuntimeToolResolver toolResolver,
        SrtSubtitleService subtitleService,
        SubtitleWorkerClient workerClient,
        TaskQueueScheduler scheduler,
        UserSettingsService settingsService)
    {
        this.storageService = storageService;
        this.themeService = themeService;
        this.modelImportService = modelImportService;
        this.toolResolver = toolResolver;
        this.subtitleService = subtitleService;
        this.workerClient = workerClient;
        this.scheduler = scheduler;
        this.settingsService = settingsService;
        settings = settingsService.Load();

        SelectedSourceLanguage = SourceLanguages[0];
        SelectedTargetLanguage = TargetLanguages[0];
        SelectedTranslationModel = TranslationModels[0];
        SelectedPerformanceProfile = PerformanceProfiles.First(option => option.Value == settings.PerformanceProfile);
        MaxConcurrentTasks = settings.MaxConcurrentTasks;
        scheduler.MaxConcurrency = MaxConcurrentTasks;
        scheduler.StateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshQueueState);
        Tasks.CollectionChanged += (_, _) => RefreshQueueState();

        elapsedTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var task in Tasks)
            {
                task.RefreshElapsed();
            }
        });
    }

    public IReadOnlyList<LanguageOption> SourceLanguages { get; } = ModelCatalog.SourceLanguages;

    public IReadOnlyList<LanguageOption> TargetLanguages { get; } = ModelCatalog.TargetLanguages;

    public IReadOnlyList<TranslationModelProfile> TranslationModels { get; } = ModelCatalog.TranslationProfiles;

    public IReadOnlyList<int> ConcurrencyOptions { get; } = [1, 2, 3, 4];

    public IReadOnlyList<PerformanceProfileOption> PerformanceProfiles { get; } =
    [
        new(PerformanceProfile.Fast, "GPU 极速", "Whisper 1/1 搜索，速度最快，准确率可能略有下降"),
        new(PerformanceProfile.Balanced, "GPU 平衡", "Whisper 3/3 搜索，默认兼顾速度与准确率"),
        new(PerformanceProfile.Quality, "最高质量", "Whisper 5/5 搜索，速度较慢")
    ];

    public ObservableCollection<SubtitleTaskViewModel> Tasks { get; } = [];

    public ObservableCollection<ComponentStatusViewModel> EnvironmentItems { get; } = [];

    public bool HasTasks => Tasks.Count > 0;

    public bool NoTasks => !HasTasks;

    public bool HasPendingTasks => Tasks.Any(task => task.State == SubtitleTaskState.Pending);

    public bool HasActiveTasks => Tasks.Any(task => task.IsActive || task.IsQueued);

    public bool HasSelectedTask => SelectedTask is not null;

    public bool CanExport => SelectedTask?.HasCues == true;

    public bool CanImportModels => !IsImporting && !HasActiveTasks;

    public string QueueSummary
    {
        get
        {
            var completed = Tasks.Count(task => task.State is SubtitleTaskState.Ready or SubtitleTaskState.Completed);
            var failed = Tasks.Count(task => task.State is SubtitleTaskState.Failed or SubtitleTaskState.BurnFailed);
            return $"{Tasks.Count} 个任务 · 运行 {scheduler.RunningCount} · 排队 {scheduler.PendingCount} · 完成 {completed}" +
                   (failed > 0 ? $" · 失败 {failed}" : string.Empty);
        }
    }

    [ObservableProperty]
    private string _outputDirectory = string.Empty;

    [ObservableProperty]
    private LanguageOption _selectedSourceLanguage;

    [ObservableProperty]
    private LanguageOption _selectedTargetLanguage;

    [ObservableProperty]
    private TranslationModelProfile _selectedTranslationModel;

    [ObservableProperty]
    private PerformanceProfileOption _selectedPerformanceProfile;

    [ObservableProperty]
    private int _maxConcurrentTasks = 2;

    [ObservableProperty]
    private bool _translateEnabled = true;

    [ObservableProperty]
    private bool _generateBilingual = true;

    [ObservableProperty]
    private bool _isDarkMode;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private string _statusText = "添加多个视频后，点击开始队列";

    [ObservableProperty]
    private string _environmentSummary = "正在检查本地运行环境…";

    [ObservableProperty]
    private SubtitleTaskViewModel? _selectedTask;

    partial void OnTranslateEnabledChanged(bool value)
    {
        if (!value)
        {
            GenerateBilingual = false;
        }
    }

    partial void OnIsDarkModeChanged(bool value) => themeService.SetDarkMode(value);

    partial void OnIsImportingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanImportModels));
        ImportWhisperModelCommand.NotifyCanExecuteChanged();
        ImportTranslationModelCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTaskChanged(SubtitleTaskViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedTask));
        OnPropertyChanged(nameof(CanExport));
        ExportSubtitlesCommand.NotifyCanExecuteChanged();
    }

    partial void OnMaxConcurrentTasksChanged(int value)
    {
        var clamped = Math.Clamp(value, 1, 4);
        if (clamped != value)
        {
            MaxConcurrentTasks = clamped;
            return;
        }

        scheduler.MaxConcurrency = clamped;
        SaveSettings();
        RefreshQueueState();
    }

    partial void OnSelectedPerformanceProfileChanged(PerformanceProfileOption value) => SaveSettings();

    public void Initialize()
    {
        RefreshEnvironment();
        elapsedTimer.Start();
    }

    public void CancelAllTasks()
    {
        foreach (var task in Tasks.Where(task => task.CanCancel))
        {
            task.CancelCommand.Execute(null);
        }
    }

    [RelayCommand]
    private async Task PickInputAsync()
    {
        if (TranslateEnabled && SelectedSourceLanguage.Code == SelectedTargetLanguage.Code)
        {
            StatusText = "源语言与目标语言不能相同";
            return;
        }

        var selected = await storageService.PickMediaFilesAsync();
        if (selected.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            OutputDirectory = Path.GetDirectoryName(selected[0]) ?? string.Empty;
        }

        var added = 0;
        foreach (var inputPath in selected)
        {
            if (Tasks.Any(task =>
                    PathEquals(task.InputPath, inputPath) &&
                    task.State is SubtitleTaskState.Pending or
                        SubtitleTaskState.Queued or
                        SubtitleTaskState.Running or
                        SubtitleTaskState.BurnQueued or
                        SubtitleTaskState.Burning))
            {
                continue;
            }

            var outputBase = ReserveOutputBaseName(inputPath, OutputDirectory);
            var threads = Math.Max(1, Environment.ProcessorCount / Math.Max(MaxConcurrentTasks, 1));
            var request = new PipelineRequest(
                Path.GetFullPath(inputPath),
                Path.GetFullPath(OutputDirectory),
                SelectedSourceLanguage,
                SelectedTargetLanguage,
                TranslateEnabled,
                TranslateEnabled && GenerateBilingual,
                SelectedTranslationModel,
                AccelerationMode.Auto,
                SelectedPerformanceProfile.Value,
                threads,
                outputBase);
            var task = new SubtitleTaskViewModel(
                request,
                workerClient,
                settings.BurnStyle,
                RetryTask,
                QueueBurn,
                SaveBurnDefaults);
            task.StateChanged += TaskOnStateChanged;
            Tasks.Add(task);
            SelectedTask = task;
            added++;
        }

        StatusText = added > 0
            ? $"已添加 {added} 个视频，配置已为每个任务保存快照"
            : "所选视频已在活动队列中";
        RefreshQueueState();
    }

    [RelayCommand]
    private async Task PickOutputDirectoryAsync()
    {
        var selected = await storageService.PickOutputDirectoryAsync();
        if (selected is not null)
        {
            OutputDirectory = selected;
            StatusText = "输出目录已更新；仅应用于之后添加的任务";
        }
    }

    [RelayCommand(CanExecute = nameof(HasPendingTasks))]
    private void StartPending()
    {
        foreach (var task in Tasks.Where(task => task.State == SubtitleTaskState.Pending).ToArray())
        {
            QueueConvert(task);
        }

        RefreshQueueState();
    }

    [RelayCommand(CanExecute = nameof(HasActiveTasks))]
    private void CancelAll()
    {
        CancelAllTasks();
        StatusText = "已请求取消全部活动任务";
    }

    [RelayCommand(CanExecute = nameof(CanImportModels))]
    private async Task ImportWhisperModelAsync()
    {
        var selected = await storageService.PickModelFileAsync("whisper");
        if (selected is null)
        {
            return;
        }

        await RunImportAsync("正在导入 Whisper 模型…", token => modelImportService.ImportWhisperModelAsync(selected, token));
    }

    [RelayCommand(CanExecute = nameof(CanImportModels))]
    private async Task ImportTranslationModelAsync()
    {
        var selected = await storageService.PickModelFileAsync("translation");
        if (selected is null)
        {
            return;
        }

        await RunImportAsync(
            $"正在导入 {SelectedTranslationModel.DisplayName}…",
            token => modelImportService.ImportTranslationModelAsync(selected, SelectedTranslationModel, token));
    }

    [RelayCommand]
    private void RefreshEnvironment()
    {
        var snapshot = toolResolver.Inspect();
        var statuses = new[]
            {
                workerClient.Inspect(),
                snapshot.Ffmpeg,
                snapshot.Whisper,
                snapshot.WhisperModel,
                snapshot.Llama
            }
            .Concat(snapshot.TranslationModels)
            .ToArray();
        EnvironmentItems.Clear();
        foreach (var status in statuses)
        {
            EnvironmentItems.Add(new ComponentStatusViewModel(status));
        }

        var readyCount = statuses.Count(status => status.IsReady);
        EnvironmentSummary = readyCount == statuses.Length
            ? "全部组件已就绪"
            : $"{readyCount}/{statuses.Length} 项就绪 · 可导入缺失模型";
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportSubtitlesAsync()
    {
        var task = SelectedTask;
        if (task is null)
        {
            return;
        }

        var suggested = Path.GetFileName(task.SelectedSubtitleTrack?.Path)
            ?? $"{task.Request.OutputBaseName}.srt";
        var path = await storageService.PickSubtitleSavePathAsync(suggested);
        if (path is null)
        {
            return;
        }

        var cues = task.Cues.Select(cue => cue.ToModel()).ToArray();
        var selectedName = task.SelectedSubtitleTrack?.DisplayName;
        if (selectedName == "双语字幕")
        {
            await subtitleService.WriteBilingualAsync(path, cues);
        }
        else if (selectedName == "译文字幕")
        {
            await subtitleService.WriteTranslatedAsync(path, cues);
        }
        else
        {
            await subtitleService.WriteSourceAsync(path, cues);
        }

        StatusText = $"已导出编辑后的字幕：{path}";
    }

    private void QueueConvert(SubtitleTaskViewModel task)
    {
        task.MarkQueued();
        scheduler.Enqueue(task.RunConvertAsync, task.QueueCancellationToken);
    }

    private void QueueBurn(SubtitleTaskViewModel task)
    {
        if (!task.CanBurn)
        {
            return;
        }

        task.MarkQueued(burn: true);
        scheduler.Enqueue(task.RunBurnAsync, task.QueueCancellationToken);
    }

    private void RetryTask(SubtitleTaskViewModel task)
    {
        if (task.HasConversionResult)
        {
            task.MarkQueued(burn: true);
            scheduler.Enqueue(task.RunBurnAsync, task.QueueCancellationToken);
        }
        else
        {
            QueueConvert(task);
        }
    }

    private async Task RunImportAsync(string message, Func<CancellationToken, Task<string>> import)
    {
        importCancellation?.Dispose();
        importCancellation = new CancellationTokenSource();
        IsImporting = true;
        StatusText = message;
        try
        {
            var path = await import(importCancellation.Token);
            StatusText = $"模型导入完成：{path}";
            RefreshEnvironment();
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消模型导入";
        }
        catch (Exception exception)
        {
            StatusText = $"模型导入失败：{exception.Message}";
        }
        finally
        {
            IsImporting = false;
        }
    }

    private string ReserveOutputBaseName(string inputPath, string outputDirectory)
    {
        var original = SanitizeFileName(Path.GetFileNameWithoutExtension(inputPath));
        for (var index = 1; index < 10_000; index++)
        {
            var candidate = index == 1 ? original : $"{original} ({index})";
            var reservedByTask = Tasks.Any(task =>
                PathEquals(task.OutputDirectory, outputDirectory) &&
                string.Equals(task.Request.OutputBaseName, candidate, PathComparison));
            var exists = Directory.Exists(outputDirectory) &&
                         Directory.EnumerateFiles(outputDirectory, candidate + "*.srt").Any();
            if (!reservedByTask && !exists)
            {
                return candidate;
            }
        }

        throw new IOException("无法为字幕任务分配不重名的输出名称。");
    }

    private void TaskOnStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => TaskOnStateChanged(sender, e));
            return;
        }

        OnPropertyChanged(nameof(CanExport));
        RefreshQueueState();
    }

    private void RefreshQueueState()
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(NoTasks));
        OnPropertyChanged(nameof(HasPendingTasks));
        OnPropertyChanged(nameof(HasActiveTasks));
        OnPropertyChanged(nameof(CanImportModels));
        OnPropertyChanged(nameof(QueueSummary));
        StartPendingCommand.NotifyCanExecuteChanged();
        CancelAllCommand.NotifyCanExecuteChanged();
        ImportWhisperModelCommand.NotifyCanExecuteChanged();
        ImportTranslationModelCommand.NotifyCanExecuteChanged();
        ExportSubtitlesCommand.NotifyCanExecuteChanged();
    }

    private void SaveBurnDefaults(SubtitleBurnStyle style)
    {
        settings = settings with { BurnStyle = style };
        SaveSettings();
    }

    private void SaveSettings()
    {
        if (SelectedPerformanceProfile is null)
        {
            return;
        }

        settings = settings with
        {
            MaxConcurrentTasks = Math.Clamp(MaxConcurrentTasks, 1, 4),
            PerformanceProfile = SelectedPerformanceProfile.Value
        };
        settingsService.Save(settings);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "subtitles" : sanitized;
    }
}
