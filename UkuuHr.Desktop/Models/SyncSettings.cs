using System.Text.Json;
using System.Text.Json.Serialization;

namespace UkuuHr.Sync;

/// <summary>
/// Configuration for the Ukuu HR Access Sync Bridge.
/// Enhanced with date range selection for targeted data retrieval.
/// Kept in UkuuHr.Sync namespace for backward compatibility with existing tests.
/// </summary>
public class SyncSettings
{
    // ── Device Connection ────────────────────────────────────────────────────
    public string DeviceIp { get; set; } = "192.168.1.137";
    public int DevicePort { get; set; } = 80;
    public bool? UseHttps { get; set; } = false;
    public string DeviceUsername { get; set; } = "admin";
    public string DevicePassword { get; set; } = "";

    // ── Cloud API ────────────────────────────────────────────────────────────
    public string CloudUrl { get; set; } = "https://ukuuhr.com";
    public string? ApiKey { get; set; }

    // ── Sync Schedule ────────────────────────────────────────────────────────
    public int SyncIntervalMinutes { get; set; } = 5;
    public bool AutoSyncEnabled { get; set; } = true;

    // ── Date Range ───────────────────────────────────────────────────────────
    public DateRangePreset DateRangePreset { get; set; } = DateRangePreset.Last7Days;
    public string? CustomFromDate { get; set; }
    public string? CustomToDate { get; set; }

    // ── Computed Date Range ──────────────────────────────────────────────────

    /// <summary>
    /// Computes the actual from/to dates based on the selected preset,
    /// falling back to custom dates when preset is Custom.
    /// </summary>
    public (DateTime From, DateTime To) GetDateRange()
    {
        var now = DateTime.UtcNow;
        return DateRangePreset switch
        {
            DateRangePreset.Today => (now.Date, now),
            DateRangePreset.Yesterday => (now.Date.AddDays(-1), now.Date),
            DateRangePreset.Last7Days => (now.AddDays(-7), now),
            DateRangePreset.Last30Days => (now.AddDays(-30), now),
            DateRangePreset.ThisMonth => (new DateTime(now.Year, now.Month, 1), now),
            DateRangePreset.LastMonth => (new DateTime(now.Year, now.Month, 1).AddMonths(-1), new DateTime(now.Year, now.Month, 1)),
            DateRangePreset.Custom => (
                DateTime.TryParse(CustomFromDate, out var cf) ? cf : now.AddDays(-7),
                DateTime.TryParse(CustomToDate, out var ct) ? ct : now
            ),
            _ => (now.AddDays(-7), now)
        };
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public bool IsValid() =>
        !string.IsNullOrEmpty(DeviceIp) &&
        !string.IsNullOrEmpty(DeviceUsername) &&
        !string.IsNullOrEmpty(CloudUrl);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static SyncSettings? FromJson(string json)
    {
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<SyncSettings>(json, opts);
        }
        catch { return null; }
    }

    public static SyncSettings Load(string path)
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            var settings = FromJson(json);
            if (settings != null && settings.IsValid()) return settings;
        }
        return new SyncSettings();
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, ToJson());
        }
        catch { }
    }
}

/// <summary>
/// Predefined date range presets for quick selection.
/// The user can pick a preset or enter custom dates.
/// </summary>
public enum DateRangePreset
{
    Today = 0,
    Yesterday = 1,
    Last7Days = 2,
    Last30Days = 3,
    ThisMonth = 4,
    LastMonth = 5,
    Custom = 6
}
