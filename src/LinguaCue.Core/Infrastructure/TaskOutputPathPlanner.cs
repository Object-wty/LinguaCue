namespace LinguaCue.Infrastructure;

public sealed record TaskOutputLocation(
    string TaskDirectory,
    string OutputBaseName);

public static class TaskOutputPathPlanner
{
    private const int MaximumNameLength = 120;
    private const string FallbackName = "subtitles";

    private static readonly HashSet<string> WindowsReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CLOCK$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static TaskOutputLocation Reserve(
        string outputRoot,
        string inputPath,
        IEnumerable<string>? reservedDirectories = null)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ArgumentException("请选择输出目录。", nameof(outputRoot));
        }

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("输入媒体路径不能为空。", nameof(inputPath));
        }

        var resolvedRoot = Path.GetFullPath(outputRoot);
        var originalName = SanitizeFileName(Path.GetFileNameWithoutExtension(inputPath));
        var reserved = new HashSet<string>(PathComparer);
        if (reservedDirectories is not null)
        {
            foreach (var directory in reservedDirectories.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                reserved.Add(NormalizeDirectory(directory));
            }
        }

        for (var index = 1; index < 10_000; index++)
        {
            var candidate = AddCollisionSuffix(originalName, index);
            var taskDirectory = Path.GetFullPath(Path.Combine(resolvedRoot, candidate));
            if (!reserved.Contains(NormalizeDirectory(taskDirectory)) && !Directory.Exists(taskDirectory))
            {
                return new TaskOutputLocation(taskDirectory, candidate);
            }
        }

        throw new IOException("无法为字幕任务分配不重名的输出目录。");
    }

    public static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FallbackName;
        }

        var invalidCharacters = Path.GetInvalidFileNameChars()
            .Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*'])
            .ToHashSet();
        var sanitized = new string(value
                .Select(character => character < ' ' || invalidCharacters.Contains(character) ? '_' : character)
                .ToArray())
            .Trim()
            .TrimEnd('.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return FallbackName;
        }

        sanitized = Truncate(sanitized, MaximumNameLength).TrimEnd(' ', '.');
        var deviceName = sanitized.Split('.', 2)[0];
        if (WindowsReservedNames.Contains(deviceName))
        {
            sanitized = $"_{sanitized}";
            sanitized = Truncate(sanitized, MaximumNameLength).TrimEnd(' ', '.');
        }

        return string.IsNullOrWhiteSpace(sanitized) ? FallbackName : sanitized;
    }

    private static string AddCollisionSuffix(string originalName, int index)
    {
        if (index == 1)
        {
            return originalName;
        }

        var suffix = $" ({index})";
        var availableLength = MaximumNameLength - suffix.Length;
        var prefix = Truncate(originalName, availableLength).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = FallbackName;
        }

        return prefix + suffix;
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static string NormalizeDirectory(string directory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
