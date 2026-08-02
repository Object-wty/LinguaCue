namespace LinguaCue.Models;

public enum PipelineStage
{
    Validating,
    ExtractingAudio,
    Transcribing,
    Translating,
    WritingSubtitles,
    BurningSubtitles,
    Completed
}

public enum AccelerationMode
{
    Auto,
    Cpu,
    Gpu
}

public enum PerformanceProfile
{
    Fast,
    Balanced,
    Quality
}

public sealed record PipelineProgress(
    PipelineStage Stage,
    double Percentage,
    string Message,
    string? Detail = null);

public sealed record PipelineRequest(
    string InputPath,
    string OutputDirectory,
    LanguageOption SourceLanguage,
    LanguageOption TargetLanguage,
    bool Translate,
    bool GenerateBilingual,
    TranslationModelProfile TranslationModel,
    AccelerationMode Acceleration = AccelerationMode.Auto,
    PerformanceProfile PerformanceProfile = PerformanceProfile.Balanced,
    int Threads = 0,
    string? OutputBaseName = null);

public sealed record PipelineResult(
    IReadOnlyList<SubtitleCue> Cues,
    string SourceSubtitlePath,
    string? TranslatedSubtitlePath,
    string? BilingualSubtitlePath);

public enum BurnEncoderMode
{
    Auto,
    Software
}

public sealed record SubtitleBurnStyle(
    string FontName,
    double FontSize,
    string PrimaryColor,
    string OutlineColor,
    double OutlineWidth,
    int MarginBottom)
{
    public static SubtitleBurnStyle Default { get; } = new(
        "Noto Sans SC",
        20,
        "#FFFFFF",
        "#000000",
        3,
        20);
}

public sealed record BurnRequest(
    string InputVideoPath,
    string SubtitlePath,
    string OutputPath,
    SubtitleBurnStyle Style,
    BurnEncoderMode Encoder = BurnEncoderMode.Auto);

public sealed record BurnResult(
    string BurnedVideoPath,
    string Encoder,
    TimeSpan Duration);
