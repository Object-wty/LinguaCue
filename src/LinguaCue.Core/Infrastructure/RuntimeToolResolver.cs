using System.Runtime.InteropServices;
using LinguaCue.Models;

namespace LinguaCue.Infrastructure;

public enum RuntimeTool
{
    Ffmpeg,
    Ffprobe,
    Whisper,
    Llama,
    LlamaServer
}

public enum RuntimeBackend
{
    Cpu,
    Cuda,
    Vulkan,
    Metal
}

public sealed record ResolvedRuntimeTool(string Path, RuntimeBackend Backend);

public sealed class RuntimeToolResolver(PortableLayout layout)
{
    public string RuntimeIdentifier { get; } = BuildRuntimeIdentifier();

    public string? Resolve(RuntimeTool tool) =>
        ResolveWithBackend(tool, AccelerationMode.Auto)?.Path;

    public ResolvedRuntimeTool? ResolveWithBackend(
        RuntimeTool tool,
        AccelerationMode acceleration = AccelerationMode.Auto)
    {
        var executableNames = GetExecutableNames(tool);
        var category = GetCategory(tool);
        var categoryRoots = new[]
        {
            Path.Combine(layout.RuntimeRoot, RuntimeIdentifier, category),
            Path.Combine(layout.RuntimeRoot, category)
        };

        if (tool is RuntimeTool.Ffmpeg or RuntimeTool.Ffprobe)
        {
            return FindInRoots(categoryRoots, executableNames, RuntimeBackend.Cpu);
        }

        var backends = BuildBackendOrder(acceleration);
        foreach (var backend in backends)
        {
            var backendFolder = backend.ToString().ToLowerInvariant();
            var backendRoots = categoryRoots.Select(root => Path.Combine(root, backendFolder));
            var resolved = FindInRoots(backendRoots, executableNames, backend);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        // Existing portable packages place CPU binaries directly in the category folder.
        return FindInRoots(categoryRoots, executableNames, RuntimeBackend.Cpu);
    }

    public ToolchainSnapshot Inspect()
    {
        var ffmpeg = Resolve(RuntimeTool.Ffmpeg);
        var ffprobe = Resolve(RuntimeTool.Ffprobe);
        var whisperSelection = ResolveWithBackend(RuntimeTool.Whisper);
        var llamaSelection = ResolveWithBackend(RuntimeTool.LlamaServer) ?? ResolveWithBackend(RuntimeTool.Llama);
        var whisper = whisperSelection?.Path;
        var llama = llamaSelection?.Path;
        var whisperModel = layout.FindSpeechModel();
        var translationModels = ModelCatalog.TranslationProfiles
            .Select(profile =>
            {
                var path = layout.FindTranslationModel(profile);
                return ToStatus(profile.DisplayName, path, profile.FileName);
            })
            .ToArray();

        return new ToolchainSnapshot(
            ToStatus("FFmpeg", ffmpeg, "未找到 ffmpeg"),
            ToStatus("FFprobe", ffprobe, "未找到 ffprobe（进度估算将不可用）"),
            ToStatus("whisper.cpp", whisper, "未找到 whisper-cli", whisperSelection?.Backend),
            ToStatus("Whisper large-v3-turbo", whisperModel, ModelCatalog.WhisperModelFileName),
            ToStatus("llama.cpp", llama, "未找到 llama-server/llama-completion/llama-cli", llamaSelection?.Backend),
            translationModels);
    }

    private static ComponentStatus ToStatus(
        string name,
        string? path,
        string missingDetail,
        RuntimeBackend? backend = null) =>
        path is null
            ? new ComponentStatus(name, false, missingDetail, null)
            : new ComponentStatus(name, true, backend is null ? "已就绪" : $"已就绪 · {backend}", path);

    private static ResolvedRuntimeTool? FindInRoots(
        IEnumerable<string> roots,
        IEnumerable<string> executableNames,
        RuntimeBackend backend)
    {
        foreach (var root in roots)
        {
            foreach (var executableName in executableNames)
            {
                var candidate = Path.Combine(root, executableName);
                if (File.Exists(candidate))
                {
                    return new ResolvedRuntimeTool(candidate, backend);
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<RuntimeBackend> BuildBackendOrder(AccelerationMode acceleration)
    {
        if (acceleration == AccelerationMode.Cpu)
        {
            return [RuntimeBackend.Cpu];
        }

        if (OperatingSystem.IsMacOS())
        {
            return [RuntimeBackend.Metal, RuntimeBackend.Cpu];
        }

        return [RuntimeBackend.Cuda, RuntimeBackend.Vulkan, RuntimeBackend.Cpu];
    }

    private static string[] GetExecutableNames(RuntimeTool tool)
    {
        var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        return tool switch
        {
            RuntimeTool.Ffmpeg => [$"ffmpeg{suffix}"],
            RuntimeTool.Ffprobe => [$"ffprobe{suffix}"],
            RuntimeTool.Whisper => [$"whisper-cli{suffix}", $"main{suffix}"],
            RuntimeTool.Llama => [$"llama-completion{suffix}", $"llama-cli{suffix}"],
            RuntimeTool.LlamaServer => [$"llama-server{suffix}"],
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null)
        };
    }

    private static string GetCategory(RuntimeTool tool) => tool switch
    {
        RuntimeTool.Ffmpeg or RuntimeTool.Ffprobe => "ffmpeg",
        RuntimeTool.Whisper => "whisper",
        RuntimeTool.Llama or RuntimeTool.LlamaServer => "llama",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null)
    };

    private static string BuildRuntimeIdentifier()
    {
        var os = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsMacOS()
                ? "osx"
                : OperatingSystem.IsLinux()
                    ? "linux"
                    : throw new PlatformNotSupportedException("LinguaCue supports Windows, macOS, and Linux.");

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException(
                $"Unsupported architecture: {RuntimeInformation.ProcessArchitecture}")
        };

        return $"{os}-{architecture}";
    }
}
