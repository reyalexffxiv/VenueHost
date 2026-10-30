using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
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
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    private static readonly string[] CommandNames = ["/venuehost", "/vhost"];

    private Configuration Configuration { get; }

    private GameChatService GameChatService { get; }

    /// <summary>Service that expands configured macros and queues manual shout commands.</summary>
    private ShoutService ShoutService { get; }

    /// <summary>Service that owns UI-only helpers such as window toggles and shared textures.</summary>
    private UiService UiService { get; }

    /// <summary>Service that opens native file dialogs without blocking Dalamud UI drawing.</summary>
    private FileDialogService FileDialogService { get; }

    /// <summary>Shared service context used by plugin services and windows.</summary>
    private IServiceContext Services { get; }

    private readonly WindowSystem windowSystem = new("VenueHost");

    private MainWindow MainWindow { get; }

    private ConfigWindow ConfigWindow { get; }

    private DjDatabaseWindow DjDatabaseWindow { get; }

    private StaffDatabaseWindow StaffDatabaseWindow { get; }

    /// <summary>Service that owns automatic DJ shout timing and status.</summary>
    private DjAutoShoutService DjAutoShoutService { get; }

    /// <summary>Service that owns automatic staff role shout timing and staggered queue state.</summary>
    private StaffAutoShoutService StaffAutoShoutService { get; }

    /// <summary>Service that owns manual and automatic event/custom shouts.</summary>
    private EventShoutService EventShoutService { get; }

    private AnnouncementPriorityService ShoutPriorityService { get; }

    private AutomationControlService AutomationControlService { get; }

    private VenueProfileService VenueProfiles { get; }

    public Plugin()
    {
        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Configuration.Initialize(PluginInterface);
        // Contrast Mode is not user-facing in v0.8.6. Avoid leaving anyone
        // stranded in an old experimental palette after removing its toggle.
        if (this.Configuration.ContrastModeEnabled)
        {
            this.Configuration.ContrastModeEnabled = false;
            this.Configuration.Save();
        }

        this.Services = new ServiceContext(
            PluginInterface,
            CommandManager,
            Framework,
            Log,
            TextureProvider);

        if (this.Configuration.CleanLegacyManualWaitLinesOnce())
        {
            this.Configuration.Save();
            Log.Information("Cleaned legacy manual wait-only lines from Venue Host macros.");
        }

        this.GameChatService = new GameChatService(Log);
        this.Services.Add(this.GameChatService);
        ChatGui.ChatMessage += this.ObserveChatMessage;

        this.ShoutService = new ShoutService(this.Configuration, this.Services);
        this.Services.Add(this.ShoutService);

        this.DjAutoShoutService = new DjAutoShoutService(this.Configuration, this.Services);
        this.Services.Add(this.DjAutoShoutService);

        this.StaffAutoShoutService = new StaffAutoShoutService(this.Configuration, this.Services);
        this.Services.Add(this.StaffAutoShoutService);

        this.EventShoutService = new EventShoutService(this.Configuration, this.Services);
        this.Services.Add(this.EventShoutService);

        this.ShoutPriorityService = new AnnouncementPriorityService(this.Configuration,
            this.GameChatService, this.DjAutoShoutService, this.StaffAutoShoutService, this.EventShoutService);

        this.AutomationControlService = new AutomationControlService(this.Configuration,
            this.DjAutoShoutService, this.StaffAutoShoutService, this.EventShoutService, this.GameChatService);
        this.Services.Add(this.AutomationControlService);

        this.FileDialogService = new FileDialogService(this.Configuration, this.Services);
        this.Services.Add(this.FileDialogService);

        this.UiService = new UiService(this.Configuration, this.Services);
        this.Services.Add(this.UiService);

        this.VenueProfiles = new VenueProfileService(this.Configuration,
            PluginInterface.ConfigDirectory.FullName,
            () =>
            {
                // These callbacks run after window construction. Validate the
                // references explicitly rather than dereferencing properties
                // that are assigned later in this constructor.
                var main = this.MainWindow;
                var djDatabase = this.DjDatabaseWindow;
                var staffDatabase = this.StaffDatabaseWindow;
                if (main is null || djDatabase is null || staffDatabase is null)
                    throw new InvalidOperationException("VenueHost windows are not ready for profile switching.");
                if (main.HasUnsavedProfileDrafts())
                    throw new InvalidOperationException("Save or discard open event/macro edits before switching venues.");
                if (djDatabase.IsOpen || staffDatabase.IsOpen)
                    throw new InvalidOperationException("Close the DJ and Staff Database windows before switching venues.");
                this.AutomationControlService.PauseForProfileSwitch();
                this.ShoutPriorityService.Reset();
            },
            () =>
            {
                var main = this.MainWindow;
                var djDatabase = this.DjDatabaseWindow;
                var staffDatabase = this.StaffDatabaseWindow;
                if (main is null || djDatabase is null || staffDatabase is null)
                    throw new InvalidOperationException("VenueHost windows are not ready for profile switching.");
                this.DjAutoShoutService.Refresh();
                this.StaffAutoShoutService.Refresh();
                this.EventShoutService.Refresh();
                djDatabase.IsOpen = false;
                staffDatabase.IsOpen = false;
                main.ResetForProfileSwitch();
            });
        this.Configuration.BindProfiles(this.VenueProfiles);
        this.Services.Add(this.VenueProfiles);

        this.MainWindow = new MainWindow(this.Configuration, this.Services);
        this.ConfigWindow = new ConfigWindow(this.Configuration, this.Services);
        this.DjDatabaseWindow = new DjDatabaseWindow(this.Configuration, this.Services);
        this.StaffDatabaseWindow = new StaffDatabaseWindow(this.Configuration, this.Services);

        this.UiService.BindWindowToggles(
            () => this.MainWindow.Toggle(),
            () => this.ConfigWindow.Toggle(),
            () => this.DjDatabaseWindow.Toggle(),
            () => this.StaffDatabaseWindow.Toggle());

        this.windowSystem.AddWindow(this.MainWindow);
        this.windowSystem.AddWindow(this.ConfigWindow);
        this.windowSystem.AddWindow(this.DjDatabaseWindow);
        this.windowSystem.AddWindow(this.StaffDatabaseWindow);

        foreach (var commandName in CommandNames)
        {
            CommandManager.AddHandler(commandName, new CommandInfo(this.OnCommand)
            {
                HelpMessage = "Open Venue Host. Use 'settings' to open the settings window.",
            });
        }

        Framework.Update += this.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += this.ToggleMainUi;

        Log.Information("Venue Host loaded.");
    }


    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleMainUi;
        Framework.Update -= this.OnFrameworkUpdate;
        ChatGui.ChatMessage -= this.ObserveChatMessage;

        foreach (var commandName in CommandNames)
            CommandManager.RemoveHandler(commandName);

        this.windowSystem.RemoveAllWindows();
        this.StaffDatabaseWindow.Dispose();
        this.DjDatabaseWindow.Dispose();
        this.ConfigWindow.Dispose();
        this.MainWindow.Dispose();
        this.UiService.Dispose();
    }

    private void ObserveChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
        => this.GameChatService.ObserveChatChannel(message.LogKind);

    private void ToggleMainUi() => this.UiService.ToggleMainUi();

    private void ToggleConfigUi() => this.UiService.ToggleConfigUi();


    private void OnFrameworkUpdate(IFramework framework)
    {
        // Always flush manually triggered chat, even while automation is paused.
        // The dispatcher blocks automatic commands during pause; schedulers stay stopped.
        this.GameChatService.SmartChannelsEnabled = this.Configuration.SmartChatChannelsEnabled;
        this.GameChatService.FlushOnePendingCommand();
        if (this.AutomationControlService.Paused)
            return;
        this.ShoutPriorityService.Tick(this.AutomationControlService.DjOn,
            this.AutomationControlService.StaffOn, this.AutomationControlService.EventOn);
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
