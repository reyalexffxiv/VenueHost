using System;

namespace VenueHost.Models;

/// <summary>
/// One staff schedule row. Its HH:mm times are resolved inside the event
/// window, just like DJ rows, but multiple staff members may overlap.
/// </summary>
[Serializable]
public sealed class StaffScheduleEntry
{
    /// <summary>Optional link to the shared directory; legacy name-based rows remain supported.</summary>
    public string PersonId { get; set; } = string.Empty;

    public int Order { get; set; }

    public string Name { get; set; } = string.Empty;

    // Kept for beta config compatibility and possible future staff/contact use.
    public string Link { get; set; } = string.Empty;

    public string Role { get; set; } = "Photographer";

    public string StartTime { get; set; } = "18:00";

    public string EndTime { get; set; } = "19:00";

    /// <summary>
    /// Quick override for breaks or last-minute unavailability. Unchecked rows stay
    /// scheduled, but are skipped by staff role shouts.
    /// </summary>
    public bool Available { get; set; } = true;
}
