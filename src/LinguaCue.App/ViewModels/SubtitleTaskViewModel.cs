using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinguaCue.Models;
using LinguaCue.Services;

namespace LinguaCue.ViewModels;

public enum SubtitleTaskState
{
    Pending,
    Queued,
    Running,
    Ready,
    BurnQueued,
    Burning,
    Completed,
    Failed,
    BurnFailed,
    Canceled
}

public sealed record SubtitleTrackOption(string DisplayName, string Path)
{
    public override string ToString() => DisplayName;
}

public sealed partial class SubtitleTaskViewModel : ObservableObject
{
    private readonly SubtitleWorkerClient workerClient;
    private readonly Action<SubtitleTaskViewModel> retryAction;
    private readonly Action<SubtitleTaskViewModel> burnAction;
    private readonly Action<SubtitleBurnStyle> saveBurnDefaults;
    private readonly Stopwatch stopwatch = new();
    private CancellationTokenSource? cancellation;
    private string? lastProgressDetail;
    private PipelineResult? pipelineResult;
    private SubtitleBurnStyle? queuedBurnStyle;
    private string? queuedBurnSubtitlePath;

    public SubtitleTaskViewModel(
        PipelineRequest request,
        SubtitleWorkerClient workerClient,
        SubtitleBurnStyle burnDefaults,
        Action<SubtitleTaskViewModel> retryAction,
        Action<SubtitleTaskViewModel> burnAction,
        Action<SubtitleBurnStyle> saveBurnDefaults)
    {
        Request = request;
        this.workerClient = workerClient;
        this.retryAction = retryAction;
        this.burnAction = burnAction;
        this.saveBurnDefaults = saveBurnDefaults;
        BurnFontName = burnDefaults.FontName;
        BurnFontSize = burnDefaults.FontSize;
        BurnPrimaryColor = burnDefaults.PrimaryColor;
        BurnOutlineColor = burnDefaults.OutlineColor;
        BurnOutlineWidth = burnDefaults.OutlineWidth;
        BurnMarginBottom = burnDefaults.MarginBottom;
    }

    public event EventHandler? StateChanged;

    public PipelineRequest Request { get; }

    public string InputPath => Request.InputPath;

    public string FileName => Path.GetFileName(InputPath);

    public string OutputDirectory => Request.OutputDirectory;

    public string ConfigurationText => Request.Translate
        ? $"{Request.SourceLanguage.DisplayName} → {Request.TargetLanguage.DisplayName} · {Request.TranslationModel.DisplayName}"
        : $"{Request.SourceLanguage.DisplayName} · 仅转录";

    public ObservableCollection<SubtitleCueViewModel> Cues { get; } = [];

    public ObservableCollection<SubtitleTrackOption> SubtitleTracks { get; } = [];

    public bool HasCues => Cues.Count > 0;

    public bool NoCues => !HasCues;

    public string CueCountText => HasCues ? $"{Cues.Count} 条" : "尚无字幕";

    public bool IsActive => State is SubtitleTaskState.Running or SubtitleTaskState.Burning;

    public bool IsQueued => State is SubtitleTaskState.Queued or SubtitleTaskState.BurnQueued;

    public bool CanCancel => IsActive || IsQueued;

    public bool CanRetry => State is SubtitleTaskState.Failed or SubtitleTaskState.BurnFailed or SubtitleTaskState.Canceled;

    public bool CanBurn => pipelineResult is not null && SelectedSubtitleTrack is not null && !IsActive && !IsQueued;

    public bool HasBurnedVideo => !string.IsNullOrWhiteSpace(BurnedVideoPath);

    public bool HasConversionResult => pipelineResult is not null;

    public CancellationToken QueueCancellationToken => cancellation?.Token ?? CancellationToken.None;

    [ObservableProperty]
    private SubtitleTaskState _state = SubtitleTaskState.Pending;

    [ObservableProperty]
    private string _statusText = "待开始";

    [ObservableProperty]
    private string _stageText = "等待加入处理队列";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _elapsedText = "00:00";

    [ObservableProperty]
    private string _logText = "LinguaCue 仅在本机处理媒体，不会上传文件。";

    [ObservableProperty]
    private SubtitleCueViewModel? _selectedCue;

    [ObservableProperty]
    private SubtitleTrackOption? _selectedSubtitleTrack;

    [ObservableProperty]
    private string? _burnedVideoPath;

    [ObservableProperty]
    private string _burnFontName;

    [ObservableProperty]
    private double _burnFontSize;

    [ObservableProperty]
    private string _burnPrimaryColor;

    [ObservableProperty]
    private string _burnOutlineColor;

    [ObservableProperty]
    private double _burnOutlineWidth;

    [ObservableProperty]
    private int _burnMarginBottom;

    partial void OnSelectedSubtitleTrackChanged(SubtitleTrackOption? value) => NotifyCommandState();

    public void MarkQueued(bool burn = false)
    {
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        if (burn)
        {
            queuedBurnStyle = GetBurnStyle();
            queuedBurnSubtitlePath = SelectedSubtitleTrack?.Path;
            if (queuedBurnStyle is not null)
            {
                saveBurnDefaults(queuedBurnStyle);
            }
        }
        State = burn ? SubtitleTaskState.BurnQueued : SubtitleTaskState.Queued;
        StatusText = burn ? "烧录排队中" : "排队中";
        StageText = "等待可用处理槽位";
        ProgressValue = 0;
        NotifyStateChanged();
    }

    public async Task RunConvertAsync()
    {
        if (State != SubtitleTaskState.Queued || cancellation?.IsCancellationRequested != false)
        {
            return;
        }

        await OnUiAsync(() =>
        {
            State = SubtitleTaskState.Running;
            StatusText = "正在启动转换器";
            StageText = "独立控制台进程";
            ProgressValue = 0;
            Cues.Clear();
            SubtitleTracks.Clear();
            pipelineResult = null;
            BurnedVideoPath = null;
            LogText = string.Empty;
            lastProgressDetail = null;
            stopwatch.Restart();
            NotifyStateChanged();
        });

        try
        {
            var result = await workerClient.RunAsync(
                Request,
                new Progress<PipelineProgress>(UpdateProgress),
                AppendLog,
                cancellation.Token);
            await OnUiAsync(() => ApplyResult(result));
        }
        catch (OperationCanceledException)
        {
            await OnUiAsync(() =>
            {
                State = SubtitleTaskState.Canceled;
                StatusText = "已取消";
                StageText = "当前任务已安全停止";
                AppendLogOnUi("用户取消了当前任务。");
            });
        }
        catch (Exception exception)
        {
            await OnUiAsync(() =>
            {
                State = SubtitleTaskState.Failed;
                StatusText = "处理失败";
                StageText = exception.Message.Split('\n')[0];
                AppendLogOnUi(exception.ToString());
            });
        }
        finally
        {
            stopwatch.Stop();
            await OnUiAsync(NotifyStateChanged);
        }
    }

    public async Task RunBurnAsync()
    {
        if (State != SubtitleTaskState.BurnQueued ||
            cancellation?.IsCancellationRequested != false ||
            queuedBurnSubtitlePath is null ||
            queuedBurnStyle is null)
        {
            return;
        }

        var subtitlePath = queuedBurnSubtitlePath;
        var style = queuedBurnStyle;
        await OnUiAsync(() =>
        {
            State = SubtitleTaskState.Burning;
            StatusText = "正在烧录字幕";
            StageText = SelectedSubtitleTrack?.DisplayName ?? Path.GetFileName(subtitlePath);
            ProgressValue = 0;
            stopwatch.Restart();
            NotifyStateChanged();
        });

        try
        {
            var outputName = $"{Request.OutputBaseName ?? Path.GetFileNameWithoutExtension(InputPath)}.subtitled.mp4";
            var result = await workerClient.RunBurnAsync(
                new BurnRequest(
                    InputPath,
                    subtitlePath,
                    Path.Combine(OutputDirectory, outputName),
                    style),
                new Progress<PipelineProgress>(UpdateProgress),
                AppendLog,
                cancellation.Token);
            await OnUiAsync(() =>
            {
                BurnedVideoPath = result.BurnedVideoPath;
                State = SubtitleTaskState.Completed;
                StatusText = "烧录完成";
                StageText = $"{result.Encoder} · {result.BurnedVideoPath}";
                ProgressValue = 100;
                AppendLogOnUi($"烧录视频：{result.BurnedVideoPath}");
            });
        }
        catch (OperationCanceledException)
        {
            await OnUiAsync(() =>
            {
                State = SubtitleTaskState.Canceled;
                StatusText = "烧录已取消";
                StageText = "未保留不完整的视频";
                AppendLogOnUi("用户取消了字幕烧录。");
            });
        }
        catch (Exception exception)
        {
            await OnUiAsync(() =>
            {
                State = SubtitleTaskState.BurnFailed;
                StatusText = "烧录失败";
                StageText = exception.Message.Split('\n')[0];
                AppendLogOnUi(exception.ToString());
            });
        }
        finally
        {
            stopwatch.Stop();
            await OnUiAsync(NotifyStateChanged);
        }
    }

    public void RefreshElapsed()
    {
        var elapsed = stopwatch.Elapsed;
        ElapsedText = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    public SubtitleBurnStyle GetBurnStyle() => new(
        string.IsNullOrWhiteSpace(BurnFontName) ? SubtitleBurnStyle.Default.FontName : BurnFontName.Trim(),
        Math.Clamp(BurnFontSize, 8, 240),
        BurnPrimaryColor,
        BurnOutlineColor,
        Math.Clamp(BurnOutlineWidth, 0, 20),
        Math.Clamp(BurnMarginBottom, 0, 2000));

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        cancellation?.Cancel();
        if (IsQueued)
        {
            State = SubtitleTaskState.Canceled;
            StatusText = "已取消";
            StageText = "任务在启动前已取消";
            NotifyStateChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry() => retryAction(this);

    [RelayCommand(CanExecute = nameof(CanBurn))]
    private void Burn() => burnAction(this);

    private void ApplyResult(PipelineResult result)
    {
        pipelineResult = result;
        foreach (var cue in result.Cues)
        {
            Cues.Add(new SubtitleCueViewModel(cue));
        }

        SelectedCue = Cues.FirstOrDefault();
        SubtitleTracks.Add(new SubtitleTrackOption("原文字幕", result.SourceSubtitlePath));
        if (result.TranslatedSubtitlePath is not null)
        {
            SubtitleTracks.Add(new SubtitleTrackOption("译文字幕", result.TranslatedSubtitlePath));
        }

        if (result.BilingualSubtitlePath is not null)
        {
            SubtitleTracks.Add(new SubtitleTrackOption("双语字幕", result.BilingualSubtitlePath));
        }

        SelectedSubtitleTrack = SubtitleTracks.LastOrDefault();
        State = SubtitleTaskState.Ready;
        StatusText = "字幕转换完成";
        StageText = $"已生成 {SubtitleTracks.Count} 个 SRT · {Cues.Count} 条字幕";
        ProgressValue = 100;
        AppendLogOnUi($"原文：{result.SourceSubtitlePath}");
        if (result.TranslatedSubtitlePath is not null)
        {
            AppendLogOnUi($"译文：{result.TranslatedSubtitlePath}");
        }

        if (result.BilingualSubtitlePath is not null)
        {
            AppendLogOnUi($"双语：{result.BilingualSubtitlePath}");
        }

        OnPropertyChanged(nameof(HasCues));
        OnPropertyChanged(nameof(NoCues));
        OnPropertyChanged(nameof(CueCountText));
    }

    private void UpdateProgress(PipelineProgress update) => Dispatcher.UIThread.Post(() =>
    {
        ProgressValue = update.Percentage;
        StatusText = update.Message;
        StageText = update.Stage switch
        {
            PipelineStage.Validating => "检查输入、工具与模型",
            PipelineStage.ExtractingAudio => "音频预处理",
            PipelineStage.Transcribing => "语音转文字",
            PipelineStage.Translating => "离线翻译",
            PipelineStage.WritingSubtitles => "正在生成 SRT",
            PipelineStage.BurningSubtitles => "正在转码并写入字幕",
            PipelineStage.Completed => "任务完成",
            _ => StageText
        };
        RefreshElapsed();
        if (!string.IsNullOrWhiteSpace(update.Detail) && update.Detail != lastProgressDetail)
        {
            lastProgressDetail = update.Detail;
            AppendLogOnUi(update.Detail);
        }
    });

    private void AppendLog(string message) => Dispatcher.UIThread.Post(() => AppendLogOnUi(message));

    private void AppendLogOnUi(string message)
    {
        var timestamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogText = string.IsNullOrEmpty(LogText) ? timestamped : $"{LogText}{Environment.NewLine}{timestamped}";
        RefreshElapsed();
    }

    private void NotifyStateChanged()
    {
        RefreshElapsed();
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanBurn));
        OnPropertyChanged(nameof(HasBurnedVideo));
        OnPropertyChanged(nameof(HasConversionResult));
        NotifyCommandState();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyCommandState()
    {
        CancelCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        BurnCommand.NotifyCanExecuteChanged();
    }

    private static async Task OnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }
}
