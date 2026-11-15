using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunConvert;

/// <summary>A result file: name, temporary download link and size.</summary>
public sealed class ResultFile
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    /// <summary>Temporary download link.</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("size")] public long? Size { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
}

/// <summary>A step of a job. On error, <see cref="Code"/> is the category and <see cref="Reason"/> the exact, stable cause.</summary>
public sealed class JobTask
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("job_id")] public string? JobId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("operation")] public string Operation { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("percent")] public int? Percent { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("engine")] public string? Engine { get; set; }
    [JsonPropertyName("credits")] public int? Credits { get; set; }
    [JsonPropertyName("input")] public List<string>? Input { get; set; }
    /// <summary>import/direct/upload only: PUT the file here (see <see cref="Files.UploadAsync(string, string, string?, CancellationToken)"/>).</summary>
    [JsonPropertyName("upload_url")] public string? UploadUrl { get; set; }
    [JsonPropertyName("upload_deadline")] public string? UploadDeadline { get; set; }
    [JsonPropertyName("result")] public TaskResult? Result { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("reason_params")] public Dictionary<string, JsonElement>? ReasonParams { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("ended_at")] public string? EndedAt { get; set; }

    public sealed class TaskResult
    {
        [JsonPropertyName("files")] public List<ResultFile>? Files { get; set; }
    }
}

/// <summary>A job: named tasks that run in order (import → convert → export).</summary>
public sealed class Job
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>waiting | processing | finished | error</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("tag")] public string? Tag { get; set; }
    [JsonPropertyName("metadata")] public Dictionary<string, JsonElement>? Metadata { get; set; }
    [JsonPropertyName("credits_used")] public int? CreditsUsed { get; set; }
    [JsonPropertyName("credit_type")] public string? CreditType { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("ended_at")] public string? EndedAt { get; set; }
    [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
    [JsonPropertyName("tasks")] public List<JobTask>? Tasks { get; set; }

    /// <summary>The task with this name, or null.</summary>
    public JobTask? Task(string name) => Tasks?.FirstOrDefault(t => t.Name == name);

    /// <summary>The files produced by the job's export/url tasks.</summary>
    public List<ResultFile> ExportedFiles() =>
        (Tasks ?? new()).Where(t => t.Operation == "export/url").SelectMany(t => t.Result?.Files ?? new()).ToList();
}

/// <summary>One page of jobs.</summary>
public sealed record JobPage(List<Job> Data, int Page, int TotalPages, int Total);

/// <summary>Result of <see cref="RunConvertClient.ConvertAsync(string, ConvertOptions, CancellationToken)"/>.</summary>
public sealed record ConvertResult(Job Job, List<ResultFile> Files, List<string> Saved);

/// <summary>Options for <see cref="RunConvertClient.ConvertAsync(string, ConvertOptions, CancellationToken)"/>.</summary>
public sealed class ConvertOptions
{
    /// <summary>The target format, e.g. "mp3" (required).</summary>
    public string To { get; set; } = "";
    /// <summary>The source format; read from the file name or URL when null.</summary>
    public string? From { get; set; }
    /// <summary>Engine options, e.g. { ["audioBitrate"] = "192k" }.</summary>
    public Dictionary<string, object?>? Options { get; set; }
    /// <summary>Save results here (created if needed). Null: don't download.</summary>
    public string? OutDir { get; set; }
    public string? Tag { get; set; }
    public TimeSpan? Timeout { get; set; }
    public Action<Job>? OnUpdate { get; set; }
}

/// <summary>Builders for task definitions (plain dictionaries, so any documented option can be added).</summary>
public static class Tasks
{
    /// <summary>Imports a file from a public http(s) URL.</summary>
    public static Dictionary<string, object?> ImportUrl(string sourceUrl) => new() { ["operation"] = "import/url", ["source_url"] = sourceUrl };

    /// <summary>An upload task; the created task carries UploadUrl.</summary>
    public static Dictionary<string, object?> ImportUpload() => new() { ["operation"] = "import/direct/upload" };

    /// <summary>Converts <paramref name="input"/> (a task name, or string[] to merge/combine).</summary>
    public static Dictionary<string, object?> Convert(string inputFormat, string outputFormat, object input, IDictionary<string, object?>? options = null)
    {
        var t = options is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(options);
        t["operation"] = "convert";
        t["input"] = input;
        t["input_format"] = inputFormat;
        t["output_format"] = outputFormat;
        return t;
    }

    /// <summary>Temporary download links for <paramref name="input"/> (a task name or string[]).</summary>
    public static Dictionary<string, object?> ExportUrl(object input) => new() { ["operation"] = "export/url", ["input"] = input };
}

/// <summary>The API said no: {"success": false, "error": {"code", "message"}}. New codes can appear; branch on Status for unknown ones.</summary>
public class RunConvertException : Exception
{
    public RunConvertException(string message, int status = 0, string? code = null, string? body = null, Exception? inner = null) : base(message, inner)
    {
        Status = status;
        Code = code ?? "";
        Body = body;
    }

    /// <summary>HTTP status, 0 when the request never got a response.</summary>
    public int Status { get; }
    /// <summary>API error code, e.g. BILLING_INSUFFICIENT_CREDITS ("" when none).</summary>
    public string Code { get; }
    public string? Body { get; }
}

/// <summary>The job ended with status "error"; <see cref="Task"/> is the first failed task.</summary>
public sealed class JobFailedException : RunConvertException
{
    public JobFailedException(Job job, JobTask? task)
        : base(string.IsNullOrEmpty(task?.Message) ? $"Job {job.Id} failed" : task!.Message!, 0, task?.Code)
    {
        Job = job;
        Task = task;
    }

    public Job Job { get; }
    public JobTask? Task { get; }
}

/// <summary>WaitAsync gave up before the job reached "finished" or "error".</summary>
public sealed class WaitTimeoutException : RunConvertException
{
    public WaitTimeoutException(Job job, TimeSpan timeout) : base($"Job {job.Id} still {job.Status} after {timeout.TotalSeconds:0}s", 0, "WAIT_TIMEOUT") => Job = job;

    public Job Job { get; }
}
