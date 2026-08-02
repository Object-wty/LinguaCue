using LinguaCue.Infrastructure;

namespace LinguaCue.Services;

public static class SubtitlePipelineFactory
{
    public static SubtitlePipeline Create(PortableLayout layout)
    {
        var toolResolver = new RuntimeToolResolver(layout);
        var processRunner = new ProcessRunner();
        return new SubtitlePipeline(
            layout,
            toolResolver,
            new FfmpegAudioExtractor(processRunner, toolResolver),
            new WhisperTranscriber(processRunner, toolResolver, layout),
            new HyMtTranslator(processRunner, toolResolver, layout),
            new SrtSubtitleService());
    }

    public static SubtitleBurner CreateBurner(PortableLayout layout)
    {
        var toolResolver = new RuntimeToolResolver(layout);
        var processRunner = new ProcessRunner();
        return new SubtitleBurner(
            processRunner,
            toolResolver,
            layout,
            new SrtSubtitleService(),
            new AssSubtitleService());
    }
}
