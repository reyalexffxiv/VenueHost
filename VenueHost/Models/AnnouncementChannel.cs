namespace VenueHost.Models;

/// <summary>
/// Preferred public channel for a macro when Smart Channels is disabled.
/// Keep numeric values stable because Dalamud serializes these in saved profiles.
/// </summary>
public enum AnnouncementChannel
{
    Yell = 0,
    Shout = 1,
}
