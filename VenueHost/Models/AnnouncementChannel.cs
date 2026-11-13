namespace VenueHost.Models;

/// <summary>
/// Preferred public channel for a macro. Smart Channels alternates only on a repeated channel.
/// Keep numeric values stable because Dalamud serializes these in saved profiles.
/// </summary>
public enum AnnouncementChannel
{
    Yell = 0,
    Shout = 1,
}
