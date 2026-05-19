using System;
using System.Collections.Generic;
using System.Linq;
using VenueHost.Models;

namespace VenueHost.Services;

/// <summary>
/// Owns automatic DJ shout scheduling and state.
///
/// The service decides when an automatic DJ shout is due. It delegates the actual
/// macro expansion and chat queueing to <see cref="ShoutService"/> so timing logic
/// and chat output stay separate.
/// </summary>
public sealed class DjAutoShoutService : BaseService
{
    private DateTime nextAutoCurrentDjShoutAtUtc = DateTime.MinValue;
    private bool previousAutoCurrentDjShoutEnabled;
    private int previousAutoCurrentDjShoutIntervalMinutes;
    private bool autoSawUsableDjSinceEnabled;
    private int? lastAutoDjOrderSeen;

    /// <summary>
    /// Initializes a new instance of the <see cref="DjAutoShoutService"/> class.
    /// </summary>
    public DjAutoShoutService(Configuration configuration, IServiceContext services)
        : base(configuration, services)
    {
        this.previousAutoCurrentDjShoutEnabled = configuration.AutoCurrentDjShoutEnabled;
        this.previousAutoCurrentDjShoutIntervalMinutes = configuration.AutoCurrentDjShoutIntervalMinutes;
    }

    /// <summary>Human-readable status text shown in the UI.</summary>
    public string Status { get; private set; } = "Disabled.";

    /// <summary>The next planned automatic DJ shout time, or <c>null</c> when none is scheduled.</summary>
    public DateTime? NextShoutAtUtc => this.nextAutoCurrentDjShoutAtUtc == DateTime.MinValue ||
                                       this.nextAutoCurrentDjShoutAtUtc == DateTime.MaxValue
        ? null
        : this.nextAutoCurrentDjShoutAtUtc;

    private GameChatService Chat => this.Services.Get<GameChatService>();

    private ShoutService Shout => this.Services.Get<ShoutService>();

    /// <summary>
    /// Recalculate the next automatic DJ shout from the current server time.
    /// Call this after DJ schedule or auto-shout settings change.
    /// </summary>
    public void Refresh()
    {
        this.nextAutoCurrentDjShoutAtUtc = DateTime.MinValue;
        this.previousAutoCurrentDjShoutEnabled = false;
        this.autoSawUsableDjSinceEnabled = false;
        this.lastAutoDjOrderSeen = null;

        this.Status = this.Configuration.AutoCurrentDjShoutEnabled
            ? "Schedule refreshed. Next shout will be recalculated from the DJ lineup."
            : "Disabled.";
    }

    /// <summary>
    /// Runs one DJ auto-shout scheduler tick. This is called from the Dalamud framework update.
    /// </summary>
    public void Update()
    {
        var config = this.Configuration;
        config.ClampAutoShoutSettings();

        if (!config.AutoCurrentDjShoutEnabled)
        {
            this.nextAutoCurrentDjShoutAtUtc = DateTime.MinValue;
            this.previousAutoCurrentDjShoutEnabled = false;
            this.autoSawUsableDjSinceEnabled = false;
            this.lastAutoDjOrderSeen = null;
            this.Status = "Disabled.";
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var intervalChanged = this.previousAutoCurrentDjShoutIntervalMinutes != config.AutoCurrentDjShoutIntervalMinutes;

        if (!this.previousAutoCurrentDjShoutEnabled || this.nextAutoCurrentDjShoutAtUtc == DateTime.MinValue || intervalChanged)
        {
            this.nextAutoCurrentDjShoutAtUtc = this.CalculateNextAutoShoutUtc(nowUtc);
            this.previousAutoCurrentDjShoutEnabled = true;
            this.previousAutoCurrentDjShoutIntervalMinutes = config.AutoCurrentDjShoutIntervalMinutes;
            this.autoSawUsableDjSinceEnabled = false;
            this.lastAutoDjOrderSeen = null;
        }

        if (this.nextAutoCurrentDjShoutAtUtc == DateTime.MaxValue)
        {
            this.Status = "Enabled, but no valid event-window schedule time was found.";
            return;
        }

        if (nowUtc < this.nextAutoCurrentDjShoutAtUtc)
        {
            var activeDj = config.GetCurrentServerTimeDj(nowUtc);
            if (activeDj is not null)
                this.RememberAutoDj(activeDj);

            var activeText = activeDj is null ? "no active DJ right now" : $"active DJ: {activeDj.DJName}";
            this.Status = $"Enabled. Using event window, {activeText}. Next shout at {this.nextAutoCurrentDjShoutAtUtc:yyyy-MM-dd HH:mm:ss} ST.";
            return;
        }

        if (this.Chat.HasPendingCommands)
        {
            this.nextAutoCurrentDjShoutAtUtc = nowUtc.AddSeconds(10);
            this.Status = $"Waiting for queued chat to finish. Pending: {this.Chat.PendingCommandCount}.";
            return;
        }

        var scheduledShoutAtUtc = this.nextAutoCurrentDjShoutAtUtc;
        var currentDj = config.GetCurrentServerTimeDj(scheduledShoutAtUtc);
        if (currentDj is null)
        {
            this.nextAutoCurrentDjShoutAtUtc = this.CalculateNextAutoShoutUtc(nowUtc.AddSeconds(1));
            if (this.ShouldDisableAutoAfterLastDj(this.nextAutoCurrentDjShoutAtUtc))
            {
                this.DisableAutoShoutAfterLastDj();
                return;
            }

            this.Status = this.nextAutoCurrentDjShoutAtUtc == DateTime.MaxValue
                ? $"Enabled. No active DJ at {nowUtc:yyyy-MM-dd HH:mm:ss} ST and no future DJ slot was found."
                : $"Enabled. No active DJ at {nowUtc:yyyy-MM-dd HH:mm:ss} ST. Next shout at {this.nextAutoCurrentDjShoutAtUtc:yyyy-MM-dd HH:mm:ss} ST.";
            return;
        }

        this.RememberAutoDj(currentDj);

        var previousDj = config.GetPreviousDjBefore(currentDj);
        var currentSlot = config.GetTimelineSlotForEntry(currentDj);
        if (previousDj is not null && currentSlot is not null && IsScheduledSlotStart(scheduledShoutAtUtc, currentSlot))
        {
            this.RememberAutoDj(currentDj);
            this.Shout.SendTransitionShoutFor(previousDj, currentDj);
            this.nextAutoCurrentDjShoutAtUtc = this.CalculateNextAutoShoutUtc(nowUtc.AddSeconds(1));
            if (this.ShouldDisableAutoAfterLastDj(this.nextAutoCurrentDjShoutAtUtc))
            {
                this.DisableAutoShoutAfterLastDj();
                return;
            }

            this.Status = $"Sent transition shout: {previousDj.DJName} → {currentDj.DJName}. Next at {FormatServerTime(this.nextAutoCurrentDjShoutAtUtc)}.";
            return;
        }

        this.RememberAutoDj(currentDj);
        this.Shout.SendCurrentDjShoutFor(currentDj);
        this.nextAutoCurrentDjShoutAtUtc = this.CalculateNextAutoShoutUtc(nowUtc.AddSeconds(1));
        if (this.ShouldDisableAutoAfterLastDj(this.nextAutoCurrentDjShoutAtUtc))
        {
            this.DisableAutoShoutAfterLastDj();
            return;
        }

        this.Status = $"Sent current DJ shout for {currentDj.DJName}. Next at {FormatServerTime(this.nextAutoCurrentDjShoutAtUtc)}.";
    }

    /// <summary>Returns the UI label for the next planned automatic DJ shout.</summary>
    public string GetNextShoutTypeDescription()
    {
        if (!this.Configuration.AutoCurrentDjShoutEnabled)
            return "Disabled";

        if (!this.NextShoutAtUtc.HasValue)
            return "Waiting for valid schedule";

        var targetDj = this.Configuration.GetCurrentServerTimeDj(this.NextShoutAtUtc.Value);
        if (targetDj is null)
            return "No DJ found for next shout";

        var previousDj = this.Configuration.GetPreviousDjBefore(targetDj);
        var targetSlot = this.Configuration.GetTimelineSlotForEntry(targetDj);
        if (previousDj is not null && targetSlot is not null && IsScheduledSlotStart(this.NextShoutAtUtc.Value, targetSlot))
            return $"Transition, {previousDj.DJName} → {targetDj.DJName}";

        return $"Current DJ shout, {targetDj.DJName}";
    }

    private static string FormatServerTime(DateTime utcTime)
    {
        return utcTime == DateTime.MaxValue ? "none" : $"{utcTime:yyyy-MM-dd HH:mm:ss} ST";
    }

    private void RememberAutoDj(DjScheduleEntry entry)
    {
        this.autoSawUsableDjSinceEnabled = true;
        this.lastAutoDjOrderSeen = entry.Order;
    }

    private bool ShouldDisableAutoAfterLastDj(DateTime nextCandidateUtc)
    {
        if (!this.autoSawUsableDjSinceEnabled || !this.lastAutoDjOrderSeen.HasValue)
            return false;

        // The event-window scheduler returns DateTime.MaxValue when there is no
        // future current-DJ or transition shout left. That is the only moment we
        // should disable auto-shout automatically.
        //
        // Do not compare row order here. A future shout can legitimately point
        // to the same row again when the repeat interval fires during a long DJ
        // slot, and date-aware schedules can cross midnight without row numbers
        // being a reliable lifecycle boundary.
        return nextCandidateUtc == DateTime.MaxValue;
    }

    private void DisableAutoShoutAfterLastDj()
    {
        this.Configuration.AutoCurrentDjShoutEnabled = false;
        this.Configuration.Save();
        this.nextAutoCurrentDjShoutAtUtc = DateTime.MinValue;
        this.previousAutoCurrentDjShoutEnabled = false;
        this.autoSawUsableDjSinceEnabled = false;
        this.lastAutoDjOrderSeen = null;
        this.Status = "Disabled after the last scheduled DJ.";
    }

    private static bool IsScheduledSlotStart(DateTime scheduledShoutAtUtc, Configuration.EventTimelineSlot slot)
    {
        return scheduledShoutAtUtc.Year == slot.StartUtc.Year &&
               scheduledShoutAtUtc.Month == slot.StartUtc.Month &&
               scheduledShoutAtUtc.Day == slot.StartUtc.Day &&
               scheduledShoutAtUtc.Hour == slot.StartUtc.Hour &&
               scheduledShoutAtUtc.Minute == slot.StartUtc.Minute;
    }

    private DateTime CalculateNextAutoShoutUtc(DateTime nowUtc)
    {
        var interval = TimeSpan.FromMinutes(this.Configuration.AutoCurrentDjShoutIntervalMinutes);
        var candidates = new List<DateTime>();

        foreach (var slot in this.Configuration.BuildEventTimeline().Where(slot => slot.IsInsideEventWindow))
        {
            var candidate = CalculateNextOccurrenceInSlot(nowUtc, slot.StartUtc, slot.EndUtc, interval);
            if (candidate.HasValue)
                candidates.Add(candidate.Value);
        }

        return candidates.Count == 0 ? DateTime.MaxValue : candidates.Min();
    }

    private static DateTime? CalculateNextOccurrenceInSlot(DateTime nowUtc, DateTime slotStartUtc, DateTime slotEndUtc, TimeSpan interval)
    {
        if (nowUtc <= slotStartUtc)
            return slotStartUtc;

        if (nowUtc >= slotEndUtc)
            return null;

        var elapsedTicks = (nowUtc - slotStartUtc).Ticks;
        var intervalTicks = interval.Ticks;
        var steps = (elapsedTicks + intervalTicks - 1) / intervalTicks;
        var candidate = slotStartUtc.AddTicks(steps * intervalTicks);

        return candidate < slotEndUtc ? candidate : null;
    }
}
