using System;

namespace VenueHost.Models;

/// <summary>
/// Reusable DJ contact/stream entry, used to fill lineup rows quickly.
/// </summary>
[Serializable]
public sealed class DjDatabaseEntry
{
    public string Name { get; set; } = string.Empty;

    public string Link { get; set; } = string.Empty;
}
