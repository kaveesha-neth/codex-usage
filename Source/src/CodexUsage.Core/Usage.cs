using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexUsage.Core;

public sealed record QuotaWindow(string Kind, double? UsedPercent, double? WindowDurationMins, long? ResetsAt)
{
    [JsonIgnore] public double? RemainingPercent => UsedPercent is { } n ? Math.Max(0, 100 - n) : null;
    [JsonIgnore] public DateTimeOffset? ResetUtc { get { try { return ResetsAt is { } n ? DateTimeOffset.FromUnixTimeSeconds(n) : null; } catch (ArgumentOutOfRangeException) { return null; } } }
    [JsonIgnore] public string DurationLabel => WindowDurationMins is > 0 and var n
        ? n % 10080 == 0 ? $"{n / 10080:G} week window" : n % 1440 == 0 ? $"{n / 1440:G} day window" : n % 60 == 0 ? $"{n / 60:G}-hour window" : $"{n:G}-minute window"
        : $"{Kind} window · duration unavailable";
    public string ResetLocal(TimeZoneInfo zone) => ResetUtc is { } r ? TimeZoneInfo.ConvertTime(r, zone).ToString("ddd, dd MMM yyyy · h:mm:ss tt zzz", CultureInfo.CurrentCulture) : "Unavailable";
    public string Countdown(DateTimeOffset now)
    {
        if (ResetUtc is not { } r) return "Unavailable";
        var t = r - now;
        if (t <= TimeSpan.Zero) return "Refreshing…";
        return t.TotalDays >= 1 ? $"in {(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : t.TotalHours >= 1 ? $"in {(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s" : $"in {t.Minutes}m {t.Seconds}s";
    }
    public static string Percent(double? n) => n?.ToString("G", CultureInfo.CurrentCulture) + (n.HasValue ? "%" : "Unavailable");
}
public sealed record QuotaBucket(string Id, string Name, IReadOnlyList<QuotaWindow> Windows);
public sealed record UsageSnapshot(DateTimeOffset FetchedAt, IReadOnlyList<QuotaBucket> Buckets);

public static class UsageAdapter
{
    public static UsageSnapshot Parse(JsonElement result, DateTimeOffset now)
    {
        if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Unexpected usage shape");
        var buckets = new Dictionary<string, QuotaBucket>(StringComparer.Ordinal);
        var map = Get(result, "rateLimitsByLimitId");
        if (map.ValueKind == JsonValueKind.Object)
            foreach (var item in map.EnumerateObject()) buckets[item.Name] = Bucket(item.Value, item.Name);
        var single = Get(result, "rateLimits");
        if (single.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined)) throw new InvalidDataException("Unexpected legacy quota shape");
        if (single.ValueKind == JsonValueKind.Object)
        {
            var id = String(single, "limitId");
            // Legacy single view is a mirror. Never duplicate the mapped bucket.
            if (id is not null && !buckets.ContainsKey(id)) buckets[id] = Bucket(single, id);
            else if (buckets.Count == 0) buckets["unidentified"] = Bucket(single, "unidentified");
        }
        if (map.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined)) throw new InvalidDataException("Unexpected bucket map");
        if (map.ValueKind == JsonValueKind.Undefined && single.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Usage fields unavailable");
        return new(now, buckets.Values.ToArray());
    }
    private static QuotaBucket Bucket(JsonElement value, string id)
    {
        var windows = new List<QuotaWindow>();
        foreach (var kind in new[] { "primary", "secondary" })
        {
            var w = Get(value, kind);
            // Explicit null means the service did not return this window; do not invent it.
            if (w.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            windows.Add(new(kind, Number(w, "usedPercent"), Number(w, "windowDurationMins"), Integer(w, "resetsAt")));
        }
        return new(id, String(value, "limitName") ?? Readable(id), windows);
    }
    public static string Readable(string id) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(Regex.Replace(id, "[_./-]+", " "));
    public static JsonElement Get(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    public static string? String(JsonElement e, string key) => Get(e, key) is var v && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && !string.IsNullOrWhiteSpace(s) ? s : null;
    private static double? Number(JsonElement e, string key) => Get(e, key) is var v && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    private static long? Integer(JsonElement e, string key) => Get(e, key) is var v && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}

public sealed class UsageState
{
    public UsageSnapshot? Snapshot { get; private set; }
    public string Connection { get; private set; } = "Connecting";
    public bool Stale { get; private set; } = true;
    public void Success(UsageSnapshot value) { Snapshot = value; Stale = false; Connection = "Connected"; }
    public void Failure(string status = "Disconnected") { Stale = true; Connection = status; }
    public void Clear(string status) { Snapshot = null; Stale = false; Connection = status; }
    public bool IsStale(DateTimeOffset now) => Snapshot is not null && (Stale || now - Snapshot.FetchedAt > TimeSpan.FromMinutes(6));
    public void LoadCache(UsageSnapshot value) { Snapshot = value; Failure("Connecting"); }
}
public static class RetryPolicy
{
    public static TimeSpan Delay(int attempt, double jitter) => TimeSpan.FromSeconds(Math.Min(120, Math.Pow(2, Math.Clamp(attempt, 0, 7))) * (0.8 + Math.Clamp(jitter, 0, 1) * 0.4));
}
