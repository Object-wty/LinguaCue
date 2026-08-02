using System.Diagnostics;
using System.Text;

namespace LinguaCue.Infrastructure;

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    string? StandardInput = null);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ToolExecutionException(string message, ProcessResult? result = null, Exception? inner = null)
    : Exception(message, inner)
{
    public ProcessResult? Result { get; } = result;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessRequest request,
        Action<string>? onStandardOutput = null,
        Action<string>? onStandardError = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    private const int MaximumCapturedCharacters = 1_000_000;

    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        Action<string>? onStandardOutput = null,
        Action<string>? onStandardError = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory ?? Path.GetDirectoryName(request.FileName) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        if (request.StandardInput is not null)
        {
            startInfo.StandardInputEncoding = Encoding.UTF8;
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ToolExecutionException($"无法启动工具：{request.FileName}");
            }
        }
        catch (Exception exception) when (exception is not ToolExecutionException)
        {
            throw new ToolExecutionException($"无法启动工具：{request.FileName}", inner: exception);
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
                // The process exited between the state check and Kill.
            }
        });

        if (request.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        var output = new StringBuilder();
        var error = new StringBuilder();
        var outputTask = ReadLinesAsync(process.StandardOutput, output, onStandardOutput, cancellationToken);
        var errorTask = ReadLinesAsync(process.StandardError, error, onStandardError, cancellationToken);

        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), outputTask, errorTask);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        StringBuilder capture,
        Action<string>? callback,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            callback?.Invoke(line);
            if (capture.Length < MaximumCapturedCharacters)
            {
                capture.AppendLine(line);
            }
        }
    }
}
