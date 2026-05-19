using System;

namespace VenueHost.Models;

/// <summary>
/// Per-role staff shout configuration. Each staff role can have its own
/// auto-shout toggle, interval, macro, and delay.
/// </summary>
[Serializable]
public sealed class StaffRoleShoutSetting
{
    public string Role { get; set; } = "Photographer";

    public bool AutoEnabled { get; set; }

    public int IntervalMinutes { get; set; } = 15;

    public string Macro { get; set; } = string.Empty;

    public float DelaySeconds { get; set; } = 2f;
}
