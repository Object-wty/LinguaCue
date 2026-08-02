namespace LinguaCue.Models;

public sealed record ComponentStatus(string Name, bool IsReady, string Detail, string? Path);

public sealed record ToolchainSnapshot(
    ComponentStatus Ffmpeg,
    ComponentStatus Ffprobe,
    ComponentStatus Whisper,
    ComponentStatus WhisperModel,
    ComponentStatus Llama,
    IReadOnlyList<ComponentStatus> TranslationModels)
{
    public bool IsTranscriptionReady =>
        Ffmpeg.IsReady && Whisper.IsReady && WhisperModel.IsReady;
}

