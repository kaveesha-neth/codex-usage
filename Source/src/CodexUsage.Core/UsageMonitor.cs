using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsage.Core;

public sealed record CachedUsage(string AccountKey, UsageSnapshot Snapshot);
public sealed record MonitorView(UsageSnapshot? Snapshot, string Connection, bool Stale, string Detail, string? Plan);

public sealed class UsageMonitor : IAsyncDisposable
{
    private readonly Func<ProcessStartInfo> start;
    private readonly string cachePath;
    private readonly SemaphoreSlim gate = new(1, 1), wake = new(0, 1);
    private readonly CancellationTokenSource stop = new();
    private AppServerClient? client;
    private Task? loop;
    private string? accountKey, loginId;
    private bool cacheChecked;
    private int failures, authChanged;
    private DateTimeOffset lastResetAttempt = DateTimeOffset.MinValue;
    public MonitorView View { get; private set; } = new(null, "Connecting", false, "Starting Codex app-server…", null);
    public event Action<MonitorView>? Changed;
    public UsageMonitor(Func<ProcessStartInfo> start, string cachePath) { this.start = start; this.cachePath = cachePath; }
    public void Start() => loop ??= RunAsync();
    public void Refresh() { try { wake.Release(); } catch (SemaphoreFullException) { } }
    private void Publish(MonitorView value) { View = value; Changed?.Invoke(value); }
    private async Task EnsureClientAsync(CancellationToken ct)
    {
        if (client?.IsConnected == true) return;
        if (client is not null) await client.DisposeAsync();
        loginId = null;
        client = new AppServerClient();
        client.Notification += (method, data) =>
        {
            // Notification payloads are deliberately not persisted. Sparse usage updates trigger a full read.
            if (method == "account/updated") Interlocked.Exchange(ref authChanged, 1);
            if (method == "account/login/completed") loginId = null;
            if (method is "account/rateLimits/updated" or "account/updated" or "account/login/completed") Refresh();
        };
        client.Disconnected += () => { Publish(View with { Connection = "Disconnected", Stale = View.Snapshot is not null, Detail = "Codex connection closed. Reconnecting…" }); Refresh(); };
        await client.StartAsync(start(), ct);
    }
    private async Task PollAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Interlocked.Exchange(ref authChanged, 0) != 0)
            {
                accountKey = null; cacheChecked = true; DeleteCache();
                Publish(new(null, "Checking account", false, "Authentication changed; reading account…", null));
            }
            await EnsureClientAsync(ct);
            var response = await client!.RequestAsync("account/read", new { refreshToken = false }, ct);
            var account = UsageAdapter.Get(response, "account");
            var type = UsageAdapter.String(account, "type");
            if (type != "chatgpt")
            {
                DeleteCache(); accountKey = null;
                Publish(new(null, loginId is not null ? "Signing in" : type == "apiKey" ? "API key only" : "Sign in required", false,
                    type == "apiKey" ? "API-key authentication uses separate billing. Sign in with ChatGPT to read subscription quotas." : loginId is not null ? "Complete the managed ChatGPT sign-in in your browser." : "Use Sign in to connect your managed ChatGPT account. Other auth types are not supported by this widget.", null));
                failures = 0; return;
            }
            // Only a one-way fingerprint is retained, never account email or full account response.
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.GetRawText())));
            var plan = UsageAdapter.String(account, "planType");
            if (accountKey is not null && accountKey != key) { DeleteCache(); Publish(new(null, "Account changed", false, "Loading current account…", plan)); }
            accountKey = key;
            if (!cacheChecked)
            {
                cacheChecked = true;
                try
                {
                    var cache = JsonSerializer.Deserialize<CachedUsage>(File.ReadAllText(cachePath));
                    if (cache?.AccountKey == key && cache.Snapshot is { Buckets: not null } cached && cached.Buckets.All(b => b is not null && b.Windows is not null) && cached.FetchedAt <= DateTimeOffset.UtcNow.AddMinutes(5)) Publish(new(cache.Snapshot, "Connecting", true, "Cached snapshot; awaiting live confirmation.", plan));
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
            }
            var limits = await client.RequestAsync("account/rateLimits/read", ct: ct);
            var snapshot = UsageAdapter.Parse(limits, DateTimeOffset.UtcNow);
            failures = 0;
            Publish(new(snapshot, "Connected", false, snapshot.Buckets.Count == 0 ? "No quota buckets were returned by this account." : "Usage read through local Codex app-server. No model requests.", plan));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllTextAsync(cachePath + ".tmp", JsonSerializer.Serialize(new CachedUsage(key, snapshot)), ct);
                File.Move(cachePath + ".tmp", cachePath, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            failures++;
            Publish(View with { Connection = "Disconnected", Stale = View.Snapshot is not null, Detail = "Could not read usage. Verify the native Codex executable and managed sign-in. Retrying with bounded backoff. (" + e.GetType().Name + ")" });
            if (client is not null) { await client.DisposeAsync(); client = null; }
        }
        finally { gate.Release(); }
    }
    private async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await PollAsync(stop.Token);
                var now = DateTimeOffset.UtcNow;
                var delay = failures > 0 ? failures <= 6 ? RetryPolicy.Delay(failures - 1, Random.Shared.NextDouble()) : TimeSpan.FromMinutes(3) : TimeSpan.FromMinutes(3);
                var resets = View.Snapshot?.Buckets.SelectMany(b => b.Windows).Select(w => w.ResetUtc).OfType<DateTimeOffset>().ToArray() ?? [];
                if (failures == 0 && resets.Length > 0)
                {
                    var next = resets.Min();
                    if (next <= now) { lastResetAttempt = now; delay = TimeSpan.FromSeconds(30); }
                    else delay = TimeSpan.FromSeconds(Math.Clamp((next - now).TotalSeconds + 1, 1, 180));
                }
                // After the bounded retry burst, ignore duplicate wakeups during the cooldown.
                if (failures > 0) { while (wake.Wait(0)) { } await Task.Delay(delay, stop.Token); }
                else await wake.WaitAsync(delay, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public void ClockTick(DateTimeOffset now)
    {
        // Local countdowns call this; at most one reset read every 30 seconds.
        if (View.Snapshot?.Buckets.SelectMany(b => b.Windows).Any(w => w.ResetUtc is { } r && r <= now) == true && now - lastResetAttempt >= TimeSpan.FromSeconds(30))
        { lastResetAttempt = now; Refresh(); }
    }
    public async Task<string?> SignInAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        ct = linked.Token;
        await gate.WaitAsync(ct);
        try
        {
            await EnsureClientAsync(ct);
            if (loginId is not null) return null;
            var response = await client!.RequestAsync("account/login/start", new { type = "chatgpt" }, ct);
            loginId = UsageAdapter.String(response, "loginId");
            Publish(View with { Connection = "Signing in", Detail = "Complete managed ChatGPT sign-in in your browser." });
            return UsageAdapter.String(response, "authUrl");
        }
        finally { gate.Release(); }
    }
    public async Task CancelLoginAsync()
    {
        await gate.WaitAsync();
        try { if (loginId is { } id && client?.IsConnected == true) await client.RequestAsync("account/login/cancel", new { loginId = id }, stop.Token); loginId = null; }
        finally { gate.Release(); Refresh(); }
    }
    public async Task SignOutAsync()
    {
        await gate.WaitAsync();
        try
        {
            await EnsureClientAsync(stop.Token);
            await client!.RequestAsync("account/logout", ct: stop.Token);
            loginId = null; accountKey = null; DeleteCache();
            Publish(new(null, "Sign in required", false, "Signed out of the shared managed Codex account.", null));
        }
        finally { gate.Release(); Refresh(); }
    }
    private void DeleteCache() { try { File.Delete(cachePath); File.Delete(cachePath + ".tmp"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        if (loop is not null) await loop;
        await gate.WaitAsync();
        try { if (client is not null) await client.DisposeAsync(); } finally { gate.Release(); }
        stop.Dispose();
    }
}
