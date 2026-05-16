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
    public const int CurrentConfigVersion = 14;

    public const string DefaultCurrentDjMacro =
        "/y ♪♪ Live DJ @ {VenueName}! Don’t miss the vibe ♪♪ → {CurrentDJLink}\n" +
        "{GiveawayLine}";

    public const string DefaultTransitionMacro =
        "/y Thank you for an amazing set, {CurrentDJName}! ♪♪ Don't forget to tune in to our next awesome live DJ, {NextDJName}! ♪♪ {NextDJLink}";

    public const string DefaultTellCurrentDjMacro =
        "/tell <t> Our current DJ is {CurrentDJName} - {CurrentDJLink}";

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
        this.EnsureEventWindowDefaults();
        this.NormalizeDjDatabase(saveToStore: false);
        this.ClampAutoShoutSettings();
    }

    private void MigrateConfig()
    {
        this.DjSchedule ??= [];
        this.DjDatabase ??= [];

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
