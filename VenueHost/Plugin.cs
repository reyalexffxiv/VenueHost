using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using VenueHost.Services;
using VenueHost.Windows;

namespace VenueHost;

/// <summary>
/// Dalamud plugin entry point. This wires together config, UI windows, commands, and queued chat sending.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    private static readonly string[] CommandNames = ["/venuehost", "/vhost"];

    public Configuration Configuration { get; }

    public GameChatService GameChatService { get; }

    public ISharedImmediateTexture? MascotTexture { get; }

    public readonly WindowSystem WindowSystem = new("VenueHost");

    private MainWindow MainWindow { get; }

    private ConfigWindow ConfigWindow { get; }

    private DjDatabaseWindow DjDatabaseWindow { get; }

    private DateTime nextAutoCurrentDjShoutAtUtc = DateTime.MinValue;

    private bool previousAutoCurrentDjShoutEnabled;

    private int previousAutoCurrentDjShoutIntervalMinutes;

    private bool autoSawUsableDjSinceEnabled;

    private int? lastAutoDjOrderSeen;

    public string AutoShoutStatus { get; private set; } = "Disabled.";

    public DateTime? NextAutoShoutAtUtc => this.nextAutoCurrentDjShoutAtUtc == DateTime.MinValue || this.nextAutoCurrentDjShoutAtUtc == DateTime.MaxValue
        ? null
        : this.nextAutoCurrentDjShoutAtUtc;

    public Plugin()
    {
        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Configuration.Initialize(PluginInterface);
        this.previousAutoCurrentDjShoutEnabled = this.Configuration.AutoCurrentDjShoutEnabled;
        this.previousAutoCurrentDjShoutIntervalMinutes = this.Configuration.AutoCurrentDjShoutIntervalMinutes;

        if (this.Configuration.CleanLegacyManualWaitLinesOnce())
        {
            this.Configuration.Save();
            Log.Information("Cleaned legacy manual wait-only lines from Venue Host macros.");
        }

        this.GameChatService = new GameChatService(Log);
        this.MascotTexture = LoadMascotTexture();
        this.MainWindow = new MainWindow(this);
        this.ConfigWindow = new ConfigWindow(this);
        this.DjDatabaseWindow = new DjDatabaseWindow(this);

        this.WindowSystem.AddWindow(this.MainWindow);
        this.WindowSystem.AddWindow(this.ConfigWindow);
        this.WindowSystem.AddWindow(this.DjDatabaseWindow);

        foreach (var commandName in CommandNames)
        {
            CommandManager.AddHandler(commandName, new CommandInfo(this.OnCommand)
            {
                HelpMessage = "Open Venue Host. Use 'settings' to open the settings window.",
            });
        }

        Framework.Update += this.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += this.WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += this.ToggleMainUi;

        Log.Information("Venue Host loaded.");
    }


    private static ISharedImmediateTexture? LoadMascotTexture()
    {
        try
        {
            return TextureProvider.GetFromManifestResource(Assembly.GetExecutingAssembly(), "VenueHost.Assets.cute_peepo.png");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load Venue Host mascot texture.");
            return null;
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= this.WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleMainUi;
        Framework.Update -= this.OnFrameworkUpdate;

        foreach (var commandName in CommandNames)
            CommandManager.RemoveHandler(commandName);

        this.WindowSystem.RemoveAllWindows();
        this.DjDatabaseWindow.Dispose();
        this.ConfigWindow.Dispose();
        this.MainWindow.Dispose();
    }

    public void ToggleMainUi() => this.MainWindow.Toggle();

    public void ToggleConfigUi() => this.ConfigWindow.Toggle();

    public void ToggleDjDatabaseUi() => this.DjDatabaseWindow.Toggle();

    /// <summary>
    /// Recalculates the next automatic shout from the current UTC/server time.
    /// Useful after staff edit the DJ schedule while auto-shout is already enabled.
    /// </summary>
    public void RefreshAutoShoutSchedule()
    {
        this.nextAutoCurrentDjShoutAtUtc = DateTime.MinValue;
        this.previousAutoCurrentDjShoutEnabled = false;

        this.AutoShoutStatus = this.Configuration.AutoCurrentDjShoutEnabled
            ? "Schedule refreshed. Next shout will be recalculated from the DJ lineup."
            : "Disabled.";

    }


    public string GetNextAutoShoutTypeDescription()
    {
        if (!this.Configuration.AutoCurrentDjShoutEnabled)
            return "Disabled";

        if (!this.NextAutoShoutAtUtc.HasValue)
            return "Waiting for valid schedule";

        var targetDj = this.Configuration.GetCurrentServerTimeDj(this.NextAutoShoutAtUtc.Value);
        if (targetDj is null)
            return "No DJ found for next shout";

        var previousDj = this.Configuration.GetPreviousDjBefore(targetDj);
        var targetSlot = this.Configuration.GetTimelineSlotForEntry(targetDj);
        if (previousDj is not null && targetSlot is not null && IsScheduledSlotStart(this.NextAutoShoutAtUtc.Value, targetSlot))
            return $"Transition, {previousDj.DJName} → {targetDj.DJName}";

        return $"Current DJ shout, {targetDj.DJName}";
    }

    public void SendCurrentDjShout()
    {
        this.SendCurrentDjShoutFor(this.Configuration.GetSelectedDj());
    }

    private void SendCurrentDjShoutFor(Models.DjScheduleEntry? currentDj)
    {
        if (!HasUsableDj(currentDj))
            return;

        var expandedMacro = MacroVariableService.Expand(
            this.Configuration.CurrentDjMacro,
            this.Configuration.BuildVariables(currentDj));

        this.GameChatService.QueueMacroCommands(expandedMacro, TimeSpan.FromSeconds(this.Configuration.CurrentDjMacroDelaySeconds));
    }

    public void SendTransitionShout()
    {
        var currentDj = this.Configuration.GetSelectedDj();
        var nextDj = this.Configuration.GetNextDjAfter(currentDj);
        this.SendTransitionShoutFor(currentDj, nextDj);
    }

    public void TellCurrentDjToTarget()
    {
        var currentDj = this.Configuration.GetSelectedDj();
        if (!HasUsableDj(currentDj))
            return;

        var expandedMacro = MacroVariableService.Expand(
            this.Configuration.TellCurrentDjMacro,
            this.Configuration.BuildVariables(currentDj));

        this.GameChatService.QueueMacroCommands(expandedMacro, TimeSpan.FromSeconds(this.Configuration.TellCurrentDjMacroDelaySeconds));
    }

    private void SendTransitionShoutFor(Models.DjScheduleEntry? endingDj, Models.DjScheduleEntry? incomingDj)
    {
        if (!HasUsableDj(endingDj) || !HasUsableDj(incomingDj))
            return;

        var expandedMacro = MacroVariableService.Expand(
            this.Configuration.TransitionMacro,
            this.Configuration.BuildVariables(endingDj, incomingDj));

        this.GameChatService.QueueMacroCommands(expandedMacro, TimeSpan.FromSeconds(this.Configuration.TransitionMacroDelaySeconds));
    }


    private static bool HasUsableDj(Models.DjScheduleEntry? entry)
    {
        return entry is not null && !string.IsNullOrWhiteSpace(entry.DJName);
    }

    public string PreviewCurrentDjShout()
    {
        return MacroVariableService.Expand(this.Configuration.CurrentDjMacro, this.Configuration.BuildVariables());
    }

    public string PreviewTransitionShout()
    {
        return MacroVariableService.Expand(this.Configuration.TransitionMacro, this.Configuration.BuildVariables());
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.GameChatService.FlushOnePendingCommand();
        this.HandleAutoCurrentDjShout();
    }

    private void HandleAutoCurrentDjShout()
    {
        var config = this.Configuration;
        config.ClampAutoShoutSettings();

        if (!config.AutoCurrentDjShoutEnabled)
        {
            this.nextAutoCurrentDjShoutAtUtc = DateTime.MinValue;
            this.previousAutoCurrentDjShoutEnabled = false;
            this.autoSawUsableDjSinceEnabled = false;
            this.lastAutoDjOrderSeen = null;
            this.AutoShoutStatus = "Disabled.";
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var interval = TimeSpan.FromMinutes(config.AutoCurrentDjShoutIntervalMinutes);
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
            this.AutoShoutStatus = "Enabled, but no valid event-window schedule time was found.";
            return;
        }

        if (nowUtc < this.nextAutoCurrentDjShoutAtUtc)
        {
            var activeDj = config.GetCurrentServerTimeDj(nowUtc);
            if (activeDj is not null)
                this.RememberAutoDj(activeDj);

            var activeText = activeDj is null ? "no active DJ right now" : $"active DJ: {activeDj.DJName}";
            this.AutoShoutStatus = $"Enabled. Using event window, {activeText}. Next shout at {this.nextAutoCurrentDjShoutAtUtc:yyyy-MM-dd HH:mm:ss} ST.";
            return;
        }

        if (this.GameChatService.HasPendingCommands)
        {
            this.nextAutoCurrentDjShoutAtUtc = nowUtc.AddSeconds(10);
            this.AutoShoutStatus = $"Waiting for queued chat to finish. Pending: {this.GameChatService.PendingCommandCount}.";
            return;
        }

        var scheduledShoutAtUtc = this.nextAutoCurrentDjShoutAtUtc;
        var currentDj = config.GetCurrentServerTimeDj(scheduledShoutAtUtc);
        if (currentDj is null)
        {
            this.nextAutoCurrentDjShoutAtUtc = this.CalculateNextAutoShoutUtc(nowUtc.AddSeconds(1));
            if (this.ShouldDisableAutoAfterLastDj(config, this.nextAutoCurrentDjShoutAtUtc))
            {
                this.DisableAutoShoutAfterLastDj();
                return;
            }

            this.AutoShoutStatus = this.nextAutoCurrentDjShoutAtUtc == DateTime.MaxValue
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
            this.SendTransitionShoutFor(previousDj, currentDj);
            this.nextAutoCurrentDjShoutAtUtc = this.CalculateNextAutoShoutUtc(nowUtc.AddSeconds(1));
            if (this.ShouldDisableAutoAfterLastDj(config, this.nextAutoCurrentDjShoutAtUtc))
            {
                this.DisableAutoShoutAfterLastDj();
                return;
            }

            this.AutoShoutStatus = $"Sent transition shout: {previousDj.DJName} → {currentDj.DJName}. Next at {FormatServerTime(this.nextAutoCurrentDjShoutAtUtc)}.";
            return;
        }

        this.RememberAutoDj(currentDj);
        this.SendCurrentDjShoutFor(currentDj);
        this.nextAutoCurrentDjShoutAtUtc = this.CalculateNextAutoShoutUtc(nowUtc.AddSeconds(1));
        if (this.ShouldDisableAutoAfterLastDj(config, this.nextAutoCurrentDjShoutAtUtc))
        {
            this.DisableAutoShoutAfterLastDj();
            return;
        }

        this.AutoShoutStatus = $"Sent current DJ shout for {currentDj.DJName}. Next at {FormatServerTime(this.nextAutoCurrentDjShoutAtUtc)}.";
    }


    private static string FormatServerTime(DateTime utcTime)
    {
        return utcTime == DateTime.MaxValue ? "none" : $"{utcTime:yyyy-MM-dd HH:mm:ss} ST";
    }


    private void RememberAutoDj(Models.DjScheduleEntry entry)
    {
        this.autoSawUsableDjSinceEnabled = true;
        this.lastAutoDjOrderSeen = entry.Order;
    }

    private bool ShouldDisableAutoAfterLastDj(Configuration config, DateTime nextCandidateUtc)
    {
        if (!this.autoSawUsableDjSinceEnabled || !this.lastAutoDjOrderSeen.HasValue)
            return false;

        if (nextCandidateUtc == DateTime.MaxValue)
            return true;

        var nextDj = config.GetCurrentServerTimeDj(nextCandidateUtc);
        return nextDj is not null && nextDj.Order <= this.lastAutoDjOrderSeen.Value;
    }

    private void DisableAutoShoutAfterLastDj()
    {
        this.Configuration.AutoCurrentDjShoutEnabled = false;
        this.Configuration.Save();
        this.nextAutoCurrentDjShoutAtUtc = DateTime.MinValue;
        this.previousAutoCurrentDjShoutEnabled = false;
        this.autoSawUsableDjSinceEnabled = false;
        this.lastAutoDjOrderSeen = null;
        this.AutoShoutStatus = "Disabled after the last scheduled DJ.";
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
        var config = this.Configuration;
        var interval = TimeSpan.FromMinutes(config.AutoCurrentDjShoutIntervalMinutes);
        var candidates = new List<DateTime>();

        foreach (var slot in config.BuildEventTimeline().Where(slot => slot.IsInsideEventWindow))
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

    private void OnCommand(string command, string args)
    {
        var trimmedArgs = args.Trim().ToLowerInvariant();

        // Keep the public command surface intentionally small for beta.
        // Operational actions live in the UI, where staff can see the selected DJ and schedule state.
        if (trimmedArgs == "settings")
        {
            this.ConfigWindow.IsOpen = true;
            return;
        }

        this.MainWindow.Toggle();
    }
}
