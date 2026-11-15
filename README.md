# RunConvert SDK for .NET

> **Not published yet.** This SDK launches on NuGet soon, so the install command below won't work until then. Updates: https://www.runconvert.com/sdks

Convert files between 240+ formats (3,000+ conversions) with the [RunConvert API](https://www.runconvert.com/docs):
documents, images, video, audio, ebooks, archives, fonts and CAD. .NET 8+, no dependencies.

```bash
dotnet add package RunConvert
```

```csharp
using RunConvert;

using var rc = new RunConvertClient(); // reads RUNCONVERT_API_KEY

// One call: upload, convert, wait, download.
var result = await rc.ConvertAsync("report.docx", new ConvertOptions { To = "pdf", OutDir = "out" });
Console.WriteLine(string.Join(", ", result.Saved)); // out/report.pdf
```

Get an API key in **Dashboard → API Keys** on [runconvert.com](https://www.runconvert.com). Keep keys on the server.
With dependency injection, register one `RunConvertClient` as a singleton (or pass an `HttpClient` from `IHttpClientFactory`).

## Convert

```csharp
// From a URL (imported by RunConvert, nothing to upload)
await rc.ConvertAsync("https://example.com/talk.mp4", new ConvertOptions { To = "mp3", Options = new() { ["audioBitrate"] = "192k" } });

// Progress and a time limit
await rc.ConvertAsync("movie.mov", new ConvertOptions { To = "mp4", OutDir = "out", Timeout = TimeSpan.FromMinutes(10), OnUpdate = job => Console.WriteLine(job.Status) });
```

## Jobs with your own tasks

```csharp
// Merge two PDFs
var job = await rc.Jobs.CreateAsync(new Dictionary<string, object?>
{
    ["a"] = Tasks.ImportUrl("https://example.com/cover.pdf"),
    ["b"] = Tasks.ImportUrl("https://example.com/body.pdf"),
    ["merge"] = Tasks.Convert("pdf", "pdf", new[] { "a", "b" }),
    ["export"] = Tasks.ExportUrl("merge"),
}, tag: "merge");
job = await rc.Jobs.WaitAsync(job.Id);
foreach (var f in job.ExportedFiles()) await rc.Files.DownloadAsync(f, "out");

// Upload your own file
var upload = await rc.Jobs.CreateAsync(new Dictionary<string, object?>
{
    ["upload"] = Tasks.ImportUpload(),
    ["convert"] = Tasks.Convert("png", "webp", "upload", new Dictionary<string, object?> { ["image_quality"] = 80 }),
    ["export"] = Tasks.ExportUrl("convert"),
});
await rc.Files.UploadAsync(upload.Task("upload")!.UploadUrl!, "image.png");
```

| Method | What it does |
|---|---|
| `rc.Jobs.CreateAsync / GetAsync / ListAsync / AllAsync / WaitAsync / CancelAsync / DeleteAsync` | Jobs, with paging and polling |
| `rc.Files.UploadAsync(url, path or stream)` / `DownloadAsync(file, dir)` | Transfers |
| `rc.Operations.ListAsync() / ForInputAsync("mp4") / ForPairAsync("mp4", "mp3")` | Formats, targets, engines as `JsonNode` (no key needed) |
| `rc.MeAsync()` | Account, team and credit balance |

## Webhooks

```csharp
app.MapPost("/webhooks/runconvert", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    try
    {
        var ev = Webhooks.ConstructEvent(ms.ToArray(), req.Headers["X-RunConvert-Signature"], Environment.GetEnvironmentVariable("RUNCONVERT_WEBHOOK_SECRET")!);
        if ((string?)ev["event"] == "job.finished") { /* … */ }
        return Results.Ok();
    }
    catch (ArgumentException) { return Results.BadRequest(); }
});
```

## Errors

| Exception | When |
|---|---|
| `RunConvertException` | The API said no: `Status`, `Code` (e.g. `BILLING_INSUFFICIENT_CREDITS`), `Message` |
| `JobFailedException` | The job ended in `error`; `Task` has `Code`, `Reason`, `Message` |
| `WaitTimeoutException` | `WaitAsync` ran past its timeout |

Network errors, `429` and `5xx` are retried twice with backoff (`maxRetries`). Creating a job is only
retried when the request never reached the API, so a job is never created twice.

## Development

```bash
dotnet test --filter "Category!=Live"                                   # no network
RUNCONVERT_API_KEY=rc_live_… dotnet test --filter "Category=Live"       # real API, about 2 credits
```

## License

MIT
