using System;
using VenueHost.Models;

namespace VenueHost.Services;

/// <summary>
/// Expands Venue Host macros and queues the resulting chat commands.
/// </summary>
/// <remarks>
/// This service owns shout output behavior. Timing decisions live in the dedicated
/// auto-shout services, which call the explicit overloads here when a shout is due.
/// Keeping scheduling and output separate makes each path easier to verify in Dalamud.
/// </remarks>
public sealed class ShoutService : BaseService
{
    public ShoutService(Configuration configuration, IServiceContext services)
        : base(configuration, services)
    {
    }

    private GameChatService Chat => this.Services.Get<GameChatService>();

    /// <summary>Queues the current-DJ macro for the DJ performing right now.</summary>
    public void SendCurrentDjShout()
    {
        this.SendCurrentDjShoutFor(this.Configuration.GetCurrentServerTimeDj(DateTime.UtcNow), isManual: true);
    }

    /// <summary>Queues the current-DJ macro for a specific DJ row.</summary>
    /// <remarks>
    /// Auto-shout scheduling can call this overload without duplicating macro
    /// expansion logic. The method intentionally no-ops when the DJ row is empty.
    /// </remarks>
    public void SendCurrentDjShoutFor(DjScheduleEntry? currentDj, bool isManual = false)
    {
        if (!HasUsableDj(currentDj))
            return;

        var expandedMacro = MacroVariableService.Expand(
            this.Configuration.CurrentDjMacro,
            this.Configuration.BuildVariables(currentDj));

        this.QueueShoutMacro(expandedMacro, this.Configuration.CurrentDjMacroDelaySeconds, isManual);
    }

    /// <summary>Queues the handoff from the live DJ to the next scheduled DJ.</summary>
    public void SendTransitionShout()
    {
        var nowUtc = DateTime.UtcNow;
        var currentDj = this.Configuration.GetCurrentServerTimeDj(nowUtc);
        var nextDj = this.Configuration.GetNextServerTimeDj(nowUtc);
        this.SendTransitionShoutFor(currentDj, nextDj, isManual: true);
    }

    /// <summary>Queues the transition macro for a specific outgoing and incoming DJ pair.</summary>
    public void SendTransitionShoutFor(DjScheduleEntry? endingDj, DjScheduleEntry? incomingDj, bool isManual = false)
    {
        if (!HasUsableDj(endingDj) || !HasUsableDj(incomingDj))
            return;

        var expandedMacro = MacroVariableService.Expand(
            this.Configuration.TransitionMacro,
            this.Configuration.BuildVariables(endingDj, incomingDj));

        this.QueueShoutMacro(expandedMacro, this.Configuration.TransitionMacroDelaySeconds, isManual);
    }

    /// <summary>Queues the configured tell macro for the live DJ.</summary>
    public void TellCurrentDjToTarget()
    {
        var currentDj = this.Configuration.GetCurrentServerTimeDj(DateTime.UtcNow);
        if (!HasUsableDj(currentDj))
            return;

        var expandedMacro = MacroVariableService.Expand(
            this.Configuration.TellCurrentDjMacro,
            this.Configuration.BuildVariables(currentDj));

        this.QueueShoutMacro(expandedMacro, this.Configuration.TellCurrentDjMacroDelaySeconds, isManual: true);
    }

    /// <summary>Queues the role macro for the currently selected staff role.</summary>
    public void SendSelectedStaffRoleShout()
    {
        this.SendStaffRoleShout(this.Configuration.SelectedStaffRole, isManual: true);
    }

    /// <summary>Queues the role macro for active staff in the supplied role.</summary>
    public void SendStaffRoleShout(string? role, bool isManual = false)
    {
        var nowUtc = DateTime.UtcNow;
        role = string.IsNullOrWhiteSpace(role) ? "Photographer" : role.Trim();
        if (this.Configuration.GetCurrentServerTimeStaffForRole(nowUtc, role).Count == 0)
            return;

        var expandedMacro = MacroVariableService.Expand(
            this.Configuration.GetStaffShoutMacro(role),
            this.Configuration.BuildStaffRoleVariables(role, nowUtc));

        this.QueueShoutMacro(expandedMacro, this.Configuration.GetStaffShoutDelaySeconds(role), isManual,
            isManual ? null : role);
    }

    public string PreviewCurrentDjShout()
    {
        return MacroVariableService.Expand(this.Configuration.CurrentDjMacro, this.Configuration.BuildVariables());
    }

    public string PreviewTransitionShout()
    {
        return MacroVariableService.Expand(this.Configuration.TransitionMacro, this.Configuration.BuildVariables());
    }

    public string PreviewSelectedStaffRoleShout()
    {
        var role = this.Configuration.SelectedStaffRole;
        return MacroVariableService.Expand(
            this.Configuration.GetStaffShoutMacro(role),
            this.Configuration.BuildStaffRoleVariables(role));
    }

    /// <summary>Keep manual and automatic macros on explicit dispatch paths.</summary>
    private void QueueShoutMacro(string macro, float delaySeconds, bool isManual, string? staffRole = null)
    {
        var delay = TimeSpan.FromSeconds(delaySeconds);
        if (isManual)
            this.Chat.QueueManualMacroCommands(macro, delay);
        else
            this.Chat.QueueMacroCommands(macro, delay, staffRole);
    }

    private static bool HasUsableDj(DjScheduleEntry? entry)
    {
        return entry is not null && !string.IsNullOrWhiteSpace(entry.DJName);
    }
}
