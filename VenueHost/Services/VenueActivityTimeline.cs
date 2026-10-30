using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VenueHost.Services;

/// <summary>Automatic outgoing shout categories (not shift changes).</summary>
public enum VenueActivityKind
{
    DjShout,
    StaffShout,
    EventShout,
}

/// <summary>A read-only estimate of an automatic outgoing chat shout.</summary>
public readonly record struct VenueActivity(
    VenueActivityKind Kind, DateTime TimeUtc, string Title, string Detail)
{
    public string KindLabel => this.Kind switch
    {
        VenueActivityKind.DjShout => "DJ",
        VenueActivityKind.StaffShout => "STAFF",
        _ => "EVENT",
    };

    public string TimeLabel => this.TimeUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " ST";
}

/// <summary>
/// Unifies the DJ, staff role, and custom event automatic shout forecasts.
/// Only forecasts configured automation; never sends chat, refreshes schedulers,
/// normalizes saved settings, or advances the real queued dispatch state.
/// This is an estimate, not a guaranteed chat send time.
/// </summary>
public static class VenueActivityTimeline
{
    public static IReadOnlyList<VenueActivity> Build(
        Configuration config, DjAutoShoutService djShouts,
        StaffAutoShoutService staffShouts, EventShoutService eventShouts,
        DateTime nowUtc, int limit = 16)
    {
        limit = Math.Clamp(limit, 1, 20);
        var activity = new List<VenueActivity>();

        if (config.AutoCurrentDjShoutEnabled)
            AddDjShouts(config, djShouts, nowUtc, activity);

        if (config.StaffAutomationMasterEnabled)
            AddStaffShouts(config, staffShouts, nowUtc, activity);

        if (config.EventAutomationMasterEnabled)
        {
            foreach (var shout in eventShouts.GetUpcomingEventShoutForecast(nowUtc, 20))
            {
                if (shout.NextShoutAtUtc is not { } time || time < nowUtc)
                    continue;
                var name = string.IsNullOrWhiteSpace(shout.Shout.Name)
                    ? "Event shout" : shout.Shout.Name.Trim();
                activity.Add(new VenueActivity(VenueActivityKind.EventShout, time, name,
                    "Automatic event announcement (estimated)"));
            }
        }

        return activity
            .Where(item => item.TimeUtc >= nowUtc)
            .OrderBy(item => item.TimeUtc)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    private static void AddDjShouts(Configuration config, DjAutoShoutService dj,
        DateTime nowUtc, List<VenueActivity> activity)
    {
        var slots = config.BuildEventTimeline()
            .Where(slot => slot.IsInsideEventWindow && slot.EndUtc > nowUtc)
            .OrderBy(slot => slot.StartUtc)
            .ThenBy(slot => slot.Entry.Order)
            .ToList();

        var nextRuntimeUtc = dj.NextShoutAtUtc;
        var interval = Math.Clamp(config.AutoCurrentDjShoutIntervalMinutes, 1, 120);
        // Respect the actual runtime timer when available: otherwise the preview
        // might re-display an already-sent scheduled shout.
        var earliest = nextRuntimeUtc.HasValue && nextRuntimeUtc.Value > nowUtc
            ? nextRuntimeUtc.Value : nowUtc;

        foreach (var slot in slots)
        {
            var hasRuntimeTimerInSlot = nextRuntimeUtc is { } runtime &&
                runtime >= nowUtc && runtime >= slot.StartUtc && runtime < slot.EndUtc;
            // A live timer can be delayed a few seconds by chat queueing or a
            // pause. Keep its exact ETA rather than silently jumping to the
            // next HH:mm interval boundary.
            var next = hasRuntimeTimerInSlot ? nextRuntimeUtc!.Value :
                AlignAtOrAfter(earliest, slot.StartUtc, interval);
            for (var count = 0; count < 20 && next < slot.EndUtc; count++)
            {
                var previous = config.GetPreviousDjBefore(slot.Entry);
                var isTransition = next >= slot.StartUtc &&
                    next < slot.StartUtc.AddMinutes(1) && previous is not null;
                var djName = slot.Entry.DJName?.Trim() ?? "DJ";
                var title = isTransition
                    ? $"{previous!.DJName} → {djName}"
                    : djName;
                activity.Add(new VenueActivity(VenueActivityKind.DjShout, next, title,
                    isTransition ? "DJ transition shout (estimated)" : "Current DJ shout (estimated)"));
                next = AlignAtOrAfter(next.AddTicks(1), slot.StartUtc, interval);
            }
        }
    }

    private static void AddStaffShouts(Configuration config, StaffAutoShoutService staff,
        DateTime nowUtc, List<VenueActivity> activity)
    {
        // Use the saved role settings directly rather than Ensure* helpers:
        // building an Overview must never create/alter persisted role settings.
        var enabledRoles = (config.StaffRoleShoutSettings ?? [])
            .Where(setting => setting.AutoEnabled && !string.IsNullOrWhiteSpace(setting.Role))
            .GroupBy(setting => setting.Role.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (enabledRoles.Count == 0)
            return;

        var staffSlots = config.BuildStaffScheduleTimeline()
            .Where(slot => slot.Entry.Available && slot.IsInsideEventWindow && slot.EndUtc > nowUtc)
            .ToList();
        var staffCandidates = new List<VenueActivity>();
        foreach (var setting in enabledRoles)
        {
            var role = setting.Role.Trim();
            var slots = staffSlots
                .Where(slot => string.Equals(slot.Entry.Role?.Trim(), role, StringComparison.OrdinalIgnoreCase))
                .OrderBy(slot => slot.StartUtc)
                .ToList();
            if (slots.Count == 0)
                continue;

            var interval = Math.Clamp(setting.IntervalMinutes, 1, 120);
            var nextRuntimeUtc = staff.GetNextShoutAtUtc(role);
            var earliest = nextRuntimeUtc.HasValue && nextRuntimeUtc.Value > nowUtc
                ? nextRuntimeUtc.Value : nowUtc;

            // A shout is eligible only while its role actually has staff on duty.
            // Merge overlapping shifts of the same role so staff count doesn't
            // cause duplicate role-level announcements.
            var merged = new List<(DateTime Start, DateTime End)>();
            foreach (var slot in slots)
            {
                if (merged.Count > 0 && slot.StartUtc <= merged[^1].End)
                {
                    var last = merged[^1];
                    merged[^1] = (last.Start, slot.EndUtc > last.End ? slot.EndUtc : last.End);
                }
                else
                    merged.Add((slot.StartUtc, slot.EndUtc));
            }

            foreach (var shift in merged)
            {
                var hasRuntimeTimerInShift = nextRuntimeUtc is { } runtime &&
                    runtime >= nowUtc && runtime >= shift.Start && runtime < shift.End;
                var next = hasRuntimeTimerInShift ? nextRuntimeUtc!.Value :
                    AlignAtOrAfter(earliest, shift.Start, interval);
                for (var count = 0; count < 20 && next < shift.End; count++)
                {
                    staffCandidates.Add(new VenueActivity(VenueActivityKind.StaffShout, next,
                        role, "Automatic staff role shout (estimated)"));
                    next = AlignAtOrAfter(next.AddTicks(1), shift.Start, interval);
                }
            }
        }

        // Staff role shouts have their own optional shared stagger. Apply a
        // conservative read-only ETA when multiple roles are close together.
        // DJ/event shouts use separate queues and are not delayed here.
        if (config.AutoStaffRoleShoutStaggerEnabled)
        {
            var stagger = TimeSpan.FromMinutes(Math.Clamp(config.AutoStaffRoleShoutStaggerMinutes, 1, 30));
            var lastProjected = DateTime.MinValue;
            foreach (var candidate in staffCandidates
                         .OrderBy(item => item.TimeUtc)
                         .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase))
            {
                var at = candidate.TimeUtc;
                if (lastProjected != DateTime.MinValue && at < lastProjected.Add(stagger))
                    at = lastProjected.Add(stagger);
                activity.Add(candidate with { TimeUtc = at });
                lastProjected = at;
            }
        }
        else
            activity.AddRange(staffCandidates);
    }

    private static DateTime AlignAtOrAfter(DateTime earliest, DateTime startsAt, int intervalMinutes)
    {
        if (earliest <= startsAt)
            return startsAt;
        var intervalTicks = TimeSpan.FromMinutes(intervalMinutes).Ticks;
        var elapsed = earliest.Ticks - startsAt.Ticks;
        var steps = (elapsed + intervalTicks - 1) / intervalTicks;
        return startsAt.AddTicks(steps * intervalTicks);
    }
}
