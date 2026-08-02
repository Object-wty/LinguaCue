using LinguaCue.Infrastructure;
using LinguaCue.Models;

namespace LinguaCue.Tests;

public sealed class PortableLayoutTests
{
    [Fact]
    public void Create_WithPortableFlag_UsesApplicationDirectory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "portable.flag"), "portable");

            var layout = PortableLayout.Create(root);

            Assert.Equal(Path.GetFullPath(root), layout.DataRoot);
            Assert.True(Directory.Exists(layout.TempRoot));
            Assert.True(Directory.Exists(layout.TranslationModelRoot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindTranslationModel_PrefersBundledModel()
    {
        var root = CreateTemporaryDirectory();
        var dataRoot = CreateTemporaryDirectory();
        try
        {
            var profile = ModelCatalog.TranslationProfiles[0];
            var bundledDirectory = Path.Combine(root, "models", "translation");
            Directory.CreateDirectory(bundledDirectory);
            var bundledPath = Path.Combine(bundledDirectory, profile.FileName);
            File.WriteAllText(bundledPath, "placeholder");
            var layout = PortableLayout.Create(root, dataRoot);

            var resolved = layout.FindTranslationModel(profile);

            Assert.Equal(bundledPath, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void RuntimeToolResolver_FindsRidSpecificExecutable()
    {
        var root = CreateTemporaryDirectory();
        var dataRoot = CreateTemporaryDirectory();
        try
        {
            var layout = PortableLayout.Create(root, dataRoot);
            var resolver = new RuntimeToolResolver(layout);
            var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            var toolDirectory = Path.Combine(layout.RuntimeRoot, resolver.RuntimeIdentifier, "whisper");
            Directory.CreateDirectory(toolDirectory);
            var expected = Path.Combine(toolDirectory, $"whisper-cli{suffix}");
            File.WriteAllText(expected, string.Empty);

            Assert.Equal(expected, resolver.Resolve(RuntimeTool.Whisper));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void RuntimeToolResolver_FindsLlamaServerExecutable()
    {
        var root = CreateTemporaryDirectory();
        var dataRoot = CreateTemporaryDirectory();
        try
        {
            var layout = PortableLayout.Create(root, dataRoot);
            var resolver = new RuntimeToolResolver(layout);
            var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            var toolDirectory = Path.Combine(layout.RuntimeRoot, resolver.RuntimeIdentifier, "llama");
            Directory.CreateDirectory(toolDirectory);
            var expected = Path.Combine(toolDirectory, $"llama-server{suffix}");
            File.WriteAllText(expected, string.Empty);

            Assert.Equal(expected, resolver.Resolve(RuntimeTool.LlamaServer));
            Assert.True(resolver.Inspect().Llama.IsReady);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void RuntimeToolResolver_AutoPrefersGpuAndCpuModeUsesCpuFolder()
    {
        var root = CreateTemporaryDirectory();
        var dataRoot = CreateTemporaryDirectory();
        try
        {
            var layout = PortableLayout.Create(root, dataRoot);
            var resolver = new RuntimeToolResolver(layout);
            var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            var category = Path.Combine(layout.RuntimeRoot, resolver.RuntimeIdentifier, "whisper");
            var gpuBackend = OperatingSystem.IsMacOS() ? "metal" : "cuda";
            var gpuDirectory = Path.Combine(category, gpuBackend);
            var cpuDirectory = Path.Combine(category, "cpu");
            Directory.CreateDirectory(gpuDirectory);
            Directory.CreateDirectory(cpuDirectory);
            var gpuPath = Path.Combine(gpuDirectory, $"whisper-cli{suffix}");
            var cpuPath = Path.Combine(cpuDirectory, $"whisper-cli{suffix}");
            File.WriteAllText(gpuPath, string.Empty);
            File.WriteAllText(cpuPath, string.Empty);

            var automatic = resolver.ResolveWithBackend(RuntimeTool.Whisper, AccelerationMode.Auto);
            var cpu = resolver.ResolveWithBackend(RuntimeTool.Whisper, AccelerationMode.Cpu);

            Assert.Equal(gpuPath, automatic?.Path);
            Assert.NotEqual(RuntimeBackend.Cpu, automatic?.Backend);
            Assert.Equal(cpuPath, cpu?.Path);
            Assert.Equal(RuntimeBackend.Cpu, cpu?.Backend);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LinguaCue.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
