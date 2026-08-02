using LinguaCue.Infrastructure;
using LinguaCue.Models;

namespace LinguaCue.Services;

public sealed class ModelImportService(PortableLayout layout)
{
    private const long MinimumModelSize = 1024L * 1024L;

    public Task<string> ImportWhisperModelAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        ImportAsync(sourcePath, layout.GetSpeechModelImportPath(), ".bin", cancellationToken);

    public Task<string> ImportTranslationModelAsync(
        string sourcePath,
        TranslationModelProfile profile,
        CancellationToken cancellationToken = default) =>
        ImportAsync(sourcePath, layout.GetTranslationModelImportPath(profile), ".gguf", cancellationToken);

    private static async Task<string> ImportAsync(
        string sourcePath,
        string destinationPath,
        string expectedExtension,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("选择的模型文件不存在。", sourcePath);
        }

        if (!string.Equals(Path.GetExtension(sourcePath), expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"模型文件必须使用 {expectedExtension} 扩展名。");
        }

        if (new FileInfo(sourcePath).Length < MinimumModelSize)
        {
            throw new InvalidDataException("模型文件过小，可能未完整下载。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryPath = destinationPath + $".import-{Guid.NewGuid():N}";
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, 1024 * 1024, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

