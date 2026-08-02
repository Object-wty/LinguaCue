namespace LinguaCue.Models;

public sealed record LanguageOption(string Code, string DisplayName, string PromptName)
{
    public override string ToString() => DisplayName;
}

public sealed record TranslationModelProfile(
    string Id,
    string DisplayName,
    string Description,
    string FileName,
    int RecommendedMemoryGb)
{
    public override string ToString() => DisplayName;
}

public static class ModelCatalog
{
    public const string WhisperModelFileName = "ggml-large-v3-turbo.bin";

    public static IReadOnlyList<TranslationModelProfile> TranslationProfiles { get; } =
    [
        new(
            "standard",
            "标准 · Hy-MT2 1.8B Q4_K_M",
            "默认选择，速度与质量平衡，建议 8 GB 以上内存",
            "Hy-MT2-1.8B-Q4_K_M.gguf",
            8),
        new(
            "quality",
            "高质量 · Hy-MT2 7B Q4_K_M",
            "更好的上下文与术语一致性，建议 16 GB 以上内存",
            "Hy-MT2-7B-Q4_K_M.gguf",
            16)
    ];

    public static IReadOnlyList<LanguageOption> TargetLanguages { get; } =
    [
        new("zh", "中文", "Chinese"),
        new("en", "英语", "English"),
        new("ja", "日语", "Japanese"),
        new("ko", "韩语", "Korean"),
        new("fr", "法语", "French"),
        new("de", "德语", "German"),
        new("es", "西班牙语", "Spanish"),
        new("pt", "葡萄牙语", "Portuguese"),
        new("it", "意大利语", "Italian"),
        new("ru", "俄语", "Russian"),
        new("ar", "阿拉伯语", "Arabic"),
        new("th", "泰语", "Thai"),
        new("vi", "越南语", "Vietnamese"),
        new("id", "印度尼西亚语", "Indonesian"),
        new("ms", "马来语", "Malay"),
        new("tr", "土耳其语", "Turkish"),
        new("pl", "波兰语", "Polish"),
        new("nl", "荷兰语", "Dutch")
    ];

    public static IReadOnlyList<LanguageOption> SourceLanguages { get; } =
    [
        new("auto", "自动检测", "Auto-detect"),
        .. TargetLanguages
    ];
}
