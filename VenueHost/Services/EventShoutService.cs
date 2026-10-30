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
/// Event shouts are venue-wide custom macros. Automatic timing follows the event's
/// dates, with same-day pre-party flexibility and per-shout HH:mm windows.
/// </remarks>
public sealed class EventShoutService : BaseService
{
    /// <summary>
    /// Minimum spacing between automatic event shouts when several are due together.
    /// This mirrors the staff role shout queue behavior and prevents chat walls.
    /// </summary>
    private static readonly TimeSpan EventShoutQueueSpacing = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Small tolerance for automatic shout timers. Framework ticks, chat queueing,
    /// and window transitions can happen a few seconds after the exact HH:mm
    /// boundary, so due shouts remain valid briefly instead of being skipped.
    /// </summary>
    private static readonly TimeSpan DueShoutGrace = TimeSpan.FromMinutes(2);

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

    /// <summary>Preserve scheduled intervals across pause; never replay stale queued occurrences.</summary>
    public void ResumeAfterPause(DateTime nowUtc, TimeSpan elapsed)
    {
        this.pendingEventShoutQueue.Clear();
        foreach (var order in this.nextShoutAtUtcByOrder.Keys.ToList())
        {
            var next = this.nextShoutAtUtcByOrder[order];
            if (next == DateTime.MinValue || next == DateTime.MaxValue)
                continue;
            var shifted = next.Add(elapsed);
            if (shifted <= nowUtc)
            {
                var shout = this.Configuration.EventShouts?.FirstOrDefault(item => item.Order == order);
                if (shout is not null && this.Configuration.TryGetEventShoutWindow(out var start, out var end) &&
                    TryGetEffectiveShoutWindow(shout, start, end, nowUtc,
                        out var effectiveStart, out var effectiveEnd))
                    shifted = CalculateNextOccurrence(nowUtc.AddSeconds(1), effectiveStart, effectiveEnd,
                        TimeSpan.FromMinutes(Math.Clamp(shout.IntervalMinutes, 1, 120)),
                        allowRecentlyDueOccurrence: false);
                else
                    shifted = DateTime.MaxValue;
            }
            this.nextShoutAtUtcByOrder[order] = shifted;
        }
        if (this.nextEventShoutAllowedAtUtc > DateTime.MinValue &&
            this.nextEventShoutAllowedAtUtc < DateTime.MaxValue)
            this.nextEventShoutAllowedAtUtc = this.nextEventShoutAllowedAtUtc.Add(elapsed);
    }

    /// <summary>Runs one event-shout scheduler tick. Called from the Dalamud framework update.</summary>
    public void Update(bool dispatch = true, int? requestedOrder = null)
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

        if (nowUtc < eventStartUtc || nowUtc > eventEndUtc.Add(DueShoutGrace))
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
                next > shoutEndUtc)
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

        if (!dispatch) return;

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

        if (requestedOrder.HasValue && queuedShout.Order != requestedOrder.Value)
        {
            queuedShout = enabledShouts.FirstOrDefault(item => item.Order == requestedOrder.Value &&
                this.pendingEventShoutQueue.Contains(item.Order));
            if (queuedShout is null || !TryGetEffectiveShoutWindow(queuedShout, eventStartUtc, eventEndUtc, nowUtc, out _, out _))
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
                TimeSpan.FromMinutes(queuedShout.IntervalMinutes),
                allowRecentlyDueOccurrence: false);
        }
        else
        {
            this.nextShoutAtUtcByOrder[queuedShout.Order] = DateTime.MaxValue;
        }

        this.nextEventShoutAllowedAtUtc = nowUtc.Add(EventShoutQueueSpacing);
        this.Status = $"Sent custom shout: {queuedShout.Name}. Next queued shout allowed at {this.nextEventShoutAllowedAtUtc:HH:mm:ss} ST.";
    }

    public bool CanDispatchNow(DateTime nowUtc) => nowUtc >= this.nextEventShoutAllowedAtUtc;

    public IReadOnlyList<int> PendingOrders => this.pendingEventShoutQueue;

    /// <summary>Queues the selected event shout manually.</summary>
    public void SendSelectedEventShout()
    {
        this.SendEventShout(this.Configuration.GetSelectedEventShout(), isManual: true);
    }

    /// <summary>Queues a specific event shout manually or from the timer.</summary>
    public void SendEventShout(EventShoutEntry? entry, bool isManual = false)
    {
        if (!Configuration.HasUsableEventShout(entry))
            return;

        var expandedMacro = MacroVariableService.Expand(
            entry!.Macro,
            this.Configuration.BuildEventShoutVariables(entry));

        var delay = TimeSpan.FromSeconds(entry.DelaySeconds);
        if (isManual)
            this.Chat.QueueManualMacroCommands(expandedMacro, delay);
        else
            this.Chat.QueueMacroCommands(expandedMacro, delay);
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
    public IReadOnlyList<UpcomingEventShout> GetUpcomingEventShouts(int limit = 8, DateTime? asOfUtc = null)
    {
        // Observation must not affect execution. The old preview called QueueDueShouts
        // and edited both scheduling dictionaries and the live FIFO queue. Keep the
        // same projection algorithm, but operate exclusively on local copies.
        limit = Math.Clamp(limit, 1, 20);
        var nowUtc = asOfUtc ?? DateTime.UtcNow;
        var shouts = (this.Configuration.EventShouts ?? [])
            .Where(entry => Configuration.HasUsableEventShout(entry) && entry.AutoEnabled)
            .OrderBy(entry => entry.Order)
            .ToList();
        if (shouts.Count == 0)
            return [];

        // Read the same event-derived limits used by the live scheduler.
        // Pre-event promotions may run earlier on event day (or the prior
        // evening for events that start shortly after midnight).
        var config = this.Configuration;
        if (!config.TryGetEventShoutWindow(out var eventStartUtc, out var eventEndUtc) ||
            nowUtc > eventEndUtc.Add(DueShoutGrace))
            return [];

        var projectedTimes = new Dictionary<int, DateTime>(this.nextShoutAtUtcByOrder);
        var projectedQueue = this.pendingEventShoutQueue
            .Where(order => shouts.Any(shout => shout.Order == order))
            .Distinct()
            .ToList();

        foreach (var shout in shouts)
        {
            if (!TryGetEffectiveShoutWindow(shout, eventStartUtc, eventEndUtc, nowUtc, out var shoutStartUtc, out var shoutEndUtc))
            {
                projectedTimes[shout.Order] = DateTime.MaxValue;
                continue;
            }

            if (!projectedTimes.TryGetValue(shout.Order, out var next) ||
                next <= DateTime.MinValue || next < shoutStartUtc || next > shoutEndUtc)
            {
                projectedTimes[shout.Order] = CalculateNextOccurrence(
                    nowUtc, shoutStartUtc, shoutEndUtc, TimeSpan.FromMinutes(shout.IntervalMinutes));
            }
        }

        // Reproduce the scheduler's due-time order, without adding to its real queue.
        var due = shouts
            .Where(shout => projectedTimes.TryGetValue(shout.Order, out var next) &&
                            next != DateTime.MaxValue && nowUtc >= next && !projectedQueue.Contains(shout.Order))
            .OrderBy(shout => projectedTimes[shout.Order])
            .ThenBy(shout => shout.Order);

        foreach (var shout in due)
            projectedQueue.Add(shout.Order);

        var result = new List<UpcomingEventShout>();
        var displayTime = nowUtc > this.nextEventShoutAllowedAtUtc ? nowUtc : this.nextEventShoutAllowedAtUtc;
        foreach (var order in projectedQueue)
        {
            var shout = shouts.FirstOrDefault(item => item.Order == order);
            if (shout is null || projectedTimes.GetValueOrDefault(order, DateTime.MaxValue) == DateTime.MaxValue)
                continue;

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
                     .Where(shout => !projectedQueue.Contains(shout.Order))
                     .Select(shout => new UpcomingEventShout(shout,
                         projectedTimes.TryGetValue(shout.Order, out var next) && next != DateTime.MaxValue
                             ? next : null))
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

    /// <summary>
    /// Read-only multi-occurrence forecast for the Overview. The existing preview
    /// supplies the first queued/scheduled occurrence per shout; further occurrences
    /// follow each row's interval boundaries. Never modifies timers or chat queues.
    /// Future dispatch may move when chat is busy, automation is paused, or settings change.
    /// </summary>
    public IReadOnlyList<UpcomingEventShout> GetUpcomingEventShoutForecast(DateTime nowUtc, int limit = 16)
    {
        limit = Math.Clamp(limit, 1, 20);
        var firstOccurrences = this.GetUpcomingEventShouts(20, nowUtc);
        if (firstOccurrences.Count == 0)
            return [];

        // Preview the next instances of recurring shouts without inserting them
        // into the real scheduler's per-order FIFO or time dictionaries.
        var config = this.Configuration;
        if (!config.TryGetEventShoutWindow(out var startUtc, out var endUtc))
            return [];

        var candidates = new List<(EventShoutEntry Shout, DateTime At, int Priority)>();
        var priority = 0;
        foreach (var item in firstOccurrences)
        {
            if (item.NextShoutAtUtc is not { } firstAt || firstAt < nowUtc)
                continue;

            // First instances retain the real FIFO estimate from the base preview.
            candidates.Add((item.Shout, firstAt, priority++));
            if (!TryGetEffectiveShoutWindow(item.Shout, startUtc, endUtc, nowUtc,
                    out var rowStart, out var rowEnd))
                continue;

            var interval = TimeSpan.FromMinutes(Math.Clamp(item.Shout.IntervalMinutes, 1, 240));
            // The first forecast occurrence might have been delayed by queue
            // staggering. Advance from its projected send time, aligning every
            // later candidate back to the row's interval grid.
            var next = CalculateNextOccurrence(firstAt.AddTicks(1), rowStart, rowEnd,
                interval, allowRecentlyDueOccurrence: false);
            for (var n = 0; n < limit && next != DateTime.MaxValue; n++)
            {
                candidates.Add((item.Shout, next, priority++));
                next = CalculateNextOccurrence(next.AddTicks(1), rowStart, rowEnd,
                    interval, allowRecentlyDueOccurrence: false);
            }
        }

        // Stagger adjacent announcements even when several recurrence schedules
        // collide. This is an ETA, not a promise that the chat command was sent.
        var results = new List<UpcomingEventShout>(limit);
        var lastSent = DateTime.MinValue;
        foreach (var candidate in candidates.OrderBy(item => item.At).ThenBy(item => item.Priority))
        {
            var at = candidate.At;
            if (lastSent != DateTime.MinValue && at < lastSent.Add(EventShoutQueueSpacing))
                at = lastSent.Add(EventShoutQueueSpacing);
            results.Add(new UpcomingEventShout(candidate.Shout, at));
            lastSent = at;
            if (results.Count == limit)
                break;
        }

        return results;
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
            .FirstOrDefault(candidate => candidate.End.Add(DueShoutGrace) >= nowUtc);

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
    private static DateTime CalculateNextOccurrence(
        DateTime nowUtc,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        TimeSpan interval,
        bool allowRecentlyDueOccurrence = true)
    {
        if (interval < TimeSpan.FromMinutes(1))
            interval = TimeSpan.FromMinutes(1);

        if (nowUtc <= windowStartUtc)
            return windowStartUtc;

        if (allowRecentlyDueOccurrence && nowUtc <= windowEndUtc.Add(DueShoutGrace))
        {
            var elapsedTicksForPrevious = Math.Max(0, (nowUtc - windowStartUtc).Ticks);
            var intervalTicksForPrevious = interval.Ticks;
            var previousSteps = elapsedTicksForPrevious / intervalTicksForPrevious;
            var previousCandidate = windowStartUtc.AddTicks(previousSteps * intervalTicksForPrevious);

            if (previousCandidate >= windowStartUtc &&
                previousCandidate <= windowEndUtc &&
                nowUtc <= previousCandidate.Add(DueShoutGrace))
            {
                return previousCandidate;
            }
        }

        if (nowUtc >= windowEndUtc)
            return DateTime.MaxValue;

        var elapsedTicks = (nowUtc - windowStartUtc).Ticks;
        var intervalTicks = interval.Ticks;
        var steps = (elapsedTicks + intervalTicks - 1) / intervalTicks;
        var candidate = windowStartUtc.AddTicks(steps * intervalTicks);

        return candidate <= windowEndUtc ? candidate : DateTime.MaxValue;
    }

    public readonly record struct UpcomingEventShout(EventShoutEntry Shout, DateTime? NextShoutAtUtc)
    {
        public string TimeText => this.NextShoutAtUtc?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "—";
    }
}
