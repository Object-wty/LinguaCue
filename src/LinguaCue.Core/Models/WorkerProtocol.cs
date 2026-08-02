using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace LinguaCue.Models;

public enum WorkerMessageKind
{
    Progress,
    Result,
    Error,
    Canceled
}

public enum WorkerOperation
{
    Convert,
    Burn
}

public sealed record WorkerMessage(
    WorkerMessageKind Kind,
    PipelineProgress? Progress = null,
    PipelineResult? Result = null,
    string? Message = null,
    string? Detail = null,
    WorkerOperation Operation = WorkerOperation.Convert,
    BurnResult? BurnResult = null)
{
    public static WorkerMessage FromProgress(
        PipelineProgress progress,
        WorkerOperation operation = WorkerOperation.Convert) =>
        new(WorkerMessageKind.Progress, Progress: progress, Operation: operation);

    public static WorkerMessage FromResult(PipelineResult result) =>
        new(WorkerMessageKind.Result, Result: result);

    public static WorkerMessage FromBurnResult(BurnResult result) =>
        new(WorkerMessageKind.Result, Operation: WorkerOperation.Burn, BurnResult: result);

    public static WorkerMessage FromError(
        Exception exception,
        WorkerOperation operation = WorkerOperation.Convert) =>
        new(WorkerMessageKind.Error, Message: exception.Message, Detail: exception.ToString(), Operation: operation);

    public static WorkerMessage CanceledMessage(WorkerOperation operation = WorkerOperation.Convert) =>
        new(WorkerMessageKind.Canceled, Message: "任务已取消。", Operation: operation);
}

public static class WorkerProtocol
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static string Serialize(WorkerMessage message) =>
        JsonSerializer.Serialize(message, SerializerOptions);

    public static WorkerMessage Deserialize(string line) =>
        JsonSerializer.Deserialize<WorkerMessage>(line, SerializerOptions)
        ?? throw new JsonException("Worker 返回了空消息。");

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
