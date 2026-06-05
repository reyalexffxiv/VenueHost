using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using VenueHost.Models;
using VenueHost.Services;

namespace VenueHost.Windows;

/// <summary>
/// Main control panel for venue staff during an event night.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private const float MainWindowWidth = 980f;
    private const float MainWindowMinHeight = 900f;
    private const float EventDetailsInputWidth = 180f;
    private const float EventCommandInputWidth = 120f;
    private const string EventShoutEditorWindowId = "Event Shout Editor###VenueHostEventShoutEditor";


    private readonly Configuration configuration;
    private readonly IServiceContext services;

    /// <summary>Shared plugin configuration used by this window.</summary>
    private Configuration Configuration => this.configuration;

    /// <summary>Shared service context used to resolve plugin services.</summary>
    private IServiceContext Services => this.services;

    /// <summary>UI helper service for cross-window actions and shared UI assets.</summary>
    private UiService Ui => this.Services.Get<UiService>();

    /// <summary>Macro expansion and chat queue helper service.</summary>
    private ShoutService Shout => this.Services.Get<ShoutService>();

    /// <summary>Automatic DJ shout scheduler and status provider.</summary>
    private DjAutoShoutService DjAutoShout => this.Services.Get<DjAutoShoutService>();

    /// <summary>Automatic staff role shout scheduler and status provider.</summary>
    private StaffAutoShoutService StaffAutoShout => this.Services.Get<StaffAutoShoutService>();

    /// <summary>Manual and automatic event/custom shout scheduler.</summary>
    private EventShoutService EventShouts => this.Services.Get<EventShoutService>();

    public MainWindow(Configuration configuration, IServiceContext services)
        : base("Venue Host###VenueHostMain")
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        // Keep the operational layout stable horizontally while still allowing users
        // to resize vertically for longer schedules and status panels.
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(MainWindowWidth, MainWindowMinHeight),
            MaximumSize = new Vector2(MainWindowWidth, float.MaxValue),
        };

        this.TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            ShowTooltip = () => ImGui.SetTooltip("Setup / Macros"),
            Click = _ => this.Ui.ToggleConfigUi(),
            Priority = 10,
        });
    }

    private readonly Dictionary<int, string> djPickerSearchTextByOrder = new();
    private readonly Dictionary<int, string> djPickerQuickAddNameByOrder = new();
    private readonly Dictionary<int, string> djPickerQuickAddLinkByOrder = new();
    private readonly Dictionary<int, string> staffPickerSearchTextByOrder = new();

    // Draft state for the non-blocking Add/Edit Event Shout editor window.
    // The editor works on this temporary copy so Cancel can discard changes cleanly.
    private bool eventShoutEditorOpen;
    private bool eventShoutEditorIsNew;
    private int eventShoutEditorOrder;
    private string eventShoutEditorName = string.Empty;
    private string eventShoutEditorMacro = string.Empty;
    private bool eventShoutEditorActive;
    private bool eventShoutEditorAutoEnabled;
    private int eventShoutEditorIntervalMinutes = 15;
    private string eventShoutEditorStartTime = "18:00";
    private string eventShoutEditorEndTime = "23:00";
    private float eventShoutEditorDelaySeconds = 2f;

    public void Dispose()
    {
    }

    /// <summary>
    /// Keeps the main operational window at the designed width and minimum height.
    /// ImGui can restore a previously saved size before constraints are applied,
    /// so this guards against horizontal stretch and too-short layouts without
    /// disabling vertical expansion.
    /// </summary>
    private void EnforceMainWindowSize()
    {
        var currentSize = ImGui.GetWindowSize();
        var targetHeight = Math.Max(currentSize.Y, MainWindowMinHeight);

        if (Math.Abs(currentSize.X - MainWindowWidth) <= 0.5f &&
            Math.Abs(currentSize.Y - targetHeight) <= 0.5f)
        {
            return;
        }

        ImGui.SetWindowSize(new Vector2(MainWindowWidth, targetHeight));
    }

    /// <summary>Refreshes automatic shout timers after schedule or settings edits.</summary>
    private void RefreshAutoShoutSchedule()
    {
        this.DjAutoShout.Refresh();
        this.StaffAutoShout.Refresh();
        this.EventShouts.Refresh();
    }

    public override void Draw()
    {
        this.EnforceMainWindowSize();

        using var contrast = UiTheme.PushContrastIfEnabled(this.Configuration.ContrastModeEnabled);

        if (!ImGui.BeginTabBar("VenueHostTabs"))
            return;

        if (ImGui.BeginTabItem("DJ Lineup"))
        {
            this.DrawDjLineupTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Staff Schedule"))
        {
            this.DrawStaffScheduleTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Event Shouts"))
        {
            this.DrawEventShoutsTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();

        this.DrawEventShoutEditorWindow(this.Configuration);
        this.DrawMascotCorner();
    }

    private void DrawDjLineupTab()
    {
        var config = this.Configuration;
        config.EnsureScheduleDefaults();

        ImGui.TextUnformatted("Event schedule");
        ImGui.Separator();
        this.DrawEventSchedule(config);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Event details");
        ImGui.Separator();
        this.DrawEventDetails(config);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("DJ schedule inside event window (ST / UTC)");
        ImGui.TextDisabled("Manual buttons use the selected row. Auto-shout follows the event window in ST/UTC.");
        ImGui.Spacing();

        this.DrawDjScheduleTableRegion(config);

        ImGui.Spacing();
        this.DrawScheduleButtons(config);
        this.DrawClearLineupConfirmation(config);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawManualActions(config);

        ImGui.Spacing();
        this.DrawStatusPanel(config);
        this.DrawScheduleWarnings(config);
    }



    private void DrawStaffScheduleTab()
    {
        var config = this.Configuration;
        config.EnsureStaffScheduleDefaults();

        this.DrawStaffScheduleWindow(config);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Staff rows inside staff window (ST / UTC)");
        ImGui.TextDisabled("Manual shouts use the selected role. Auto shouts follow each role interval inside the staff window.");
        ImGui.Spacing();

        this.DrawStaffScheduleTableRegion(config);

        ImGui.Spacing();
        this.DrawStaffScheduleButtons(config);
        this.DrawClearStaffListConfirmation(config);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawStaffScheduleManualActions(config);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawStaffScheduleStatusPanel(config);
    }

    private void DrawEventShoutsTab()
    {
        var config = this.Configuration;
        config.EnsureEventShoutDefaults();

        this.DrawEventShoutWindow(config);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Event / custom shouts");
        ImGui.TextDisabled("Create reusable venue-wide macros. Auto timers run only inside the event shout window.");
        ImGui.Spacing();

        this.DrawEventShoutsTable(config);

        ImGui.Spacing();
        this.DrawEventShoutButtons(config);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawEventShoutStatusPanel(config);
    }

    private void DrawEventShoutWindow(Configuration config)
    {
        config.EnsureEventShoutWindowDefaults();
        var startX = ImGui.GetCursorPosX();
        var endX = startX + 260f;

        ImGui.TextUnformatted("Event shout window");
        ImGui.Separator();
        ImGui.TextDisabled("Shout Start");
        ImGui.SameLine(endX);
        ImGui.TextDisabled("Shout End");

        this.DateInputAndSave("##EventShoutStartDate", config.EventShoutStartDate, v => config.EventShoutStartDate = v, 11);
        ImGui.SameLine();
        this.TimePickerAndSave("EventShoutStart", config.EventShoutStartTime, value => config.EventShoutStartTime = value);

        ImGui.SameLine(endX);
        this.DateInputAndSave("##EventShoutEndDate", config.EventShoutEndDate, v => config.EventShoutEndDate = v, 11);
        ImGui.SameLine();
        this.TimePickerAndSave("EventShoutEnd", config.EventShoutEndTime, value => config.EventShoutEndTime = value);

        ImGui.SameLine();
        if (this.ColoredButton("Use Event Window", new Vector2(135f, 24f), ButtonTone.Neutral) && config.TryGetEventWindow(out _, out _))
        {
            config.EventShoutStartDate = config.EventStartDate;
            config.EventShoutStartTime = config.EventStartTime;
            config.EventShoutEndDate = config.EventEndDate;
            config.EventShoutEndTime = config.EventEndTime;
            config.Save();
            this.EventShouts.Refresh();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy the DJ/Event schedule window into the event shout window.");

        ImGui.TextDisabled("Dates/times are ST / UTC. Automatic custom shouts only run inside this window.");
    }

    private void DrawEventShoutsTable(Configuration config)
    {
        if (config.EventShouts.Count == 0)
        {
            ImGui.TextDisabled("No custom shouts yet. Use Add Shout to create one.");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings;
        if (!ImGui.BeginTable("VenueHostEventShoutsV12", 7, flags, new Vector2(632f, 0)))
            return;

        ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 34f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 224f);
        ImGui.TableSetupColumn("Auto", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 52f);
        ImGui.TableSetupColumn("Interval", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 72f);
        ImGui.TableSetupColumn("Start", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 66f);
        ImGui.TableSetupColumn("End", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 66f);
        ImGui.TableSetupColumn("Active", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 78f);
        DrawCenteredTableHeaders(string.Empty, "Name", "Auto", "Interval", "Start", "End", "Active");

        foreach (var entry in config.EventShouts.OrderBy(entry => entry.Order))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            CenterNextItem(18f);
            var selected = config.SelectedEventShoutOrder == entry.Order;
            if (ImGui.RadioButton($"##SelectEventShout{entry.Order}", selected))
            {
                config.SelectedEventShoutOrder = entry.Order;
                config.Save();
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(entry.Name) ? "New Shout" : entry.Name);

            ImGui.TableSetColumnIndex(2);
            CenterNextItem(22f);
            var autoEnabled = entry.AutoEnabled;
            if (ImGui.Checkbox($"##EventShoutAuto{entry.Order}", ref autoEnabled))
            {
                entry.AutoEnabled = autoEnabled;
                config.Save();
                this.EventShouts.Refresh();
            }

            ImGui.TableSetColumnIndex(3);
            DrawCenteredText($"{Math.Clamp(entry.IntervalMinutes, 1, 240)} min");

            ImGui.TableSetColumnIndex(4);
            DrawCenteredText(NormalizeTimeText(entry.StartTime));

            ImGui.TableSetColumnIndex(5);
            DrawCenteredText(NormalizeTimeText(entry.EndTime));

            ImGui.TableSetColumnIndex(6);
            CenterNextItem(22f);
            var active = entry.Active;
            if (ImGui.Checkbox($"##EventShoutActive{entry.Order}", ref active))
            {
                entry.Active = active;
                config.Save();
                this.EventShouts.Refresh();
            }
        }

        ImGui.EndTable();
    }

    private void DrawEventShoutButtons(Configuration config)
    {
        if (this.ColoredButton("Add Shout", new Vector2(95, 28), ButtonTone.Positive))
            this.OpenAddEventShoutEditor(config);

        var selected = config.GetSelectedEventShout();
        var hasSelected = selected is not null;
        if (!hasSelected)
            ImGui.BeginDisabled();

        ImGui.SameLine();
        if (this.ColoredButton("Edit Shout", new Vector2(92, 28), ButtonTone.Primary) && selected is not null)
            this.OpenEditEventShoutEditor(selected);

        ImGui.SameLine();
        if (this.ColoredButton("Remove", new Vector2(80, 28), ButtonTone.Danger) && selected is not null)
        {
            config.EventShouts.Remove(selected);
            config.NormalizeEventShoutOrders();
            config.SelectedEventShoutOrder = config.EventShouts.FirstOrDefault()?.Order ?? 0;
            config.Save();
            this.EventShouts.Refresh();
        }

        ImGui.SameLine();
        if (this.ColoredButton("Move Up", new Vector2(82, 28), ButtonTone.Neutral))
            this.MoveSelectedEventShout(config, -1);

        ImGui.SameLine();
        if (this.ColoredButton("Move Down", new Vector2(96, 28), ButtonTone.Neutral))
            this.MoveSelectedEventShout(config, 1);

        ImGui.SameLine();
        if (this.ColoredButton("Shout Now", new Vector2(96, 28), ButtonTone.Primary))
            this.EventShouts.SendSelectedEventShout();

        if (!hasSelected)
            ImGui.EndDisabled();
    }

    private void OpenAddEventShoutEditor(Configuration config)
    {
        this.eventShoutEditorIsNew = true;
        this.eventShoutEditorOrder = 0;
        this.eventShoutEditorName = $"Custom Shout {config.EventShouts.Count + 1}";
        this.eventShoutEditorMacro = Configuration.DefaultEventShoutMacro;
        this.eventShoutEditorActive = true;
        this.eventShoutEditorAutoEnabled = false;
        this.eventShoutEditorIntervalMinutes = 15;
        this.eventShoutEditorStartTime = NormalizeTimeText(config.EventShoutStartTime);
        this.eventShoutEditorEndTime = NormalizeTimeText(config.EventShoutEndTime);
        this.eventShoutEditorDelaySeconds = 2f;
        this.eventShoutEditorOpen = true;
    }

    private void OpenEditEventShoutEditor(EventShoutEntry entry)
    {
        this.eventShoutEditorIsNew = false;
        this.eventShoutEditorOrder = entry.Order;
        this.eventShoutEditorName = string.IsNullOrWhiteSpace(entry.Name) ? "New Shout" : entry.Name;
        this.eventShoutEditorMacro = entry.Macro ?? string.Empty;
        this.eventShoutEditorActive = entry.Active;
        this.eventShoutEditorAutoEnabled = entry.AutoEnabled;
        this.eventShoutEditorIntervalMinutes = Math.Clamp(entry.IntervalMinutes, 1, 240);
        this.eventShoutEditorStartTime = NormalizeTimeText(entry.StartTime);
        this.eventShoutEditorEndTime = NormalizeTimeText(entry.EndTime);
        this.eventShoutEditorDelaySeconds = Math.Clamp(entry.DelaySeconds, 0f, 10f);
        this.eventShoutEditorOpen = true;
    }

    private void DrawEventShoutEditorWindow(Configuration config)
    {
        if (!this.eventShoutEditorOpen)
            return;

        // Use a regular floating window instead of a modal popup so venue
        // operators can still interact with the main controls while editing.
        ImGui.SetNextWindowSize(new Vector2(820f, 590f), ImGuiCond.Appearing);
        if (!ImGui.Begin(EventShoutEditorWindowId, ref this.eventShoutEditorOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted(this.eventShoutEditorIsNew ? "Add Event Shout" : "Edit Event Shout");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("Name##EventShoutEditorName", ref this.eventShoutEditorName, 80);

        ImGui.SameLine();
        ImGui.Checkbox("Active##EventShoutEditorActive", ref this.eventShoutEditorActive);

        ImGui.SameLine();
        ImGui.Checkbox("Auto timer##EventShoutEditorAuto", ref this.eventShoutEditorAutoEnabled);

        if (DrawIntStepper("Interval##EventShoutEditorInterval", ref this.eventShoutEditorIntervalMinutes, 1, 240, 1, "min"))
            this.eventShoutEditorIntervalMinutes = Math.Clamp(this.eventShoutEditorIntervalMinutes, 1, 240);

        ImGui.TextUnformatted("Shout row window");
        ImGui.TextDisabled("This row only auto-shouts during this time, inside the global Event Shout window.");
        this.TimePickerAndSave("EventShoutEditorStart", this.eventShoutEditorStartTime, value => this.eventShoutEditorStartTime = value, width: 88f);
        ImGui.SameLine();
        ImGui.TextUnformatted("Start");
        ImGui.SameLine(210f);
        this.TimePickerAndSave("EventShoutEditorEnd", this.eventShoutEditorEndTime, value => this.eventShoutEditorEndTime = value, width: 88f);
        ImGui.SameLine();
        ImGui.TextUnformatted("End");

        if (DrawFloatStepper("Wait between lines##EventShoutEditorWait", ref this.eventShoutEditorDelaySeconds, 0f, 10f, 0.5f, "sec"))
            this.eventShoutEditorDelaySeconds = Math.Clamp(this.eventShoutEditorDelaySeconds, 0f, 10f);

        ImGui.Spacing();
        ImGui.TextUnformatted("Macro");
        ImGui.TextDisabled("Use full chat commands, one per line. Example: /sh Welcome to {VenueName}!");
        ImGui.SetNextItemWidth(780f);
        ImGui.InputTextMultiline("##EventShoutEditorMacro", ref this.eventShoutEditorMacro, 2000, new Vector2(780f, 170f));

        ImGui.Spacing();
        ImGui.TextUnformatted("Preview");
        var previewEntry = new EventShoutEntry
        {
            Order = this.eventShoutEditorOrder,
            Name = this.eventShoutEditorName,
            Macro = this.eventShoutEditorMacro,
            Active = this.eventShoutEditorActive,
            AutoEnabled = this.eventShoutEditorAutoEnabled,
            IntervalMinutes = this.eventShoutEditorIntervalMinutes,
            StartTime = this.eventShoutEditorStartTime,
            EndTime = this.eventShoutEditorEndTime,
            DelaySeconds = this.eventShoutEditorDelaySeconds,
        };
        var preview = this.EventShouts.PreviewEventShout(previewEntry);
        ImGui.SetNextItemWidth(780f);
        ImGui.InputTextMultiline("##EventShoutEditorPreview", ref preview, 2000, new Vector2(780f, 110f), ImGuiInputTextFlags.ReadOnly);

        ImGui.Spacing();
        var canSave = !string.IsNullOrWhiteSpace(this.eventShoutEditorName) && !string.IsNullOrWhiteSpace(this.eventShoutEditorMacro);
        if (!canSave)
            ImGui.BeginDisabled();

        if (this.ColoredButton("Save", new Vector2(110f, 28f), ButtonTone.Positive))
        {
            this.SaveEventShoutEditor(config);
            this.eventShoutEditorOpen = false;
        }

        if (!canSave)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (this.ColoredButton("Cancel", new Vector2(110f, 28f), ButtonTone.Neutral))
            this.eventShoutEditorOpen = false;

        ImGui.End();
    }

    private void SaveEventShoutEditor(Configuration config)
    {
        EventShoutEntry entry;
        if (this.eventShoutEditorIsNew)
        {
            entry = new EventShoutEntry();
            config.EventShouts.Add(entry);
        }
        else
        {
            entry = config.EventShouts.FirstOrDefault(item => item.Order == this.eventShoutEditorOrder) ?? new EventShoutEntry();
            if (!config.EventShouts.Contains(entry))
                config.EventShouts.Add(entry);
        }

        entry.Name = string.IsNullOrWhiteSpace(this.eventShoutEditorName) ? "New Shout" : this.eventShoutEditorName.Trim();
        entry.Macro = string.IsNullOrWhiteSpace(this.eventShoutEditorMacro) ? Configuration.DefaultEventShoutMacro : this.eventShoutEditorMacro;
        entry.Active = this.eventShoutEditorActive;
        entry.AutoEnabled = this.eventShoutEditorAutoEnabled;
        entry.IntervalMinutes = Math.Clamp(this.eventShoutEditorIntervalMinutes, 1, 240);
        entry.StartTime = NormalizeTimeText(this.eventShoutEditorStartTime);
        entry.EndTime = NormalizeTimeText(this.eventShoutEditorEndTime);
        entry.DelaySeconds = Math.Clamp(this.eventShoutEditorDelaySeconds, 0f, 10f);

        config.NormalizeEventShoutOrders();
        config.SelectedEventShoutOrder = entry.Order;
        config.Save();
        this.EventShouts.Refresh();
    }

    private void DrawEventShoutStatusPanel(Configuration config)
    {
        ImGui.TextUnformatted("Auto event shouts");
        ImGui.TextDisabled($"Server Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} ST / UTC");
        ImGui.TextDisabled(this.EventShouts.Status);

        var selected = config.GetSelectedEventShout();
        var nextSelected = this.EventShouts.GetNextShoutAtUtc(selected);
        ImGui.TextDisabled($"Selected next: {(nextSelected.HasValue ? nextSelected.Value.ToString("yyyy-MM-dd HH:mm:ss") + " ST" : "none")}");

        var upcoming = this.EventShouts.GetUpcomingEventShouts(8);
        if (upcoming.Count == 0)
            return;

        ImGui.Spacing();
        if (!ImGui.BeginTable("VenueHostUpcomingEventShoutsV2", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings, new Vector2(440f, 0)))
            return;

        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 92f);
        ImGui.TableSetupColumn("Shout", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 230f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 96f);
        DrawCenteredTableHeaders("Time", "Shout", "Status");

        for (var i = 0; i < upcoming.Count; i++)
        {
            var item = upcoming[i];
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawCenteredText(item.TimeText);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(item.Shout.Name);
            ImGui.TableSetColumnIndex(2);
            DrawCenteredDisabledText(i == 0 ? "Next" : "Queued");
        }

        ImGui.EndTable();
    }

    private void MoveSelectedEventShout(Configuration config, int direction)
    {
        var selected = config.GetSelectedEventShout();
        if (selected is null)
            return;

        config.NormalizeEventShoutOrders();
        var index = config.EventShouts.FindIndex(entry => entry.Order == selected.Order);
        var newIndex = index + direction;
        if (index < 0 || newIndex < 0 || newIndex >= config.EventShouts.Count)
            return;

        (config.EventShouts[index], config.EventShouts[newIndex]) = (config.EventShouts[newIndex], config.EventShouts[index]);
        config.NormalizeEventShoutOrders();
        config.SelectedEventShoutOrder = config.EventShouts[newIndex].Order;
        config.Save();
        this.EventShouts.Refresh();
    }


    private const int NaturalScheduleRowLimit = 12;

    private void DrawDjScheduleTableRegion(Configuration config)
    {
        if (config.DjSchedule.Count <= NaturalScheduleRowLimit)
        {
            this.DrawDjScheduleTable(config);
            return;
        }

        var tableHeight = CalculateScheduleTableScrollHeight(NaturalScheduleRowLimit);
        if (ImGui.BeginChild("DjScheduleScrollRegion", new Vector2(0, tableHeight), false, ImGuiWindowFlags.None))
            this.DrawDjScheduleTable(config);
        ImGui.EndChild();
    }

    private void DrawStaffScheduleTableRegion(Configuration config)
    {
        if (config.StaffSchedule.Count <= NaturalScheduleRowLimit)
        {
            this.DrawStaffScheduleTable(config);
            return;
        }

        var tableHeight = CalculateScheduleTableScrollHeight(NaturalScheduleRowLimit);
        if (ImGui.BeginChild("StaffScheduleScrollRegion", new Vector2(0, tableHeight), false, ImGuiWindowFlags.None))
            this.DrawStaffScheduleTable(config);
        ImGui.EndChild();
    }

    private static float CalculateScheduleTableScrollHeight(int visibleRows)
    {
        var rowHeight = Math.Max(24f, ImGui.GetTextLineHeightWithSpacing() + 7f);
        var headerHeight = Math.Max(24f, ImGui.GetTextLineHeightWithSpacing() + 5f);
        return headerHeight + (rowHeight * visibleRows) + 8f;
    }

    private void DrawStaffScheduleWindow(Configuration config)
    {
        config.EnsureStaffWindowDefaults();
        var startX = ImGui.GetCursorPosX();
        var endX = startX + 260f;

        ImGui.TextUnformatted("Staff working window");
        ImGui.Separator();
        ImGui.TextDisabled("Staff Start");
        ImGui.SameLine(endX);
        ImGui.TextDisabled("Staff End");

        this.DateInputAndSave("##StaffStartDate", config.StaffStartDate, v => config.StaffStartDate = v, 11);
        ImGui.SameLine();
        this.TimePickerAndSave("StaffStart", config.StaffStartTime, value => config.StaffStartTime = value);

        ImGui.SameLine(endX);
        this.DateInputAndSave("##StaffEndDate", config.StaffEndDate, v => config.StaffEndDate = v, 11);
        ImGui.SameLine();
        this.TimePickerAndSave("StaffEnd", config.StaffEndTime, value => config.StaffEndTime = value);

        ImGui.SameLine();
        if (this.ColoredButton("Use Event Window", new Vector2(135f, 24f), ButtonTone.Neutral) && config.TryGetEventWindow(out _, out _))
        {
            config.StaffStartDate = config.EventStartDate;
            config.StaffStartTime = config.EventStartTime;
            config.StaffEndDate = config.EventEndDate;
            config.StaffEndTime = config.EventEndTime;
            config.Save();
            this.RefreshAutoShoutSchedule();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy the DJ/Event schedule window into the staff working window.");

        ImGui.TextDisabled("Dates/times are ST / UTC. Staff row times are interpreted inside this staff window.");
    }

    private void DrawStaffScheduleTable(Configuration config)
    {
        // Keep the staff schedule compact. Unlike DJ rows, this table has no
        // stream-link column, so letting it stretch across the full window makes the
        // name column look oversized. Fixed column sizes also keep the header labels
        // and row controls visually aligned.
        const float tableWidth = 715f;
        const float pickerColumnWidth = 28f;
        const float staffColumnWidth = 235f;
        const float roleColumnWidth = 145f;
        const float timeColumnWidth = 108f;
        // Give the Active column enough room for its centered header text.
        // The checkbox remains compact, but the header should not truncate.
        const float activeColumnWidth = 76f;

        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.NoHostExtendX | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("VenueHostStaffScheduleTable", 6, tableFlags, new Vector2(tableWidth, 0f)))
            return;

        ImGui.TableSetupColumn("##Picker", ImGuiTableColumnFlags.WidthFixed, pickerColumnWidth);
        ImGui.TableSetupColumn("Staff Member", ImGuiTableColumnFlags.WidthFixed, staffColumnWidth);
        ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, roleColumnWidth);
        ImGui.TableSetupColumn("Start", ImGuiTableColumnFlags.WidthFixed, timeColumnWidth);
        ImGui.TableSetupColumn("End", ImGuiTableColumnFlags.WidthFixed, timeColumnWidth);
        ImGui.TableSetupColumn("Active", ImGuiTableColumnFlags.WidthFixed, activeColumnWidth);
        DrawCenteredTableHeaders(string.Empty, "Staff Member", "Role", "Start", "End", "Active");

        for (var i = 0; i < config.StaffSchedule.Count; i++)
        {
            var entry = config.StaffSchedule[i];
            ImGui.PushID($"StaffScheduleRow{i}");
            ImGui.TableNextRow();

            var selected = config.SelectedStaffScheduleOrder == entry.Order;
            if (selected)
            {
                var selectedColor = this.Configuration.ContrastModeEnabled
                    ? new Vector4(0.42f, 0.00f, 0.34f, 0.42f)
                    : new Vector4(0.16f, 0.32f, 0.52f, 0.22f);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ToImGuiColor(selectedColor));
            }

            ImGui.TableNextColumn();
            CenterNextItem(18f);
            if (ImGui.RadioButton("##StaffScheduleRowSelect", selected))
            {
                config.SelectedStaffScheduleOrder = entry.Order;
                config.Save();
            }

            ImGui.TableNextColumn();
            this.StaffPickerAndSave(entry);

            ImGui.TableNextColumn();
            this.StaffRoleComboAndSave(entry);

            ImGui.TableNextColumn();
            this.TimePickerAndSave("StaffScheduleStart", entry.StartTime, value => entry.StartTime = value);

            ImGui.TableNextColumn();
            this.TimePickerAndSave("StaffScheduleEnd", entry.EndTime, value => entry.EndTime = value);

            ImGui.TableNextColumn();
            var available = entry.Available;
            CenterNextItem(22f);
            if (ImGui.Checkbox("##StaffScheduleAvailable", ref available))
            {
                config.SelectedStaffScheduleOrder = entry.Order;
                entry.Available = available;
                config.Save();
                this.RefreshAutoShoutSchedule();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Temporarily include or exclude this scheduled staff member from manual and auto shouts.");

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawStaffScheduleButtons(Configuration config)
    {
        if (this.ColoredButton("Add Staff", new Vector2(115, 28), ButtonTone.Positive))
        {
            var last = config.StaffSchedule.Count > 0 ? config.StaffSchedule[^1] : null;
            var startTime = NormalizeTimeText(last?.EndTime ?? "18:00");
            var duration = last is null ? 60 : GetReasonableDurationMinutes(last.StartTime, last.EndTime);
            var endTime = AddMinutesToServerTime(startTime, duration);

            config.StaffSchedule.Add(new StaffScheduleEntry
            {
                Order = config.StaffSchedule.Count + 1,
                Name = string.Empty,
                Role = config.SelectedStaffRole,
                StartTime = startTime,
                EndTime = endTime,
                Available = true,
            });
            config.NormalizeStaffScheduleOrders();
            config.SelectedStaffScheduleOrder = config.StaffSchedule[^1].Order;
            config.Save();
            this.RefreshAutoShoutSchedule();
        }

        ImGui.SameLine();
        if (this.ColoredButton("Remove", new Vector2(90, 28), ButtonTone.Danger) && config.StaffSchedule.Count > 0)
        {
            config.StaffSchedule.RemoveAll(entry => entry.Order == config.SelectedStaffScheduleOrder);
            config.NormalizeStaffScheduleOrders();
            config.SelectedStaffScheduleOrder = config.StaffSchedule.Count == 0 ? 0 : Math.Clamp(config.SelectedStaffScheduleOrder, 1, config.StaffSchedule.Count);
            config.Save();
            this.RefreshAutoShoutSchedule();
        }

        ImGui.SameLine();
        if (this.ColoredButton("Move Up", new Vector2(100, 28), ButtonTone.Neutral))
            this.MoveSelectedStaffScheduleEntry(config, -1);

        ImGui.SameLine();
        if (this.ColoredButton("Move Down", new Vector2(110, 28), ButtonTone.Neutral))
            this.MoveSelectedStaffScheduleEntry(config, 1);

        ImGui.SameLine();
        if (this.ColoredButton("Staff Database", new Vector2(130, 28), ButtonTone.Primary))
            this.Ui.ToggleStaffDatabaseUi();

        // Keep the destructive clear action near the staff schedule controls.
        // Right-aligning it made the compact staff table feel disconnected.
        ImGui.SameLine();
        if (this.ColoredButton("Clear List", new Vector2(130f, 28), ButtonTone.Warning))
            this.showClearStaffListConfirmation = true;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clear all staff schedule rows after confirmation.");
    }

    private bool showClearStaffListConfirmation;

    private void DrawClearStaffListConfirmation(Configuration config)
    {
        if (this.showClearStaffListConfirmation)
        {
            ImGui.OpenPopup("Clear staff schedule?##VenueHostClearStaffScheduleConfirm");
            this.showClearStaffListConfirmation = false;
        }

        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var popupSize = new Vector2(455, 165);
        ImGui.SetNextWindowSize(popupSize, ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(windowPos + (windowSize - popupSize) * 0.5f, ImGuiCond.Appearing);

        var popupOpen = true;
        if (!ImGui.BeginPopupModal("Clear staff schedule?##VenueHostClearStaffScheduleConfirm", ref popupOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings))
            return;

        ImGui.TextWrapped("Clear the full staff schedule?");
        ImGui.TextDisabled("This removes every staff row. Use Add Staff to start a new schedule.");
        ImGui.Spacing();

        if (this.ColoredButton("Yes, clear staff schedule##ConfirmClearStaffSchedule", new Vector2(205, 28), ButtonTone.Danger))
        {
            config.StaffSchedule.Clear();
            config.SelectedStaffScheduleOrder = 0;
            config.AutoPhotographerShoutEnabled = false;
            foreach (var setting in config.StaffRoleShoutSettings)
                setting.AutoEnabled = false;
            config.Save();
            this.RefreshAutoShoutSchedule();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (this.ColoredButton("Cancel##CancelClearStaffSchedule", new Vector2(120, 28), ButtonTone.Neutral))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void DrawStaffScheduleManualActions(Configuration config)
    {
        var nowUtc = DateTime.UtcNow;
        var current = config.GetCurrentServerTimeStaffForRole(nowUtc, config.SelectedStaffRole);
        var currentNames = FormatStaffNames(current);
        var currentCount = CountUniqueStaffNames(current);
        var selected = config.GetSelectedStaffScheduleEntry();

        ImGui.TextUnformatted("Manual actions");
        ImGui.TextDisabled("Role to shout");
        ImGui.SetNextItemWidth(160f);
        if (ImGui.BeginCombo("##StaffRoleToShout", config.SelectedStaffRole))
        {
            foreach (var role in GetKnownStaffRoles(config))
            {
                var isSelected = string.Equals(config.SelectedStaffRole, role, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(role, isSelected))
                {
                    config.SelectedStaffRole = role;
                    config.Save();
                }
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        var roleSetting = config.GetOrCreateStaffRoleShoutSetting(config.SelectedStaffRole);
        ImGui.Spacing();
        ImGui.TextDisabled($"Quick auto settings for {roleSetting.Role}");

        var autoEnabled = roleSetting.AutoEnabled;
        if (ImGui.Checkbox("Auto shout enabled", ref autoEnabled))
        {
            roleSetting.AutoEnabled = autoEnabled;
            if (roleSetting.Role.Equals("Photographer", StringComparison.OrdinalIgnoreCase))
                config.AutoPhotographerShoutEnabled = autoEnabled;
            config.Save();
            this.RefreshAutoShoutSchedule();
        }

        var intervalMinutes = roleSetting.IntervalMinutes;
        if (DrawIntStepper($"Interval##QuickStaffRoleInterval{roleSetting.Role}", ref intervalMinutes, 1, 120, 1, "min"))
        {
            roleSetting.IntervalMinutes = intervalMinutes;
            if (roleSetting.Role.Equals("Photographer", StringComparison.OrdinalIgnoreCase))
                config.AutoPhotographerShoutIntervalMinutes = intervalMinutes;
            config.Save();
            this.RefreshAutoShoutSchedule();
        }

        ImGui.TextDisabled($"Selected: {DisplayStaffMember(selected)} | Role: {config.SelectedStaffRole} | Current: {currentNames}");

        if (currentCount == 0)
            ImGui.BeginDisabled();

        if (this.ColoredButton($"Shout {config.SelectedStaffRole}", new Vector2(230, 32), ButtonTone.Primary))
            this.Shout.SendSelectedStaffRoleShout();

        if (currentCount == 0)
            ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(currentCount > 0 ? "Shout the selected role currently scheduled and marked Available." : GetNoCurrentStaffReason(config, nowUtc));
    }

    private void DrawStaffScheduleStatusPanel(Configuration config)
    {
        var serverNow = DateTime.UtcNow;
        var current = config.GetCurrentServerTimeStaffForRole(serverNow, config.SelectedStaffRole);
        var currentText = FormatStaffNames(current);
        var nextAutoAt = this.StaffAutoShout.GetNextShoutAtUtc(this.Configuration.SelectedStaffRole);
        var nextAutoText = nextAutoAt.HasValue ? FormatStatusTime(nextAutoAt.Value, includeSeconds: true) : "none";
        var staffWindowText = config.TryGetStaffWindow(out var staffStartUtc, out var staffEndUtc)
            ? $"{staffStartUtc:yyyy-MM-dd HH:mm} → {staffEndUtc:yyyy-MM-dd HH:mm} ST"
            : "invalid";

        ImGui.TextUnformatted("Auto shouts");

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var leftWidth = MathF.Min(460f, availableWidth * 0.55f);
        var rightWidth = MathF.Max(300f, availableWidth - leftWidth - 18f);

        ImGui.BeginGroup();
        if (ImGui.BeginTable("VenueHostStaffScheduleStatusPanel", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 175f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
            this.DrawStatusRow("Auto Staff Shouts", config.IsAnyStaffAutoShoutEnabled() ? "ON" : "OFF");
            this.DrawStatusRow("Server Time", $"{serverNow:yyyy-MM-dd HH:mm:ss} ST / UTC");
            this.DrawStatusRow("Staff Window", staffWindowText);
            this.DrawStatusRow("Selected Role", config.SelectedStaffRole);
            this.DrawStatusRow("Current Staff", currentText);
            this.DrawStatusRow("Next Staff Shout", nextAutoText);
            this.DrawStatusRow("Next Shout Type", nextAutoAt.HasValue ? $"{config.SelectedStaffRole} shout" : "none");
            ImGui.EndTable();
        }
        ImGui.EndGroup();

        if (availableWidth > 760f)
        {
            ImGui.SameLine(0f, 18f);
            ImGui.BeginGroup();
            this.DrawStaffAutoShoutSequence(config, serverNow, rightWidth);
            ImGui.EndGroup();
        }
        else
        {
            ImGui.Spacing();
            this.DrawStaffAutoShoutSequence(config, serverNow, availableWidth);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Schedule check");
        foreach (var line in GetStaffScheduleCheckLines(config, serverNow))
            ImGui.TextDisabled(line);
    }

    private void DrawStaffAutoShoutSequence(Configuration config, DateTime nowUtc, float width)
    {
        ImGui.TextUnformatted("Upcoming role shouts");

        var rows = this.BuildStaffAutoShoutSequenceRows(nowUtc).Take(6).ToList();
        if (rows.Count == 0)
        {
            ImGui.TextDisabled("No automatic role shouts queued.");
            return;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(4f, 2f));
        if (ImGui.BeginTable(
                "VenueHostStaffAutoShoutSequence",
                3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoHostExtendX,
                new Vector2(MathF.Min(width, 520f), 0f)))
        {
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 72f);
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 130f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 190f);
            ImGui.TableHeadersRow();

            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                ImGui.TableNextRow();
                if (index == 0)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.Header));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.TimeText);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Role);
                ImGui.TableNextColumn();
                if (index == 0)
                    ImGui.TextUnformatted(row.Status);
                else
                    ImGui.TextDisabled(row.Status);
            }

            ImGui.EndTable();
        }
        ImGui.PopStyleVar();

        if (config.AutoStaffRoleShoutStaggerEnabled)
            ImGui.TextDisabled($"Queue spacing: {config.AutoStaffRoleShoutStaggerMinutes} min between role shouts.");
        else
            ImGui.TextDisabled("Queue spacing: disabled, due roles may shout together.");
    }

    private List<(DateTime DisplayTimeUtc, string TimeText, string Role, string Status)> BuildStaffAutoShoutSequenceRows(DateTime nowUtc)
    {
        return this.StaffAutoShout.GetUpcomingRoleShouts(nowUtc, maxRows: 6)
            .Select(row => (row.DisplayTimeUtc, $"{row.DisplayTimeUtc:HH:mm:ss}", row.Role, row.Status))
            .ToList();
    }

    private static string StripImGuiId(string label)
    {
        var markerIndex = label.IndexOf("##", StringComparison.Ordinal);
        return markerIndex >= 0 ? label[..markerIndex] : label;
    }

    private static bool DrawIntStepper(string label, ref int value, int min, int max, int step, string suffix)
    {
        var changed = false;
        var inputValue = value;
        var displayLabel = StripImGuiId(label);

        ImGui.PushID(label);
        ImGui.SetNextItemWidth(70);
        if (ImGui.InputInt("##value", ref inputValue, 0, 0))
        {
            value = Math.Clamp(inputValue, min, max);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("-", new Vector2(28, 0)))
        {
            value = Math.Clamp(value - step, min, max);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("+", new Vector2(28, 0)))
        {
            value = Math.Clamp(value + step, min, max);
            changed = true;
        }

        ImGui.SameLine();
        ImGui.TextUnformatted($"{displayLabel} ({suffix})");
        ImGui.PopID();

        return changed;
    }


    private static bool DrawFloatStepper(string label, ref float value, float min, float max, float step, string suffix)
    {
        var changed = false;
        var inputValue = value;
        var displayLabel = StripImGuiId(label);

        ImGui.PushID(label);
        ImGui.SetNextItemWidth(70);
        if (ImGui.InputFloat("##value", ref inputValue, 0f, 0f, "%.1f"))
        {
            value = Math.Clamp(inputValue, min, max);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("-", new Vector2(28, 0)))
        {
            value = Math.Clamp(value - step, min, max);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("+", new Vector2(28, 0)))
        {
            value = Math.Clamp(value + step, min, max);
            changed = true;
        }

        ImGui.SameLine();
        ImGui.TextUnformatted($"{displayLabel} ({suffix})");
        ImGui.PopID();

        return changed;
    }

    private void StaffPickerAndSave(StaffScheduleEntry entry)
    {
        var displayName = string.IsNullOrWhiteSpace(entry.Name) ? "Pick Staff..." : entry.Name;
        ImGui.SetNextItemWidth(-1);

        if (ImGui.Button($"{displayName}##StaffPickerButton", new Vector2(-1, 0)))
        {
            this.Configuration.SelectedStaffScheduleOrder = entry.Order;
            this.staffPickerSearchTextByOrder[entry.Order] = string.Empty;
            this.Configuration.Save();
            ImGui.OpenPopup("StaffPickerPopup");
        }

        if (!ImGui.BeginPopup("StaffPickerPopup"))
            return;

        ImGui.TextUnformatted("Pick Staff from Staff Database");
        ImGui.TextDisabled("Pick any saved staff member. Their role is copied into the schedule row.");
        ImGui.Separator();

        if (!this.staffPickerSearchTextByOrder.TryGetValue(entry.Order, out var searchText))
            searchText = string.Empty;

        ImGui.SetNextItemWidth(260f);
        if (ImGui.InputText("Search##StaffPickerSearch", ref searchText, 80))
            this.staffPickerSearchTextByOrder[entry.Order] = searchText;

        var matches = this.Configuration.StaffDatabase
            .Where(staff => MatchesStaffSearch(staff, searchText))
            .OrderBy(staff => staff.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ImGui.Spacing();
        if (matches.Count == 0)
        {
            ImGui.TextDisabled("No staff found in Staff Database.");
        }
        else if (ImGui.BeginChild("StaffPickerResults", new Vector2(430, Math.Min(matches.Count, 8) * 28f + 8f), true, ImGuiWindowFlags.None))
        {
            for (var i = 0; i < matches.Count; i++)
            {
                var staff = matches[i];
                var label = $"{staff.Name}  —  {staff.Role}";
                if (ImGui.Selectable($"{label}##PickStaff{i}"))
                {
                    entry.Name = staff.Name;
                    entry.Link = string.Empty;
                    entry.Role = string.IsNullOrWhiteSpace(staff.Role) ? entry.Role : this.Configuration.GetExistingOrDefaultStaffRole(staff.Role);
                    this.Configuration.Save();
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.EndChild();
        }

        ImGui.Spacing();
        if (ImGui.Button("Open Staff Database", new Vector2(160, 24)))
            this.Ui.ToggleStaffDatabaseUi();
        ImGui.SameLine();
        if (ImGui.Button("Close", new Vector2(80, 24)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }


    private void StaffRoleComboAndSave(StaffScheduleEntry entry)
    {
        var currentRole = string.IsNullOrWhiteSpace(entry.Role) ? "Photographer" : entry.Role;
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##StaffRole", currentRole))
            return;

        foreach (var role in GetKnownStaffRoles(this.Configuration))
        {
            var selected = string.Equals(currentRole, role, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(role, selected))
            {
                this.Configuration.SelectedStaffScheduleOrder = entry.Order;
                entry.Role = role;
                this.Configuration.Save();
                this.RefreshAutoShoutSchedule();
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static IReadOnlyList<string> GetKnownStaffRoles(Configuration config) => config.GetKnownStaffRoles();

    private void MoveSelectedStaffScheduleEntry(Configuration config, int direction)
    {
        var currentIndex = config.StaffSchedule.FindIndex(entry => entry.Order == config.SelectedStaffScheduleOrder);
        var targetIndex = currentIndex + direction;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= config.StaffSchedule.Count)
            return;

        (config.StaffSchedule[currentIndex], config.StaffSchedule[targetIndex]) = (config.StaffSchedule[targetIndex], config.StaffSchedule[currentIndex]);
        config.NormalizeStaffScheduleOrders();
        config.SelectedStaffScheduleOrder = targetIndex + 1;
        config.Save();
        this.RefreshAutoShoutSchedule();
    }

    private static bool MatchesStaffSearch(StaffDatabaseEntry entry, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return true;

        return (entry.Name?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (entry.Role?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string DisplayStaffMember(StaffScheduleEntry? entry)
    {
        if (entry is null)
            return "none";

        return string.IsNullOrWhiteSpace(entry.Name) ? "unnamed staff row" : entry.Name;
    }

    private static string DisplayStaffMemberWithRow(StaffScheduleEntry entry)
    {
        return string.IsNullOrWhiteSpace(entry.Name) ? $"row {entry.Order}" : $"{entry.Name}, row {entry.Order}";
    }

    private static IReadOnlyList<string> GetUniqueStaffNames(IEnumerable<StaffScheduleEntry> staffEntries)
    {
        return staffEntries
            .Select(entry => entry.Name?.Trim() ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int CountUniqueStaffNames(IEnumerable<StaffScheduleEntry> staffEntries)
    {
        return GetUniqueStaffNames(staffEntries).Count;
    }

    private static string FormatStaffNames(IEnumerable<StaffScheduleEntry> staffEntries)
    {
        var names = GetUniqueStaffNames(staffEntries);
        return names.Count == 0 ? "none active now" : string.Join(", ", names);
    }

    private static string GetNoCurrentStaffReason(Configuration config, DateTime utcNow)
    {
        var scheduledNow = config.BuildStaffScheduleTimeline()
            .Where(slot => slot.IsInsideEventWindow && utcNow >= slot.StartUtc && utcNow < slot.EndUtc)
            .Select(slot => slot.Entry)
            .ToList();

        if (scheduledNow.Count == 0)
            return $"Disabled because no {config.SelectedStaffRole.ToLowerInvariant()} staff is scheduled right now.";

        if (scheduledNow.All(entry => !entry.Available))
            return "Disabled because scheduled staff are not marked Active.";

        return "Disabled because no usable staff name is active right now.";
    }

    private static IReadOnlyList<string> GetStaffScheduleCheckLines(Configuration config, DateTime utcNow)
    {
        if (config.StaffSchedule.Count == 0)
            return new[] { "No staff rows added yet." };

        var timeline = config.BuildStaffScheduleTimeline();
        var activeAll = timeline
            .Where(slot => slot.IsInsideEventWindow && utcNow >= slot.StartUtc && utcNow < slot.EndUtc)
            .Select(slot => slot.Entry)
            .ToList();
        var activeAvailable = activeAll.Where(entry => entry.Available).ToList();
        var uniqueAvailable = GetUniqueStaffNames(activeAvailable);

        var lines = new List<string>();
        var emptyRows = config.StaffSchedule.Count(entry => string.IsNullOrWhiteSpace(entry.Name));
        var outsideRows = timeline.Count(slot => !slot.IsInsideEventWindow);
        var duplicateActive = activeAvailable
            .GroupBy(entry => entry.Name?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicateActive.Count > 0)
            lines.Add($"Warning: {string.Join(", ", duplicateActive)} appears more than once in the current staff schedule.");

        if (outsideRows > 0)
            lines.Add($"Warning: {outsideRows} staff row(s) are outside the staff window.");

        if (activeAll.Count > 0 && activeAvailable.Count == 0)
            lines.Add("Staff are scheduled now, but none are marked Active.");
        else if (uniqueAvailable.Count > 0)
            lines.Add($"{uniqueAvailable.Count} staff member(s) active now.");
        else
            lines.Add("No staff active now.");

        if (emptyRows > 0)
            lines.Add($"{emptyRows} row(s) still need a staff name.");

        if (lines.Count == 1 && lines[0] == "No staff active now." && emptyRows == 0 && outsideRows == 0 && duplicateActive.Count == 0)
            return new[] { "Schedule looks good." };

        return lines;
    }

    private void DrawEventSchedule(Configuration config)
    {
        config.EnsureEventWindowDefaults();

        // The event window is the date-aware boundary used by the scheduler.
        // Individual DJ rows stay simple HH:mm values, then get placed inside
        // this ST/UTC window when auto-shout calculates the event timeline.
        // Keep the controls compact and left-aligned so Start/End read as one
        // connected schedule block instead of two distant islands.
        var startX = ImGui.GetCursorPosX();
        var endX = startX + 260f;

        ImGui.TextDisabled("Start");
        ImGui.SameLine(endX);
        ImGui.TextDisabled("End");

        this.DateInputAndSave("##EventStartDate", config.EventStartDate, v => config.EventStartDate = v, 11);
        ImGui.SameLine();
        this.TimePickerAndSave("EventStart", config.EventStartTime, value => config.EventStartTime = value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Start time in FFXIV Server Time, which Venue Host treats as UTC.");

        ImGui.SameLine(endX);
        this.DateInputAndSave("##EventEndDate", config.EventEndDate, v => config.EventEndDate = v, 11);
        ImGui.SameLine();
        this.TimePickerAndSave("EventEnd", config.EventEndTime, value => config.EventEndTime = value);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("End time in FFXIV Server Time, which Venue Host treats as UTC.");

        ImGui.TextDisabled("Dates/times are ST / UTC. DJ row times are interpreted inside this event window.");
    }

    private void DrawEventDetails(Configuration config)
    {
        if (!ImGui.BeginTable("VenueHostEventDetails", 2, ImGuiTableFlags.SizingStretchProp))
            return;

        var labelColumnWidth = this.Configuration.ContrastModeEnabled ? 140f : 105f;
        ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, labelColumnWidth);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Venue");
        ImGui.TableNextColumn();
        this.InputAndSave("##VenueName", config.VenueName, v => config.VenueName = v, 160, EventDetailsInputWidth);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var giveawayEnabled = config.GiveawayEnabled;
        if (ImGui.Checkbox("Giveaway", ref giveawayEnabled))
        {
            config.GiveawayEnabled = giveawayEnabled;
            config.Save();
        }

        ImGui.TableNextColumn();
        if (!config.GiveawayEnabled)
            ImGui.BeginDisabled();

        this.InputAndSave("##GiveawayText", config.GiveawayText, v => config.GiveawayText = v, 220, EventDetailsInputWidth);

        if (!config.GiveawayEnabled)
            ImGui.EndDisabled();

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var commandEnabled = config.GiveawayCommandEnabled;
        if (!config.GiveawayEnabled)
            ImGui.BeginDisabled();

        ImGui.Indent(12f);
        if (ImGui.Checkbox("Command", ref commandEnabled))
        {
            config.GiveawayCommandEnabled = commandEnabled;
            config.Save();
        }

        if (!config.GiveawayEnabled)
            ImGui.EndDisabled();
        ImGui.Unindent(12f);

        ImGui.TableNextColumn();
        if (!config.GiveawayEnabled || !config.GiveawayCommandEnabled)
            ImGui.BeginDisabled();

        this.InputAndSave("##GiveawayCommand", config.GiveawayCommand, v => config.GiveawayCommand = v, 80, EventCommandInputWidth);

        if (!config.GiveawayEnabled || !config.GiveawayCommandEnabled)
            ImGui.EndDisabled();

        ImGui.EndTable();
    }

    private void DrawDjScheduleTable(Configuration config)
    {
        var warningRows = GetScheduleWarningRows(config);
        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        // Use a versioned table ID so changed default widths are not masked by
        // ImGui's persisted column layout from older builds.
        if (!ImGui.BeginTable("VenueHostDjScheduleV2", 6, tableFlags))
            return;

        ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, 58f);
        ImGui.TableSetupColumn("DJ", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Link", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        // Keep per-DJ time columns tight; these rows repeat often, so a compact
        // picker keeps DJ/link information from being squeezed unnecessarily.
        ImGui.TableSetupColumn("Start", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 78f);
        ImGui.TableSetupColumn("End", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 78f);
        ImGui.TableSetupColumn("Giveaway", ImGuiTableColumnFlags.WidthFixed, 72f);
        DrawCenteredTableHeaders("Order", "DJ", "Link", "Start", "End", "Giveaway");

        for (var i = 0; i < config.DjSchedule.Count; i++)
        {
            var entry = config.DjSchedule[i];
            ImGui.PushID($"DjScheduleRow{i}");
            ImGui.TableNextRow();
            var hasWarning = warningRows.Contains(entry.Order);
            var selected = config.SelectedDjOrder == entry.Order;
            if (selected)
            {
                var selectedColor = this.Configuration.ContrastModeEnabled
                    ? new Vector4(0.42f, 0.00f, 0.34f, 0.42f)
                    : new Vector4(0.16f, 0.32f, 0.52f, 0.22f);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ToImGuiColor(selectedColor));
            }

            ImGui.TableNextColumn();
            if (hasWarning)
            {
                var warningColor = this.Configuration.ContrastModeEnabled
                    ? new Vector4(1.00f, 0.95f, 0.00f, 0.35f)
                    : new Vector4(0.70f, 0.42f, 0.12f, 0.26f);
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ToImGuiColor(warningColor));
            }

            if (ImGui.RadioButton($"{entry.Order}##SelectedDj", selected))
            {
                config.SelectedDjOrder = entry.Order;
                config.Save();
            }

            ImGui.TableNextColumn();
            this.DjPickerAndSave(entry);

            ImGui.TableNextColumn();
            this.DrawLinkCell(entry);

            ImGui.TableNextColumn();
            CenterNextItem(70f);
            this.TimePickerAndSave("Start", entry.StartTime, value => entry.StartTime = value, width: 70f);

            ImGui.TableNextColumn();
            CenterNextItem(70f);
            this.TimePickerAndSave("End", entry.EndTime, value => entry.EndTime = value, width: 70f);

            ImGui.TableNextColumn();
            var giveawayEnabled = entry.GiveawayEnabled;
            CenterNextItem(22f);
            if (ImGui.Checkbox("##DjGiveaway", ref giveawayEnabled))
            {
                entry.GiveawayEnabled = giveawayEnabled;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Include giveaway line for this DJ when the global Giveaway option is enabled.");

            ImGui.PopID();
        }

        ImGui.EndTable();
    }


    /// <summary>
    /// Draws the stream link as clean clickable text in the lineup table.
    ///
    /// Permanent DJ links should be edited from the DJ Database, keeping the main
    /// event lineup focused and visually tidy.
    /// </summary>
    private void DrawLinkCell(DjScheduleEntry entry)
    {
        var link = (entry.DJLink ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(link))
        {
            ImGui.TextDisabled("No link");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("No stream link set for this DJ.");
            }

            return;
        }

        var canOpen = IsHttpLink(link);
        var displayLink = TrimTextToWidth(GetCompactDisplayLink(link), ImGui.GetContentRegionAvail().X);

        if (!canOpen)
        {
            ImGui.TextDisabled(displayLink);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Link must start with http:// or https://.");
            }

            return;
        }

        var linkColor = this.Configuration.ContrastModeEnabled
            ? new Vector4(0.00f, 1.00f, 0.95f, 1.00f)
            : new Vector4(0.35f, 0.65f, 1.00f, 1.00f);

        ImGui.PushStyleColor(ImGuiCol.Text, linkColor);
        ImGui.TextUnformatted(displayLink);
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip($"Open stream link:\n{link}");
        }

        if (ImGui.IsItemClicked())
        {
            OpenExternalLink(link);
        }
    }


    private static string GetCompactDisplayLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
            return link;

        var host = uri.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            host = host[4..];

        var path = uri.AbsolutePath.Trim('/');
        return string.IsNullOrWhiteSpace(path) ? host : $"{host}/{path}";
    }

    private static string TrimTextToWidth(string text, float maxWidth)
    {
        const string ellipsis = "...";

        if (string.IsNullOrEmpty(text) || maxWidth <= 0f)
        {
            return text;
        }

        if (ImGui.CalcTextSize(text).X <= maxWidth)
        {
            return text;
        }

        var availableWidth = Math.Max(0f, maxWidth - ImGui.CalcTextSize(ellipsis).X);
        var trimmed = text;

        while (trimmed.Length > 0 && ImGui.CalcTextSize(trimmed).X > availableWidth)
        {
            trimmed = trimmed[..^1];
        }

        return trimmed + ellipsis;
    }

    private static bool IsHttpLink(string? link)
    {
        return Uri.TryCreate(link, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static void OpenExternalLink(string? link)
    {
        if (!IsHttpLink(link))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = link,
            UseShellExecute = true,
        });
    }

    private void DrawScheduleButtons(Configuration config)
    {
        if (this.ColoredButton("Add DJ", new Vector2(100, 28), ButtonTone.Positive))
        {
            var wasEmpty = !config.DjSchedule.Any(Configuration.HasUsableDj);
            var last = config.DjSchedule.Count > 0 ? config.DjSchedule[^1] : null;
            var startTime = NormalizeTimeText(last?.EndTime ?? "18:00");
            var duration = last is null ? 60 : GetReasonableDurationMinutes(last.StartTime, last.EndTime);
            var endTime = AddMinutesToServerTime(startTime, duration);

            config.DjSchedule.Add(new DjScheduleEntry
            {
                Order = config.DjSchedule.Count + 1,
                DJName = string.Empty,
                DJLink = string.Empty,
                StartTime = startTime,
                EndTime = endTime,
                GiveawayEnabled = config.GiveawayEnabled,
            });
            config.NormalizeScheduleOrders();
            config.SelectedDjOrder = config.DjSchedule[^1].Order;
            if (wasEmpty)
                config.AutoCurrentDjShoutEnabled = true;

            config.Save();
            this.RefreshAutoShoutSchedule();
        }

        ImGui.SameLine();

        if (this.ColoredButton("Remove", new Vector2(90, 28), ButtonTone.Danger) && config.DjSchedule.Count > 0)
        {
            config.DjSchedule.RemoveAll(entry => entry.Order == config.SelectedDjOrder);
            config.NormalizeScheduleOrders();
            if (config.DjSchedule.Count == 0)
                config.SelectedDjOrder = 0;
            else
                config.SelectedDjOrder = Math.Clamp(config.SelectedDjOrder, 1, config.DjSchedule.Count);
            if (config.DjSchedule.Any(Configuration.HasUsableDj))
            {
                // Venue Host treats a lineup with at least one real DJ as ready
                // for auto-shout. This covers the common setup flow where staff
                // remove old rows one by one until only the next event DJ remains.
                config.AutoCurrentDjShoutEnabled = true;
            }
            else
            {
                config.AutoCurrentDjShoutEnabled = false;
            }

            config.Save();
            this.RefreshAutoShoutSchedule();
        }

        ImGui.SameLine();

        if (this.ColoredButton("Move Up", new Vector2(100, 28), ButtonTone.Neutral))
            this.MoveSelected(config, -1);

        ImGui.SameLine();

        if (this.ColoredButton("Move Down", new Vector2(110, 28), ButtonTone.Neutral))
            this.MoveSelected(config, 1);

        ImGui.SameLine();

        if (this.ColoredButton("Auto Times", new Vector2(105, 28), ButtonTone.Neutral))
            this.AutoFillTimesFromSelected(config);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sequence times from the selected row downward so each DJ starts when the previous DJ ends.");

        ImGui.SameLine();

        if (this.ColoredButton("DJ Database", new Vector2(120, 28), ButtonTone.Primary))
            this.Ui.ToggleDjDatabaseUi();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open saved DJ names and stream links.");

        var clearWidth = 130f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        if (availableWidth > clearWidth + spacing)
            ImGui.SameLine(ImGui.GetCursorPosX() + availableWidth - clearWidth);
        else
            ImGui.SameLine();

        if (this.ColoredButton("Clear Lineup", new Vector2(clearWidth, 28), ButtonTone.Warning))
            this.showClearLineupConfirmation = true;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clear all DJ rows after confirmation.");
    }

    private bool showClearLineupConfirmation;

    private void DrawClearLineupConfirmation(Configuration config)
    {
        if (this.showClearLineupConfirmation)
        {
            ImGui.OpenPopup("Clear lineup?##VenueHostClearLineupConfirm");
            this.showClearLineupConfirmation = false;
        }

        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var popupSize = new Vector2(430, 155);
        ImGui.SetNextWindowSize(popupSize, ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(windowPos + (windowSize - popupSize) * 0.5f, ImGuiCond.Appearing);

        var popupOpen = true;
        if (!ImGui.BeginPopupModal("Clear lineup?##VenueHostClearLineupConfirm", ref popupOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings))
            return;

        ImGui.TextWrapped("Clear the full DJ lineup?");
        ImGui.TextDisabled("This removes every DJ row. Use Add DJ to start a new lineup.");
        ImGui.Spacing();

        if (this.ColoredButton("Yes, clear lineup##ConfirmClearLineup", new Vector2(170, 28), ButtonTone.Danger))
        {
            config.DjSchedule.Clear();
            config.SelectedDjOrder = 0;
            config.AutoCurrentDjShoutEnabled = false;
            config.Save();
            this.RefreshAutoShoutSchedule();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (this.ColoredButton("Cancel##CancelClearLineup", new Vector2(120, 28), ButtonTone.Neutral))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void AutoFillTimesFromSelected(Configuration config)
    {
        var selectedIndex = config.DjSchedule.FindIndex(entry => entry.Order == config.SelectedDjOrder);
        if (selectedIndex < 0)
            return;

        // Sequence the lineup from the selected row downward. Each row starts
        // where the previous row ends, keeping that row's existing slot length
        // when it is reasonable. Very long/invalid beta-test ranges fall back
        // to a normal 60-minute DJ slot so one odd row does not poison the list.
        var currentStart = NormalizeTimeText(config.DjSchedule[selectedIndex].StartTime);

        for (var i = selectedIndex; i < config.DjSchedule.Count; i++)
        {
            var entry = config.DjSchedule[i];
            var duration = GetReasonableDurationMinutes(entry.StartTime, entry.EndTime);

            entry.StartTime = currentStart;
            entry.EndTime = AddMinutesToServerTime(currentStart, duration);
            currentStart = entry.EndTime;
        }

        config.Save();
        this.RefreshAutoShoutSchedule();
    }

    private void MoveSelected(Configuration config, int direction)
    {
        var currentIndex = config.DjSchedule.FindIndex(entry => entry.Order == config.SelectedDjOrder);
        var targetIndex = currentIndex + direction;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= config.DjSchedule.Count)
            return;

        (config.DjSchedule[currentIndex], config.DjSchedule[targetIndex]) = (config.DjSchedule[targetIndex], config.DjSchedule[currentIndex]);
        config.NormalizeScheduleOrders();
        config.SelectedDjOrder = targetIndex + 1;
        config.Save();
        this.RefreshAutoShoutSchedule();
    }




    private void DrawManualActions(Configuration config)
    {
        var selected = config.GetSelectedDj();
        var next = config.GetNextDjAfter(selected);
        var hasSelectedDj = HasUsableDj(selected);
        var hasNextDj = HasUsableDj(next);

        ImGui.TextUnformatted("Manual actions");
        ImGui.TextDisabled($"Selected: {DisplayDj(selected)} | Next: {DisplayDj(next)}");

        // Manual actions intentionally mirror the valid workflow: a current DJ
        // shout/tell needs a selected DJ, while a transition shout also needs a
        // following DJ. This prevents blank or final-row macros from being sent.
        if (!hasSelectedDj)
            ImGui.BeginDisabled();

        if (this.ColoredButton("Shout Current DJ", new Vector2(210, 32), ButtonTone.Primary))
            this.Shout.SendCurrentDjShout();

        if (!hasSelectedDj)
            ImGui.EndDisabled();

        ImGui.SameLine();

        if (!hasSelectedDj || !hasNextDj)
            ImGui.BeginDisabled();

        if (this.ColoredButton("Thank Current / Welcome Next", new Vector2(270, 32), ButtonTone.Primary))
            this.Shout.SendTransitionShout();

        if (!hasSelectedDj || !hasNextDj)
            ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(hasNextDj ? "Send a transition shout from the selected DJ to the next DJ." : "Disabled because there is no next DJ after the selected row.");

        ImGui.SameLine();

        if (!hasSelectedDj)
            ImGui.BeginDisabled();

        if (this.ColoredButton("Tell DJ to Target", new Vector2(230, 32), ButtonTone.Positive))
            this.Shout.TellCurrentDjToTarget();

        if (!hasSelectedDj)
            ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(hasSelectedDj ? "Sends the selected DJ info to your current target using /tell <t>." : "Disabled because the selected row has no DJ yet.");

        ImGui.Spacing();
        ImGui.TextDisabled("Quick auto settings");

        var autoEnabled = config.AutoCurrentDjShoutEnabled;
        if (ImGui.Checkbox("Enable automatic DJ shouts", ref autoEnabled))
        {
            config.AutoCurrentDjShoutEnabled = autoEnabled;
            config.Save();
            this.RefreshAutoShoutSchedule();
        }

        var intervalMinutes = config.AutoCurrentDjShoutIntervalMinutes;
        if (DrawIntStepper("Auto DJ shout interval##QuickDjAutoInterval", ref intervalMinutes, 1, 120, 1, "min"))
        {
            config.AutoCurrentDjShoutIntervalMinutes = intervalMinutes;
            config.Save();
            this.RefreshAutoShoutSchedule();
        }
    }

    private void DrawStatusPanel(Configuration config)
    {
        var serverNow = DateTime.UtcNow;
        var scheduledCurrent = config.GetCurrentServerTimeDj(serverNow);
        var nextAutoAt = this.DjAutoShout.NextShoutAtUtc;
        var nextAutoText = nextAutoAt.HasValue ? FormatStatusTime(nextAutoAt.Value, includeSeconds: true) : "none";
        var nextAutoDj = nextAutoAt.HasValue ? config.GetCurrentServerTimeDj(nextAutoAt.Value) : null;
        var autoDjText = scheduledCurrent is null ? "none active now" : DisplayDjWithRow(scheduledCurrent);
        var currentSlot = scheduledCurrent is null ? null : config.GetTimelineSlotForEntry(scheduledCurrent);
        var currentSlotText = currentSlot is null ? "none" : FormatStatusRange(currentSlot.StartUtc, currentSlot.EndUtc);
        var staffWindowText = config.TryGetStaffWindow(out var staffStartUtc, out var staffEndUtc)
            ? $"{staffStartUtc:yyyy-MM-dd HH:mm} → {staffEndUtc:yyyy-MM-dd HH:mm} ST"
            : "invalid";
        var nextTransitionText = GetNextTransitionText(config, scheduledCurrent);

        ImGui.TextUnformatted("Auto schedule");
        if (ImGui.BeginTable("VenueHostStatusPanel", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 135f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

            this.DrawStatusRow("Auto Shout", config.AutoCurrentDjShoutEnabled ? "ON" : "OFF");
            this.DrawStatusRow("Server Time", $"{serverNow:yyyy-MM-dd HH:mm:ss} ST / UTC");
            this.DrawStatusRow("Auto DJ", autoDjText);
            this.DrawStatusRow("Current Slot", currentSlotText);
            if (scheduledCurrent is null && nextAutoDj is not null)
                this.DrawStatusRow("Next DJ", $"{DisplayDjWithRow(nextAutoDj)} at {nextAutoText}");
            this.DrawStatusRow("Next Transition", nextTransitionText);
            this.DrawStatusRow("Next Shout", nextAutoText);
            this.DrawStatusRow("Next Shout Type", this.DjAutoShout.GetNextShoutTypeDescription());

            ImGui.EndTable();
        }
    }


    private void DrawMascotCorner()
    {
        var texture = this.Ui.MascotTexture;
        if (texture is null)
            return;

        var wrap = texture.GetWrapOrDefault();
        if (wrap is null)
            return;

        var windowSize = ImGui.GetWindowSize();
        if (windowSize.X < 760f || windowSize.Y < 560f)
            return;

        // Keep the mascot decorative and predictable: anchored to the bottom-right
        // corner of the main window, away from tables and action buttons. Drawing it
        // directly on the window draw list avoids taking layout space from the UI.
        const float mascotSize = 90f;
        const float rightPadding = 36f;
        const float bottomPadding = 34f;

        var windowPos = ImGui.GetWindowPos();
        var imageMin = new Vector2(
            windowPos.X + windowSize.X - rightPadding - mascotSize,
            windowPos.Y + windowSize.Y - bottomPadding - mascotSize);
        var imageMax = imageMin + new Vector2(mascotSize, mascotSize);

        ImGui.GetWindowDrawList().AddImage(wrap.Handle, imageMin, imageMax);
    }

    private void DrawStatusRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        ImGui.TextWrapped(value);
    }

    private void DrawScheduleWarnings(Configuration config)
    {
        var warnings = config.GetScheduleWarnings();

        ImGui.Spacing();
        ImGui.TextUnformatted("Schedule check");
        if (warnings.Count == 0)
        {
            ImGui.TextDisabled("Schedule looks good.");
            return;
        }

        foreach (var warning in warnings)
            ImGui.BulletText(warning);
    }

    private static HashSet<int> GetScheduleWarningRows(Configuration config)
    {
        var rows = new HashSet<int>();

        foreach (var duplicateGroup in config.DjSchedule
                     .Where(entry => Configuration.HasUsableDj(entry) && Configuration.TryParseServerTime(entry.StartTime, out _) && Configuration.TryParseServerTime(entry.EndTime, out _))
                     .GroupBy(entry => $"{NormalizeTimeText(entry.StartTime)}->{NormalizeTimeText(entry.EndTime)}")
                     .Where(group => group.Count() > 1))
        {
            foreach (var duplicate in duplicateGroup)
                rows.Add(duplicate.Order);
        }

        foreach (var outsideSlot in config.BuildEventTimeline().Where(slot => !slot.IsInsideEventWindow))
            rows.Add(outsideSlot.Entry.Order);

        int? previousEndMinutes = null;
        int? previousStartMinutes = null;
        int? previousRawStartMinutes = null;
        bool previousCrossedMidnight = false;
        DjScheduleEntry? previousEntry = null;

        foreach (var entry in config.DjSchedule.OrderBy(entry => entry.Order))
        {
            if (!Configuration.HasUsableDj(entry))
                continue;

            if (!Configuration.TryParseServerTime(entry.StartTime, out var start) ||
                !Configuration.TryParseServerTime(entry.EndTime, out var end) ||
                start == end)
            {
                rows.Add(entry.Order);
                previousEntry = null;
                previousStartMinutes = null;
                previousEndMinutes = null;
                previousRawStartMinutes = null;
                previousCrossedMidnight = false;
                continue;
            }

            var rawStartMinutes = (int)start.TotalMinutes;
            var rawEndMinutes = (int)end.TotalMinutes;
            var crossesMidnight = rawEndMinutes <= rawStartMinutes;
            var startMinutes = rawStartMinutes;
            var endMinutes = rawEndMinutes;
            if (crossesMidnight)
                endMinutes += 24 * 60;

            if (previousRawStartMinutes.HasValue && rawStartMinutes < previousRawStartMinutes.Value && !previousCrossedMidnight)
            {
                if (previousEntry is not null)
                    rows.Add(previousEntry.Order);
                rows.Add(entry.Order);
            }

            if (previousStartMinutes.HasValue)
            {
                while (startMinutes < previousStartMinutes.Value)
                    startMinutes += 24 * 60;

                while (endMinutes <= startMinutes)
                    endMinutes += 24 * 60;
            }

            if (previousEndMinutes.HasValue && previousEntry is not null && startMinutes != previousEndMinutes.Value)
            {
                rows.Add(previousEntry.Order);
                rows.Add(entry.Order);
            }

            previousEntry = entry;
            previousStartMinutes = startMinutes;
            previousEndMinutes = endMinutes;
            previousRawStartMinutes = rawStartMinutes;
            previousCrossedMidnight = crossesMidnight;
        }

        return rows;
    }


    private static void DrawCenteredTableHeaders(params string[] labels)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        for (var column = 0; column < labels.Length; column++)
        {
            ImGui.TableSetColumnIndex(column);
            var label = labels[column];
            if (string.IsNullOrEmpty(label))
                continue;

            var textWidth = ImGui.CalcTextSize(label).X;
            CenterNextItem(textWidth);
            ImGui.TableHeader(label);
        }
    }

    private static void DrawCenteredText(string text)
    {
        var textWidth = ImGui.CalcTextSize(text).X;
        CenterNextItem(textWidth);
        ImGui.TextUnformatted(text);
    }

    private static void DrawCenteredDisabledText(string text)
    {
        var textWidth = ImGui.CalcTextSize(text).X;
        CenterNextItem(textWidth);
        ImGui.TextDisabled(text);
    }

    private static void CenterNextItem(float itemWidth)
    {
        var availableWidth = ImGui.GetContentRegionAvail().X;
        if (availableWidth > itemWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((availableWidth - itemWidth) * 0.5f));
    }

    private static uint ToImGuiColor(Vector4 color)
    {
        var r = (uint)(Math.Clamp(color.X, 0f, 1f) * 255f);
        var g = (uint)(Math.Clamp(color.Y, 0f, 1f) * 255f);
        var b = (uint)(Math.Clamp(color.Z, 0f, 1f) * 255f);
        var a = (uint)(Math.Clamp(color.W, 0f, 1f) * 255f);
        return r | (g << 8) | (b << 16) | (a << 24);
    }

    private bool ColoredButton(string label, Vector2 size, ButtonTone tone)
    {
        var (normal, hovered, active) = GetButtonColors(tone);
        var contrast = this.Configuration.ContrastModeEnabled;
        ImGui.PushStyleColor(ImGuiCol.Button, normal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);

        // In Contrast Mode, buttons stay dark with neon text/borders instead of
        // using bright solid fills. This better matches screen-reader-friendly,
        // high-contrast expectations while avoiding a wall of traffic-light blocks.
        if (contrast)
        {
            var accent = GetButtonAccentColor(tone);
            ImGui.PushStyleColor(ImGuiCol.Text, accent);
            ImGui.PushStyleColor(ImGuiCol.Border, accent);
        }

        // Keep action labels visually centred, especially in Contrast Mode where
        // larger text and thicker borders can make labels feel slightly offset.
        ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.5f, 0.5f));
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleVar();

        if (contrast)
            ImGui.PopStyleColor(2);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    private (Vector4 Normal, Vector4 Hovered, Vector4 Active) GetButtonColors(ButtonTone tone)
    {
        if (this.Configuration.ContrastModeEnabled)
        {
            return tone switch
            {
                ButtonTone.Primary => (new Vector4(0.00f, 0.025f, 0.030f, 1f), new Vector4(0.00f, 0.11f, 0.12f, 1f), new Vector4(0.00f, 0.20f, 0.22f, 1f)),
                ButtonTone.Positive => (new Vector4(0.00f, 0.030f, 0.010f, 1f), new Vector4(0.00f, 0.12f, 0.04f, 1f), new Vector4(0.00f, 0.22f, 0.08f, 1f)),
                ButtonTone.Warning => (new Vector4(0.055f, 0.025f, 0.000f, 1f), new Vector4(0.16f, 0.075f, 0.00f, 1f), new Vector4(0.26f, 0.12f, 0.00f, 1f)),
                ButtonTone.Danger => (new Vector4(0.045f, 0.000f, 0.038f, 1f), new Vector4(0.15f, 0.00f, 0.125f, 1f), new Vector4(0.26f, 0.00f, 0.22f, 1f)),
                _ => (new Vector4(0.00f, 0.025f, 0.018f, 1f), new Vector4(0.00f, 0.10f, 0.07f, 1f), new Vector4(0.00f, 0.18f, 0.12f, 1f)),
            };
        }

        return tone switch
        {
            ButtonTone.Primary => (new Vector4(0.13f, 0.32f, 0.55f, 1f), new Vector4(0.18f, 0.42f, 0.70f, 1f), new Vector4(0.10f, 0.26f, 0.45f, 1f)),
            ButtonTone.Positive => (new Vector4(0.12f, 0.45f, 0.24f, 1f), new Vector4(0.17f, 0.58f, 0.32f, 1f), new Vector4(0.09f, 0.34f, 0.18f, 1f)),
            ButtonTone.Warning => (new Vector4(0.55f, 0.32f, 0.10f, 1f), new Vector4(0.70f, 0.42f, 0.15f, 1f), new Vector4(0.42f, 0.23f, 0.08f, 1f)),
            ButtonTone.Danger => (new Vector4(0.52f, 0.16f, 0.16f, 1f), new Vector4(0.68f, 0.22f, 0.22f, 1f), new Vector4(0.38f, 0.11f, 0.11f, 1f)),
            _ => (new Vector4(0.34f, 0.34f, 0.34f, 1f), new Vector4(0.43f, 0.43f, 0.43f, 1f), new Vector4(0.27f, 0.27f, 0.27f, 1f)),
        };
    }

    private enum ButtonTone
    {
        Neutral,
        Primary,
        Positive,
        Warning,
        Danger,
    }

    private static Vector4 GetButtonAccentColor(ButtonTone tone)
    {
        return tone switch
        {
            ButtonTone.Primary => new Vector4(0.00f, 1.00f, 0.95f, 1f),
            ButtonTone.Positive => new Vector4(0.10f, 1.00f, 0.25f, 1f),
            ButtonTone.Warning => new Vector4(1.00f, 0.62f, 0.00f, 1f),
            ButtonTone.Danger => new Vector4(1.00f, 0.05f, 0.85f, 1f),
            _ => new Vector4(0.58f, 1.00f, 0.78f, 1f),
        };
    }


    private void DjPickerAndSave(DjScheduleEntry entry)
    {
        var displayName = string.IsNullOrWhiteSpace(entry.DJName) ? "Pick DJ..." : entry.DJName;
        ImGui.SetNextItemWidth(-1);

        if (ImGui.Button($"{displayName}##DjPickerButton", new Vector2(-1, 0)))
        {
            this.djPickerSearchTextByOrder[entry.Order] = string.Empty;
            this.djPickerQuickAddNameByOrder[entry.Order] = string.IsNullOrWhiteSpace(entry.DJName) ? string.Empty : entry.DJName;
            this.djPickerQuickAddLinkByOrder[entry.Order] = string.IsNullOrWhiteSpace(entry.DJLink) ? string.Empty : entry.DJLink;
            ImGui.OpenPopup("DjPickerPopup");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pick a DJ from the DJ Database. The stream link is filled automatically.");

        if (!ImGui.BeginPopup("DjPickerPopup"))
            return;

        ImGui.TextUnformatted("Pick DJ from database");
        ImGui.TextDisabled("Type to filter, then click a DJ to fill this lineup row.");
        ImGui.Separator();

        if (!this.djPickerSearchTextByOrder.TryGetValue(entry.Order, out var searchText))
            searchText = string.Empty;

        ImGui.SetNextItemWidth(260f);
        if (ImGui.InputText("Search##DjPickerSearch", ref searchText, 80))
            this.djPickerSearchTextByOrder[entry.Order] = searchText;

        ImGui.Spacing();

        this.Configuration.NormalizeDjDatabase();

        var matches = this.Configuration.DjDatabase
            .Where(dbEntry => MatchesDjSearch(dbEntry, searchText))
            .OrderBy(dbEntry => dbEntry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
        {
            ImGui.TextDisabled("No DJs found in the database.");
        }
        else
        {
            var visibleRows = Math.Min(matches.Count, 8);
            if (ImGui.BeginChild("DjPickerResults", new Vector2(420, visibleRows * 28f + 8f), true, ImGuiWindowFlags.None))
            {
                for (var matchIndex = 0; matchIndex < matches.Count; matchIndex++)
                {
                    var dbEntry = matches[matchIndex];
                    var label = string.IsNullOrWhiteSpace(dbEntry.Link)
                        ? dbEntry.Name
                        : $"{dbEntry.Name}  —  {dbEntry.Link}";

                    if (ImGui.Selectable($"{label}##Pick{matchIndex}"))
                    {
                        var hadUsableDjBefore = this.Configuration.DjSchedule.Any(Configuration.HasUsableDj);
                        entry.DJName = dbEntry.Name;
                        entry.DJLink = dbEntry.Link;
                        this.EnableAutoShoutWhenFirstDjIsAdded(hadUsableDjBefore);
                        this.Configuration.Save();
                        this.RefreshAutoShoutSchedule();
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
            ImGui.EndChild();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Quick add DJ");
        ImGui.TextDisabled("Add a missing DJ to the database and fill this lineup row.");

        if (!this.djPickerQuickAddNameByOrder.TryGetValue(entry.Order, out var quickName))
            quickName = string.Empty;

        if (!this.djPickerQuickAddLinkByOrder.TryGetValue(entry.Order, out var quickLink))
            quickLink = string.Empty;

        if (string.IsNullOrWhiteSpace(quickName) && !string.IsNullOrWhiteSpace(searchText) && matches.Count == 0)
            quickName = searchText.Trim();

        ImGui.SetNextItemWidth(220f);
        if (ImGui.InputText("Name##DjPickerQuickAddName", ref quickName, 80))
            this.djPickerQuickAddNameByOrder[entry.Order] = quickName;

        ImGui.SetNextItemWidth(420f);
        if (ImGui.InputText("Link##DjPickerQuickAddLink", ref quickLink, 240))
            this.djPickerQuickAddLinkByOrder[entry.Order] = quickLink;

        var canQuickAdd = !string.IsNullOrWhiteSpace(quickName);
        if (!canQuickAdd)
            ImGui.BeginDisabled();

        if (ImGui.Button("Quick Add + Use", new Vector2(140, 24)))
        {
            var requestedName = (quickName ?? string.Empty).Trim();
            var requestedLink = (quickLink ?? string.Empty).Trim();
            var existingEntry = this.Configuration.DjDatabase.FirstOrDefault(dbEntry =>
                string.Equals((dbEntry.Name ?? string.Empty).Trim(), requestedName, StringComparison.OrdinalIgnoreCase));

            string nameToSave;
            string linkToSave;
            if (existingEntry is not null)
            {
                nameToSave = existingEntry.Name;

                if (string.IsNullOrWhiteSpace(existingEntry.Link) && !string.IsNullOrWhiteSpace(requestedLink))
                    existingEntry.Link = requestedLink;

                linkToSave = existingEntry.Link;
            }
            else
            {
                nameToSave = this.Configuration.GetUniqueDjDatabaseName(requestedName);
                linkToSave = requestedLink;

                this.Configuration.DjDatabase.Add(new DjDatabaseEntry
                {
                    Name = nameToSave,
                    Link = linkToSave,
                });
            }

            this.Configuration.NormalizeDjDatabase();

            var hadUsableDjBefore = this.Configuration.DjSchedule.Any(Configuration.HasUsableDj);
            entry.DJName = nameToSave;
            entry.DJLink = linkToSave;
            this.EnableAutoShoutWhenFirstDjIsAdded(hadUsableDjBefore);
            this.Configuration.Save();
            this.RefreshAutoShoutSchedule();

            this.djPickerQuickAddNameByOrder[entry.Order] = string.Empty;
            this.djPickerQuickAddLinkByOrder[entry.Order] = string.Empty;

            ImGui.CloseCurrentPopup();
        }

        if (!canQuickAdd)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Open DJ Database", new Vector2(140, 24)))
            this.Ui.ToggleDjDatabaseUi();

        ImGui.SameLine();
        if (ImGui.Button("Close", new Vector2(80, 24)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }


    private void EnableAutoShoutWhenFirstDjIsAdded(bool hadUsableDjBefore)
    {
        if (hadUsableDjBefore)
            return;

        if (this.Configuration.DjSchedule.Any(Configuration.HasUsableDj))
            this.Configuration.AutoCurrentDjShoutEnabled = true;
    }

    private static bool MatchesDjSearch(DjDatabaseEntry entry, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return true;

        return (entry.Name?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (entry.Link?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void DateInputAndSave(string label, string value, Action<string> setter, int maxLength)
    {
        var editedValue = value ?? string.Empty;
        ImGui.SetNextItemWidth(110f);
        if (ImGui.InputText(label, ref editedValue, maxLength))
        {
            setter(editedValue.Trim());
            this.Configuration.Save();
            this.RefreshAutoShoutSchedule();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use yyyy-MM-dd, for example 2026-05-16.");
    }

    private void TimePickerAndSave(string label, string value, Action<string> setter, float width = 110f)
    {
        var displayValue = NormalizeTimeText(value);
        var popupId = $"{label}TimePickerPopup";

        if (!string.Equals(displayValue, value ?? string.Empty, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(value))
        {
            setter(displayValue);
            this.Configuration.Save();
        }

        ImGui.SetNextItemWidth(width);
        if (ImGui.Button($"{displayValue} ▼##{label}TimePickerButton", new Vector2(width, 0)))
            ImGui.OpenPopup(popupId);

        if (!ImGui.BeginPopup(popupId))
            return;

        var time = ParseOrDefaultTime(displayValue);
        var hour = time.Hours;
        var minute = time.Minutes;

        ImGui.TextUnformatted($"{label} Time (ST / UTC)");
        ImGui.Separator();
        ImGui.TextDisabled("Use +/- for safe edits, or type HH:mm below.");
        ImGui.Spacing();

        ImGui.TextUnformatted("Hour");
        ImGui.SameLine(80f);
        if (ImGui.Button($"-##{label}HourDown", new Vector2(32, 24)))
            this.SetTimeAndSave(hour - 1, minute, setter);
        ImGui.SameLine();
        ImGui.TextUnformatted(hour.ToString("00"));
        ImGui.SameLine();
        if (ImGui.Button($"+##{label}HourUp", new Vector2(32, 24)))
            this.SetTimeAndSave(hour + 1, minute, setter);

        ImGui.TextUnformatted("Minute");
        ImGui.SameLine(80f);
        if (ImGui.Button($"-5##{label}MinuteDown", new Vector2(32, 24)))
            this.SetTimeAndSave(hour, minute - 5, setter);
        ImGui.SameLine();
        ImGui.TextUnformatted(minute.ToString("00"));
        ImGui.SameLine();
        if (ImGui.Button($"+5##{label}MinuteUp", new Vector2(32, 24)))
            this.SetTimeAndSave(hour, minute + 5, setter);

        ImGui.Spacing();
        ImGui.TextUnformatted("Quick set");
        if (ImGui.Button($"Now ST##{label}Now", new Vector2(80, 24)))
        {
            var now = DateTime.UtcNow.TimeOfDay;
            this.SetTimeAndSave(now.Hours, RoundToNearestFive(now.Minutes), setter);
        }

        ImGui.SameLine();
        if (ImGui.Button($"Next 15m##{label}Next15", new Vector2(90, 24)))
        {
            var now = DateTime.UtcNow.TimeOfDay;
            var roundedMinute = ((now.Minutes + 14) / 15) * 15;
            this.SetTimeAndSave(now.Hours, roundedMinute, setter);
        }

        ImGui.Spacing();
        var typedValue = displayValue;
        ImGui.SetNextItemWidth(110f);
        if (ImGui.InputText($"Type HH:mm##{label}TypedTime", ref typedValue, 6))
        {
            if (Configuration.TryParseServerTime(typedValue, out var typedTime))
            {
                setter(FormatTime(typedTime.Hours, typedTime.Minutes));
                this.Configuration.Save();
                this.RefreshAutoShoutSchedule();
            }
        }

        ImGui.Spacing();
        if (ImGui.Button($"Close##{label}Close", new Vector2(80, 24)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void SetTimeAndSave(int hour, int minute, Action<string> setter)
    {
        while (minute < 0)
        {
            minute += 60;
            hour--;
        }

        while (minute >= 60)
        {
            minute -= 60;
            hour++;
        }

        hour = ((hour % 24) + 24) % 24;
        setter(FormatTime(hour, minute));
        this.Configuration.Save();
        this.RefreshAutoShoutSchedule();
    }

    private static TimeSpan ParseOrDefaultTime(string value)
    {
        return Configuration.TryParseServerTime(value, out var parsed) ? parsed : TimeSpan.Zero;
    }

    private static string NormalizeTimeText(string? value)
    {
        return Configuration.TryParseServerTime(value, out var parsed)
            ? FormatTime(parsed.Hours, parsed.Minutes)
            : "00:00";
    }

    private static int RoundToNearestFive(int minute)
    {
        return (int)Math.Round(minute / 5.0, MidpointRounding.AwayFromZero) * 5;
    }

    private static string FormatTime(int hour, int minute)
    {
        hour = ((hour % 24) + 24) % 24;
        minute = ((minute % 60) + 60) % 60;
        return $"{hour:00}:{minute:00}";
    }

    private void InputAndSave(string label, string value, Action<string> setter, int maxLength, float width = -1f)
    {
        var editedValue = value ?? string.Empty;
        ImGui.SetNextItemWidth(width);
        if (ImGui.InputText(label, ref editedValue, maxLength))
        {
            setter(editedValue);
            this.Configuration.Save();
        }
    }

    private void InputEntryAndSave(string label, string value, Action<string> setter, int maxLength = 240)
    {
        var editedValue = value ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText(label, ref editedValue, maxLength))
        {
            setter(editedValue);
            this.Configuration.Save();
        }
    }


    private static string AddMinutesToServerTime(string value, int minutesToAdd)
    {
        var baseTime = Configuration.TryParseServerTime(value, out var parsed) ? parsed : TimeSpan.Zero;
        var totalMinutes = ((int)baseTime.TotalMinutes + minutesToAdd) % (24 * 60);
        if (totalMinutes < 0)
            totalMinutes += 24 * 60;

        return FormatTime(totalMinutes / 60, totalMinutes % 60);
    }

    private static int GetDurationMinutes(string startText, string endText)
    {
        if (!Configuration.TryParseServerTime(startText, out var start) || !Configuration.TryParseServerTime(endText, out var end))
            return 60;

        var startMinutes = (int)start.TotalMinutes;
        var endMinutes = (int)end.TotalMinutes;
        if (endMinutes <= startMinutes)
            endMinutes += 24 * 60;

        return Math.Max(1, endMinutes - startMinutes);
    }

    private static int GetReasonableDurationMinutes(string startText, string endText)
    {
        var duration = GetDurationMinutes(startText, endText);

        // DJ slots are usually short. If a row accidentally spans most of a day
        // during setup, treat it as a malformed test value and use one hour.
        return duration is > 0 and <= 8 * 60 ? duration : 60;
    }

    private static string GetNextTransitionText(Configuration config, DjScheduleEntry? current)
    {
        if (current is null)
            return "none";

        var next = config.GetNextDjAfter(current);
        if (next is null)
            return "none";

        var currentName = string.IsNullOrWhiteSpace(current.DJName) ? $"slot {current.Order}" : current.DJName;
        var nextName = string.IsNullOrWhiteSpace(next.DJName) ? $"slot {next.Order}" : next.DJName;
        var nextSlot = config.GetTimelineSlotForEntry(next);
        var transitionTime = nextSlot is null
            ? (string.IsNullOrWhiteSpace(next.StartTime) ? "unknown" : $"{next.StartTime} ST")
            : FormatStatusTime(nextSlot.StartUtc, includeSeconds: false);

        return $"{transitionTime}, {currentName} → {nextName}";
    }


    private static string FormatStatusRange(DateTime startUtc, DateTime endUtc)
    {
        return $"{startUtc:HH:mm} → {endUtc:HH:mm} ST";
    }

    private static string FormatStatusTime(DateTime utcTime, bool includeSeconds)
    {
        return includeSeconds ? $"{utcTime:HH:mm:ss} ST" : $"{utcTime:HH:mm} ST";
    }

    private static bool HasUsableDj(DjScheduleEntry? entry)
    {
        return entry is not null && !string.IsNullOrWhiteSpace(entry.DJName);
    }

    private static string DisplayDjWithRow(DjScheduleEntry? entry)
    {
        if (entry is null)
            return "none";

        return string.IsNullOrWhiteSpace(entry.DJName) ? $"slot {entry.Order}" : $"{entry.DJName}, row {entry.Order}";
    }

    private static string DisplayDj(DjScheduleEntry? entry)
    {
        if (entry is null)
            return "none";

        return string.IsNullOrWhiteSpace(entry.DJName) ? $"slot {entry.Order}" : $"{entry.Order}. {entry.DJName}";
    }
}
