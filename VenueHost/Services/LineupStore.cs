using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using VenueHost.Models;

namespace VenueHost.Services;

/// <summary>
/// Extra durable storage for the active event lineup.
///
/// Dalamud already persists Configuration as JSON, but the lineup is important
/// during live hosting. Keeping a small sidecar snapshot in the plugin config
/// folder gives Venue Host a second recovery path after plugin updates or config
/// schema changes.
/// </summary>
public sealed class LineupStore
{
    private readonly string snapshotPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public LineupStore(string snapshotPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath) ?? ".");
        this.snapshotPath = snapshotPath;
    }

    public void Save(Configuration config)
    {
        var snapshot = LineupSnapshot.FromConfiguration(config);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        File.WriteAllText(this.snapshotPath, json);
    }

    public bool TryLoad(out LineupSnapshot snapshot)
    {
        snapshot = new LineupSnapshot();

        if (!File.Exists(this.snapshotPath))
            return false;

        try
        {
            var json = File.ReadAllText(this.snapshotPath);
            snapshot = JsonSerializer.Deserialize<LineupSnapshot>(json, JsonOptions) ?? new LineupSnapshot();
            snapshot.DjSchedule ??= [];
            return true;
        }
        catch
        {
            // A broken backup must never stop the plugin from loading.
            snapshot = new LineupSnapshot();
            return false;
        }
    }

    public sealed class LineupSnapshot
    {
        public List<DjScheduleEntry> DjSchedule { get; set; } = [];
        public int SelectedDjOrder { get; set; }
        public string VenueName { get; set; } = "Urban";
        public string EventName { get; set; } = string.Empty;
        public string EventStartDate { get; set; } = string.Empty;
        public string EventStartTime { get; set; } = string.Empty;
        public string EventEndDate { get; set; } = string.Empty;
        public string EventEndTime { get; set; } = string.Empty;
        public string GiveawayText { get; set; } = string.Empty;
        public string GiveawayCommand { get; set; } = string.Empty;
        public bool GiveawayEnabled { get; set; }
        public bool GiveawayCommandEnabled { get; set; }
        public DateTime SavedAtUtc { get; set; }

        public static LineupSnapshot FromConfiguration(Configuration config)
            => new()
            {
                DjSchedule = config.DjSchedule,
                SelectedDjOrder = config.SelectedDjOrder,
                VenueName = config.VenueName,
                EventName = config.EventName,
                EventStartDate = config.EventStartDate,
                EventStartTime = config.EventStartTime,
                EventEndDate = config.EventEndDate,
                EventEndTime = config.EventEndTime,
                GiveawayText = config.GiveawayText,
                GiveawayCommand = config.GiveawayCommand,
                GiveawayEnabled = config.GiveawayEnabled,
                GiveawayCommandEnabled = config.GiveawayCommandEnabled,
                SavedAtUtc = DateTime.UtcNow,
            };
    }
}
