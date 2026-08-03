using LinguaCue.Infrastructure;
using LinguaCue.Models;
using LinguaCue.Services;
using LinguaCue.ViewModels;

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
            File.WriteAllText(Path.Combine(outputRoot, "lesson (3)"), "occupied");

            var location = TaskOutputPathPlanner.Reserve(
                outputRoot,
                Path.Combine("D:\\imports", "lesson.mp4"),
                [reserved]);

            Assert.Equal(Path.Combine(outputRoot, "lesson (4)"), location.TaskDirectory);
            Assert.Equal("lesson (4)", location.OutputBaseName);
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

    [Fact]
    public void TryRelocatePendingOutput_UpdatesOnlyTaskThatHasNotStarted()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var task = CreateTask(root);
            var customDirectory = Path.Combine(root, "custom-output", "lesson");

            Assert.True(task.TryRelocatePendingOutput(customDirectory, "lesson"));
            Assert.Equal(Path.GetFullPath(customDirectory), task.OutputDirectory);
            Assert.Equal("lesson", task.Request.OutputBaseName);

            task.State = SubtitleTaskState.Queued;
            Assert.False(task.TryRelocatePendingOutput(
                Path.Combine(root, "must-not-replace-queued-task"),
                "ignored"));
            Assert.Equal(Path.GetFullPath(customDirectory), task.OutputDirectory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MarkQueued_SnapshotsAutomaticBurnChoiceForThatQueueRun()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var task = CreateTask(root);

            task.MarkQueued(autoBurnAfterConversion: true);

            Assert.Equal(SubtitleTaskState.Queued, task.State);
            Assert.True(task.AutoBurnAfterConversion);

            task.State = SubtitleTaskState.Pending;
            task.MarkQueued(autoBurnAfterConversion: false);
            Assert.False(task.AutoBurnAfterConversion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SubtitleTaskViewModel CreateTask(string root)
    {
        var inputPath = Path.Combine(root, "source", "lesson.mp4");
        var request = new PipelineRequest(
            inputPath,
            Path.Combine(root, "initial-output", "lesson"),
            ModelCatalog.SourceLanguages[0],
            ModelCatalog.TargetLanguages[0],
            Translate: false,
            GenerateBilingual: false,
            ModelCatalog.TranslationProfiles[0],
            OutputBaseName: "lesson");
        return new SubtitleTaskViewModel(
            request,
            new SubtitleWorkerClient(PortableLayout.Create(root, root)),
            SubtitleBurnStyle.Default,
            BurnSubtitleKind.Source,
            _ => { },
            _ => { },
            _ => { });
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LinguaCue.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
