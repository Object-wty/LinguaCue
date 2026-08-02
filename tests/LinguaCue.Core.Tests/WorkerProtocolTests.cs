using LinguaCue.Models;

namespace LinguaCue.Tests;

public sealed class WorkerProtocolTests
{
    [Fact]
    public void RoundTrip_ProgressMessage_PreservesUtf8ContentAndEnum()
    {
        var expected = WorkerMessage.FromProgress(new PipelineProgress(
            PipelineStage.Translating,
            72.5,
            "正在翻译字幕",
            "模型已就绪：中文无乱码"));

        var json = WorkerProtocol.Serialize(expected);
        var actual = WorkerProtocol.Deserialize(json);

        Assert.Equal(WorkerMessageKind.Progress, actual.Kind);
        Assert.Equal(PipelineStage.Translating, actual.Progress?.Stage);
        Assert.Equal(72.5, actual.Progress?.Percentage);
        Assert.Equal("模型已就绪：中文无乱码", actual.Progress?.Detail);
        Assert.Contains("\"kind\":\"progress\"", json);
    }

    [Fact]
    public void RoundTrip_ResultMessage_PreservesCuesAndPaths()
    {
        var result = new PipelineResult(
            [new SubtitleCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(2), "Hello", "你好")],
            "/output/demo.source.srt",
            "/output/demo.zh.srt",
            "/output/demo.bilingual.zh.srt");

        var actual = WorkerProtocol.Deserialize(
            WorkerProtocol.Serialize(WorkerMessage.FromResult(result)));

        Assert.Equal("你好", actual.Result?.Cues[0].TranslatedText);
        Assert.Equal("/output/demo.zh.srt", actual.Result?.TranslatedSubtitlePath);
    }

    [Fact]
    public void RoundTrip_BurnResult_PreservesOperationAndUtf8Path()
    {
        var message = WorkerMessage.FromBurnResult(new BurnResult(
            "D:/视频/演示.subtitled.mp4",
            "h264_nvenc",
            TimeSpan.FromSeconds(12.5)));

        var actual = WorkerProtocol.Deserialize(WorkerProtocol.Serialize(message));

        Assert.Equal(WorkerOperation.Burn, actual.Operation);
        Assert.Equal("D:/视频/演示.subtitled.mp4", actual.BurnResult?.BurnedVideoPath);
        Assert.Equal("h264_nvenc", actual.BurnResult?.Encoder);
    }
}
