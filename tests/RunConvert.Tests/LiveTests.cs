using RunConvert;
using Xunit;

/// <summary>Live tests against the real API (about 2 credits). Run with RUNCONVERT_API_KEY set and --filter Category=Live.</summary>
[Trait("Category", "Live")]
public class LiveTests
{
    private static bool HasKey => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RUNCONVERT_API_KEY"));

    [SkippableLiveFact]
    public async Task MeAndOperations()
    {
        using var rc = new RunConvertClient();
        Assert.NotNull((await rc.MeAsync())!["credits"]);
        using var pub = new RunConvertClient("");
        Assert.Contains((await pub.Operations.ForInputAsync("TXT"))!["outputs"]!.AsArray(), o => o!["format"]!.GetValue<string>() == "pdf");
        Assert.NotEmpty((await pub.Operations.ForPairAsync("mp4", "mp3"))!["engines"]!.AsArray());
    }

    [SkippableLiveFact]
    public async Task ConvertTextToPdf()
    {
        var dir = Directory.CreateTempSubdirectory("rc-live-");
        var src = Path.Combine(dir.FullName, "sdk-live-test.txt");
        await File.WriteAllTextAsync(src, "RunConvert .NET SDK live test\nLine two\n\nCafé – 你好 – Привет\n");
        var seen = new List<string>();
        using var rc = new RunConvertClient();
        var res = await rc.ConvertAsync(src, new ConvertOptions { To = "pdf", OutDir = Path.Combine(dir.FullName, "out"), Tag = "sdk-dotnet-live-test", Timeout = TimeSpan.FromSeconds(150), OnUpdate = j => { if (seen.LastOrDefault() != j.Status) seen.Add(j.Status); } });
        var pdf = await File.ReadAllBytesAsync(res.Saved[0]);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
        Console.WriteLine($"  job {res.Job.Id}: {string.Join(" → ", seen)}, {res.Job.CreditsUsed} credit(s), {pdf.Length} bytes");
    }

    [SkippableLiveFact]
    public async Task BadKey()
    {
        using var rc = new RunConvertClient("rc_live_invalid", maxRetries: 0);
        var e = await Assert.ThrowsAsync<RunConvertException>(() => rc.MeAsync());
        Assert.Equal(401, e.Status);
    }

    /// <summary>A Fact that is skipped without RUNCONVERT_API_KEY.</summary>
    private sealed class SkippableLiveFactAttribute : FactAttribute
    {
        public SkippableLiveFactAttribute() { if (!HasKey) Skip = "set RUNCONVERT_API_KEY to run live tests"; }
    }
}
