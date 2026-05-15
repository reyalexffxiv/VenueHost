using System;

namespace VenueHost.Models;

/// <summary>
/// Reusable DJ contact/stream entry, used to fill lineup rows quickly.
/// </summary>
[Serializable]
public sealed class DjDatabaseEntry
{
    /// <summary>
    /// Runtime-only stable ImGui identity for this row. This is intentionally not
    /// stored in SQLite; the database uniqueness key remains the DJ name. Stable
    /// IDs prevent edits from jumping to another row when the visible list is
    /// re-sorted after a name change.
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public string RuntimeId { get; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string Link { get; set; } = string.Empty;
}
