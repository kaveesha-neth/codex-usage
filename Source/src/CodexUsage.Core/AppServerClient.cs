using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace CodexUsage.Core;

/// <summary>JSONL transport. A closed allowlist prevents model, purchase, and reset calls.</summary>
public sealed class AppServerClient : IAsyncDisposable
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    { "initialize", "account/read", "account/rateLimits/read", "account/login/start", "account/login/cancel", "account/logout" };
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private Process? process;
    private Task? readerTask, errorTask;
    private long nextId;
    public event Action<string, JsonElement>? Notification;
    public event Action? Disconnected;
    public bool IsConnected => process is { HasExited: false } && readerTask is { IsCompleted: false };

    public async Task StartAsync(ProcessStartInfo start, CancellationToken ct)
    {
        if (process is not null) throw new InvalidOperationException("Client already started");
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        process = Process.Start(start) ?? throw new IOException("Could not launch Codex");
        readerTask = ReadLoopAsync(process.StandardOutput, stop.Token);
        // Drain stderr without storing or logging it: it may contain sensitive data.
        errorTask = DrainAsync(process.StandardError, stop.Token);
        await RequestAsync("initialize", new { clientInfo = new { name = "codex_usage_widget", title = "Codex Usage Widget", version = "1.0.0" } }, ct);
        await WriteAsync(new { method = "initialized", @params = new { } }, ct);
    }
    public async Task<JsonElement> RequestAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        if (!Allowed.Contains(method)) throw new InvalidOperationException("Method blocked by read-only monitor policy");
        if (method == "account/login/start" && JsonSerializer.SerializeToElement(parameters).GetProperty("type").GetString() != "chatgpt")
            throw new InvalidOperationException("Only managed ChatGPT login is supported");
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await WriteAsync(new { id, method, @params = parameters ?? new { } }, ct);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task WriteAsync(object value, CancellationToken ct)
    {
        await writer.WaitAsync(ct);
        try
        {
            if (process is null || process.HasExited) throw new IOException("Codex not connected");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }
        finally { writer.Release(); }
    }
    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length > 4_000_000) throw new InvalidDataException("Oversized protocol message");
                using var doc = JsonDocument.Parse(line);
                var msg = doc.RootElement;
                var id = UsageAdapter.Get(msg, "id");
                var method = UsageAdapter.String(msg, "method");
                if (method is not null)
                {
                    if (id.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                        await WriteAsync(new { id = id.Clone(), error = new { code = -32601, message = "Read-only widget does not handle server requests" } }, ct);
                    else Notification?.Invoke(method, UsageAdapter.Get(msg, "params").Clone());
                }
                else if (id.TryGetInt64(out var number) && pending.TryRemove(number, out var target))
                {
                    if (UsageAdapter.Get(msg, "error").ValueKind == JsonValueKind.Object)
                        target.TrySetException(new IOException("Codex rejected the request. Check CLI version or sign-in."));
                    else target.TrySetResult(UsageAdapter.Get(msg, "result").Clone());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException) { }
        finally
        {
            foreach (var entry in pending) entry.Value.TrySetException(new IOException("Codex connection closed"));
            if (!stop.IsCancellationRequested) Disconnected?.Invoke();
        }
    }
    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    { try { var buffer = new char[2048]; while (await reader.ReadAsync(buffer.AsMemory(), ct) > 0) { } } catch (Exception e) when (e is IOException or OperationCanceledException) { } }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        if (process is { } p)
        {
            try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { }
            if (readerTask is not null) await readerTask;
            if (errorTask is not null) await errorTask;
            p.Dispose();
        }
        stop.Dispose();
    }
}
