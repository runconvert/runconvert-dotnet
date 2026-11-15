using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RunConvert;
using Xunit;

public class UnitTests
{
    public record Call(string Method, string Path, Dictionary<string, string> Query, HttpRequestMessage Request, string Body);

    /// <summary>Routes every request to a handler instead of the network.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<Call> Calls = new();
        private readonly Func<Call, int, HttpResponseMessage> _reply;
        public FakeHandler(Func<Call, int, HttpResponseMessage> reply) => _reply = reply;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var q = (req.RequestUri!.Query.TrimStart('?')).Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            var call = new Call(req.Method.Method, req.RequestUri.AbsolutePath.Replace("/v3", ""), q, req, body);
            Calls.Add(call);
            return _reply(call, Calls.Count);
        }
    }

    private static HttpResponseMessage Ok(string data, string? meta = null) =>
        new(HttpStatusCode.OK) { Content = new StringContent($"{{\"success\":true,\"data\":{data}{(meta is null ? "" : ",\"meta\":" + meta)}}}") };

    private static HttpResponseMessage Fail(int status, string code, string msg) =>
        new((HttpStatusCode)status) { Content = new StringContent($"{{\"success\":false,\"error\":{{\"code\":\"{code}\",\"message\":\"{msg}\"}}}}") };

    private static (RunConvertClient, FakeHandler) Fake(Func<Call, int, HttpResponseMessage> reply)
    {
        var h = new FakeHandler(reply);
        var rc = new RunConvertClient("rc_live_test", "https://api.example/v3", maxRetries: 2, httpClient: new HttpClient(h)) { };
        rc.Delay = (_, _) => Task.CompletedTask; // no real waiting
        return (rc, h);
    }

    [Fact]
    public async Task SendsKeyAndUnwrapsEnvelope()
    {
        var (rc, h) = Fake((_, _) => Ok("{\"credits\":{\"total_remaining\":42}}"));
        Assert.Equal(42, (await rc.MeAsync())!["credits"]!["total_remaining"]!.GetValue<int>());
        Assert.Equal("Bearer rc_live_test", h.Calls[0].Request.Headers.Authorization!.ToString());
        Assert.StartsWith("runconvert-dotnet/", h.Calls[0].Request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task ApiErrorsAreTyped()
    {
        var (rc, _) = Fake((_, _) => Fail(422, "BILLING_INSUFFICIENT_CREDITS", "Insufficient credits."));
        var e = await Assert.ThrowsAsync<RunConvertException>(() => rc.Jobs.GetAsync("job_1"));
        Assert.Equal((422, "BILLING_INSUFFICIENT_CREDITS", "Insufficient credits."), (e.Status, e.Code, e.Message));
    }

    [Fact]
    public async Task RetriesGetButNeverDuplicatesAJob()
    {
        var (rc, h) = Fake((_, n) => n < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok("{\"id\":\"job_1\",\"status\":\"finished\"}"));
        Assert.Equal("finished", (await rc.Jobs.GetAsync("job_1")).Status);
        Assert.Equal(3, h.Calls.Count);

        var (rc2, h2) = Fake((_, _) => Fail(500, "INTERNAL", "boom"));
        await Assert.ThrowsAsync<RunConvertException>(() => rc2.Jobs.CreateAsync(new Dictionary<string, object?>()));
        Assert.Single(h2.Calls);

        var (rc3, h3) = Fake((_, n) => n == 1 ? Fail(429, "ACTIVE_JOB_LIMIT_REACHED", "x") : Ok("{\"id\":\"job_1\",\"status\":\"waiting\"}"));
        Assert.Equal("job_1", (await rc3.Jobs.CreateAsync(new Dictionary<string, object?>())).Id);
        Assert.Equal(2, h3.Calls.Count);
        Assert.Equal("tasks", h3.Calls[1].Query["include"]);
    }

    [Fact]
    public async Task ListAndAll()
    {
        var (rc, h) = Fake((c, _) => Ok($"[{{\"id\":\"job_p{c.Query["page"]}\",\"status\":\"finished\"}}]", $"{{\"page\":{c.Query["page"]},\"total_pages\":2}}"));
        var page = await rc.Jobs.ListAsync("finished", "x", 1, 1);
        Assert.Equal(2, page.TotalPages);
        Assert.Equal("finished", h.Calls[0].Query["filter[status]"]);
        Assert.Equal("x", h.Calls[0].Query["filter[tag]"]);
        var ids = new List<string>();
        await foreach (var j in rc.Jobs.AllAsync(perPage: 1)) ids.Add(j.Id);
        Assert.Equal(new[] { "job_p1", "job_p2" }, ids);
    }

    [Fact]
    public async Task WaitFinishedFailedAndTimeout()
    {
        var states = new[] { "waiting", "processing", "finished" };
        var (rc, _) = Fake((_, n) => Ok($"{{\"id\":\"job_1\",\"status\":\"{states[Math.Min(n - 1, 2)]}\"}}"));
        var seen = new List<string>();
        Assert.Equal("finished", (await rc.Jobs.WaitAsync("job_1", null, j => seen.Add(j.Status))).Status);
        Assert.Equal(states, seen);

        var (rc2, _) = Fake((_, _) => Ok("{\"id\":\"job_1\",\"status\":\"error\",\"tasks\":[{\"name\":\"c\",\"operation\":\"convert\",\"status\":\"error\",\"code\":\"INVALID_DATA\",\"reason\":\"INPUT_EMPTY\",\"message\":\"The file is empty.\"}]}"));
        var e = await Assert.ThrowsAsync<JobFailedException>(() => rc2.Jobs.WaitAsync("job_1"));
        Assert.Equal(("INPUT_EMPTY", "The file is empty."), (e.Task!.Reason, e.Message));

        var (rc3, _) = Fake((_, _) => Ok("{\"id\":\"job_1\",\"status\":\"processing\"}"));
        await Assert.ThrowsAsync<WaitTimeoutException>(() => rc3.Jobs.WaitAsync("job_1", TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public async Task DeleteAndOperations()
    {
        var (rc, h) = Fake((c, _) => c.Method == "DELETE" ? new HttpResponseMessage(HttpStatusCode.NoContent) : Ok("{\"engines\":[]}"));
        await rc.Jobs.DeleteAsync("job_1");
        await rc.Operations.ForPairAsync("MP4", "MP3");
        Assert.Equal(("mp4", "mp3"), (h.Calls[1].Query["input"], h.Calls[1].Query["output"]));
    }

    [Fact]
    public void WebhookSignatures()
    {
        var body = "{\"event\":\"job.finished\",\"job\":{\"id\":\"job_1\"}}";
        var sig = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("whsec_x"), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        Assert.True(Webhooks.Verify(body, sig, "whsec_x"));
        Assert.False(Webhooks.Verify(body + " ", sig, "whsec_x"));
        Assert.False(Webhooks.Verify(body, sig, "other"));
        Assert.False(Webhooks.Verify(body, "sha256=nothex", "whsec_x"));
        Assert.False(Webhooks.Verify(body, null, "whsec_x"));
        Assert.Equal("job_1", Webhooks.ConstructEvent(Encoding.UTF8.GetBytes(body), sig, "whsec_x")["job"]!["id"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => Webhooks.ConstructEvent(Encoding.UTF8.GetBytes(body), "sha256=" + new string('0', 64), "whsec_x"));
    }

    [Fact]
    public async Task ConvertUploadsWaitsAndDownloads()
    {
        var polls = 0;
        var (rc, h) = Fake((c, _) =>
        {
            if (c.Method == "POST" && c.Path == "/jobs")
                return Ok("{\"id\":\"job_1\",\"status\":\"waiting\",\"tasks\":[{\"name\":\"import-1\",\"operation\":\"import/direct/upload\",\"status\":\"waiting\",\"upload_url\":\"https://up.example/u/1?token=t\"}]}");
            if (c.Method == "PUT") return new HttpResponseMessage(HttpStatusCode.OK);
            if (c.Path == "/notes.pdf") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("PDF") };
            var status = ++polls > 1 ? "finished" : "processing";
            return Ok($"{{\"id\":\"job_1\",\"status\":\"{status}\",\"tasks\":[{{\"name\":\"export-1\",\"operation\":\"export/url\",\"status\":\"finished\",\"result\":{{\"files\":[{{\"filename\":\"notes.pdf\",\"url\":\"https://dl.example/notes.pdf\"}}]}}}}]}}");
        });
        var dir = Directory.CreateTempSubdirectory("rc-");
        var src = Path.Combine(dir.FullName, "notes.txt");
        await File.WriteAllTextAsync(src, "hello");
        var res = await rc.ConvertAsync(src, new ConvertOptions { To = "pdf", OutDir = Path.Combine(dir.FullName, "out"), Options = new() { ["page_size"] = "a4" } });
        var convert = JsonNode.Parse(h.Calls[0].Body)!["tasks"]!["convert-1"]!;
        Assert.Equal(("txt", "pdf", "a4"), (convert["input_format"]!.GetValue<string>(), convert["output_format"]!.GetValue<string>(), convert["page_size"]!.GetValue<string>()));
        var put = h.Calls.First(c => c.Method == "PUT");
        Assert.Equal(("notes.txt", "t", "hello"), (put.Query["filename"], put.Query["token"], put.Body));
        Assert.Equal("PDF", await File.ReadAllTextAsync(res.Saved[0]));
    }

    [Fact]
    public async Task ConvertFromUrlImportsIt()
    {
        var (rc, h) = Fake((c, _) => Ok($"{{\"id\":\"job_2\",\"status\":\"{(c.Method == "GET" ? "finished" : "waiting")}\"}}"));
        await rc.ConvertAsync("https://example.com/a/talk.MP4?x=1", new ConvertOptions { To = "mp3" });
        var tasks = JsonNode.Parse(h.Calls[0].Body)!["tasks"]!;
        Assert.Equal("import/url", tasks["import-1"]!["operation"]!.GetValue<string>());
        Assert.Equal("mp4", tasks["convert-1"]!["input_format"]!.GetValue<string>());
    }
}
