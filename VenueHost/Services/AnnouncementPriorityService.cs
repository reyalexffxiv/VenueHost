using System;
using System.Collections.Generic;
using System.Linq;
using VenueHost.Models;

namespace VenueHost.Services;

/// <summary>Only arbitrates simultaneous automatic announcements. The existing
/// schedulers retain their own recurrence state, and GameChatService dispatches lines.</summary>
public sealed class AnnouncementPriorityService
{
    private readonly Configuration config;
    private readonly GameChatService chat;
    private readonly DjAutoShoutService dj;
    private readonly StaffAutoShoutService staff;
    private readonly EventShoutService events;
    private DateTime nextAllowedAtUtc = DateTime.MinValue;

    public AnnouncementPriorityService(Configuration config, GameChatService chat,
        DjAutoShoutService dj, StaffAutoShoutService staff, EventShoutService events)
    {
        this.config = config;
        this.chat = chat;
        this.dj = dj;
        this.staff = staff;
        this.events = events;
    }

    public void Reset() => this.nextAllowedAtUtc = DateTime.MinValue;

    public void Tick(bool djOn, bool staffOn, bool eventOn)
    {
        this.chat.SmartChannelsEnabled = this.config.SmartChatChannelsEnabled;
        // Prepare all categories first; a scheduler cannot win simply because
        // it was called before another scheduler in Plugin.OnFrameworkUpdate.
        if (djOn) this.dj.Update(dispatch: false);
        if (staffOn) this.staff.Update(dispatch: false);
        if (eventOn) this.events.Update(dispatch: false);

        var now = DateTime.UtcNow;
        if (this.chat.HasPendingCommands ||
            (this.config.AutoStaffRoleShoutStaggerEnabled && now < this.nextAllowedAtUtc))
            return;

        var candidates = new List<(string Key, DateTime Due, Action Send)>();
        if (djOn && this.dj.GetDuePriorityKey(now) is { } djKey && this.dj.NextShoutAtUtc is { } djDue)
            candidates.Add((djKey, djDue, () => this.dj.Update()));
        if (staffOn && this.staff.CanDispatchNow(now))
            foreach (var role in this.staff.PendingRoles.ToArray())
            {
                var due = this.staff.GetNextShoutAtUtc(role);
                if (due.HasValue && due <= now)
                    candidates.Add(($"staff:{role}", due.Value, () => this.staff.Update(requestedRole: role)));
            }
        if (eventOn && this.events.CanDispatchNow(now))
            foreach (var order in this.events.PendingOrders.ToArray())
            {
                var entry = this.config.EventShouts.FirstOrDefault(item => item.Order == order);
                if (entry is null) continue;
                var due = this.events.GetNextShoutAtUtc(entry);
                if (due.HasValue && due <= now)
                    candidates.Add(($"event:{entry.Id}", due.Value, () => this.events.Update(requestedOrder: order)));
            }
        // Priority only breaks collisions in the same clock minute. Older due
        // minutes retain precedence so a delayed shout never starves.
        var chosen = candidates.OrderBy(item => item.Due.Ticks / TimeSpan.TicksPerMinute)
            .ThenBy(item => this.config.PriorityOf(item.Key))
            .ThenBy(item => item.Due).FirstOrDefault();
        if (chosen.Send is null) return;
        chosen.Send();
        if (this.chat.HasPendingCommands && this.config.AutoStaffRoleShoutStaggerEnabled)
            this.nextAllowedAtUtc = now.AddMinutes(Math.Clamp(this.config.AutoStaffRoleShoutStaggerMinutes, 1, 30));
    }
}
