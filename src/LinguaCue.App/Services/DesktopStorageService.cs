using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace LinguaCue.Services;

public sealed class DesktopStorageService(Window owner) : IStorageService
{
    public async Task<IReadOnlyList<string>> PickMediaFilesAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择视频或音频",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("媒体文件")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.mp3", "*.wav", "*.m4a", "*.flac", "*.aac", "*.ogg"]
                },
                FilePickerFileTypes.All
            ]
        });

        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
    }

    public async Task<string?> PickOutputDirectoryAsync()
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择字幕输出目录",
            AllowMultiple = false
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickModelFileAsync(string modelType)
    {
        var isWhisper = string.Equals(modelType, "whisper", StringComparison.OrdinalIgnoreCase);
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = isWhisper ? "导入 Whisper GGML 模型" : "导入 Hy-MT2 GGUF 模型",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(isWhisper ? "Whisper 模型" : "GGUF 模型")
                {
                    Patterns = isWhisper ? ["*.bin"] : ["*.gguf"]
                }
            ]
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSubtitleSavePathAsync(string suggestedFileName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出字幕",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "srt",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("SubRip 字幕") { Patterns = ["*.srt"] }
            ]
        });

        return file?.TryGetLocalPath();
    }
}
