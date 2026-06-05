using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VenueHost.Models;

namespace VenueHost.Services;

/// <summary>
/// Owns manual and automatic event/custom shouts.
/// </summary>
/// <remarks>
/// Event shouts are venue-wide custom macros. Automatic timing follows the event
/// shout window so a saved timer cannot keep shouting outside the planned window.
/// </remarks>
public sealed class EventShoutService : BaseService
{
    /// <summary>
    /// Minimum spacing between automatic event shouts when several are due together.
    /// This mirrors the staff role shout queue behavior and prevents chat walls.
    /// </summary>
    private static readonly TimeSpan EventShoutQueueSpacing = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Next raw timer due time per saved shout row.
    /// </summary>
    private readonly Dictionary<int, DateTime> nextShoutAtUtcByOrder = new();

    /// <summary>
    /// Persistent FIFO queue for shouts that are already due.
    /// Keeping a queue avoids starvation when multiple shouts become due together
    /// or while the shared chat queue is still flushing older macro lines.
    /// </summary>
    private readonly List<int> pendingEventShoutQueue = new();

    /// <summary>
    /// Earliest time another automatic event shout may be sent.
    /// Manual Shout Now actions do not use this automatic stagger guard.
    /// </summary>
    private DateTime nextEventShoutAllowedAtUtc = DateTime.MinValue;

    public EventShoutService(Configuration configuration, IServiceContext services)
        : base(configuration, services)
    {
    }

    /// <summary>Human-readable status text shown in the Event Shouts tab.</summary>
    public string Status { get; private set; } = "Disabled.";

    private GameChatService Chat => this.Services.Get<GameChatService>();

    /// <summary>Clears planned shout times so timers are recalculated from current settings.</summary>
    public void Refresh()
    {
        this.ClearSchedulerState();
        this.Status = "Event shout timers refreshed.";
    }

    /// <summary>Runs one event-shout scheduler tick. Called from the Dalamud framework update.</summary>
    public void Update()
    {
        var config = this.Configuration;
        config.EnsureEventShoutDefaults();
        config.ClampAutoShoutSettings();

        var enabledShouts = config.GetAutoEnabledEventShouts().ToList();
        if (enabledShouts.Count == 0)
        {
            this.ClearSchedulerState();
            this.Status = "Disabled.";
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (!config.TryGetEventShoutWindow(out var eventStartUtc, out var eventEndUtc))
        {
            this.ClearSchedulerState();
            this.Status = "Enabled, but the event shout window is invalid.";
            return;
        }

        if (nowUtc < eventStartUtc || nowUtc >= eventEndUtc)
        {
            this.ClearSchedulerState();
            this.Status = $"Enabled, waiting for event shout window ({eventStartUtc:yyyy-MM-dd HH:mm} → {eventEndUtc:yyyy-MM-dd HH:mm} ST).";
            return;
        }

        this.RemoveStaleTimers(enabledShouts);

        foreach (var shout in enabledShouts)
        {
            if (!TryGetEffectiveShoutWindow(shout, eventStartUtc, eventEndUtc, nowUtc, out var shoutStartUtc, out var shoutEndUtc))
            {
                this.nextShoutAtUtcByOrder[shout.Order] = DateTime.MaxValue;
                continue;
            }

            if (!this.nextShoutAtUtcByOrder.TryGetValue(shout.Order, out var next) ||
                next <= DateTime.MinValue ||
                next < shoutStartUtc ||
                next >= shoutEndUtc)
            {
                this.nextShoutAtUtcByOrder[shout.Order] = CalculateNextOccurrence(nowUtc, shoutStartUtc, shoutEndUtc, TimeSpan.FromMinutes(shout.IntervalMinutes));
            }
        }

        this.QueueDueShouts(enabledShouts, nowUtc);

        var queuedShout = this.GetFirstQueuedValidShout(enabledShouts, eventStartUtc, eventEndUtc, nowUtc);
        if (queuedShout is null)
        {
            var nextPair = this.GetNextScheduledPair(enabledShouts);
            this.Status = nextPair is null
                ? "Enabled, but no future event shout is inside the event window."
                : $"Enabled. Next custom shout: {nextPair.Value.Shout.Name} at {nextPair.Value.Next:yyyy-MM-dd HH:mm:ss} ST.";
            return;
        }

        if (this.Chat.HasPendingCommands)
        {
            this.Status = $"Waiting for queued chat to finish before event shout: {queuedShout.Name}. Pending: {this.Chat.PendingCommandCount}.";
            return;
        }

        if (nowUtc < this.nextEventShoutAllowedAtUtc)
        {
            this.Status = $"Queued event shout: {queuedShout.Name}. Waiting until {this.nextEventShoutAllowedAtUtc:HH:mm:ss} ST.";
            return;
        }

        this.pendingEventShoutQueue.Remove(queuedShout.Order);
        this.SendEventShout(queuedShout);
        if (TryGetEffectiveShoutWindow(queuedShout, eventStartUtc, eventEndUtc, nowUtc, out var selectedStartUtc, out var selectedEndUtc))
        {
            this.nextShoutAtUtcByOrder[queuedShout.Order] = CalculateNextOccurrence(
                nowUtc.AddSeconds(1),
                selectedStartUtc,
                selectedEndUtc,
                TimeSpan.FromMinutes(queuedShout.IntervalMinutes));
        }
        else
        {
            this.nextShoutAtUtcByOrder[queuedShout.Order] = DateTime.MaxValue;
        }

        this.nextEventShoutAllowedAtUtc = nowUtc.Add(EventShoutQueueSpacing);
        this.Status = $"Sent custom shout: {queuedShout.Name}. Next queued shout allowed at {this.nextEventShoutAllowedAtUtc:HH:mm:ss} ST.";
    }

    /// <summary>Queues the selected event shout manually.</summary>
    public void SendSelectedEventShout()
    {
        this.SendEventShout(this.Configuration.GetSelectedEventShout());
    }

    /// <summary>Queues a specific event shout manually or from the timer.</summary>
    public void SendEventShout(EventShoutEntry? entry)
    {
        if (!Configuration.HasUsableEventShout(entry))
            return;

        var expandedMacro = MacroVariableService.Expand(
            entry!.Macro,
            this.Configuration.BuildEventShoutVariables(entry));

        this.Chat.QueueMacroCommands(expandedMacro, TimeSpan.FromSeconds(entry.DelaySeconds));
    }

    /// <summary>Returns the selected event shout macro after variable expansion.</summary>
    public string PreviewSelectedEventShout()
    {
        return this.PreviewEventShout(this.Configuration.GetSelectedEventShout());
    }

    /// <summary>Returns a specific event shout macro after variable expansion.</summary>
    public string PreviewEventShout(EventShoutEntry? entry)
    {
        return entry is null
            ? string.Empty
            : MacroVariableService.Expand(entry.Macro, this.Configuration.BuildEventShoutVariables(entry));
    }

    /// <summary>Returns the next planned automatic shout time for a saved entry.</summary>
    public DateTime? GetNextShoutAtUtc(EventShoutEntry? entry)
    {
        if (entry is null || !this.nextShoutAtUtcByOrder.TryGetValue(entry.Order, out var next) || next == DateTime.MaxValue)
            return null;

        return next;
    }

    /// <summary>
    /// Returns upcoming event shouts in the same order the scheduler will process them.
    /// This method projects the staggered send times so the UI preview matches the
    /// real FIFO queue instead of showing every due shout at the same raw timestamp.
    /// </summary>
    public IReadOnlyList<UpcomingEventShout> GetUpcomingEventShouts(int limit = 8)
    {
        var nowUtc = DateTime.UtcNow;
        var shouts = this.Configuration.GetAutoEnabledEventShouts().ToList();
        if (shouts.Count == 0)
            return [];

        if (!this.Configuration.TryGetEventShoutWindow(out var eventStartUtc, out var eventEndUtc) || nowUtc < eventStartUtc || nowUtc >= eventEndUtc)
            return [];

        foreach (var shout in shouts)
        {
            if (!TryGetEffectiveShoutWindow(shout, eventStartUtc, eventEndUtc, nowUtc, out var shoutStartUtc, out var shoutEndUtc))
            {
                this.nextShoutAtUtcByOrder[shout.Order] = DateTime.MaxValue;
                continue;
            }

            if (!this.nextShoutAtUtcByOrder.TryGetValue(shout.Order, out var next) ||
                next <= DateTime.MinValue ||
                next < shoutStartUtc ||
                next >= shoutEndUtc)
            {
                this.nextShoutAtUtcByOrder[shout.Order] = CalculateNextOccurrence(nowUtc, shoutStartUtc, shoutEndUtc, TimeSpan.FromMinutes(shout.IntervalMinutes));
            }
        }

        this.QueueDueShouts(shouts, nowUtc);

        var result = new List<UpcomingEventShout>();
        var displayTime = nowUtc > this.nextEventShoutAllowedAtUtc ? nowUtc : this.nextEventShoutAllowedAtUtc;

        foreach (var order in this.pendingEventShoutQueue.ToList())
        {
            var shout = shouts.FirstOrDefault(item => item.Order == order);
            if (shout is null)
            {
                this.pendingEventShoutQueue.Remove(order);
                continue;
            }

            result.Add(new UpcomingEventShout(shout, displayTime));
            displayTime = displayTime.Add(EventShoutQueueSpacing);
            if (result.Count >= limit)
                return result;
        }

        var nextDisplayTime = result.Count > 0
            ? displayTime
            : this.nextEventShoutAllowedAtUtc > nowUtc
                ? this.nextEventShoutAllowedAtUtc
                : DateTime.MinValue;

        foreach (var item in shouts
                     .Where(shout => !this.pendingEventShoutQueue.Contains(shout.Order))
                     .Select(shout => new UpcomingEventShout(shout, this.GetNextShoutAtUtc(shout)))
                     .Where(item => item.NextShoutAtUtc.HasValue)
                     .OrderBy(item => item.NextShoutAtUtc)
                     .ThenBy(item => item.Shout.Order))
        {
            var projectedTime = item.NextShoutAtUtc!.Value;
            if (nextDisplayTime > DateTime.MinValue && projectedTime < nextDisplayTime)
                projectedTime = nextDisplayTime;

            result.Add(new UpcomingEventShout(item.Shout, projectedTime));
            if (result.Count >= limit)
                return result;

            nextDisplayTime = projectedTime.Add(EventShoutQueueSpacing);
        }

        return result;
    }

    private (EventShoutEntry Shout, DateTime Next)? GetNextScheduledPair(IEnumerable<EventShoutEntry> enabledShouts)
    {
        var pairs = enabledShouts
            .Select(shout => new { Shout = shout, Next = this.GetNextShoutAtUtc(shout) })
            .Where(item => item.Next.HasValue)
            .OrderBy(item => item.Next!.Value)
            .ThenBy(item => item.Shout.Order)
            .ToList();

        if (pairs.Count == 0)
            return null;

        return (pairs[0].Shout, pairs[0].Next!.Value);
    }

    /// <summary>
    /// Removes timers and queued items for shouts that are no longer active/auto-enabled.
    /// </summary>
    private void RemoveStaleTimers(IReadOnlyCollection<EventShoutEntry> enabledShouts)
    {
        var enabledOrders = enabledShouts.Select(shout => shout.Order).ToHashSet();
        foreach (var order in this.nextShoutAtUtcByOrder.Keys.Where(order => !enabledOrders.Contains(order)).ToList())
            this.nextShoutAtUtcByOrder.Remove(order);

        this.pendingEventShoutQueue.RemoveAll(order => !enabledOrders.Contains(order));
    }

    /// <summary>
    /// Clears all automatic scheduling state when the global shout window is closed
    /// or no usable auto-enabled shouts remain.
    /// </summary>
    private void ClearSchedulerState()
    {
        this.nextShoutAtUtcByOrder.Clear();
        this.pendingEventShoutQueue.Clear();
        this.nextEventShoutAllowedAtUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Moves due shouts into the persistent FIFO queue in raw due-time order.
    /// A shout already waiting in the queue is not re-added.
    /// </summary>
    private void QueueDueShouts(IReadOnlyCollection<EventShoutEntry> enabledShouts, DateTime nowUtc)
    {
        var due = enabledShouts
            .Where(shout => this.nextShoutAtUtcByOrder.TryGetValue(shout.Order, out var next) && next != DateTime.MaxValue && nowUtc >= next)
            .OrderBy(shout => this.nextShoutAtUtcByOrder[shout.Order])
            .ThenBy(shout => shout.Order);

        foreach (var shout in due)
        {
            if (!this.pendingEventShoutQueue.Contains(shout.Order))
                this.pendingEventShoutQueue.Add(shout.Order);
        }
    }

    /// <summary>
    /// Returns the first queued shout that is still valid inside both the global
    /// event shout window and its own row-level Start/End window.
    /// Invalid queued items are pruned so they do not block later shouts.
    /// </summary>
    private EventShoutEntry? GetFirstQueuedValidShout(
        IReadOnlyCollection<EventShoutEntry> enabledShouts,
        DateTime eventStartUtc,
        DateTime eventEndUtc,
        DateTime nowUtc)
    {
        foreach (var order in this.pendingEventShoutQueue.ToList())
        {
            var shout = enabledShouts.FirstOrDefault(item => item.Order == order);
            if (shout is null || !TryGetEffectiveShoutWindow(shout, eventStartUtc, eventEndUtc, nowUtc, out _, out _))
            {
                this.pendingEventShoutQueue.Remove(order);
                this.nextShoutAtUtcByOrder[order] = DateTime.MaxValue;
                continue;
            }

            return shout;
        }

        return null;
    }


    /// <summary>
    /// Intersects a row-level shout time window with the global Event Shout window.
    /// Row windows are HH:mm values and may cross midnight, so several candidate
    /// dates are tested around the global event start date.
    /// </summary>
    private static bool TryGetEffectiveShoutWindow(
        EventShoutEntry shout,
        DateTime eventStartUtc,
        DateTime eventEndUtc,
        DateTime nowUtc,
        out DateTime shoutStartUtc,
        out DateTime shoutEndUtc)
    {
        shoutStartUtc = default;
        shoutEndUtc = default;

        if (!Configuration.TryParseServerTime(shout.StartTime, out var startTime) ||
            !Configuration.TryParseServerTime(shout.EndTime, out var endTime))
        {
            return false;
        }

        var candidates = new List<(DateTime Start, DateTime End)>();
        for (var dayOffset = -1; dayOffset <= 2; dayOffset++)
        {
            var candidateStart = eventStartUtc.Date.AddDays(dayOffset).Add(startTime);
            var candidateEnd = candidateStart.Date.Add(endTime);
            if (candidateEnd <= candidateStart)
                candidateEnd = candidateEnd.AddDays(1);

            var effectiveStart = candidateStart > eventStartUtc ? candidateStart : eventStartUtc;
            var effectiveEnd = candidateEnd < eventEndUtc ? candidateEnd : eventEndUtc;
            if (effectiveEnd > effectiveStart)
                candidates.Add((effectiveStart, effectiveEnd));
        }

        var selected = candidates
            .OrderBy(candidate => candidate.Start)
            .FirstOrDefault(candidate => candidate.End > nowUtc);

        if (selected.End <= selected.Start)
            return false;

        shoutStartUtc = selected.Start;
        shoutEndUtc = selected.End;
        return true;
    }

    /// <summary>
    /// Calculates the next interval boundary inside a shout's effective window.
    /// Returns <see cref="DateTime.MaxValue"/> when no future occurrence fits.
    /// </summary>
    private static DateTime CalculateNextOccurrence(DateTime nowUtc, DateTime windowStartUtc, DateTime windowEndUtc, TimeSpan interval)
    {
        if (interval < TimeSpan.FromMinutes(1))
            interval = TimeSpan.FromMinutes(1);

        if (nowUtc <= windowStartUtc)
            return windowStartUtc;

        if (nowUtc >= windowEndUtc)
            return DateTime.MaxValue;

        var elapsedTicks = (nowUtc - windowStartUtc).Ticks;
        var intervalTicks = interval.Ticks;
        var steps = (elapsedTicks + intervalTicks - 1) / intervalTicks;
        var candidate = windowStartUtc.AddTicks(steps * intervalTicks);

        return candidate < windowEndUtc ? candidate : DateTime.MaxValue;
    }

    public readonly record struct UpcomingEventShout(EventShoutEntry Shout, DateTime? NextShoutAtUtc)
    {
        public string TimeText => this.NextShoutAtUtc?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "—";
    }
}
