using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RunConvert;

/// <summary>
/// The RunConvert API client (v3). Reuse one instance.
/// <code>
/// var rc = new RunConvertClient();   // reads RUNCONVERT_API_KEY
/// var r = await rc.ConvertAsync("report.docx", new ConvertOptions { To = "pdf", OutDir = "out" });
/// </code>
/// </summary>
public sealed class RunConvertClient : IDisposable
{
    public const string Version = "0.1.0";
    public const string DefaultBaseUrl = "https://api.runconvert.com/v3";
    private static readonly HashSet<int> RetryStatus = new() { 408, 425, 429, 500, 502, 503, 504 };
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly int _maxRetries;
    private readonly TimeSpan _timeout;
    private readonly bool _ownsHttp;
    internal readonly HttpClient Http;
    internal readonly string UserAgent = $"runconvert-dotnet/{Version} dotnet/{Environment.Version}";
    /// <summary>Waits between retries and polls (replaced in tests).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay = (d, ct) => System.Threading.Tasks.Task.Delay(d, ct);

    /// <param name="apiKey">Defaults to the RUNCONVERT_API_KEY environment variable.</param>
    /// <param name="baseUrl">Defaults to https://api.runconvert.com/v3.</param>
    /// <param name="timeout">Per API request (uploads and downloads have none). Default 60 s.</param>
    /// <param name="maxRetries">Retries for network errors, 429 and 5xx. Default 2.</param>
    /// <param name="httpClient">Your own HttpClient (e.g. from IHttpClientFactory).</param>
    public RunConvertClient(string? apiKey = null, string baseUrl = DefaultBaseUrl, TimeSpan? timeout = null, int maxRetries = 2, HttpClient? httpClient = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("RUNCONVERT_API_KEY") ?? "";
        _baseUrl = baseUrl.TrimEnd('/');
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
        _maxRetries = maxRetries;
        _ownsHttp = httpClient is null;
        Http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        Jobs = new Jobs(this);
        Operations = new Operations(this);
        Files = new Files(this);
    }

    public Jobs Jobs { get; }
    public Operations Operations { get; }
    public Files Files { get; }

    /// <summary>The account, team and credit balance behind the API key.</summary>
    public async Task<JsonNode?> MeAsync(CancellationToken ct = default) => (await RequestAsync(HttpMethod.Get, "/me", null, null, ct)).Data;

    /// <summary>
    /// Converts one file in one call: upload a local path (or import an http(s) URL), convert, wait,
    /// and download into <see cref="ConvertOptions.OutDir"/> when set.
    /// </summary>
    public async Task<ConvertResult> ConvertAsync(string fileOrUrl, ConvertOptions o, CancellationToken ct = default)
    {
        var isUrl = fileOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || fileOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var name = isUrl ? Path.GetFileName(new Uri(fileOrUrl).AbsolutePath) : Path.GetFileName(fileOrUrl);
        var from = (o.From ?? Path.GetExtension(name).TrimStart('.')).ToLowerInvariant();
        if (from.Length == 0) throw new ArgumentException("Can't tell the source format: set ConvertOptions.From (e.g. \"mp4\").");
        var tasks = new Dictionary<string, object?>
        {
            ["import-1"] = isUrl ? Tasks.ImportUrl(fileOrUrl) : Tasks.ImportUpload(),
            ["convert-1"] = Tasks.Convert(from, o.To.TrimStart('.').ToLowerInvariant(), "import-1", o.Options),
            ["export-1"] = Tasks.ExportUrl("convert-1"),
        };
        var job = await Jobs.CreateAsync(tasks, o.Tag, null, ct);
        if (!isUrl)
        {
            var upload = job.Task("import-1");
            if (string.IsNullOrEmpty(upload?.UploadUrl)) throw new RunConvertException("The job has no upload URL");
            await Files.UploadAsync(upload!.UploadUrl!, fileOrUrl, name, ct);
        }
        job = await Jobs.WaitAsync(job.Id, o.Timeout, o.OnUpdate, ct);
        var files = job.ExportedFiles();
        var saved = new List<string>();
        if (o.OutDir is not null)
            foreach (var f in files) saved.Add(await Files.DownloadAsync(f, o.OutDir, ct));
        return new ConvertResult(job, files, saved);
    }

    internal sealed record Result(JsonNode? Data, JsonNode? Meta);

    internal static T? As<T>(JsonNode? node) => node is null ? default : node.Deserialize<T>(Json);

    /// <summary>Sends a request and unwraps {"success": true, "data": …, "meta": …}.</summary>
    internal async Task<Result> RequestAsync(HttpMethod method, string path, object? body, IDictionary<string, string?>? query, CancellationToken ct)
    {
        var qs = query is null ? "" : string.Join("&", query.Where(kv => !string.IsNullOrEmpty(kv.Value)).Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        var uri = _baseUrl + path + (qs.Length > 0 ? "?" + qs : "");
        var payload = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, Json);
        // POST /jobs is only repeated when it never reached the server: a job is never created twice.
        var idempotent = method != HttpMethod.Post || path.EndsWith("/cancel", StringComparison.Ordinal);
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, uri);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.UserAgent.ParseAdd(UserAgent);
            if (_apiKey.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            if (payload is not null) req.Content = new ByteArrayContent(payload) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            HttpResponseMessage res;
            try
            {
                res = await Http.SendAsync(req, cts.Token);
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                var reached = e is TaskCanceledException; // a timeout may have reached the server
                if (attempt < _maxRetries && (idempotent || !reached)) { await Delay(Backoff(attempt), ct); continue; }
                throw new RunConvertException($"Request to RunConvert failed: {e.Message}", inner: e);
            }
            using (res)
            {
                var status = (int)res.StatusCode;
                if (RetryStatus.Contains(status) && attempt < _maxRetries && (idempotent || status == 429))
                {
                    var wait = res.Headers.RetryAfter?.Delta is { } d && d > TimeSpan.Zero ? (d > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : d) : Backoff(attempt);
                    await Delay(wait, ct);
                    continue;
                }
                if (res.StatusCode == HttpStatusCode.NoContent) return new Result(null, null);
                var text = await res.Content.ReadAsStringAsync(ct);
                JsonNode? root = null;
                try { root = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (JsonException) { }
                var success = root?["success"] is JsonValue sv && sv.TryGetValue<bool>(out var b) ? b : (bool?)null;
                if (status >= 400 || success == false)
                {
                    var err = root?["error"];
                    throw new RunConvertException(err?["message"]?.GetValue<string>() ?? $"RunConvert API error (HTTP {status})", status, err?["code"]?.GetValue<string>(), text);
                }
                return new Result(root?["data"], root?["meta"]);
            }
        }
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(8000, 500 * Math.Pow(2, attempt)) * (0.75 + Random.Shared.NextDouble() / 2));

    public void Dispose()
    {
        if (_ownsHttp) Http.Dispose();
    }
}

/// <summary>Create, read, wait for and stop jobs.</summary>
public sealed class Jobs
{
    private readonly RunConvertClient _c;
    internal Jobs(RunConvertClient c) => _c = c;

    private static string Esc(string id) => Uri.EscapeDataString(id);
    private static readonly Dictionary<string, string?> WithTasks = new() { ["include"] = "tasks" };

    /// <summary>Creates a job from named tasks (see <see cref="Tasks"/>). Returned with its tasks.</summary>
    public async Task<Job> CreateAsync(IDictionary<string, object?> tasks, string? tag = null, IDictionary<string, object?>? metadata = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["tasks"] = tasks };
        if (tag is not null) body["tag"] = tag;
        if (metadata is not null) body["metadata"] = metadata;
        return RunConvertClient.As<Job>((await _c.RequestAsync(HttpMethod.Post, "/jobs", body, WithTasks, ct)).Data)!;
    }

    /// <summary>A job with its tasks.</summary>
    public async Task<Job> GetAsync(string id, CancellationToken ct = default) =>
        RunConvertClient.As<Job>((await _c.RequestAsync(HttpMethod.Get, "/jobs/" + Esc(id), null, WithTasks, ct)).Data)!;

    /// <summary>One page of jobs, newest first.</summary>
    public async Task<JobPage> ListAsync(string? status = null, string? tag = null, int page = 0, int perPage = 0, CancellationToken ct = default)
    {
        var q = new Dictionary<string, string?>
        {
            ["filter[status]"] = status,
            ["filter[tag]"] = tag,
            ["page"] = page > 0 ? page.ToString() : null,
            ["per_page"] = perPage > 0 ? perPage.ToString() : null,
        };
        var r = await _c.RequestAsync(HttpMethod.Get, "/jobs", null, q, ct);
        var meta = r.Meta;
        int M(string k) => meta?[k] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        return new JobPage(RunConvertClient.As<List<Job>>(r.Data) ?? new(), M("page"), M("total_pages"), M("total"));
    }

    /// <summary>Every job, page by page: <c>await foreach (var job in rc.Jobs.AllAsync())</c>.</summary>
    public async IAsyncEnumerable<Job> AllAsync(string? status = null, string? tag = null, int perPage = 0, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var page = 1; ; page++)
        {
            var p = await ListAsync(status, tag, page, perPage, ct);
            foreach (var j in p.Data) yield return j;
            if (p.Data.Count == 0 || (p.TotalPages > 0 && page >= p.TotalPages)) yield break;
        }
    }

    /// <summary>Cancels pending tasks; credits for unfinished tasks are refunded.</summary>
    public async Task<Job> CancelAsync(string id, CancellationToken ct = default) =>
        RunConvertClient.As<Job>((await _c.RequestAsync(HttpMethod.Post, "/jobs/" + Esc(id) + "/cancel", null, null, ct)).Data)!;

    /// <summary>Deletes a job.</summary>
    public Task DeleteAsync(string id, CancellationToken ct = default) => _c.RequestAsync(HttpMethod.Delete, "/jobs/" + Esc(id), null, null, ct);

    /// <summary>
    /// Polls until "finished" or "error" (0.7 s, slowing to 3 s). Throws <see cref="JobFailedException"/> for a
    /// failed job and <see cref="WaitTimeoutException"/> after <paramref name="timeout"/>.
    /// </summary>
    public async Task<Job> WaitAsync(string id, TimeSpan? timeout = null, Action<Job>? onUpdate = null, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var delay = TimeSpan.FromMilliseconds(700);
        while (true)
        {
            var job = await GetAsync(id, ct);
            onUpdate?.Invoke(job);
            if (job.Status == "finished") return job;
            if (job.Status == "error") throw new JobFailedException(job, job.Tasks?.FirstOrDefault(t => t.Status == "error"));
            if (timeout is { } t && DateTime.UtcNow - started + delay > t) throw new WaitTimeoutException(job, t);
            await _c.Delay(delay, ct);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds + 300, 3000));
        }
    }
}

/// <summary>Formats, targets, engines and options. Public: no key needed.</summary>
public sealed class Operations
{
    private readonly RunConvertClient _c;
    internal Operations(RunConvertClient c) => _c = c;

    /// <summary>Every input format with its targets: {"categories": [...], "formats": [...]}.</summary>
    public async Task<JsonNode?> ListAsync(string? category = null, CancellationToken ct = default) =>
        (await _c.RequestAsync(HttpMethod.Get, "/operations", null, new Dictionary<string, string?> { ["category"] = category }, ct)).Data;

    /// <summary>What one format converts to, with credit costs.</summary>
    public async Task<JsonNode?> ForInputAsync(string input, CancellationToken ct = default) =>
        (await _c.RequestAsync(HttpMethod.Get, "/operations", null, new Dictionary<string, string?> { ["input"] = input.ToLowerInvariant() }, ct)).Data;

    /// <summary>Engines for one conversion and the options a convert task accepts.</summary>
    public async Task<JsonNode?> ForPairAsync(string input, string output, CancellationToken ct = default) =>
        (await _c.RequestAsync(HttpMethod.Get, "/operations", null, new Dictionary<string, string?> { ["input"] = input.ToLowerInvariant(), ["output"] = output.ToLowerInvariant() }, ct)).Data;
}

/// <summary>Uploads to import/direct/upload tasks and downloads of results.</summary>
public sealed class Files
{
    private readonly RunConvertClient _c;
    internal Files(RunConvertClient c) => _c = c;

    /// <summary>PUTs a local file to a task's upload URL before its deadline.</summary>
    public async Task UploadAsync(string uploadUrl, string path, string? filename = null, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        await UploadAsync(uploadUrl, stream, filename ?? Path.GetFileName(path), ct);
    }

    /// <summary>PUTs a stream to a task's upload URL; filename keeps its name.</summary>
    public async Task UploadAsync(string uploadUrl, Stream stream, string? filename, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(uploadUrl)) throw new ArgumentException("Empty upload URL (is it an import/direct/upload task?)");
        var url = filename is null ? uploadUrl : uploadUrl + (uploadUrl.Contains('?') ? "&" : "?") + "filename=" + Uri.EscapeDataString(filename);
        using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StreamContent(stream) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (stream.CanSeek) req.Content.Headers.ContentLength = stream.Length - stream.Position;
        req.Headers.UserAgent.ParseAdd(_c.UserAgent);
        using var res = await _c.Http.SendAsync(req, ct);
        if ((int)res.StatusCode >= 300)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new RunConvertException($"Upload failed (HTTP {(int)res.StatusCode}): {body[..Math.Min(300, body.Length)]}", (int)res.StatusCode, null, body);
        }
    }

    /// <summary>Saves a result file into <paramref name="dir"/> (created if needed), keeping its name. Returns the path.</summary>
    public Task<string> DownloadAsync(ResultFile file, string dir, CancellationToken ct = default) => DownloadAsync(file.Url, dir, file.Filename, ct);

    /// <summary>Saves a URL into <paramref name="dir"/>; filename null uses the last part of the URL.</summary>
    public async Task<string> DownloadAsync(string url, string dir, string? filename = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(filename ?? new Uri(url).AbsolutePath));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(_c.UserAgent);
        using var res = await _c.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)res.StatusCode >= 300) throw new RunConvertException($"Download failed (HTTP {(int)res.StatusCode})", (int)res.StatusCode);
        await using var output = File.Create(dest);
        await res.Content.CopyToAsync(output, ct);
        return dest;
    }
}

/// <summary>
/// Webhook signature checks. Every delivery has an X-RunConvert-Signature header: "sha256=" + hex
/// HMAC-SHA256 of the raw request body, keyed with the endpoint's secret.
/// </summary>
public static class Webhooks
{
    /// <summary>True when the signature matches the raw body (pass it exactly as received).</summary>
    public static bool Verify(byte[] rawBody, string? signature, string secret)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(secret)) return false;
        var given = signature.Trim().ToLowerInvariant();
        if (given.StartsWith("sha256=", StringComparison.Ordinal)) given = given[7..];
        if (given.Length != 64 || !given.All(Uri.IsHexDigit)) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody);
        return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(given));
    }

    public static bool Verify(string rawBody, string? signature, string secret) => Verify(Encoding.UTF8.GetBytes(rawBody), signature, secret);

    /// <summary>Verifies the signature, then parses the event ({"event", "job" | "task"}). Throws if it doesn't match.</summary>
    public static JsonNode ConstructEvent(byte[] rawBody, string? signature, string secret)
    {
        if (!Verify(rawBody, signature, secret)) throw new ArgumentException("Invalid RunConvert webhook signature");
        return JsonNode.Parse(rawBody) ?? throw new ArgumentException("Empty webhook body");
    }
}
