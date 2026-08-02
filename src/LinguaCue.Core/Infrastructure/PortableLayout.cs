using LinguaCue.Models;

namespace LinguaCue.Infrastructure;

public sealed class PortableLayout
{
    private const string DataRootEnvironmentVariable = "LINGUACUE_HOME";

    private PortableLayout(string appBaseDirectory, string dataRoot)
    {
        AppBaseDirectory = appBaseDirectory;
        DataRoot = dataRoot;
        RuntimeRoot = Path.Combine(appBaseDirectory, "runtimes");
        LogsRoot = Path.Combine(dataRoot, "logs");
        TempRoot = Path.Combine(dataRoot, "temp");
        SpeechModelRoot = Path.Combine(dataRoot, "models", "speech");
        TranslationModelRoot = Path.Combine(dataRoot, "models", "translation");
        BundledFontRoot = Path.Combine(appBaseDirectory, "assets", "fonts", "files");

        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(SpeechModelRoot);
        Directory.CreateDirectory(TranslationModelRoot);
    }

    public string AppBaseDirectory { get; }

    public string DataRoot { get; }

    public string RuntimeRoot { get; }

    public string LogsRoot { get; }

    public string TempRoot { get; }

    public string SpeechModelRoot { get; }

    public string TranslationModelRoot { get; }

    public string BundledFontRoot { get; }

    public static PortableLayout Create(string? appBaseDirectory = null, string? dataRootOverride = null)
    {
        var appBase = Path.GetFullPath(appBaseDirectory ?? AppContext.BaseDirectory);
        var configuredRoot = dataRootOverride ?? Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        var preferredRoot = !string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.GetFullPath(configuredRoot)
            : File.Exists(Path.Combine(appBase, "portable.flag"))
                ? appBase
                : GetDefaultUserDataRoot();

        try
        {
            return new PortableLayout(appBase, preferredRoot);
        }
        catch (UnauthorizedAccessException) when (!PathEquals(preferredRoot, GetDefaultUserDataRoot()))
        {
            return new PortableLayout(appBase, GetDefaultUserDataRoot());
        }
        catch (IOException) when (!PathEquals(preferredRoot, GetDefaultUserDataRoot()))
        {
            return new PortableLayout(appBase, GetDefaultUserDataRoot());
        }
    }

    public string CreateJobDirectory()
    {
        var path = Path.Combine(TempRoot, $"job-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public string? FindSpeechModel(string fileName = ModelCatalog.WhisperModelFileName)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppBaseDirectory, "models", "speech", fileName),
            Path.Combine(SpeechModelRoot, fileName)
        };

        return FindFirstExisting(candidates);
    }

    public string? FindTranslationModel(TranslationModelProfile profile)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppBaseDirectory, "models", "translation", profile.FileName),
            Path.Combine(TranslationModelRoot, profile.FileName)
        };

        return FindFirstExisting(candidates);
    }

    public string GetSpeechModelImportPath() =>
        Path.Combine(SpeechModelRoot, ModelCatalog.WhisperModelFileName);

    public string GetTranslationModelImportPath(TranslationModelProfile profile) =>
        Path.Combine(TranslationModelRoot, profile.FileName);

    private static string GetDefaultUserDataRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        return Path.Combine(localData, "LinguaCue");
    }

    private static string? FindFirstExisting(IEnumerable<string> candidates) =>
        candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
