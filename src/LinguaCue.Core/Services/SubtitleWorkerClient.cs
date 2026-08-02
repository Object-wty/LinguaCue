using System.Diagnostics;
using System.Globalization;
using System.Text;
using LinguaCue.Infrastructure;
using LinguaCue.Models;

namespace LinguaCue.Services;

public sealed class SubtitleWorkerException(string message, string? detail = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string? Detail { get; } = detail;

    public override string ToString() => string.IsNullOrWhiteSpace(Detail)
        ? base.ToString()
        : $"{base.ToString()}{Environment.NewLine}{Detail}";
}

public sealed class SubtitleWorkerClient(PortableLayout layout)
{
    private const int MaximumDiagnosticCharacters = 32_000;

    public ComponentStatus Inspect()
    {
        try
        {
            var launch = ResolveLaunch();
            return new ComponentStatus("控制台转换器", true, "已就绪", launch.WorkerPath);
        }
        catch (FileNotFoundException exception)
        {
            return new ComponentStatus("控制台转换器", false, exception.Message, null);
        }
    }

    public async Task<PipelineResult> RunAsync(
        PipelineRequest request,
        IProgress<PipelineProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var launch = ResolveLaunch();
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.FileName,
            WorkingDirectory = layout.AppBaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        foreach (var prefixArgument in launch.PrefixArguments)
        {
            startInfo.ArgumentList.Add(prefixArgument);
        }

        AddArguments(startInfo,
            "convert",
            "--input", request.InputPath,
            "--output", request.OutputDirectory,
            "--source", request.SourceLanguage.Code,
            "--target", request.TargetLanguage.Code,
            "--translate", request.Translate.ToString().ToLowerInvariant(),
            "--bilingual", request.GenerateBilingual.ToString().ToLowerInvariant(),
            "--model", request.TranslationModel.Id,
            "--acceleration", request.Acceleration.ToString().ToLowerInvariant(),
            "--performance", request.PerformanceProfile.ToString().ToLowerInvariant(),
            "--threads", Math.Max(request.Threads, 0).ToString(CultureInfo.InvariantCulture),
            "--app-root", layout.AppBaseDirectory,
            "--data-root", layout.DataRoot);
        if (!string.IsNullOrWhiteSpace(request.OutputBaseName))
        {
            AddArguments(startInfo, "--output-base", request.OutputBaseName);
        }

        log?.Invoke($"调用字幕控制台：{CommandLineFormatter.Format(startInfo.FileName, startInfo.ArgumentList)}");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new SubtitleWorkerException($"无法启动字幕控制台程序：{launch.FileName}");
            }
        }
        catch (Exception exception) when (exception is not SubtitleWorkerException)
        {
            throw new SubtitleWorkerException($"无法启动字幕控制台程序：{launch.FileName}", inner: exception);
        }

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The worker exited between the state check and Kill.
            }
        });

        var outputState = new WorkerOutputState();
        var diagnostics = new StringBuilder();
        var outputTask = ReadProtocolAsync(
            process.StandardOutput,
            outputState,
            progress,
            diagnostics,
            cancellationToken);
        var errorTask = ReadStandardErrorAsync(
            process.StandardError,
            diagnostics,
            log,
            cancellationToken);

        await Task.WhenAll(process.WaitForExitAsync(cancellationToken), outputTask, errorTask);
        cancellationToken.ThrowIfCancellationRequested();

        if (outputState.Result is not null && process.ExitCode == 0)
        {
            return outputState.Result;
        }

        if (outputState.Canceled)
        {
            throw new OperationCanceledException("字幕控制台任务已取消。", cancellationToken);
        }

        var message = outputState.ErrorMessage
            ?? $"字幕控制台程序异常退出（退出码 {process.ExitCode}）。";
        var detail = outputState.ErrorDetail ?? GetDiagnostic(diagnostics);
        throw new SubtitleWorkerException(message, detail);
    }

    public async Task<BurnResult> RunBurnAsync(
        BurnRequest request,
        IProgress<PipelineProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var launch = ResolveLaunch();
        var startInfo = new ProcessStartInfo
        {
            FileName = launch.FileName,
            WorkingDirectory = layout.AppBaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        foreach (var prefixArgument in launch.PrefixArguments)
        {
            startInfo.ArgumentList.Add(prefixArgument);
        }

        AddArguments(startInfo,
            "burn",
            "--input", request.InputVideoPath,
            "--subtitle", request.SubtitlePath,
            "--output", request.OutputPath,
            "--font-name", request.Style.FontName,
            "--font-size", request.Style.FontSize.ToString(CultureInfo.InvariantCulture),
            "--primary-color", request.Style.PrimaryColor,
            "--outline-color", request.Style.OutlineColor,
            "--outline-width", request.Style.OutlineWidth.ToString(CultureInfo.InvariantCulture),
            "--margin-bottom", request.Style.MarginBottom.ToString(CultureInfo.InvariantCulture),
            "--encoder", request.Encoder.ToString().ToLowerInvariant(),
            "--app-root", layout.AppBaseDirectory,
            "--data-root", layout.DataRoot);

        log?.Invoke($"调用字幕控制台：{CommandLineFormatter.Format(startInfo.FileName, startInfo.ArgumentList)}");
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new SubtitleWorkerException($"无法启动字幕控制台程序：{launch.FileName}");
            }
        }
        catch (Exception exception) when (exception is not SubtitleWorkerException)
        {
            throw new SubtitleWorkerException($"无法启动字幕控制台程序：{launch.FileName}", inner: exception);
        }

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The worker exited between the state check and Kill.
            }
        });

        var outputState = new WorkerOutputState();
        var diagnostics = new StringBuilder();
        var outputTask = ReadProtocolAsync(process.StandardOutput, outputState, progress, diagnostics, cancellationToken);
        var errorTask = ReadStandardErrorAsync(process.StandardError, diagnostics, log, cancellationToken);
        await Task.WhenAll(process.WaitForExitAsync(cancellationToken), outputTask, errorTask);
        cancellationToken.ThrowIfCancellationRequested();

        if (outputState.BurnResult is not null && process.ExitCode == 0)
        {
            return outputState.BurnResult;
        }

        if (outputState.Canceled)
        {
            throw new OperationCanceledException("字幕烧录任务已取消。", cancellationToken);
        }

        var message = outputState.ErrorMessage
            ?? $"字幕控制台程序异常退出（退出码 {process.ExitCode}）。";
        var detail = outputState.ErrorDetail ?? GetDiagnostic(diagnostics);
        throw new SubtitleWorkerException(message, detail);
    }

    private WorkerLaunch ResolveLaunch()
    {
        var appHostName = OperatingSystem.IsWindows() ? "LinguaCue.Cli.exe" : "LinguaCue.Cli";
        var appHostPath = Path.Combine(layout.AppBaseDirectory, appHostName);
        if (File.Exists(appHostPath))
        {
            return new WorkerLaunch(appHostPath, [], appHostPath);
        }

        var assemblyPath = Path.Combine(layout.AppBaseDirectory, "LinguaCue.Cli.dll");
        if (File.Exists(assemblyPath))
        {
            var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            return new WorkerLaunch(
                string.IsNullOrWhiteSpace(dotnetHost) ? "dotnet" : dotnetHost,
                [assemblyPath],
                assemblyPath);
        }

        throw new FileNotFoundException(
            $"未找到字幕控制台程序。请确认 {appHostName} 或 LinguaCue.Cli.dll 与 LinguaCue 位于同一目录。",
            appHostPath);
    }

    private static async Task ReadProtocolAsync(
        StreamReader reader,
        WorkerOutputState state,
        IProgress<PipelineProgress>? progress,
        StringBuilder diagnostics,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            WorkerMessage message;
            try
            {
                message = WorkerProtocol.Deserialize(line);
            }
            catch (System.Text.Json.JsonException)
            {
                AppendDiagnostic(diagnostics, line);
                continue;
            }

            switch (message.Kind)
            {
                case WorkerMessageKind.Progress when message.Progress is not null:
                    progress?.Report(message.Progress);
                    break;
                case WorkerMessageKind.Result when message.Result is not null:
                    state.Result = message.Result;
                    break;
                case WorkerMessageKind.Result when message.BurnResult is not null:
                    state.BurnResult = message.BurnResult;
                    break;
                case WorkerMessageKind.Error:
                    state.ErrorMessage = message.Message;
                    state.ErrorDetail = message.Detail;
                    break;
                case WorkerMessageKind.Canceled:
                    state.Canceled = true;
                    break;
            }
        }
    }

    private static async Task ReadStandardErrorAsync(
        StreamReader reader,
        StringBuilder diagnostics,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            AppendDiagnostic(diagnostics, line);
            log?.Invoke(line);
        }
    }

    private static void AppendDiagnostic(StringBuilder diagnostics, string line)
    {
        if (diagnostics.Length >= MaximumDiagnosticCharacters)
        {
            return;
        }

        diagnostics.AppendLine(line);
    }

    private static string GetDiagnostic(StringBuilder diagnostics) =>
        diagnostics.Length == 0 ? "没有错误详情。" : diagnostics.ToString().Trim();

    private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private sealed record WorkerLaunch(
        string FileName,
        IReadOnlyList<string> PrefixArguments,
        string WorkerPath);

    private sealed class WorkerOutputState
    {
        public PipelineResult? Result { get; set; }
        public BurnResult? BurnResult { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorDetail { get; set; }
        public bool Canceled { get; set; }
    }
}
