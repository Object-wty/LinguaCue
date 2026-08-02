namespace LinguaCue.Services;

public interface IStorageService
{
    Task<IReadOnlyList<string>> PickMediaFilesAsync();

    Task<string?> PickOutputDirectoryAsync();

    Task<string?> PickModelFileAsync(string modelType);

    Task<string?> PickSubtitleSavePathAsync(string suggestedFileName);
}
