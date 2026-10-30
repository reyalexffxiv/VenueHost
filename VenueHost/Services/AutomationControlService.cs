using System;
using System.Linq;

namespace VenueHost.Services;

/// <summary>
/// Runtime pause and persisted master-enable controls. Never refresh an unrelated scheduler.
/// This service is intentionally independent of the UI rendering lifecycle.
/// </summary>
public sealed class AutomationControlService
{
    private readonly Configuration config;
    private readonly DjAutoShoutService dj;
    private readonly StaffAutoShoutService staff;
    private readonly EventShoutService events;
    private readonly GameChatService chat;
    private DateTime? pausedAtUtc;
    private DateTime? staffOffAtUtc;
    private DateTime? eventOffAtUtc;
    // If an individual category is re-enabled while Pause All is active,
    // defer the schedule shift until the global pause ends (never count overlap twice).
    private TimeSpan staffPrePauseHold;
    private TimeSpan eventPrePauseHold;

    public AutomationControlService(Configuration config, DjAutoShoutService dj,
        StaffAutoShoutService staff, EventShoutService events, GameChatService chat)
    {
        this.config = config;
        this.dj = dj;
        this.staff = staff;
        this.events = events;
        this.chat = chat;

        // Each Dalamud enable/load starts with automation globally paused.
        // Do not persist or alter the DJ/Staff/Event enabled flags: only an
        // explicit START ALL resumes automatic schedulers and automatic dispatch.
        // Manual chat remains available while PAUSE ALL is active.
        this.pausedAtUtc = DateTime.UtcNow;
        this.chat.PauseAndCancelPending();
        this.LastControlMessage = "Automatic announcements paused on plugin load. Manual shouts remain available.";
    }

    public bool Paused => this.pausedAtUtc.HasValue;
    public bool DjAvailable => this.config.DjSchedule?.Any(Configuration.HasUsableDj) == true;
    public bool StaffAvailable => this.config.IsAnyStaffAutoShoutEnabled();
    public bool EventAvailable => this.config.EventShouts?.Any(item =>
        item.AutoEnabled && Configuration.HasUsableEventShout(item)) == true;

    public bool DjOn => this.config.AutoCurrentDjShoutEnabled && this.DjAvailable;
    public bool StaffOn => this.config.StaffAutomationMasterEnabled && this.StaffAvailable;
    public bool EventOn => this.config.EventAutomationMasterEnabled && this.EventAvailable;
    // All UI statuses and the global control reflect execution, not merely
    // the saved master switches. PauseAll blocks automatic dispatch.
    public bool DjRunning => !this.Paused && this.DjOn;
    public bool StaffRunning => !this.Paused && this.StaffOn;
    public bool EventRunning => !this.Paused && this.EventOn;
    public bool AnyRunning => this.DjRunning || this.StaffRunning || this.EventRunning;

    public string LastControlMessage { get; private set; } = "Double-click an automation state to toggle it.";

    public void ToggleDj()
    {
        if (!this.DjAvailable)
        {
            this.LastControlMessage = "No DJs scheduled";
            return;
        }
        if (this.Paused)
        {
            this.StartOnly(AutomationCategory.Dj);
            return;
        }
        this.config.AutoCurrentDjShoutEnabled = !this.config.AutoCurrentDjShoutEnabled;
        this.dj.Refresh(); // DJ only; never reset Event Shouts.
        this.config.Save();
        this.LastControlMessage = this.config.AutoCurrentDjShoutEnabled ? "DJ automation enabled." : "DJ automation disabled.";
    }

    public void ToggleStaff()
    {
        if (this.Paused)
        {
            if (!this.StaffAvailable)
            {
                this.LastControlMessage = "No staff role auto enabled";
                return;
            }
            this.StartOnly(AutomationCategory.Staff);
            return;
        }
        if (!this.config.StaffAutomationMasterEnabled || !this.config.IsAnyStaffAutoShoutEnabled())
        {
            // Preserve per-role selections. No silent enabling of all roles.
            if (!this.config.IsAnyStaffAutoShoutEnabled())
            {
                this.LastControlMessage = "No staff role auto enabled";
                return;
            }
            this.config.StaffAutomationMasterEnabled = true;
            var now = DateTime.UtcNow;
            if (this.Paused)
            {
                if (this.staffOffAtUtc.HasValue && this.pausedAtUtc.HasValue &&
                    this.staffOffAtUtc.Value < this.pausedAtUtc.Value)
                    this.staffPrePauseHold += this.pausedAtUtc.Value - this.staffOffAtUtc.Value;
            }
            else this.staff.ResumeAfterPause(now, now - (this.staffOffAtUtc ?? now));
            this.staffOffAtUtc = null;
        }
        else
        {
            this.config.StaffAutomationMasterEnabled = false;
            this.staffOffAtUtc = this.pausedAtUtc ?? DateTime.UtcNow;
        }
        this.config.Save();
        this.LastControlMessage = this.config.StaffAutomationMasterEnabled ? "Staff automation enabled." : "Staff automation disabled.";
    }

    /// <summary>Role automation lives in Staff Announcements; master activation stays in the footer.</summary>
    public void SetStaffRoleAuto(string role, bool enabled, int intervalMinutes, bool fromToggle = false)
    {
        var setting = this.config.GetOrCreateStaffRoleShoutSetting(role);
        var interval = Math.Clamp(intervalMinutes, 1, 120);
        if (setting.AutoEnabled == enabled && setting.IntervalMinutes == interval &&
            !(fromToggle && enabled && !this.StaffRunning))
            return;

        if (setting.AutoEnabled && !enabled)
            this.chat.CancelPendingAutomaticStaffRole(role);
        setting.AutoEnabled = enabled;
        setting.IntervalMinutes = interval;
        if (fromToggle && enabled)
        {
            if (this.Paused) this.StartOnly(AutomationCategory.Staff);
            else this.config.StaffAutomationMasterEnabled = true;
        }
        if (role.Equals("Photographer", StringComparison.OrdinalIgnoreCase))
        {
            this.config.AutoPhotographerShoutEnabled = enabled;
            this.config.AutoPhotographerShoutIntervalMinutes = interval;
        }
        if (!this.StaffAvailable && this.config.StaffAutomationMasterEnabled)
        {
            this.config.StaffAutomationMasterEnabled = false;
            this.staffOffAtUtc = this.pausedAtUtc ?? DateTime.UtcNow;
        }
        this.staff.Refresh();
        this.config.Save();
    }

    public void ToggleEvent()
    {
        if (this.Paused)
        {
            if (!this.EventAvailable)
            {
                this.LastControlMessage = "No event shouts auto enabled";
                return;
            }
            this.StartOnly(AutomationCategory.Event);
            return;
        }
        if (!this.config.EventAutomationMasterEnabled || !this.EventOn)
        {
            if (this.config.EventShouts?.Any(item => item.AutoEnabled && Configuration.HasUsableEventShout(item)) != true)
            {
                this.LastControlMessage = "No event shouts auto enabled";
                return;
            }
            this.config.EventAutomationMasterEnabled = true;
            var now = DateTime.UtcNow;
            if (this.Paused)
            {
                if (this.eventOffAtUtc.HasValue && this.pausedAtUtc.HasValue &&
                    this.eventOffAtUtc.Value < this.pausedAtUtc.Value)
                    this.eventPrePauseHold += this.pausedAtUtc.Value - this.eventOffAtUtc.Value;
            }
            else this.events.ResumeAfterPause(now, now - (this.eventOffAtUtc ?? now));
            this.eventOffAtUtc = null;
        }
        else
        {
            this.config.EventAutomationMasterEnabled = false;
            this.eventOffAtUtc = this.pausedAtUtc ?? DateTime.UtcNow;
        }
        this.config.Save();
        this.LastControlMessage = this.config.EventAutomationMasterEnabled ? "Event automation enabled." : "Event automation disabled.";
    }

    /// <summary>Hard pause for profile changes. No automatic or manual backlog can cross venues.</summary>
    public void PauseForProfileSwitch()
    {
        this.chat.ClearAllForProfileSwitch();
        this.pausedAtUtc = DateTime.UtcNow;
        this.staffOffAtUtc = null;
        this.eventOffAtUtc = null;
        this.staffPrePauseHold = TimeSpan.Zero;
        this.eventPrePauseHold = TimeSpan.Zero;
        this.dj.Refresh();
        this.staff.Refresh();
        this.events.Refresh();
        this.LastControlMessage = "Venue profile switched. Automation paused; press START ALL when ready.";
    }

    private enum AutomationCategory { Dj, Staff, Event }

    // From a globally paused session, starting ONE category must not silently
    // resume the others that were saved as enabled before the pause.
    private void StartOnly(AutomationCategory category)
    {
        this.config.AutoCurrentDjShoutEnabled = category == AutomationCategory.Dj && this.DjAvailable;
        this.config.StaffAutomationMasterEnabled = category == AutomationCategory.Staff && this.StaffAvailable;
        this.config.EventAutomationMasterEnabled = category == AutomationCategory.Event && this.EventAvailable;
        this.ResumeAutomaticDispatch();
        // Scheduler timers have already been shifted by ResumeAutomaticDispatch.
        // Refreshing every scheduler here would discard unrelated intervals.
        this.config.Save();
        this.LastControlMessage = $"{category} automation started.";
    }

    // A row's AUTO ON must activate its category, too. It may already have
    // enabled subitems but a disabled master (the old IDLE/ON mismatch).
    public void StartEventFromRow()
    {
        if (this.Paused) this.StartOnly(AutomationCategory.Event);
        else
        {
            this.config.EventAutomationMasterEnabled = true;
            this.config.Save();
        }
    }

    private void ResumeAutomaticDispatch()
    {
        if (!this.pausedAtUtc.HasValue) return;
        var now = DateTime.UtcNow;
        var elapsed = now - this.pausedAtUtc.Value;
        if (this.DjOn) this.dj.ResumeAfterPause(now, elapsed);
        if (this.StaffOn) this.staff.ResumeAfterPause(now, elapsed + this.staffPrePauseHold);
        if (this.EventOn) this.events.ResumeAfterPause(now, elapsed + this.eventPrePauseHold);
        this.staffPrePauseHold = TimeSpan.Zero;
        this.eventPrePauseHold = TimeSpan.Zero;
        this.pausedAtUtc = null;
        this.chat.ResumeDispatch();
    }

    // Double-click in the footer: start all configured categories, or pause
    // all automatic dispatch. Manual chat is never blocked by this control.
    public void TogglePauseAll()
    {
        if (this.AnyRunning)
        {
            var discarded = this.chat.PauseAndCancelPending();
            this.pausedAtUtc = DateTime.UtcNow;
            this.LastControlMessage = $"Automatic announcements paused. {discarded} queued automatic commands discarded.";
        }
        else
        {
            var wasPaused = this.Paused;
            var djWasOn = this.DjOn;
            var staffWasOn = this.StaffOn;
            var eventWasOn = this.EventOn;
            this.config.AutoCurrentDjShoutEnabled = this.DjAvailable;
            this.config.StaffAutomationMasterEnabled = this.StaffAvailable;
            this.config.EventAutomationMasterEnabled = this.EventAvailable;
            this.ResumeAutomaticDispatch();
            // Only newly enabled categories need their schedules initialized.
            // A true pause/resume already shifts the existing timers.
            if (!wasPaused)
            {
                if (!djWasOn && this.DjOn) this.dj.Refresh();
                if (!staffWasOn && this.StaffOn) this.staff.Refresh();
                if (!eventWasOn && this.EventOn) this.events.Refresh();
            }
            this.config.Save();
            this.LastControlMessage = "Configured automatic announcements started.";
        }
    }
}
