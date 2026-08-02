using CommunityToolkit.Mvvm.ComponentModel;
using LinguaCue.Models;

namespace LinguaCue.ViewModels;

public sealed partial class SubtitleCueViewModel(SubtitleCue cue) : ObservableObject
{
    public int Index { get; } = cue.Index;

    public TimeSpan Start { get; } = cue.Start;

    public TimeSpan End { get; } = cue.End;

    public string IndexLabel => $"#{Index:000}";

    public string TimeRange => $"{FormatShort(Start)}  →  {FormatShort(End)}";

    [ObservableProperty]
    private string _sourceText = cue.SourceText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranslation))]
    private string? _translatedText = cue.TranslatedText;

    public bool HasTranslation => !string.IsNullOrWhiteSpace(TranslatedText);

    public SubtitleCue ToModel() => new(Index, Start, End, SourceText, TranslatedText);

    private static string FormatShort(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
}

