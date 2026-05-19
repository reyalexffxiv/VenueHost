using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VenueHost.Models;
using VenueHost.Services;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace VenueHost;

/// <summary>
/// Persisted user settings for Venue Host.
/// Keep this intentionally simple so Dalamud can serialize it safely.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentConfigVersion = 17;

    public const string DefaultCurrentDjMacro =
        "/y ♪♪ Live DJ @ {VenueName}! Don’t miss the vibe ♪♪ → {CurrentDJLink}\n" +
        "{GiveawayLine}";

    public const string DefaultTransitionMacro =
        "/y Thank you for an amazing set, {CurrentDJName}! ♪♪ Don't forget to tune in to our next awesome live DJ, {NextDJName}! ♪♪ {NextDJLink}";

    public const string DefaultTellCurrentDjMacro =
        "/tell <t> Our current DJ is {CurrentDJName} - {CurrentDJLink}";

    public const string DefaultPhotographerShoutMacro =
        "/sh Are you looking for a Photographer?\n" +
        "/sh Our current photographers are: {StaffNames}\n" +
        "/sh Reach out to any of them with a Tell <3";

    public const string DefaultBarShoutMacro =
        "/sh Our Barkeepers are here to satisfy your thirst!\n" +
        "/sh Our dear {StaffNames} {IsAre} at our bar to serve you.\n" +
        "/sh Don't be shy, come say hello! <3";

    public const string DefaultCourtesanShoutMacro =
        "/sh Our Courtesans are here to indulge your every desire.\n" +
        "/sh Our dear {StaffNames} {IsAre} waiting to spoil and pamper you.\n" +
        "/sh Don't be shy, step closer and tell them your wishes! <3";

    public const string DefaultMaidShoutMacro =
        "/sh Our Maids are here to provide you with flawless care and comfort.\n" +
        "/sh Our dear {StaffNames} {IsAre} at your beck and call for the evening.\n" +
        "/sh Don't be shy, step inside and allow them to serve you! <3\n" +
        "/sh Let them brighten your day <3";

    public int Version { get; set; } = CurrentConfigVersion;

    public List<DjScheduleEntry> DjSchedule { get; set; } = [];

    public int SelectedDjOrder { get; set; } = 0;

    public List<DjDatabaseEntry> DjDatabase { get; set; } =
    [
        new DjDatabaseEntry { Name = "Gaia", Link = "https://www.twitch.tv/dj_gaia" },
        new DjDatabaseEntry { Name = "Khangomon", Link = "https://www.twitch.tv/khangomon" },
        new DjDatabaseEntry { Name = "Raindrop Kitty", Link = "https://www.twitch.tv/raindropkitty" },
        new DjDatabaseEntry { Name = "Rey Alex", Link = "https://www.twitch.tv/reyalexxx" },
    ];

    // Preserve the existing JSON field name so current users keep their saved staff schedule
    // rows while the C# model moves away from the old photographer-only naming.
    [Newtonsoft.Json.JsonProperty("Photographers")]
    public List<StaffScheduleEntry> StaffSchedule { get; set; } = [];

    [Newtonsoft.Json.JsonProperty("SelectedPhotographerOrder")]
    public int SelectedStaffScheduleOrder { get; set; } = 0;

    public List<StaffDatabaseEntry> StaffDatabase { get; set; } = [];

    public List<string> StaffRoles { get; set; } = ["Photographer", "Bar", "Courtesan", "Maid"];

    public List<StaffRoleShoutSetting> StaffRoleShoutSettings { get; set; } = [];

    // Legacy fields kept so old beta configs can still deserialize safely.
    public string CurrentDJName { get; set; } = string.Empty;
    public string CurrentDJLink { get; set; } = string.Empty;
    public string StartTime { get; set; } = string.Empty;
    public string EndTime { get; set; } = string.Empty;
    public string NextDJName { get; set; } = string.Empty;
    public string NextDJLink { get; set; } = string.Empty;

    public string VenueName { get; set; } = "Urban";
    public string EventName { get; set; } = string.Empty;
    public string EventStartDate { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string EventStartTime { get; set; } = "18:00";
    public string EventEndDate { get; set; } = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string EventEndTime { get; set; } = "02:00";

    public string StaffStartDate { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string StaffStartTime { get; set; } = "18:00";
    public string StaffEndDate { get; set; } = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string StaffEndTime { get; set; } = "02:00";

    public string GiveawayText { get; set; } = "2M Gil Giveaway";
    public string GiveawayCommand { get; set; } = "!urban";

    public bool GiveawayEnabled { get; set; } = true;
    public bool GiveawayCommandEnabled { get; set; } = true;

    public string CurrentDjMacro { get; set; } = DefaultCurrentDjMacro;
    public float CurrentDjMacroDelaySeconds { get; set; } = 2f;

    public string TransitionMacro { get; set; } = DefaultTransitionMacro;
    public float TransitionMacroDelaySeconds { get; set; } = 2f;

    public string TellCurrentDjMacro { get; set; } = DefaultTellCurrentDjMacro;
    public float TellCurrentDjMacroDelaySeconds { get; set; } = 2f;

    public bool AutoCurrentDjShoutEnabled { get; set; } = true;
    public int AutoCurrentDjShoutIntervalMinutes { get; set; } = 10;

    public string SelectedStaffRole { get; set; } = "Photographer";
    public string PhotographerShoutMacro { get; set; } = DefaultPhotographerShoutMacro;
    public string BarShoutMacro { get; set; } = DefaultBarShoutMacro;
    public string CourtesanShoutMacro { get; set; } = DefaultCourtesanShoutMacro;
    public string MaidShoutMacro { get; set; } = DefaultMaidShoutMacro;
    public float PhotographerShoutMacroDelaySeconds { get; set; } = 2f;
    public bool AutoPhotographerShoutEnabled { get; set; }
    public int AutoPhotographerShoutIntervalMinutes { get; set; } = 15;

    public bool AutoStaffRoleShoutStaggerEnabled { get; set; } = true;
    public int AutoStaffRoleShoutStaggerMinutes { get; set; } = 3;

    public bool CleanLegacyManualWaitLinesOnceDone { get; set; }

    /// <summary>
    /// Enables a stronger contrast palette across Venue Host windows.
    /// This keeps the setting user-facing and neutral as "Contrast Mode".
    /// </summary>
    public bool ContrastModeEnabled { get; set; }

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    [NonSerialized]
    private DjDatabaseStore? djDatabaseStore;

    [NonSerialized]
    private LineupStore? lineupStore;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        this.MigrateConfig();
        this.InitializeDjDatabaseStore(pluginInterface);
        this.InitializeLineupStore(pluginInterface);
        this.EnsureScheduleDefaults();
        this.EnsureStaffRoles();
        this.EnsureStaffScheduleDefaults();
        this.EnsureEventWindowDefaults();
        this.EnsureStaffWindowDefaults();
        this.NormalizeDjDatabase(saveToStore: false);
        this.EnsureStaffRoleShoutSettings();
        this.ClampAutoShoutSettings();
    }

    private void MigrateConfig()
    {
        this.DjSchedule ??= [];
        this.DjDatabase ??= [];
        this.StaffSchedule ??= [];
        this.StaffDatabase ??= [];
        this.StaffRoles ??= ["Photographer", "Bar", "Courtesan", "Maid"];
        this.StaffRoleShoutSettings ??= [];

        // Version 6 added the reusable DJ database and target tell macro.
        if (this.Version < 6 && this.DjDatabase.Count == 0)
        {
            this.DjDatabase.Add(new DjDatabaseEntry { Name = "Rey Alex", Link = "https://www.twitch.tv/reyalexxx" });
            this.DjDatabase.Add(new DjDatabaseEntry { Name = "Khangomon", Link = "https://www.twitch.tv/khangomon" });
        }

        // Version 8 keeps the DJ Database unique by DJ name. This cleans up
        // duplicated beta data that may have been created while testing.
        if (this.Version < 8)
            this.NormalizeDjDatabase(saveToStore: false);

        // Version 9 adds softer setup defaults for new schedule rows.
        // Existing schedules are left as-is so beta users do not lose data.

        // Version 10 cleans up first-beta defaults. Existing user schedules are left as-is.

        // Version 11 enables auto-shout by default for beta testers.
        if (this.Version < 11)
            this.AutoCurrentDjShoutEnabled = true;

        // Version 12 adds an explicit ST/UTC event window so tomorrow and overnight events
        // do not depend on guessing from HH:mm-only DJ rows.
        if (this.Version < 12)
            this.EnsureEventWindowDefaults();

        // Version 13 adds a sidecar lineup snapshot so a live event schedule has
        // an extra recovery path across plugin updates.
        // Version 14 adds Contrast Mode. Existing users keep the default off state.
        // Version 15 adds per-role Staff Schedule auto shout settings.
        // Version 16 adds stagger/queue spacing between automatic role shouts.

        // Version 3 added per-DJ giveaway toggles. Existing beta configs should keep
        // giveaway enabled for all rows, otherwise old schedules would silently lose giveaway lines.
        if (this.Version < 3)
        {
            foreach (var entry in this.DjSchedule)
                entry.GiveawayEnabled = true;

            this.Version = CurrentConfigVersion;
            this.Save();
        }

        if (this.Version < CurrentConfigVersion)
        {
            this.Version = CurrentConfigVersion;
            this.Save();
        }
    }


    public void EnsureStaffRoles()
    {
        this.StaffRoles ??= [];

        if (this.StaffRoles.Count == 0)
        {
            this.StaffRoles.Add("Photographer");
            this.StaffRoles.Add("Bar");
            this.StaffRoles.Add("Courtesan");
            this.StaffRoles.Add("Maid");
        }

        // Pull existing beta roles into the central role list once, then keep
        // Staff Database and Staff Schedule using this normalized list.
        foreach (var role in (this.StaffDatabase ?? []).Select(entry => entry.Role)
                     .Concat((this.StaffSchedule ?? []).Select(entry => entry.Role))
                     .Concat((this.StaffRoleShoutSettings ?? []).Select(entry => entry.Role)))
        {
            this.AddStaffRole(role, save: false);
        }

        this.StaffRoles = this.StaffRoles
            .Select(NormalizeStaffRoleName)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!this.StaffRoles.Any(role => role.Equals("Photographer", StringComparison.OrdinalIgnoreCase)))
            this.StaffRoles.Insert(0, "Photographer");

        foreach (var entry in this.StaffDatabase ?? [])
            entry.Role = this.GetExistingOrDefaultStaffRole(entry.Role);

        foreach (var entry in this.StaffSchedule ?? [])
            entry.Role = this.GetExistingOrDefaultStaffRole(entry.Role);
    }

    public bool AddStaffRole(string? role, bool save = true)
    {
        this.StaffRoles ??= [];
        role = NormalizeStaffRoleName(role);
        if (string.IsNullOrWhiteSpace(role))
            return false;

        if (this.StaffRoles.Any(existing => existing.Equals(role, StringComparison.OrdinalIgnoreCase)))
            return false;

        this.StaffRoles.Add(role);
        this.StaffRoles = this.StaffRoles.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        this.GetOrCreateStaffRoleShoutSetting(role);
        if (save)
            this.Save();
        return true;
    }

    public bool RenameStaffRole(string oldRole, string newRole)
    {
        oldRole = NormalizeStaffRoleName(oldRole);
        newRole = NormalizeStaffRoleName(newRole);
        if (string.IsNullOrWhiteSpace(oldRole) || string.IsNullOrWhiteSpace(newRole))
            return false;

        if (!this.StaffRoles.Any(role => role.Equals(oldRole, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!oldRole.Equals(newRole, StringComparison.OrdinalIgnoreCase) &&
            this.StaffRoles.Any(role => role.Equals(newRole, StringComparison.OrdinalIgnoreCase)))
            return false;

        for (var i = 0; i < this.StaffRoles.Count; i++)
        {
            if (this.StaffRoles[i].Equals(oldRole, StringComparison.OrdinalIgnoreCase))
                this.StaffRoles[i] = newRole;
        }

        foreach (var entry in this.StaffDatabase ?? [])
        {
            if (entry.Role.Equals(oldRole, StringComparison.OrdinalIgnoreCase))
                entry.Role = newRole;
        }

        foreach (var entry in this.StaffSchedule ?? [])
        {
            if (entry.Role.Equals(oldRole, StringComparison.OrdinalIgnoreCase))
                entry.Role = newRole;
        }

        foreach (var setting in this.StaffRoleShoutSettings ?? [])
        {
            if (setting.Role.Equals(oldRole, StringComparison.OrdinalIgnoreCase))
                setting.Role = newRole;
        }

        if (this.SelectedStaffRole.Equals(oldRole, StringComparison.OrdinalIgnoreCase))
            this.SelectedStaffRole = newRole;

        this.EnsureStaffRoles();
        this.EnsureStaffRoleShoutSettings();
        this.Save();
        return true;
    }

    public bool RemoveStaffRole(string role)
    {
        role = NormalizeStaffRoleName(role);
        if (string.IsNullOrWhiteSpace(role) || this.StaffRoles.Count <= 1)
            return false;

        var removed = this.StaffRoles.RemoveAll(existing => existing.Equals(role, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
            return false;

        var fallback = this.StaffRoles.FirstOrDefault() ?? "Photographer";
        foreach (var entry in this.StaffDatabase ?? [])
        {
            if (entry.Role.Equals(role, StringComparison.OrdinalIgnoreCase))
                entry.Role = fallback;
        }

        foreach (var entry in this.StaffSchedule ?? [])
        {
            if (entry.Role.Equals(role, StringComparison.OrdinalIgnoreCase))
                entry.Role = fallback;
        }

        this.StaffRoleShoutSettings?.RemoveAll(setting => setting.Role.Equals(role, StringComparison.OrdinalIgnoreCase));
        if (this.SelectedStaffRole.Equals(role, StringComparison.OrdinalIgnoreCase))
            this.SelectedStaffRole = fallback;

        this.EnsureStaffRoles();
        this.EnsureStaffRoleShoutSettings();
        this.Save();
        return true;
    }

    public string GetExistingOrDefaultStaffRole(string? role)
    {
        this.StaffRoles ??= [];
        var normalized = NormalizeStaffRoleName(role);
        return this.StaffRoles.FirstOrDefault(existing => existing.Equals(normalized, StringComparison.OrdinalIgnoreCase))
               ?? this.StaffRoles.FirstOrDefault()
               ?? "Photographer";
    }

    private static string NormalizeStaffRoleName(string? role)
    {
        return Regex.Replace((role ?? string.Empty).Trim(), @"\s+", " ");
    }


    public void EnsureStaffRoleShoutSettings()
    {
        this.StaffRoleShoutSettings ??= [];
        this.EnsureStaffRoles();

        foreach (var role in this.GetKnownStaffRoles())
            this.GetOrCreateStaffRoleShoutSetting(role);

        foreach (var setting in this.StaffRoleShoutSettings)
        {
            setting.Role = string.IsNullOrWhiteSpace(setting.Role) ? "Photographer" : setting.Role.Trim();
            setting.IntervalMinutes = Math.Clamp(setting.IntervalMinutes, 1, 120);
            setting.DelaySeconds = Math.Clamp(setting.DelaySeconds, 0f, 10f);
            if (string.IsNullOrWhiteSpace(setting.Macro))
                setting.Macro = this.GetDefaultStaffShoutMacro(setting.Role);
        }
    }

    public void EnsureScheduleDefaults()
    {
        this.DjSchedule ??= [];
        this.DjDatabase ??= [];

        if (this.DjSchedule.Count == 0)
        {
            this.SelectedDjOrder = 0;
            return;
        }

        this.NormalizeScheduleOrders();

        if (this.SelectedDjOrder <= 0 || this.DjSchedule.All(entry => entry.Order != this.SelectedDjOrder))
            this.SelectedDjOrder = this.DjSchedule[0].Order;
    }


    /// <summary>
    /// Ensures the event window has valid-looking defaults. Staff still edit
    /// individual DJ rows as HH:mm, but the event window gives those times a
    /// real UTC date boundary for auto-shout calculations.
    /// </summary>
    public void EnsureEventWindowDefaults()
    {
        var now = DateTime.UtcNow;
        if (!TryParseServerDate(this.EventStartDate, out _))
            this.EventStartDate = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (!TryParseServerTime(this.EventStartTime, out _))
            this.EventStartTime = "18:00";

        if (!TryParseServerDate(this.EventEndDate, out _))
            this.EventEndDate = now.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (!TryParseServerTime(this.EventEndTime, out _))
            this.EventEndTime = "02:00";
    }

    public bool TryGetEventWindow(out DateTime startUtc, out DateTime endUtc)
    {
        this.EnsureEventWindowDefaults();
        startUtc = DateTime.MinValue;
        endUtc = DateTime.MinValue;

        if (!TryParseServerDate(this.EventStartDate, out var startDate) ||
            !TryParseServerDate(this.EventEndDate, out var endDate) ||
            !TryParseServerTime(this.EventStartTime, out var startTime) ||
            !TryParseServerTime(this.EventEndTime, out var endTime))
        {
            return false;
        }

        startUtc = startDate.Date.Add(startTime);
        endUtc = endDate.Date.Add(endTime);
        return endUtc > startUtc;
    }

    private static string NormalizeDateText(string? value, DateTime fallback)
    {
        return TryParseServerDate(value, out var parsed)
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : fallback.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string NormalizeTimeText(string? value, string fallback)
    {
        return TryParseServerTime(value, out var parsed)
            ? parsed.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
            : fallback;
    }

    public void EnsureStaffWindowDefaults()
    {
        this.StaffStartDate = NormalizeDateText(this.StaffStartDate, DateTime.UtcNow);
        this.StaffEndDate = NormalizeDateText(this.StaffEndDate, DateTime.UtcNow.AddDays(1));
        this.StaffStartTime = NormalizeTimeText(this.StaffStartTime, string.IsNullOrWhiteSpace(this.EventStartTime) ? "18:00" : this.EventStartTime);
        this.StaffEndTime = NormalizeTimeText(this.StaffEndTime, string.IsNullOrWhiteSpace(this.EventEndTime) ? "02:00" : this.EventEndTime);
    }

    public bool TryGetStaffWindow(out DateTime startUtc, out DateTime endUtc)
    {
        this.EnsureStaffWindowDefaults();
        startUtc = default;
        endUtc = default;

        if (!DateTime.TryParseExact($"{this.StaffStartDate} {this.StaffStartTime}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out startUtc))
            return false;

        if (!DateTime.TryParseExact($"{this.StaffEndDate} {this.StaffEndTime}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out endUtc))
            return false;

        return endUtc > startUtc;
    }

    public static bool TryParseServerDate(string? value, out DateTime date)
    {
        value = (value ?? string.Empty).Trim();
        return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    public static string FormatServerDate(DateTime value)
    {
        return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Converts the row-based HH:mm lineup into an ordered date-aware timeline.
    /// Rows remain simple for staff, while auto-shout gets real UTC DateTimes.
    /// A row that falls after the configured event end is kept for validation
    /// but marked outside the event window so it will not auto-shout.
    /// </summary>
    public List<EventTimelineSlot> BuildEventTimeline()
    {
        var result = new List<EventTimelineSlot>();
        if (!this.TryGetEventWindow(out var eventStartUtc, out var eventEndUtc))
            return result;

        DateTime? previousStartUtc = null;

        foreach (var entry in this.DjSchedule.OrderBy(entry => entry.Order))
        {
            if (!HasUsableDj(entry) ||
                !TryParseServerTime(entry.StartTime, out var startTime) ||
                !TryParseServerTime(entry.EndTime, out var endTime) ||
                startTime == endTime)
            {
                continue;
            }

            var slotStartUtc = eventStartUtc.Date.Add(startTime);
            while (slotStartUtc < eventStartUtc)
                slotStartUtc = slotStartUtc.AddDays(1);

            if (previousStartUtc.HasValue)
            {
                while (slotStartUtc < previousStartUtc.Value)
                    slotStartUtc = slotStartUtc.AddDays(1);
            }

            var slotEndUtc = slotStartUtc.Date.Add(endTime);
            if (slotEndUtc <= slotStartUtc)
                slotEndUtc = slotEndUtc.AddDays(1);

            var inside = slotStartUtc >= eventStartUtc && slotEndUtc <= eventEndUtc;
            result.Add(new EventTimelineSlot(entry, slotStartUtc, slotEndUtc, inside));
            previousStartUtc = slotStartUtc;
        }

        return result;
    }

    public EventTimelineSlot? GetTimelineSlotForEntry(DjScheduleEntry? entry)
    {
        if (entry is null)
            return null;

        return this.BuildEventTimeline().FirstOrDefault(slot => ReferenceEquals(slot.Entry, entry) || slot.Entry.Order == entry.Order);
    }

    public EventTimelineSlot? GetTimelineSlotAt(DateTime utcNow)
    {
        return this.BuildEventTimeline().FirstOrDefault(slot =>
            slot.IsInsideEventWindow && utcNow >= slot.StartUtc && utcNow < slot.EndUtc);
    }

    public sealed class EventTimelineSlot
    {
        public EventTimelineSlot(DjScheduleEntry entry, DateTime startUtc, DateTime endUtc, bool isInsideEventWindow)
        {
            this.Entry = entry;
            this.StartUtc = startUtc;
            this.EndUtc = endUtc;
            this.IsInsideEventWindow = isInsideEventWindow;
        }

        public DjScheduleEntry Entry { get; }
        public DateTime StartUtc { get; }
        public DateTime EndUtc { get; }
        public bool IsInsideEventWindow { get; }
    }


    /// <summary>
    /// Keeps the reusable DJ database tidy. Names are treated as unique
    /// case-insensitively, because the lineup picker should only show one
    /// entry per DJ. When duplicates exist, the first non-empty link wins.
    /// </summary>
    public void NormalizeDjDatabase(bool saveToStore = true)
    {
        this.DjDatabase ??= [];

        var unique = new List<DjDatabaseEntry>();
        var seenNames = new Dictionary<string, DjDatabaseEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in this.DjDatabase)
        {
            if (entry is null)
                continue;

            entry.Name = (entry.Name ?? string.Empty).Trim();
            entry.Link = (entry.Link ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(entry.Name) && string.IsNullOrWhiteSpace(entry.Link))
                continue;

            var key = NormalizeDjDatabaseName(entry.Name);
            if (string.IsNullOrWhiteSpace(key))
            {
                unique.Add(entry);
                continue;
            }

            if (seenNames.TryGetValue(key, out var existing))
            {
                if (string.IsNullOrWhiteSpace(existing.Link) && !string.IsNullOrWhiteSpace(entry.Link))
                    existing.Link = entry.Link;

                continue;
            }

            seenNames[key] = entry;
            unique.Add(entry);
        }

        this.DjDatabase = unique
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (saveToStore)
            this.djDatabaseStore?.ReplaceAll(this.DjDatabase);
    }

    public string GetUniqueDjDatabaseName(string desiredName, DjDatabaseEntry? currentEntry = null)
    {
        desiredName = (desiredName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(desiredName))
            desiredName = "New DJ";

        var baseName = desiredName;
        var suffix = 2;
        while (this.DjDatabase.Any(entry => !ReferenceEquals(entry, currentEntry) &&
                                           string.Equals(NormalizeDjDatabaseName(entry.Name), NormalizeDjDatabaseName(desiredName), StringComparison.OrdinalIgnoreCase)))
        {
            desiredName = $"{baseName} {suffix}";
            suffix++;
        }

        return desiredName;
    }

    private static string NormalizeDjDatabaseName(string? value)
    {
        return Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
    }

    public void NormalizeScheduleOrders()
    {
        // Keep the current list order as the visual/manual order and only renumber it.
        // Sorting is now an explicit UI action so Move Up/Move Down behave predictably.
        for (var i = 0; i < this.DjSchedule.Count; i++)
            this.DjSchedule[i].Order = i + 1;
    }

    public DjScheduleEntry? GetSelectedDj()
    {
        this.EnsureScheduleDefaults();
        return this.DjSchedule.FirstOrDefault(entry => entry.Order == this.SelectedDjOrder) ?? this.DjSchedule.FirstOrDefault();
    }

    public DjScheduleEntry? GetNextDj()
    {
        return this.GetNextDjAfter(this.GetSelectedDj());
    }

    public DjScheduleEntry? GetNextDjAfter(DjScheduleEntry? current)
    {
        this.EnsureScheduleDefaults();
        if (current is null)
            return null;

        return this.DjSchedule.FirstOrDefault(entry => entry.Order > current.Order && HasUsableDj(entry));
    }

    public DjScheduleEntry? GetPreviousDjBefore(DjScheduleEntry? current)
    {
        this.EnsureScheduleDefaults();
        if (current is null)
            return null;

        return this.DjSchedule
            .Where(entry => entry.Order < current.Order && HasUsableDj(entry))
            .OrderByDescending(entry => entry.Order)
            .FirstOrDefault();
    }

    public DjScheduleEntry? GetCurrentServerTimeDj(DateTime utcNow)
    {
        this.EnsureScheduleDefaults();
        return this.GetTimelineSlotAt(utcNow)?.Entry;
    }


    /// <summary>
    /// A usable DJ row has a real name. Placeholder rows such as "Pick DJ..."
    /// are ignored by auto-shout, transitions, and validation.
    /// </summary>
    public static bool HasUsableDj(DjScheduleEntry? entry)
    {
        return entry is not null && !string.IsNullOrWhiteSpace(entry.DJName);
    }

    public static bool TryParseServerTime(string? value, out TimeSpan time)
    {
        value = (value ?? string.Empty).Trim();
        return TimeSpan.TryParseExact(value, @"h\:mm", null, out time) ||
               TimeSpan.TryParseExact(value, @"hh\:mm", null, out time);
    }

    private static bool IsWithinScheduleWindow(TimeSpan now, TimeSpan start, TimeSpan end)
    {
        if (start == end)
            return false;

        return end > start
            ? now >= start && now < end
            : now >= start || now < end;
    }


    public void EnsureStaffScheduleDefaults()
    {
        this.StaffSchedule ??= [];
        this.StaffDatabase ??= [];
        this.StaffRoles ??= ["Photographer", "Bar", "Courtesan", "Maid"];
        this.StaffRoleShoutSettings ??= [];
        this.NormalizeStaffScheduleOrders();
        foreach (var entry in this.StaffSchedule)
        {
            entry.Role = this.GetExistingOrDefaultStaffRole(entry.Role);
            if (!TryParseServerTime(entry.StartTime, out _))
                entry.StartTime = "18:00";
            if (!TryParseServerTime(entry.EndTime, out _))
                entry.EndTime = "19:00";
        }

        if (this.StaffSchedule.Count == 0)
        {
            this.SelectedStaffScheduleOrder = 0;
            return;
        }

        if (this.SelectedStaffScheduleOrder <= 0 || this.StaffSchedule.All(entry => entry.Order != this.SelectedStaffScheduleOrder))
            this.SelectedStaffScheduleOrder = this.StaffSchedule[0].Order;
    }

    public void NormalizeStaffScheduleOrders()
    {
        for (var i = 0; i < this.StaffSchedule.Count; i++)
            this.StaffSchedule[i].Order = i + 1;
    }

    public StaffScheduleEntry? GetSelectedStaffScheduleEntry()
    {
        this.EnsureStaffScheduleDefaults();
        return this.StaffSchedule.FirstOrDefault(entry => entry.Order == this.SelectedStaffScheduleOrder) ?? this.StaffSchedule.FirstOrDefault();
    }

    public static bool HasUsableStaffScheduleEntry(StaffScheduleEntry? entry)
    {
        return entry is not null && !string.IsNullOrWhiteSpace(entry.Name);
    }

    public List<StaffScheduleTimelineSlot> BuildStaffScheduleTimeline()
    {
        var result = new List<StaffScheduleTimelineSlot>();
        if (!this.TryGetStaffWindow(out var eventStartUtc, out var eventEndUtc))
            return result;

        DateTime? previousStartUtc = null;

        foreach (var entry in this.StaffSchedule.OrderBy(entry => entry.Order))
        {
            if (!HasUsableStaffScheduleEntry(entry) ||
                !TryParseServerTime(entry.StartTime, out var startTime) ||
                !TryParseServerTime(entry.EndTime, out var endTime) ||
                startTime == endTime)
            {
                continue;
            }

            var slotStartUtc = eventStartUtc.Date.Add(startTime);
            while (slotStartUtc < eventStartUtc)
                slotStartUtc = slotStartUtc.AddDays(1);

            if (previousStartUtc.HasValue)
            {
                while (slotStartUtc < previousStartUtc.Value)
                    slotStartUtc = slotStartUtc.AddDays(1);
            }

            var slotEndUtc = slotStartUtc.Date.Add(endTime);
            if (slotEndUtc <= slotStartUtc)
                slotEndUtc = slotEndUtc.AddDays(1);

            var inside = slotStartUtc >= eventStartUtc && slotEndUtc <= eventEndUtc;
            result.Add(new StaffScheduleTimelineSlot(entry, slotStartUtc, slotEndUtc, inside));
            previousStartUtc = slotStartUtc;
        }

        return result;
    }

    public List<StaffScheduleEntry> GetCurrentServerTimeStaff(DateTime utcNow)
    {
        return this.GetCurrentServerTimeStaffForRole(utcNow, null);
    }

    public List<StaffScheduleEntry> GetCurrentServerTimeStaffForRole(DateTime utcNow, string? role)
    {
        this.EnsureStaffScheduleDefaults();
        return this.BuildStaffScheduleTimeline()
            .Where(slot => slot.Entry.Available && slot.IsInsideEventWindow && utcNow >= slot.StartUtc && utcNow < slot.EndUtc)
            .Where(slot => string.IsNullOrWhiteSpace(role) || string.Equals(slot.Entry.Role?.Trim(), role.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(slot => slot.Entry)
            .OrderBy(entry => entry.Order)
            .ToList();
    }

    public StaffScheduleTimelineSlot? GetNextStaffScheduleTimelineSlot(DateTime utcNow)
    {
        return this.GetNextStaffScheduleTimelineSlotForRole(utcNow, null);
    }

    public StaffScheduleTimelineSlot? GetNextStaffScheduleTimelineSlotForRole(DateTime utcNow, string? role)
    {
        return this.BuildStaffScheduleTimeline()
            .Where(slot => slot.Entry.Available && slot.IsInsideEventWindow && slot.EndUtc > utcNow)
            .Where(slot => string.IsNullOrWhiteSpace(role) || string.Equals(slot.Entry.Role?.Trim(), role.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(slot => slot.StartUtc < utcNow ? utcNow : slot.StartUtc)
            .ThenBy(slot => slot.Order)
            .FirstOrDefault();
    }

    public Dictionary<string, string> BuildSelectedStaffRoleVariables(DateTime? utcNow = null)
    {
        return this.BuildStaffRoleVariables(this.SelectedStaffRole, utcNow);
    }

    public Dictionary<string, string> BuildStaffRoleVariables(string? role, DateTime? utcNow = null)
    {
        role = string.IsNullOrWhiteSpace(role) ? "Photographer" : role.Trim();
        var current = this.GetCurrentServerTimeStaffForRole(utcNow ?? DateTime.UtcNow, role);
        var namesList = current
            .Select(entry => entry.Name?.Trim() ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var names = string.Join(", ", namesList);
        var fallback = $"no active {role.ToLowerInvariant()} staff right now";
        var oneOrMany = namesList.Count == 1;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["VenueName"] = this.VenueName,
            ["EventName"] = this.EventName,
            ["Role"] = role,
            ["StaffNames"] = string.IsNullOrWhiteSpace(names) ? fallback : names,
            ["StaffCount"] = namesList.Count.ToString(CultureInfo.InvariantCulture),
            ["IsAre"] = oneOrMany ? "is" : "are",
            ["AreIs"] = oneOrMany ? "is" : "are",
            ["StaffSchedule"] = string.IsNullOrWhiteSpace(names) ? fallback : names,
            ["Photographers"] = string.IsNullOrWhiteSpace(names) ? fallback : names,
            ["PhotographerNames"] = string.IsNullOrWhiteSpace(names) ? fallback : names,
        };
    }

    public IReadOnlyList<string> GetKnownStaffRoles()
    {
        this.EnsureStaffRoles();
        return this.StaffRoles
            .Select(role => role.Trim())
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public StaffRoleShoutSetting GetOrCreateStaffRoleShoutSetting(string? role)
    {
        role = string.IsNullOrWhiteSpace(role) ? "Photographer" : role.Trim();
        this.StaffRoleShoutSettings ??= [];

        var existing = this.StaffRoleShoutSettings.FirstOrDefault(setting =>
            string.Equals(setting.Role?.Trim(), role, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var created = new StaffRoleShoutSetting
        {
            Role = role,
            AutoEnabled = role.Equals("Photographer", StringComparison.OrdinalIgnoreCase) ? this.AutoPhotographerShoutEnabled : false,
            IntervalMinutes = Math.Clamp(role.Equals("Photographer", StringComparison.OrdinalIgnoreCase) ? this.AutoPhotographerShoutIntervalMinutes : 15, 1, 120),
            Macro = this.GetStoredOrDefaultStaffShoutMacro(role),
            DelaySeconds = Math.Clamp(this.PhotographerShoutMacroDelaySeconds, 0f, 10f),
        };
        this.StaffRoleShoutSettings.Add(created);
        return created;
    }

    public IEnumerable<StaffRoleShoutSetting> GetAutoEnabledStaffRoleShoutSettings()
    {
        this.EnsureStaffRoleShoutSettings();
        var knownRoles = this.GetKnownStaffRoles().ToList();
        var orderByRole = knownRoles
            .Select((role, index) => new { role, index })
            .ToDictionary(item => item.role, item => item.index, StringComparer.OrdinalIgnoreCase);

        return this.StaffRoleShoutSettings
            .Where(setting => setting.AutoEnabled && orderByRole.ContainsKey(setting.Role))
            .OrderBy(setting => orderByRole[setting.Role])
            .ThenBy(setting => setting.Role, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string GetStoredOrDefaultStaffShoutMacro(string? role)
    {
        role = string.IsNullOrWhiteSpace(role) ? "Photographer" : role.Trim();
        return role.Equals("Bar", StringComparison.OrdinalIgnoreCase) ? this.BarShoutMacro :
               role.Equals("Courtesan", StringComparison.OrdinalIgnoreCase) ? this.CourtesanShoutMacro :
               role.Equals("Maid", StringComparison.OrdinalIgnoreCase) ? this.MaidShoutMacro :
               role.Equals("Photographer", StringComparison.OrdinalIgnoreCase) ? this.PhotographerShoutMacro :
               this.GetDefaultStaffShoutMacro(role);
    }

    public string GetDefaultStaffShoutMacro(string? role)
    {
        role = string.IsNullOrWhiteSpace(role) ? "Photographer" : role.Trim();
        return role.Equals("Bar", StringComparison.OrdinalIgnoreCase) ? DefaultBarShoutMacro :
               role.Equals("Courtesan", StringComparison.OrdinalIgnoreCase) ? DefaultCourtesanShoutMacro :
               role.Equals("Maid", StringComparison.OrdinalIgnoreCase) ? DefaultMaidShoutMacro :
               role.Equals("Photographer", StringComparison.OrdinalIgnoreCase) ? DefaultPhotographerShoutMacro :
               $"/sh Our current {role} staff: {{StaffNames}}";
    }

    public string GetStaffShoutMacro(string? role)
    {
        return this.GetOrCreateStaffRoleShoutSetting(role).Macro;
    }

    public void SetStaffShoutMacro(string? role, string value)
    {
        role = string.IsNullOrWhiteSpace(role) ? "Photographer" : role.Trim();
        var setting = this.GetOrCreateStaffRoleShoutSetting(role);
        setting.Macro = value;

        // Keep legacy fields updated so beta configs remain easy to inspect.
        if (role.Equals("Bar", StringComparison.OrdinalIgnoreCase))
            this.BarShoutMacro = value;
        else if (role.Equals("Courtesan", StringComparison.OrdinalIgnoreCase))
            this.CourtesanShoutMacro = value;
        else if (role.Equals("Maid", StringComparison.OrdinalIgnoreCase))
            this.MaidShoutMacro = value;
        else if (role.Equals("Photographer", StringComparison.OrdinalIgnoreCase))
            this.PhotographerShoutMacro = value;
    }

    public float GetStaffShoutDelaySeconds(string? role)
    {
        return this.GetOrCreateStaffRoleShoutSetting(role).DelaySeconds;
    }

    public void SetStaffShoutDelaySeconds(string? role, float value)
    {
        this.GetOrCreateStaffRoleShoutSetting(role).DelaySeconds = Math.Clamp(value, 0f, 10f);
    }

    public bool IsAnyStaffAutoShoutEnabled()
    {
        this.EnsureStaffRoleShoutSettings();
        return this.StaffRoleShoutSettings.Any(setting => setting.AutoEnabled);
    }

    public sealed class StaffScheduleTimelineSlot
    {
        public StaffScheduleTimelineSlot(StaffScheduleEntry entry, DateTime startUtc, DateTime endUtc, bool isInsideEventWindow)
        {
            this.Entry = entry;
            this.StartUtc = startUtc;
            this.EndUtc = endUtc;
            this.IsInsideEventWindow = isInsideEventWindow;
        }

        public StaffScheduleEntry Entry { get; }
        public DateTime StartUtc { get; }
        public DateTime EndUtc { get; }
        public bool IsInsideEventWindow { get; }
        public int Order => this.Entry.Order;
    }

    public string GetUniqueStaffName(string desiredName, StaffDatabaseEntry? currentEntry = null)
    {
        desiredName = (desiredName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(desiredName))
            desiredName = "New Staff";

        var baseName = desiredName;
        var suffix = 2;
        while (this.StaffDatabase.Any(entry => !ReferenceEquals(entry, currentEntry) &&
                                               string.Equals(NormalizeDjDatabaseName(entry.Name), NormalizeDjDatabaseName(desiredName), StringComparison.OrdinalIgnoreCase)))
        {
            desiredName = $"{baseName} {suffix}";
            suffix++;
        }

        return desiredName;
    }

    /// <summary>
    /// Removes manual wait-only lines once during config migration, because Venue Host exposes wait boxes in the UI.
    /// Users can still add &lt;wait.N&gt; manually later if they really want a one-off override.
    /// </summary>
    public bool CleanLegacyManualWaitLinesOnce()
    {
        if (this.CleanLegacyManualWaitLinesOnceDone)
            return false;

        var changed = this.RemoveManualWaitLinesFromMacros();
        this.CleanLegacyManualWaitLinesOnceDone = true;
        return changed;
    }

    /// <summary>
    /// Removes standalone &lt;wait.N&gt; lines from all editable macros. This is exposed
    /// as a user action so pasted legacy macros can be cleaned without touching the
    /// actual chat command text.
    /// </summary>
    public bool RemoveManualWaitLinesFromMacros()
    {
        var changed = false;

        var current = this.CurrentDjMacro;
        if (RemoveManualWaitLines(ref current))
        {
            this.CurrentDjMacro = current;
            changed = true;
        }

        var transition = this.TransitionMacro;
        if (RemoveManualWaitLines(ref transition))
        {
            this.TransitionMacro = transition;
            changed = true;
        }

        var tell = this.TellCurrentDjMacro;
        if (RemoveManualWaitLines(ref tell))
        {
            this.TellCurrentDjMacro = tell;
            changed = true;
        }

        var photographerMacro = this.PhotographerShoutMacro;
        if (RemoveManualWaitLines(ref photographerMacro))
        {
            this.PhotographerShoutMacro = photographerMacro;
            changed = true;
        }

        return changed;
    }

    public void ResetDefaultMacros()
    {
        this.CurrentDjMacro = DefaultCurrentDjMacro;
        this.CurrentDjMacroDelaySeconds = 2f;
        this.TransitionMacro = DefaultTransitionMacro;
        this.TransitionMacroDelaySeconds = 2f;
        this.TellCurrentDjMacro = DefaultTellCurrentDjMacro;
        this.TellCurrentDjMacroDelaySeconds = 2f;
        this.PhotographerShoutMacro = DefaultPhotographerShoutMacro;
        this.PhotographerShoutMacroDelaySeconds = 2f;
        this.StaffRoleShoutSettings = [];
        this.EnsureStaffRoleShoutSettings();
    }

    public Dictionary<string, string> BuildVariables(DjScheduleEntry? currentDjOverride = null, DjScheduleEntry? nextDjOverride = null)
    {
        var currentDj = currentDjOverride ?? this.GetSelectedDj();
        var nextDj = nextDjOverride ?? this.GetNextDjAfter(currentDj);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CurrentDJName"] = currentDj?.DJName ?? string.Empty,
            ["CurrentDJLink"] = currentDj?.DJLink ?? string.Empty,
            ["DJName"] = currentDj?.DJName ?? string.Empty,
            ["DJLink"] = currentDj?.DJLink ?? string.Empty,
            ["NextDJName"] = nextDj?.DJName ?? string.Empty,
            ["NextDJLink"] = nextDj?.DJLink ?? string.Empty,
            ["VenueName"] = this.VenueName,
            ["EventName"] = this.EventName,
            ["GiveawayText"] = this.IsGiveawayActiveFor(currentDj) ? this.GiveawayText : string.Empty,
            ["GiveawayCommand"] = this.IsGiveawayActiveFor(currentDj) && this.GiveawayCommandEnabled ? this.GiveawayCommand : string.Empty,
            ["CommandText"] = this.IsGiveawayActiveFor(currentDj) && this.GiveawayCommandEnabled ? this.GiveawayCommand : string.Empty,
            ["GiveawayCommandLine"] = this.BuildGiveawayCommandLine(currentDj),
            ["GiveawayLine"] = this.BuildGiveawayLine(currentDj),
            ["GiveawayEnabled"] = this.IsGiveawayActiveFor(currentDj) ? "true" : "false",
            ["DjGiveawayEnabled"] = currentDj?.GiveawayEnabled == true ? "true" : "false",
            ["GiveawayCommandEnabled"] = this.GiveawayCommandEnabled ? "true" : "false",
            ["StartTime"] = currentDj?.StartTime ?? string.Empty,
            ["EndTime"] = currentDj?.EndTime ?? string.Empty,
            ["EventStartDate"] = this.EventStartDate,
            ["EventStartTime"] = this.EventStartTime,
            ["EventEndDate"] = this.EventEndDate,
            ["EventEndTime"] = this.EventEndTime,
        };
    }

    private string BuildGiveawayLine(DjScheduleEntry? currentDj)
    {
        if (!this.IsGiveawayActiveFor(currentDj) || string.IsNullOrWhiteSpace(this.GiveawayText))
            return string.Empty;

        var line = $"/y To enter twitch {this.GiveawayText}";
        var commandLine = this.BuildGiveawayCommandLine(currentDj);
        return string.IsNullOrWhiteSpace(commandLine) ? line : $"{line} {commandLine}";
    }

    private string BuildGiveawayCommandLine(DjScheduleEntry? currentDj)
    {
        if (!this.IsGiveawayActiveFor(currentDj) || !this.GiveawayCommandEnabled || string.IsNullOrWhiteSpace(this.GiveawayCommand))
            return string.Empty;

        return $"> type {this.GiveawayCommand} on twitch chat";
    }

    private bool IsGiveawayActiveFor(DjScheduleEntry? currentDj)
    {
        return this.GiveawayEnabled && (currentDj?.GiveawayEnabled ?? true);
    }


    public List<string> GetScheduleWarnings()
    {
        this.EnsureScheduleDefaults();
        var warnings = new List<string>();

        AddDuplicateSlotWarnings(warnings, this.DjSchedule);
        AddEventWindowWarnings(warnings, this);

        int? previousEndMinutes = null;
        int? previousStartMinutes = null;
        int? previousRawStartMinutes = null;
        bool previousCrossedMidnight = false;
        DjScheduleEntry? previousEntry = null;

        foreach (var entry in this.DjSchedule.OrderBy(entry => entry.Order))
        {
            if (!HasUsableDj(entry))
                continue;

            if (!TryParseServerTime(entry.StartTime, out var start) || !TryParseServerTime(entry.EndTime, out var end))
            {
                warnings.Add($"DJ {entry.Order} ({DisplayWarningDjName(entry)}) has an invalid time. Use HH:mm server time, for example 22:00.");
                previousEntry = null;
                previousStartMinutes = null;
                previousEndMinutes = null;
                previousRawStartMinutes = null;
                previousCrossedMidnight = false;
                continue;
            }

            if (start == end)
            {
                warnings.Add($"DJ {entry.Order} ({DisplayWarningDjName(entry)}) has the same start and end time: {entry.StartTime}.");
                previousEntry = null;
                previousStartMinutes = null;
                previousEndMinutes = null;
                previousRawStartMinutes = null;
                previousCrossedMidnight = false;
                continue;
            }

            var rawStartMinutes = (int)start.TotalMinutes;
            var rawEndMinutes = (int)end.TotalMinutes;
            var crossesMidnight = rawEndMinutes <= rawStartMinutes;
            var startMinutes = rawStartMinutes;
            var endMinutes = rawEndMinutes;
            if (crossesMidnight)
                endMinutes += 24 * 60;

            if (previousRawStartMinutes.HasValue && rawStartMinutes < previousRawStartMinutes.Value && !previousCrossedMidnight)
            {
                warnings.Add($"Rows appear out of time order: DJ {entry.Order} ({DisplayWarningDjName(entry)}) starts at {entry.StartTime} after a later-looking previous row. Use Auto Times or reorder the lineup.");
            }

            if (previousStartMinutes.HasValue)
            {
                while (startMinutes < previousStartMinutes.Value)
                    startMinutes += 24 * 60;

                while (endMinutes <= startMinutes)
                    endMinutes += 24 * 60;
            }

            if (previousEndMinutes.HasValue && previousEntry is not null)
            {
                if (startMinutes < previousEndMinutes.Value)
                {
                    warnings.Add($"DJ {entry.Order} ({DisplayWarningDjName(entry)}) overlaps DJ {previousEntry.Order} ({DisplayWarningDjName(previousEntry)}): starts at {entry.StartTime} before previous ends at {FormatClockMinutes(previousEndMinutes.Value)}.");
                }
                else if (startMinutes > previousEndMinutes.Value)
                {
                    warnings.Add($"Gap between DJ {previousEntry.Order} ({DisplayWarningDjName(previousEntry)}) and DJ {entry.Order} ({DisplayWarningDjName(entry)}): {FormatMinutes(startMinutes - previousEndMinutes.Value)}.");
                }
            }

            previousEntry = entry;
            previousStartMinutes = startMinutes;
            previousEndMinutes = endMinutes;
            previousRawStartMinutes = rawStartMinutes;
            previousCrossedMidnight = crossesMidnight;
        }

        return warnings.Distinct().ToList();
    }

    private static void AddEventWindowWarnings(List<string> warnings, Configuration config)
    {
        if (!config.TryGetEventWindow(out var eventStartUtc, out var eventEndUtc))
        {
            warnings.Add("Event window is invalid. Start must be before End, using yyyy-MM-dd and HH:mm ST/UTC.");
            return;
        }

        foreach (var slot in config.BuildEventTimeline().Where(slot => !slot.IsInsideEventWindow))
        {
            warnings.Add($"DJ {slot.Entry.Order} ({DisplayWarningDjName(slot.Entry)}) is outside the event window ({eventStartUtc:yyyy-MM-dd HH:mm} → {eventEndUtc:yyyy-MM-dd HH:mm} ST) and will not auto-shout.");
        }
    }

    private static void AddDuplicateSlotWarnings(List<string> warnings, IEnumerable<DjScheduleEntry> schedule)
    {
        var grouped = schedule
            .Where(entry => HasUsableDj(entry) && TryParseServerTime(entry.StartTime, out _) && TryParseServerTime(entry.EndTime, out _))
            .GroupBy(entry => $"{NormalizeWarningTime(entry.StartTime)}->{NormalizeWarningTime(entry.EndTime)}")
            .Where(group => group.Count() > 1);

        foreach (var group in grouped)
        {
            var rows = string.Join(", ", group.Select(entry => $"DJ {entry.Order} ({DisplayWarningDjName(entry)})"));
            var first = group.First();
            warnings.Add($"Multiple DJs share the same time slot {NormalizeWarningTime(first.StartTime)} → {NormalizeWarningTime(first.EndTime)}: {rows}.");
        }
    }

    private static string NormalizeWarningTime(string? value)
    {
        return TryParseServerTime(value, out var parsed)
            ? $"{parsed.Hours:00}:{parsed.Minutes:00}"
            : (value ?? string.Empty).Trim();
    }

    private static string DisplayWarningDjName(DjScheduleEntry entry)
    {
        return string.IsNullOrWhiteSpace(entry.DJName) ? "unnamed" : entry.DJName;
    }

    private static string FormatClockMinutes(int minutes)
    {
        minutes %= 24 * 60;
        if (minutes < 0)
            minutes += 24 * 60;

        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes < 60)
            return $"{minutes}m";

        var hours = minutes / 60;
        var remainingMinutes = minutes % 60;
        return remainingMinutes == 0 ? $"{hours}h" : $"{hours}h {remainingMinutes}m";
    }

    public void ClampAutoShoutSettings()
    {
        if (this.AutoCurrentDjShoutIntervalMinutes < 1)
            this.AutoCurrentDjShoutIntervalMinutes = 1;

        if (this.AutoCurrentDjShoutIntervalMinutes > 120)
            this.AutoCurrentDjShoutIntervalMinutes = 120;

        if (this.AutoStaffRoleShoutStaggerMinutes < 1)
            this.AutoStaffRoleShoutStaggerMinutes = 1;

        if (this.AutoStaffRoleShoutStaggerMinutes > 30)
            this.AutoStaffRoleShoutStaggerMinutes = 30;
    }

    public void Save()
    {
        this.pluginInterface?.SavePluginConfig(this);
        this.lineupStore?.Save(this);
    }

    /// <summary>
    /// Commits DJ Database edits after an edit operation is finished.
    /// Normalizing on every keystroke can reorder active ImGui rows and make
    /// text appear to jump into other fields, so the database window calls this
    /// after text inputs are deactivated instead.
    /// </summary>
    public void CommitDjDatabaseChanges()
    {
        this.NormalizeDjDatabase(saveToStore: true);
        this.Save();
    }


    private void InitializeLineupStore(IDalamudPluginInterface pluginInterface)
    {
        var snapshotPath = Path.Combine(pluginInterface.ConfigDirectory.FullName, "active_lineup.json");
        this.lineupStore = new LineupStore(snapshotPath);

        if (this.DjSchedule.Count == 0 && this.lineupStore.TryLoad(out var snapshot) && snapshot.DjSchedule.Count > 0)
        {
            this.DjSchedule = snapshot.DjSchedule;
            this.SelectedDjOrder = snapshot.SelectedDjOrder;
            this.VenueName = snapshot.VenueName;
            this.EventName = snapshot.EventName;
            this.EventStartDate = snapshot.EventStartDate;
            this.EventStartTime = snapshot.EventStartTime;
            this.EventEndDate = snapshot.EventEndDate;
            this.EventEndTime = snapshot.EventEndTime;
            this.GiveawayText = snapshot.GiveawayText;
            this.GiveawayCommand = snapshot.GiveawayCommand;
            this.GiveawayEnabled = snapshot.GiveawayEnabled;
            this.GiveawayCommandEnabled = snapshot.GiveawayCommandEnabled;
        }
    }

    private void InitializeDjDatabaseStore(IDalamudPluginInterface pluginInterface)
    {
        var dbPath = Path.Combine(pluginInterface.ConfigDirectory.FullName, "dj_database.sqlite3");
        this.djDatabaseStore = new DjDatabaseStore(dbPath);

        if (this.djDatabaseStore.IsEmpty())
        {
            // First run after upgrading from the JSON-backed database.
            // Seed SQLite from the existing config so testers keep their saved DJs.
            this.djDatabaseStore.ReplaceAll(this.DjDatabase);
        }

        this.DjDatabase = this.djDatabaseStore.Load().ToList();
    }

    private static bool RemoveManualWaitLines(ref string macroText)
    {
        if (string.IsNullOrWhiteSpace(macroText))
            return false;

        var original = macroText;
        var cleanedLines = new List<string>();

        foreach (var rawLine in macroText.Replace("\r", string.Empty).Split('\n'))
        {
            if (Regex.IsMatch(rawLine.Trim(), @"^<wait\.\d+(?:\.\d+)?>$", RegexOptions.IgnoreCase))
                continue;

            cleanedLines.Add(rawLine);
        }

        macroText = string.Join("\n", cleanedLines);
        return !string.Equals(original, macroText, StringComparison.Ordinal);
    }
}
