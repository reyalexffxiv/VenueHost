using System;

namespace VenueHost.Models;

/// <summary>
/// One reusable event/custom shout macro.
/// Event shouts are venue-wide announcements that can be sent manually or on a timer.
/// </summary>
[Serializable]
public sealed class EventShoutEntry
{
    public int Order { get; set; }

    public string Name { get; set; } = "New Shout";

    public string Macro { get; set; } = "/sh Welcome to {VenueName}!";

    /// <summary>
    /// Start time for this shout inside the global Event Shout window, in ST/UTC HH:mm.
    /// </summary>
    public string StartTime { get; set; } = "18:00";

    /// <summary>
    /// End time for this shout inside the global Event Shout window, in ST/UTC HH:mm.
    /// </summary>
    public string EndTime { get; set; } = "23:00";

    public float DelaySeconds { get; set; } = 2f;

    public bool AutoEnabled { get; set; }

    public int IntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Keeps a shout saved but temporarily disabled from manual/automatic use.
    /// </summary>
    public bool Active { get; set; } = true;
}
