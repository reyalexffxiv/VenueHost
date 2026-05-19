using System;
using System.Collections.Generic;
using System.Linq;
using VenueHost.Models;

namespace VenueHost.Services;

/// <summary>
/// Owns automatic staff role shout scheduling, including optional staggered queue spacing.
/// </summary>
/// <remarks>
/// This service decides when a staff role shout is due. The actual macro expansion and
/// chat queueing stay in <see cref="ShoutService"/>, so scheduling and output remain
/// separate and easier to verify in small Dalamud refactor steps.
/// </remarks>
public sealed class StaffAutoShoutService : BaseService
{
    private readonly Dictionary<string, DateTime> nextAutoStaffShoutAtUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> pendingStaffRoleQueue = [];
    private DateTime nextAllowedAutoStaffRoleShoutAtUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="StaffAutoShoutService"/> class.
    /// </summary>
    public StaffAutoShoutService(Configuration configuration, IServiceContext services)
        : base(configuration, services)
    {
    }

    private GameChatService Chat => this.Services.Get<GameChatService>();

    private ShoutService Shout => this.Services.Get<ShoutService>();

    /// <summary>
    /// Recalculates staff auto-shout timing from the next framework tick.
    /// Call this after staff schedules or role auto-shout settings change.
    /// </summary>
    public void Refresh()
    {
        this.nextAutoStaffShoutAtUtc.Clear();
        this.pendingStaffRoleQueue.Clear();
        this.nextAllowedAutoStaffRoleShoutAtUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Returns the next planned automatic shout for a role, or <c>null</c> when none is scheduled.
    /// </summary>
    public DateTime? GetNextShoutAtUtc(string? role)
    {
        role = NormalizeRole(role);
        return this.nextAutoStaffShoutAtUtc.TryGetValue(role, out var value) && value != DateTime.MinValue
            ? value
            : null;
    }


    /// <summary>
    /// Builds a UI preview of the actual staff role shout order, including the persistent
    /// pending queue and stagger delay. This keeps the status table aligned with the
    /// scheduler instead of independently guessing from raw next-shout timestamps.
    /// </summary>
    public IReadOnlyList<StaffAutoShoutPreviewRow> GetUpcomingRoleShouts(DateTime nowUtc, int maxRows = 6)
    {
        maxRows = Math.Clamp(maxRows, 1, 20);
        this.Configuration.EnsureStaffRoleShoutSettings();

        var enabledSettings = this.Configuration.GetAutoEnabledStaffRoleShoutSettings().ToList();
        if (enabledSettings.Count == 0)
            return [];

        var enabledRoles = enabledSettings
            .Select(setting => NormalizeRole(setting.Role))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = new List<StaffAutoShoutPreviewRow>();
        var usedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stagger = TimeSpan.FromMinutes(Math.Clamp(this.Configuration.AutoStaffRoleShoutStaggerMinutes, 1, 30));
        var nextDisplayTime = GetFirstAvailablePreviewTime(
            nowUtc,
            this.Configuration.AutoStaffRoleShoutStaggerEnabled,
            this.nextAllowedAutoStaffRoleShoutAtUtc);

        foreach (var role in this.pendingStaffRoleQueue.Where(enabledRoles.Contains))
        {
            if (rows.Count >= maxRows)
                return rows;

            var activeCount = this.CountCurrentUniqueStaffForRole(nowUtc, role);
            if (activeCount <= 0)
                continue;

            var status = rows.Count == 0
                ? $"Next, {activeCount} active"
                : $"Queued, {activeCount} active";

            rows.Add(new StaffAutoShoutPreviewRow(nextDisplayTime, role, status));
            usedRoles.Add(role);

            if (this.Configuration.AutoStaffRoleShoutStaggerEnabled)
                nextDisplayTime = nextDisplayTime.Add(stagger);
        }

        var scheduledRoles = enabledSettings
            .Select(setting => new
            {
                Role = NormalizeRole(setting.Role),
                Next = this.GetStoredNextShoutAtUtc(setting.Role),
            })
            .Where(row => row.Next.HasValue && !usedRoles.Contains(row.Role))
            .OrderBy(row => row.Next!.Value)
            .ThenBy(row => row.Role, StringComparer.OrdinalIgnoreCase);

        foreach (var row in scheduledRoles)
        {
            if (rows.Count >= maxRows)
                break;

            var activeCount = this.CountCurrentUniqueStaffForRole(nowUtc, row.Role);
            var displayTime = row.Next!.Value;
            var queuedByEarlierRows = false;

            if (this.Configuration.AutoStaffRoleShoutStaggerEnabled && displayTime < nextDisplayTime)
            {
                displayTime = nextDisplayTime;
                queuedByEarlierRows = rows.Count > 0;
            }

            var staffStatus = activeCount > 0 ? $"{activeCount} active" : "no active staff";
            var status = rows.Count == 0
                ? $"Next, {staffStatus}"
                : queuedByEarlierRows ? $"Queued, {staffStatus}" : staffStatus;

            rows.Add(new StaffAutoShoutPreviewRow(displayTime, row.Role, status));

            if (this.Configuration.AutoStaffRoleShoutStaggerEnabled)
                nextDisplayTime = displayTime.Add(stagger);
        }

        return rows;
    }

    /// <summary>
    /// Runs one staff role auto-shout scheduler tick. This is called from the Dalamud framework update.
    /// </summary>
    public void Update()
    {
        var config = this.Configuration;
        config.EnsureStaffRoleShoutSettings();

        var enabledSettings = config.GetAutoEnabledStaffRoleShoutSettings().ToList();
        if (enabledSettings.Count == 0)
        {
            this.Refresh();
            return;
        }

        this.RemoveDisabledRoleSchedules(enabledSettings);

        var nowUtc = DateTime.UtcNow;
        foreach (var setting in enabledSettings)
            this.EnsureRoleSchedule(setting, nowUtc);

        var dueSettings = enabledSettings
            .Select(setting => new DueStaffRole(
                setting,
                NormalizeRole(setting.Role),
                this.GetStoredNextShoutAtUtc(setting.Role),
                this.GetCurrentSlotsForRole(nowUtc, setting.Role)))
            .Where(candidate => candidate.NextShoutAtUtc.HasValue &&
                                nowUtc >= candidate.NextShoutAtUtc.Value &&
                                candidate.CurrentSlots.Count > 0)
            .OrderBy(candidate => candidate.NextShoutAtUtc!.Value)
            .ThenBy(candidate => candidate.Role, StringComparer.OrdinalIgnoreCase)
            .ToList();

        this.QueueDueRoles(dueSettings);
        if (this.pendingStaffRoleQueue.Count == 0)
            return;

        var canSendRoleShoutNow = !config.AutoStaffRoleShoutStaggerEnabled ||
                                  this.nextAllowedAutoStaffRoleShoutAtUtc == DateTime.MinValue ||
                                  nowUtc >= this.nextAllowedAutoStaffRoleShoutAtUtc;
        if (!canSendRoleShoutNow)
            return;

        if (this.Chat.HasPendingCommands)
        {
            // Wait for the existing chat queue to drain without changing due times
            // or queue order. This keeps older due roles from being jumped by newer ones.
            return;
        }

        var due = this.GetNextQueuedDueRole(enabledSettings, nowUtc);
        if (due is null)
            return;

        this.pendingStaffRoleQueue.RemoveAll(role => string.Equals(role, due.Role, StringComparison.OrdinalIgnoreCase));
        due.Setting.IntervalMinutes = Math.Clamp(due.Setting.IntervalMinutes, 1, 120);
        this.Shout.SendStaffRoleShout(due.Role);
        this.nextAutoStaffShoutAtUtc[due.Role] = GetNextAlignedStaffShoutAt(
            nowUtc.AddSeconds(1),
            due.CurrentSlots.Min(slot => slot.StartUtc),
            due.Setting.IntervalMinutes);

        if (config.AutoStaffRoleShoutStaggerEnabled)
            this.nextAllowedAutoStaffRoleShoutAtUtc = nowUtc.AddMinutes(config.AutoStaffRoleShoutStaggerMinutes);
    }

    private void QueueDueRoles(IEnumerable<DueStaffRole> dueSettings)
    {
        foreach (var due in dueSettings)
        {
            if (!this.pendingStaffRoleQueue.Contains(due.Role, StringComparer.OrdinalIgnoreCase))
                this.pendingStaffRoleQueue.Add(due.Role);
        }
    }

    private DueStaffRole? GetNextQueuedDueRole(IReadOnlyCollection<StaffRoleShoutSetting> enabledSettings, DateTime nowUtc)
    {
        while (this.pendingStaffRoleQueue.Count > 0)
        {
            var role = this.pendingStaffRoleQueue[0];
            var setting = enabledSettings.FirstOrDefault(candidate =>
                string.Equals(NormalizeRole(candidate.Role), role, StringComparison.OrdinalIgnoreCase));
            if (setting is null)
            {
                this.pendingStaffRoleQueue.RemoveAt(0);
                continue;
            }

            var currentSlots = this.GetCurrentSlotsForRole(nowUtc, role);
            if (currentSlots.Count == 0)
            {
                this.pendingStaffRoleQueue.RemoveAt(0);
                continue;
            }

            return new DueStaffRole(
                setting,
                role,
                this.GetStoredNextShoutAtUtc(role),
                currentSlots);
        }

        return null;
    }

    private void RemoveDisabledRoleSchedules(IEnumerable<StaffRoleShoutSetting> enabledSettings)
    {
        var enabledRoles = enabledSettings
            .Select(setting => NormalizeRole(setting.Role))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var storedRole in this.nextAutoStaffShoutAtUtc.Keys.ToList())
        {
            if (!enabledRoles.Contains(storedRole))
                this.nextAutoStaffShoutAtUtc.Remove(storedRole);
        }

        this.pendingStaffRoleQueue.RemoveAll(role => !enabledRoles.Contains(role));
    }

    private void EnsureRoleSchedule(StaffRoleShoutSetting setting, DateTime nowUtc)
    {
        var role = NormalizeRole(setting.Role);
        setting.IntervalMinutes = Math.Clamp(setting.IntervalMinutes, 1, 120);

        var currentSlots = this.GetCurrentSlotsForRole(nowUtc, role);
        var nextSlot = this.Configuration.GetNextStaffScheduleTimelineSlotForRole(nowUtc, role);
        if (currentSlots.Count == 0 && nextSlot is null)
        {
            this.nextAutoStaffShoutAtUtc.Remove(role);
            return;
        }

        if (!this.nextAutoStaffShoutAtUtc.TryGetValue(role, out var nextShoutAt) || nextShoutAt == DateTime.MinValue)
        {
            this.nextAutoStaffShoutAtUtc[role] = currentSlots.Count > 0
                ? GetNextAlignedStaffShoutAt(nowUtc, currentSlots.Min(slot => slot.StartUtc), setting.IntervalMinutes)
                : nextSlot!.StartUtc;
            return;
        }

        if (currentSlots.Count == 0 && nextShoutAt <= nowUtc)
        {
            this.nextAutoStaffShoutAtUtc[role] = nextSlot?.StartUtc ?? DateTime.MinValue;
            if (this.nextAutoStaffShoutAtUtc[role] == DateTime.MinValue)
                this.nextAutoStaffShoutAtUtc.Remove(role);
        }
    }

    private DateTime? GetStoredNextShoutAtUtc(string? role)
    {
        role = NormalizeRole(role);
        return this.nextAutoStaffShoutAtUtc.TryGetValue(role, out var value) && value != DateTime.MinValue
            ? value
            : null;
    }

    private List<Configuration.StaffScheduleTimelineSlot> GetCurrentSlotsForRole(DateTime nowUtc, string? role)
    {
        role = NormalizeRole(role);
        return this.Configuration.BuildStaffScheduleTimeline()
            .Where(slot => slot.Entry.Available &&
                           slot.IsInsideEventWindow &&
                           nowUtc >= slot.StartUtc &&
                           nowUtc < slot.EndUtc &&
                           string.Equals(slot.Entry.Role?.Trim(), role, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static DateTime GetNextAlignedStaffShoutAt(DateTime nowUtc, DateTime scheduleStartUtc, int intervalMinutes)
    {
        intervalMinutes = Math.Clamp(intervalMinutes, 1, 120);
        var intervalTicks = TimeSpan.FromMinutes(intervalMinutes).Ticks;

        if (nowUtc <= scheduleStartUtc)
            return scheduleStartUtc;

        var elapsedTicks = nowUtc.Ticks - scheduleStartUtc.Ticks;
        var intervalsElapsed = (elapsedTicks + intervalTicks - 1) / intervalTicks;
        return scheduleStartUtc.AddTicks(intervalsElapsed * intervalTicks);
    }

    private sealed record DueStaffRole(
        StaffRoleShoutSetting Setting,
        string Role,
        DateTime? NextShoutAtUtc,
        List<Configuration.StaffScheduleTimelineSlot> CurrentSlots);


    private int CountCurrentUniqueStaffForRole(DateTime nowUtc, string role)
    {
        return this.GetCurrentSlotsForRole(nowUtc, role)
            .Select(slot => slot.Entry.Name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static DateTime GetFirstAvailablePreviewTime(DateTime nowUtc, bool staggerEnabled, DateTime nextAllowedUtc)
    {
        if (!staggerEnabled || nextAllowedUtc == DateTime.MinValue || nowUtc >= nextAllowedUtc)
            return nowUtc;

        return nextAllowedUtc;
    }

    private static string NormalizeRole(string? role)
    {
        return string.IsNullOrWhiteSpace(role) ? "Photographer" : role.Trim();
    }
}

/// <summary>
/// Read-only UI row describing the scheduler's upcoming staff role shout order.
/// </summary>
public sealed record StaffAutoShoutPreviewRow(DateTime DisplayTimeUtc, string Role, string Status);
