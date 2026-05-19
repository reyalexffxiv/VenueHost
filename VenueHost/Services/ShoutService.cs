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

    /// <summary>Queues the current-DJ macro for the currently selected DJ row.</summary>
    public void SendCurrentDjShout()
    {
        this.SendCurrentDjShoutFor(this.Configuration.GetSelectedDj());
    }

    /// <summary>Queues the current-DJ macro for a specific DJ row.</summary>
    /// <remarks>
    /// Auto-shout scheduling can call this overload without duplicating macro
    /// expansion logic. The method intentionally no-ops when the DJ row is empty.
    /// </remarks>
    public void SendCurrentDjShoutFor(DjScheduleEntry? currentDj)
    {
        if (!HasUsableDj(currentDj))
            return;

        var expandedMacro = MacroVariableService.Expand(
            this.Configuration.CurrentDjMacro,
            this.Configuration.BuildVariables(currentDj));

        this.Chat.QueueMacroCommands(expandedMacro, TimeSpan.FromSeconds(this.Configuration.CurrentDjMacroDelaySeconds));
    }

    /// <summary>Queues the transition macro from the selected DJ to the next scheduled DJ.</summary>
    public void SendTransitionShout()
    {
        var currentDj = this.Configuration.GetSelectedDj();
        var nextDj = this.Configuration.GetNextDjAfter(currentDj);
        this.SendTransitionShoutFor(currentDj, nextDj);
    }

    /// <summary>Queues the transition macro for a specific outgoing and incoming DJ pair.</summary>
    public void SendTransitionShoutFor(DjScheduleEntry? endingDj, DjScheduleEntry? incomingDj)
    {
        if (!HasUsableDj(endingDj) || !HasUsableDj(incomingDj))
            return;

        var expandedMacro = MacroVariableService.Expand(
            this.Configuration.TransitionMacro,
            this.Configuration.BuildVariables(endingDj, incomingDj));

        this.Chat.QueueMacroCommands(expandedMacro, TimeSpan.FromSeconds(this.Configuration.TransitionMacroDelaySeconds));
    }

    /// <summary>Queues the configured tell macro for the currently selected DJ.</summary>
    public void TellCurrentDjToTarget()
    {
        var currentDj = this.Configuration.GetSelectedDj();
        if (!HasUsableDj(currentDj))
            return;

        var expandedMacro = MacroVariableService.Expand(
            this.Configuration.TellCurrentDjMacro,
            this.Configuration.BuildVariables(currentDj));

        this.Chat.QueueMacroCommands(expandedMacro, TimeSpan.FromSeconds(this.Configuration.TellCurrentDjMacroDelaySeconds));
    }

    /// <summary>Queues the role macro for the currently selected staff role.</summary>
    public void SendSelectedStaffRoleShout()
    {
        this.SendStaffRoleShout(this.Configuration.SelectedStaffRole);
    }

    /// <summary>Queues the role macro for active staff in the supplied role.</summary>
    public void SendStaffRoleShout(string? role)
    {
        var nowUtc = DateTime.UtcNow;
        role = string.IsNullOrWhiteSpace(role) ? "Photographer" : role.Trim();
        if (this.Configuration.GetCurrentServerTimeStaffForRole(nowUtc, role).Count == 0)
            return;

        var expandedMacro = MacroVariableService.Expand(
            this.Configuration.GetStaffShoutMacro(role),
            this.Configuration.BuildStaffRoleVariables(role, nowUtc));

        this.Chat.QueueMacroCommands(expandedMacro, TimeSpan.FromSeconds(this.Configuration.GetStaffShoutDelaySeconds(role)));
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

    private static bool HasUsableDj(DjScheduleEntry? entry)
    {
        return entry is not null && !string.IsNullOrWhiteSpace(entry.DJName);
    }
}
