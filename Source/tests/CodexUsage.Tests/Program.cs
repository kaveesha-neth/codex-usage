using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CodexUsage.Core;

if (args.Contains("--fake-server")) { await FakeServer(); return; }
var passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
UsageSnapshot Parse(string json) { using var d = JsonDocument.Parse(json); return UsageAdapter.Parse(d.RootElement, DateTimeOffset.FromUnixTimeSeconds(1700000000)); }
var multi = Parse("""
{"rateLimitsByLimitId":{"codex":{"limitId":"codex","limitName":"General","primary":{"usedPercent":38.125,"windowDurationMins":300,"resetsAt":1700003600},"secondary":{"usedPercent":110,"windowDurationMins":10080,"resetsAt":1700600000}},"other_id":{"limitName":null,"primary":{"usedPercent":0,"resetsAt":0}},"unknown":null},"rateLimits":{"limitId":"codex","primary":{"usedPercent":99}}}
""");
Check(multi.Buckets.Count == 3, "Multiple buckets; legacy mirror deduplicated; null bucket retained");
Check(multi.Buckets[0].Name == "General" && multi.Buckets[1].Name == "Other Id", "Supplied name preferred and ID label derived");
Check(multi.Buckets[0].Windows.Count == 2, "Primary and secondary windows retained");
var w = multi.Buckets[0].Windows[0];
Check(w.UsedPercent == 38.125 && w.RemainingPercent == 61.875, "Fractional percentage preserved without counts");
Check(multi.Buckets[0].Windows[1].RemainingPercent == 0, "Remaining clamped at zero for over-limit usage");
Check(multi.Buckets[1].Windows[0].UsedPercent == 0 && multi.Buckets[1].Windows[0].ResetUtc == DateTimeOffset.UnixEpoch, "Explicit zero is meaningful");
Check(w.ResetUtc == DateTimeOffset.FromUnixTimeSeconds(1700003600), "Unix seconds converted correctly");
var nulls = Parse("""{"rateLimits":{"limitId":"nulls","primary":{"usedPercent":null,"windowDurationMins":null,"resetsAt":null},"secondary":null}}""");
var n = nulls.Buckets[0].Windows[0];
Check(n.UsedPercent is null && n.RemainingPercent is null && n.ResetUtc is null && n.ResetLocal(TimeZoneInfo.Utc) == "Unavailable", "Null values remain unavailable, never zero");
Check(nulls.Buckets[0].Windows.Count == 1, "Absent secondary not fabricated");
var unknown = Parse("""{"rateLimitsByLimitId":{"future":{"primary":{"usedPercent":"42","windowDurationMins":{},"resetsAt":1.5,"futureMetric":100},"secondary":"changed"}}}""");
Check(unknown.Buckets[0].Windows.All(x => x.UsedPercent is null && x.ResetUtc is null), "Changed field types fail closed to unavailable");
Check(Parse("""{"rateLimitsByLimitId":{}}""").Buckets.Count == 0, "Honest empty map");
Check(Parse("""{"rateLimits":null,"rateLimitsByLimitId":null}""").Buckets.Count == 0, "Null bucket collections produce honest empty state");
Check(new QuotaWindow("primary", null, null, long.MaxValue).ResetUtc is null, "Out-of-range timestamp is unavailable");
Check(w.DurationLabel == "5-hour window" && multi.Buckets[0].Windows[1].DurationLabel == "1 week window", "Duration derives from server minutes");
Check(w.Countdown(DateTimeOffset.FromUnixTimeSeconds(1700003601)) == "Refreshing…", "Expired reset never invents recovered usage");
var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
var before = new QuotaWindow("primary", 10, 60, new DateTimeOffset(2026, 3, 8, 6, 59, 0, TimeSpan.Zero).ToUnixTimeSeconds());
var after = before with { ResetsAt = before.ResetsAt + 120 };
Check(TimeZoneInfo.ConvertTime(before.ResetUtc!.Value, zone).Hour == 1 && TimeZoneInfo.ConvertTime(after.ResetUtc!.Value, zone).Hour == 3, "Spring daylight-saving gap conversion");
var fall1 = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero); var fall2 = fall1.AddHours(1);
Check(TimeZoneInfo.ConvertTime(fall1, zone).Hour == 1 && TimeZoneInfo.ConvertTime(fall2, zone).Hour == 1 && TimeZoneInfo.ConvertTime(fall1, zone).Offset != TimeZoneInfo.ConvertTime(fall2, zone).Offset, "Fall repeated local hour retains distinct offsets");
Check(TimeZoneInfo.ConvertTime(w.ResetUtc!.Value, TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromMinutes(330), "test", "test")).Offset == TimeSpan.FromMinutes(330), "Half-hour local timezone conversion");
var state = new UsageState(); state.Success(multi); state.Failure();
Check(state.Snapshot == multi && state.IsStale(multi.FetchedAt.AddMinutes(1)), "Outage retains timestamped stale snapshot");
state.Success(multi); Check(!state.Stale && state.Connection == "Connected", "Successful reconnect clears stale state");
Check(state.IsStale(multi.FetchedAt.AddMinutes(7)), "Silent connection becomes stale after freshness threshold");
state.Clear("Sign in required"); Check(state.Snapshot is null, "Sign-out clears cached view");
Check(RetryPolicy.Delay(0, 0) >= TimeSpan.FromSeconds(.8) && RetryPolicy.Delay(7, 1) <= TimeSpan.FromSeconds(144) && RetryPolicy.Delay(2, 0) < RetryPolicy.Delay(2, 1), "Bounded exponential retry and jitter");
var file = Path.Combine(Path.GetTempPath(), "codex-widget-tests-" + Guid.NewGuid().ToString("N"));
ProcessStartInfo FakeStart()
{
    var start = new ProcessStartInfo(Environment.ProcessPath!);
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath)!.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--fake-server"); return start;
}
await using (var rpc = new AppServerClient())
{
    var notification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    rpc.Notification += (method, _) => { if (method == "account/rateLimits/updated") notification.TrySetResult(true); };
    await rpc.StartAsync(FakeStart(), default);
    var account = await rpc.RequestAsync("account/read"); Check(UsageAdapter.String(UsageAdapter.Get(account, "account"), "type") == "chatgpt", "Stdio initialize and initialized handshake");
    var limits = await rpc.RequestAsync("account/rateLimits/read"); Check(UsageAdapter.Parse(limits, DateTimeOffset.UtcNow).Buckets.Count == 2, "JSONL response correlation");
    Check(await notification.Task.WaitAsync(TimeSpan.FromSeconds(5)), "Rate-limit update notification dispatched");
    foreach (var blocked in new[] { "thread/start", "turn/start", "account/rateLimitResetCredit/consume", "account/sendAddCreditsNudgeEmail", "credits/purchase" })
    {
        var rejected = false; try { await rpc.RequestAsync(blocked); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Read-only method allowlist blocks " + blocked);
    }
    var keyRejected = false; try { await rpc.RequestAsync("account/login/start", new { type = "apiKey" }); } catch (InvalidOperationException) { keyRejected = true; }
    Check(keyRejected, "API-key login blocked");
}
// Actual monitor transport replacement after a pipe EOF, not merely a state mock.
var starts = 0;
await using (var monitor = new UsageMonitor(() => { Interlocked.Increment(ref starts); return FakeStart(); }, file))
{
    var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    monitor.Changed += value => { if (value.Connection == "Connected" && starts >= 2) connected.TrySetResult(true); };
    monitor.Start();
    Check(await connected.Task.WaitAsync(TimeSpan.FromSeconds(15)), "Monitor reconnects and reinitializes after server EOF");
    Check(monitor.View.Snapshot?.Buckets.Count == 2 && !monitor.View.Stale, "Reconnect fetches full snapshot and clears stale status");
}
try { File.Delete(file); } catch (IOException) { }
Console.WriteLine($"\n{passed} checks passed.");

static async Task FakeServer()
{
    var initialized = false; var reads = 0;
    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var d = JsonDocument.Parse(line); var e = d.RootElement; var method = UsageAdapter.String(e, "method");
        if (method == "initialized") { initialized = true; continue; }
        var id = UsageAdapter.Get(e, "id").Clone();
        object result = new { };
        if (method != "initialize" && !initialized) { Console.WriteLine(JsonSerializer.Serialize(new { id, error = new { code = -32000, message = "Not initialized" } })); continue; }
        if (method == "account/read") result = new { account = new { type = "chatgpt", email = "synthetic@example.invalid", planType = "plus" } };
        if (method == "account/rateLimits/read")
        {
            result = new { rateLimitsByLimitId = new { codex = new { primary = new { usedPercent = 7.5, windowDurationMins = 300, resetsAt = 4102444800L } }, extra = new { secondary = new { usedPercent = 42, windowDurationMins = 60, resetsAt = 4102444800L } } } };
        }
        Console.WriteLine(JsonSerializer.Serialize(new { id, result }));
        if (method == "account/rateLimits/read")
        {
            Console.WriteLine("{\"method\":\"account/rateLimits/updated\",\"params\":{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":8}}}}");
            if (++reads >= 2) return; // Notification asks for reconciliation, then exercise EOF.
        }
    }
}
