using System;

namespace VenueHost.Models;

/// <summary>
/// One DJ slot in the venue schedule.
/// Times are stored as text because venue staff usually type server-time labels like 18:00.
/// </summary>
[Serializable]
public sealed class DjScheduleEntry
{
    public int Order { get; set; }

    public string DJName { get; set; } = string.Empty;

    public string DJLink { get; set; } = string.Empty;

    public string StartTime { get; set; } = string.Empty;

    public string EndTime { get; set; } = string.Empty;

    /// <summary>
    /// Controls whether this DJ slot includes giveaway lines when the global giveaway is enabled.
    /// </summary>
    public bool GiveawayEnabled { get; set; } = true;
}
