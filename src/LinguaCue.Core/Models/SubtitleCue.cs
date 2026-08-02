namespace LinguaCue.Models;

public sealed record SubtitleCue(
    int Index,
    TimeSpan Start,
    TimeSpan End,
    string SourceText,
    string? TranslatedText = null)
{
    public SubtitleCue WithTranslation(string translatedText) => this with
    {
        TranslatedText = translatedText
    };
}

