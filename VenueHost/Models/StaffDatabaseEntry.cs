using System;

namespace VenueHost.Models;

/// <summary>
/// Reusable venue staff entry. Roles are selected from the normalized role list
/// managed in Settings -> Staff Schedule.
/// </summary>
[Serializable]
public sealed class StaffDatabaseEntry
{
    [Newtonsoft.Json.JsonIgnore]
    public string RuntimeId { get; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    // Kept for beta config compatibility. The Staff Database UI no longer uses it.
    public string Link { get; set; } = string.Empty;

    public string Role { get; set; } = "Photographer";
}
