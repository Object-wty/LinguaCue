using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LinguaCue.Infrastructure;

namespace LinguaCue.Services;

internal sealed class LlamaServerSession : IAsyncDisposable
{
    private const int MaximumDiagnosticCharacters = 16_000;
    private const int SamplingSeed = 42;
    private readonly Process process;
    private readonly HttpClient httpClient;
    private readonly StringBuilder diagnostics = new();
    private readonly object diagnosticsLock = new();
    private readonly Task outputPump;
    private readonly Task errorPump;
    private readonly Action<string>? log;

    private LlamaServerSession(Process process, HttpClient httpClient, Action<string>? log)
    {
        this.process = process;
        this.httpClient = httpClient;
        this.log = log;
        outputPump = PumpAsync(process.StandardOutput);
        errorPump = PumpAsync(process.StandardError);
    }

    public static async Task<LlamaServerSession> StartAsync(
        ResolvedRuntimeTool executable,
        string model,
        int threads,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var port = ReserveLoopbackPort();
        var threadCount = threads > 0 ? threads : Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable.Path,
            WorkingDirectory = Path.GetDirectoryName(executable.Path) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        AddArguments(startInfo,
            "--model", model,
            "--host", IPAddress.Loopback.ToString(),
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "--ctx-size", "4096",
            "--parallel", "1",
            "--no-webui",
            "--jinja",
            "-ngl", executable.Backend == RuntimeBackend.Cpu ? "0" : "999",
            "--threads", threadCount.ToString(CultureInfo.InvariantCulture),
            "--threads-batch", threadCount.ToString(CultureInfo.InvariantCulture),
            "--reasoning", "off",
            "--seed", SamplingSeed.ToString(CultureInfo.InvariantCulture),
            "--log-verbosity", "2");

        log?.Invoke($"llama-server 后端：{executable.Backend}，线程：{threadCount}");
        log?.Invoke($"调用 llama-server：{CommandLineFormatter.Format(startInfo.FileName, startInfo.ArgumentList)}");
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ToolExecutionException($"无法启动工具：{executable.Path}");
            }
        }
        catch (Exception exception) when (exception is not ToolExecutionException)
        {
            process.Dispose();
            throw new ToolExecutionException($"无法启动工具：{executable.Path}", inner: exception);
        }

        var handler = new SocketsHttpHandler { UseProxy = false };
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://{IPAddress.Loopback}:{port}/"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        var session = new LlamaServerSession(process, httpClient, log);

        try
        {
            log?.Invoke("正在加载 Hy-MT2 模型（本次任务只加载一次）...");
            await session.WaitUntilReadyAsync(cancellationToken);
            log?.Invoke("Hy-MT2 模型已就绪，开始翻译。");
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async Task<string> CompleteAsync(
        string prompt,
        int maximumTokens,
        CancellationToken cancellationToken)
    {
        ThrowIfExited();
        var payload = JsonSerializer.Serialize(new
        {
            model = "Hy-MT2",
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0,
            seed = SamplingSeed,
            max_tokens = maximumTokens,
            stream = false
        });

        using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync("v1/chat/completions", requestContent, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ToolExecutionException(
                $"Hy-MT2 翻译服务返回 {(int)response.StatusCode} {response.ReasonPhrase}。{Environment.NewLine}" +
                $"诊断：{TrimDiagnostic(responseBody)}");
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            return content ?? string.Empty;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ToolExecutionException(
                $"Hy-MT2 翻译服务返回了无法解析的结果。{Environment.NewLine}诊断：{TrimDiagnostic(responseBody)}",
                inner: exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        httpClient.Dispose();
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The server exited between the state check and Kill.
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
        {
            // Disposal is best-effort; the process was already asked to stop.
        }

        await AwaitPumpAsync(outputPump);
        await AwaitPumpAsync(errorPump);
        process.Dispose();
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited();
            using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            healthTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                using var response = await httpClient.GetAsync("health", healthTimeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The listener is not ready yet.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // This health probe timed out; keep waiting for model loading.
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new ToolExecutionException(
            $"Hy-MT2 模型在 60 秒内未完成加载。{Environment.NewLine}诊断：{GetDiagnostics()}");
    }

    private void ThrowIfExited()
    {
        if (!process.HasExited)
        {
            return;
        }

        var exitCode = unchecked((uint)process.ExitCode);
        throw new ToolExecutionException(
            $"llama-server 意外退出，退出码：{process.ExitCode}（0x{exitCode:X8}）。{Environment.NewLine}" +
            $"诊断：{GetDiagnostics()}");
    }

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            log?.Invoke(line);

            lock (diagnosticsLock)
            {
                diagnostics.AppendLine(line);
                if (diagnostics.Length > MaximumDiagnosticCharacters)
                {
                    diagnostics.Remove(0, diagnostics.Length - MaximumDiagnosticCharacters);
                }
            }
        }
    }

    private string GetDiagnostics()
    {
        lock (diagnosticsLock)
        {
            return string.IsNullOrWhiteSpace(diagnostics.ToString())
                ? "没有错误详情。"
                : TrimDiagnostic(diagnostics.ToString());
        }
    }

    private static async Task AwaitPumpAsync(Task pump)
    {
        try
        {
            await pump.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException)
        {
            // The redirected stream may close while the process is being torn down.
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static string TrimDiagnostic(string value)
    {
        const int maximumLength = 2_000;
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[^maximumLength..];
    }
}
