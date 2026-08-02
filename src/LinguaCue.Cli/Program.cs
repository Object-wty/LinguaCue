using System.Text;
using LinguaCue.Infrastructure;
using LinguaCue.Models;
using LinguaCue.Services;

namespace LinguaCue.Cli;

internal static class Program
{
    private const int InvalidArgumentsExitCode = 2;
    private const int ProcessingFailedExitCode = 3;
    private const int CanceledExitCode = 130;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = Encoding.UTF8;

        if (args.Length == 0 || args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var writer = new ProtocolWriter();
        var operation = args[0].Equals("burn", StringComparison.OrdinalIgnoreCase)
            ? WorkerOperation.Burn
            : WorkerOperation.Convert;
        try
        {
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            return operation == WorkerOperation.Burn
                ? await RunBurnAsync(BurnOptions.Parse(args), writer, cancellation.Token)
                : await RunConvertAsync(ConvertOptions.Parse(args), writer, cancellation.Token);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            writer.Write(WorkerMessage.FromError(exception, operation));
            return InvalidArgumentsExitCode;
        }
        catch (OperationCanceledException)
        {
            writer.Write(WorkerMessage.CanceledMessage(operation));
            return CanceledExitCode;
        }
        catch (Exception exception)
        {
            writer.Write(WorkerMessage.FromError(exception, operation));
            return ProcessingFailedExitCode;
        }
    }

    private static async Task<int> RunConvertAsync(
        ConvertOptions options,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var layout = PortableLayout.Create(options.AppRoot, options.DataRoot);
        var pipeline = SubtitlePipelineFactory.Create(layout);
        var progress = new ConsoleProgress(update =>
            writer.Write(WorkerMessage.FromProgress(update)));
        var result = await pipeline.RunAsync(options.Request, progress, cancellationToken);
        writer.Write(WorkerMessage.FromResult(result));
        return 0;
    }

    private static async Task<int> RunBurnAsync(
        BurnOptions options,
        ProtocolWriter writer,
        CancellationToken cancellationToken)
    {
        var layout = PortableLayout.Create(options.AppRoot, options.DataRoot);
        var burner = SubtitlePipelineFactory.CreateBurner(layout);
        var progress = new ConsoleProgress(update =>
            writer.Write(WorkerMessage.FromProgress(update, WorkerOperation.Burn)));
        var result = await burner.BurnAsync(
            options.Request,
            progress,
            Console.Error.WriteLine,
            cancellationToken);
        writer.Write(WorkerMessage.FromBurnResult(result));
        return 0;
    }

    private const string Usage = """
        LinguaCue.Cli convert --input <媒体路径> --output <输出目录> [选项]
        LinguaCue.Cli burn --input <视频路径> --subtitle <字幕路径> --output <输出视频> [选项]

        convert 选项：
          --source <代码>       源语言，默认 auto
          --target <代码>       目标语言，默认 zh
          --translate <bool>    是否翻译，默认 true
          --bilingual <bool>    是否输出双语字幕，默认 true
          --model <id>          standard 或 quality，默认 standard
          --acceleration <值>   auto、cpu 或 gpu，默认 auto
          --performance <值>    fast、balanced 或 quality，默认 balanced
          --threads <数量>      当前任务的线程数，默认自动
          --output-base <名称>  输出文件基础名，用于并发防重名

        burn 选项：
          --font-name <字体>    默认 Noto Sans SC（应用内置）
          --font-size <字号>    默认 42
          --primary-color <色>  #RRGGBB，默认 #FFFFFF
          --outline-color <色>  #RRGGBB，默认 #000000
          --outline-width <值>  默认 3
          --margin-bottom <值>  默认 60
          --encoder <值>        auto 或 software，默认 auto

        通用选项：
          --app-root <目录>     models/ 与 runtimes/ 所在目录
          --data-root <目录>    temp/ 与导入模型所在目录

        运行时进度与结果通过标准输出以 UTF-8 JSON Lines 格式返回。
        """;

    private sealed class ProtocolWriter
    {
        private readonly object sync = new();

        public void Write(WorkerMessage message)
        {
            lock (sync)
            {
                Console.Out.WriteLine(WorkerProtocol.Serialize(message));
                Console.Out.Flush();
            }
        }
    }

    private sealed class ConsoleProgress(Action<PipelineProgress> report) : IProgress<PipelineProgress>
    {
        public void Report(PipelineProgress value) => report(value);
    }

    private sealed record ConvertOptions(PipelineRequest Request, string AppRoot, string? DataRoot)
    {
        public static ConvertOptions Parse(IReadOnlyList<string> args)
        {
            if (args.Count == 0 || !string.Equals(args[0], "convert", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("缺少 convert 命令。使用 --help 查看用法。");
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < args.Count; index += 2)
            {
                var name = args[index];
                if (!name.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Count)
                {
                    throw new ArgumentException($"无效参数：{name}");
                }

                if (!values.TryAdd(name[2..], args[index + 1]))
                {
                    throw new ArgumentException($"参数重复：{name}");
                }
            }

            var input = Required(values, "input");
            var output = Required(values, "output");
            var source = FindSource(values.GetValueOrDefault("source", "auto"));
            var target = FindTarget(values.GetValueOrDefault("target", "zh"));
            var translate = ParseBoolean(values, "translate", defaultValue: true);
            var bilingual = translate && ParseBoolean(values, "bilingual", defaultValue: true);
            var model = FindModel(values.GetValueOrDefault("model", "standard"));
            var acceleration = ParseEnum(values, "acceleration", AccelerationMode.Auto);
            var performance = ParseEnum(values, "performance", PerformanceProfile.Balanced);
            var threads = ParseInteger(values, "threads", 0, 0, 256);
            var outputBase = values.GetValueOrDefault("output-base");
            var appRoot = Path.GetFullPath(values.GetValueOrDefault("app-root", AppContext.BaseDirectory));
            var dataRoot = values.TryGetValue("data-root", out var configuredDataRoot)
                ? Path.GetFullPath(configuredDataRoot)
                : null;

            var request = new PipelineRequest(
                Path.GetFullPath(input),
                Path.GetFullPath(output),
                source,
                target,
                translate,
                bilingual,
                model,
                acceleration,
                performance,
                threads,
                outputBase);
            return new ConvertOptions(request, appRoot, dataRoot);
        }

        public static Dictionary<string, string> ParseValues(IReadOnlyList<string> args, string command)
        {
            if (args.Count == 0 || !string.Equals(args[0], command, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"缺少 {command} 命令。使用 --help 查看用法。");
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < args.Count; index += 2)
            {
                var name = args[index];
                if (!name.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Count)
                {
                    throw new ArgumentException($"无效参数：{name}");
                }

                if (!values.TryAdd(name[2..], args[index + 1]))
                {
                    throw new ArgumentException($"参数重复：{name}");
                }
            }

            return values;
        }

        private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException($"缺少必需参数 --{name}。");

        private static bool ParseBoolean(
            IReadOnlyDictionary<string, string> values,
            string name,
            bool defaultValue)
        {
            if (!values.TryGetValue(name, out var value))
            {
                return defaultValue;
            }

            return bool.TryParse(value, out var parsed)
                ? parsed
                : throw new ArgumentException($"--{name} 必须是 true 或 false。");
        }

        public static int ParseInteger(
            IReadOnlyDictionary<string, string> values,
            string name,
            int defaultValue,
            int minimum,
            int maximum)
        {
            if (!values.TryGetValue(name, out var value))
            {
                return defaultValue;
            }

            return int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum
                ? parsed
                : throw new ArgumentException($"--{name} 必须是 {minimum}–{maximum} 之间的整数。");
        }

        public static double ParseDouble(
            IReadOnlyDictionary<string, string> values,
            string name,
            double defaultValue,
            double minimum,
            double maximum)
        {
            if (!values.TryGetValue(name, out var value))
            {
                return defaultValue;
            }

            return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
                   parsed >= minimum && parsed <= maximum
                ? parsed
                : throw new ArgumentException($"--{name} 必须是 {minimum}–{maximum} 之间的数字。");
        }

        public static T ParseEnum<T>(
            IReadOnlyDictionary<string, string> values,
            string name,
            T defaultValue) where T : struct, Enum
        {
            if (!values.TryGetValue(name, out var value))
            {
                return defaultValue;
            }

            return Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
                ? parsed
                : throw new ArgumentException($"--{name} 的值 {value} 无效。");
        }

        private static LanguageOption FindSource(string code) =>
            ModelCatalog.SourceLanguages.FirstOrDefault(option =>
                string.Equals(option.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"不支持的源语言：{code}");

        private static LanguageOption FindTarget(string code) =>
            ModelCatalog.TargetLanguages.FirstOrDefault(option =>
                string.Equals(option.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"不支持的目标语言：{code}");

        private static TranslationModelProfile FindModel(string id) =>
            ModelCatalog.TranslationProfiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"不支持的翻译模型：{id}");
    }

    private sealed record BurnOptions(BurnRequest Request, string AppRoot, string? DataRoot)
    {
        public static BurnOptions Parse(IReadOnlyList<string> args)
        {
            var values = ConvertOptions.ParseValues(args, "burn");
            var defaults = SubtitleBurnStyle.Default;
            var input = Required(values, "input");
            var subtitle = Required(values, "subtitle");
            var output = Required(values, "output");
            var style = new SubtitleBurnStyle(
                values.GetValueOrDefault("font-name", defaults.FontName),
                ConvertOptions.ParseDouble(values, "font-size", defaults.FontSize, 8, 240),
                values.GetValueOrDefault("primary-color", defaults.PrimaryColor),
                values.GetValueOrDefault("outline-color", defaults.OutlineColor),
                ConvertOptions.ParseDouble(values, "outline-width", defaults.OutlineWidth, 0, 20),
                ConvertOptions.ParseInteger(values, "margin-bottom", defaults.MarginBottom, 0, 2000));
            var encoder = ConvertOptions.ParseEnum(values, "encoder", BurnEncoderMode.Auto);
            var appRoot = Path.GetFullPath(values.GetValueOrDefault("app-root", AppContext.BaseDirectory));
            var dataRoot = values.TryGetValue("data-root", out var configuredDataRoot)
                ? Path.GetFullPath(configuredDataRoot)
                : null;
            return new BurnOptions(
                new BurnRequest(
                    Path.GetFullPath(input),
                    Path.GetFullPath(subtitle),
                    Path.GetFullPath(output),
                    style,
                    encoder),
                appRoot,
                dataRoot);
        }

        private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException($"缺少必需参数 --{name}。");
    }
}
