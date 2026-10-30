using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VenueHost.Models;

namespace VenueHost.Services;

/// <summary>
/// Read-only projection of saved venue schedules for the Backstage dashboard.
/// Does not normalize config, save it, send chat, or advance any scheduler.
/// All dates use FFXIV server time (UTC).
/// </summary>
public sealed class VenueOverviewSnapshot
{
    public string EventTitle { get; private init; } = "Untitled event";
    public string Venue { get; private init; } = "Venue not configured";
    public string EventState { get; private init; } = "No valid event window";
    public string EventWindow { get; private init; } = "Set event start and end times";
    public string CurrentDj { get; private init; } = "No DJ on stage";
    public string CurrentDjTime { get; private init; } = "No active slot";
    public string NextDj { get; private init; } = "None scheduled";
    public string NextDjTime { get; private init; } = "";
    public IReadOnlyList<string> ActiveStaff { get; private init; } = [];
    // Ordered role/count pairs drive the Overview list without string parsing.
    public IReadOnlyList<KeyValuePair<string, int>> ActiveStaffRoleCounts { get; private init; }
        = Array.Empty<KeyValuePair<string, int>>();
    public int ScheduledDjCount { get; private init; }
    public int ScheduledStaffCount { get; private init; }

    public static VenueOverviewSnapshot Build(Configuration config, DateTime nowUtc)
    {
        var venue = string.IsNullOrWhiteSpace(config.VenueName) ? "Venue not configured" : config.VenueName.Trim();
        var title = string.IsNullOrWhiteSpace(config.EventName) ? "Untitled event" : config.EventName.Trim();
        var djCount = (config.DjSchedule ?? []).Count(Configuration.HasUsableDj);
        var staffCount = (config.StaffSchedule ?? []).Count(Configuration.HasUsableStaffScheduleEntry);

        if (!TryWindow(config.EventStartDate, config.EventStartTime, config.EventEndDate, config.EventEndTime,
                       out var eventStart, out var eventEnd))
        {
            return new VenueOverviewSnapshot
            {
                Venue = venue, EventTitle = title,
                ScheduledDjCount = djCount, ScheduledStaffCount = staffCount,
            };
        }

        var state = nowUtc < eventStart ? "UPCOMING" : nowUtc >= eventEnd ? "FINISHED" : "LIVE";
        var slots = new List<(string Name, DateTime Start, DateTime End)>();
        foreach (var entry in config.DjSchedule ?? [])
        {
            if (!Configuration.HasUsableDj(entry) ||
                !Configuration.TryParseServerTime(entry.StartTime, out var startTime) ||
                !Configuration.TryParseServerTime(entry.EndTime, out var endTime) || startTime == endTime)
                continue;

            var start = eventStart.Date.Add(startTime);
            while (start < eventStart)
                start = start.AddDays(1);
            var end = start.Date.Add(endTime);
            if (end <= start)
                end = end.AddDays(1);
            if (start >= eventStart && end <= eventEnd)
                slots.Add((entry.DJName, start, end));
        }

        var current = slots.Where(slot => slot.Start <= nowUtc && nowUtc < slot.End)
            .OrderBy(slot => slot.Start).FirstOrDefault();
        var next = slots.Where(slot => slot.Start > nowUtc).OrderBy(slot => slot.Start).FirstOrDefault();
        var activeStaff = new List<string>();
        var activeStaffRoles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Staff and DJs share the same event boundary. No hidden staff-only
        // dates may contradict the live Staff on Duty display.
        {
            var staffStart = eventStart;
            var staffEnd = eventEnd;
            foreach (var entry in config.StaffSchedule ?? [])
            {
                if (!entry.Available || !Configuration.HasUsableStaffScheduleEntry(entry) ||
                    !Configuration.TryParseServerTime(entry.StartTime, out var startTime) ||
                    !Configuration.TryParseServerTime(entry.EndTime, out var endTime) || startTime == endTime)
                    continue;

                var start = staffStart.Date.Add(startTime);
                while (start < staffStart)
                    start = start.AddDays(1);
                var end = start.Date.Add(endTime);
                if (end <= start)
                    end = end.AddDays(1);
                if (start >= staffStart && end <= staffEnd && nowUtc >= start && nowUtc < end)
                {
                    activeStaff.Add($"{entry.Name}  ·  {entry.Role}");
                    var role = string.IsNullOrWhiteSpace(entry.Role) ? "Unassigned" : entry.Role.Trim();
                    activeStaffRoles[role] = activeStaffRoles.TryGetValue(role, out var count) ? count + 1 : 1;
                }
            }
        }

        var sortedStaffRoles = activeStaffRoles
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new VenueOverviewSnapshot
        {
            Venue = venue,
            EventTitle = title,
            EventState = state,
            EventWindow = $"{eventStart:ddd dd MMM HH:mm} - {eventEnd:ddd dd MMM HH:mm} ST",
            CurrentDj = current.Name ?? "No DJ on stage",
            CurrentDjTime = current.Name is not null ? $"{current.Start:HH:mm} - {current.End:HH:mm} ST" : "No active slot",
            NextDj = next.Name ?? "None scheduled",
            NextDjTime = next.Name is not null ? $"{next.Start:HH:mm} - {next.End:HH:mm} ST" : "No upcoming slot",
            ActiveStaff = activeStaff.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
            ActiveStaffRoleCounts = sortedStaffRoles,
            ScheduledDjCount = djCount,
            ScheduledStaffCount = staffCount,
        };
    }

    internal static bool TryWindow(string startDate, string startTime, string endDate, string endTime,
                                  out DateTime start, out DateTime end)
    {
        var format = "yyyy-MM-dd HH:mm";
        var style = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        var validStart = DateTime.TryParseExact($"{startDate} {startTime}", format,
            CultureInfo.InvariantCulture, style, out start);
        var validEnd = DateTime.TryParseExact($"{endDate} {endTime}", format,
            CultureInfo.InvariantCulture, style, out end);
        return validStart && validEnd && end > start;
    }
}
