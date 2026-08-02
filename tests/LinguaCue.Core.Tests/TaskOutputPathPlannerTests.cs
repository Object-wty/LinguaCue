using LinguaCue.Infrastructure;

namespace LinguaCue.Tests;

public sealed class TaskOutputPathPlannerTests
{
    [Fact]
    public void Reserve_UsesConfiguredRootAndAvoidsDiskAndQueueCollisions()
    {
        var outputRoot = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(outputRoot, "lesson"));
            var reserved = Path.Combine(outputRoot, "lesson (2)");

            var location = TaskOutputPathPlanner.Reserve(
                outputRoot,
                Path.Combine("D:\\imports", "lesson.mp4"),
                [reserved]);

            Assert.Equal(Path.Combine(outputRoot, "lesson (3)"), location.TaskDirectory);
            Assert.Equal("lesson (3)", location.OutputBaseName);
            Assert.Equal(Path.GetFullPath(outputRoot), Path.GetDirectoryName(location.TaskDirectory));
        }
        finally
        {
            Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("  demo<>:\"/\\|?*  ", "demo_________")]
    [InlineData("CON", "_CON")]
    [InlineData("...", "subtitles")]
    [InlineData("   ", "subtitles")]
    public void SanitizeFileName_ProducesPortableDirectoryName(string input, string expected) =>
        Assert.Equal(expected, TaskOutputPathPlanner.SanitizeFileName(input));

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LinguaCue.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
