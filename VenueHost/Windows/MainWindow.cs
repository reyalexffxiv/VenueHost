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
public sealed partial class MainWindow : Window, IDisposable
{

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
        : base("VenueHost Backstage###VenueHostMain",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        // The full dashboard has a fixed approved layout; compact mode is separate.
        this.Size = ExpandedFixedSize;
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = ExpandedWindowConstraints;
        this.AllowPinning = false;
        this.AllowClickthrough = false;
    }

    private bool showClearStaffListConfirmation;
    private const string MacroDelayLabel = "DELAY BETWEEN LINES (SEC)";
    private readonly Dictionary<int, string> djPickerSearchTextByOrder = new();
    private readonly Dictionary<int, string> djPickerQuickAddNameByOrder = new();
    private readonly Dictionary<int, string> djPickerQuickAddLinkByOrder = new();
    private bool djPickerCreatingNew;
    private readonly Dictionary<int, string> staffPickerSearchTextByOrder = new();
    private bool staffPickerCreatingNew;
    private string staffPickerNewName = string.Empty;
    private string staffPickerNewRole = "Photographer";

    // Floating custom-shout draft. Save commits; Cancel/Close confirm only real edits.
    private bool eventShoutEditorOpen;
    private bool eventShoutEditorFocusRequested;
    // Opening snapshot is the reference for real user edits; normalized saved fields may differ.
    private EventShoutEntry? eventShoutOpeningDraft;
    private bool eventShoutEditorIsNew;
    private int eventShoutEditorOrder;
    private string eventShoutEditorName = string.Empty;
    private string eventShoutEditorMacro = string.Empty;
    private string eventShoutEditorStartTime = "18:00";
    private string eventShoutEditorEndTime = "23:00";
    private float eventShoutEditorDelaySeconds = 2f;

    public void Dispose()
    {
    }

    /// <summary>Clear UI-only drafts when a different venue becomes active.</summary>
    /// <summary>Don't silently throw away unsaved event or announcement drafts on venue switch.</summary>
    public bool HasUnsavedProfileDrafts()
        => (this.eventEditorVisible && this.EventEditorDraftDirty()) ||
           (this.eventShoutEditorOpen && this.ShoutDraftDirty(this.Configuration)) ||
           (this.announcementMacroKind != AnnouncementMacroKind.None &&
            this.AnnouncementMacroDirty(this.Configuration));

    public void ResetForProfileSwitch()
    {
        this.eventShoutEditorOpen = false;
        this.eventShoutOpeningDraft = null;
        this.eventShoutDiscardRequested = false;
        this.announcementMacroKind = AnnouncementMacroKind.None;
        this.announcementMacroDiscardRequested = false;
        this.eventEditorVisible = false;
        this.eventEditorPendingOpen = false;
        this.eventEditorDiscardRequested = false;
        this.overviewSnapshot = null;
        this.overviewSnapshotAtUtc = DateTime.MinValue;
        this.overviewTimelineAtUtc = DateTime.MinValue;
        this.staffTimeDraftEntry = null;
        this.staffRosterSearch = string.Empty;
        this.staffCollapsedRoles.Clear();
        this.djPickerSearchTextByOrder.Clear();
        this.staffPickerSearchTextByOrder.Clear();
    }

    /// <summary>
    /// Refreshes all schedulers after the event dates change. This is an explicit
    /// schedule edit, not an ordinary UI redraw; never reset event timers on Draw().
    /// </summary>
    private void RefreshAutoShoutSchedule()
    {
        this.DjAutoShout.Refresh();
        this.StaffAutoShout.Refresh();
        this.EventShouts.Refresh(); // Custom shouts now follow the event's dates too.
    }

    /// <summary>
    /// Only refresh the scheduler owning the edited time/date field.
    /// Editing a draft event-shout row must not restart any live scheduler.
    /// </summary>
    private void RefreshScheduleForEditedField(string fieldId)
    {
        if (fieldId.StartsWith("EventShoutEditor", StringComparison.Ordinal))
            return;

        if (fieldId.Contains("EventShout", StringComparison.Ordinal))
        {
            this.EventShouts.Refresh();
            return;
        }

        if (fieldId.StartsWith("Staff", StringComparison.Ordinal) ||
            fieldId.StartsWith("##Staff", StringComparison.Ordinal))
        {
            this.StaffAutoShout.Refresh();
            return;
        }

        if (fieldId.StartsWith("Event", StringComparison.Ordinal) ||
            fieldId.StartsWith("##Event", StringComparison.Ordinal))
        {
            this.RefreshAutoShoutSchedule();
            return;
        }

        this.DjAutoShout.Refresh();
    }

    // The main window is the positioning anchor for floating editors/previews,
    // even after drawing switches to a child or another ImGui window.
    private Vector2 floatingAnchorPosition;
    private Vector2 floatingAnchorSize;

    private void CenterNextFloatingWindow(Vector2 size)
        => ImGui.SetNextWindowPos(
            BackstageDesign.CenteredWindowPosition(this.floatingAnchorPosition, this.floatingAnchorSize, size),
            ImGuiCond.Appearing);

    public override void Draw()
    {
        var highContrast = this.Configuration.OverviewHighContrastPreviewEnabled;
        using var palette = BackstageDesign.PushHighContrast(highContrast);
        using (var pageTheme = UiTheme.PushContrastIfEnabled(
                   highContrast && this.activePage != BackstagePage.Overview, preserveLayout: true))
        {
            this.floatingAnchorPosition = ImGui.GetWindowPos();
            this.floatingAnchorSize = ImGui.GetWindowSize();
            this.Ui.UpdateMainWindowBounds(this.floatingAnchorPosition, this.floatingAnchorSize);
            this.DrawBackstageApp();
        }
        // Editors/previews need the same contrast even when opened over Overview.
        using var editorTheme = UiTheme.PushContrastIfEnabled(highContrast, preserveLayout: true);
        this.DrawFloatingShoutEditors(this.Configuration);
        this.DrawConfirmationWindow();
        this.DrawSharedPreviewWindow();
        this.DrawEventEditorWindow();
    }

    private void DrawDjLineupTab()
    {
        var config = this.Configuration;
        config.EnsureScheduleDefaults();
        this.DrawDjLineupRedesigned(config);
    }

    private void DrawStaffScheduleTab()
    {
        var config = this.Configuration;
        config.EnsureStaffScheduleDefaults();
        this.DrawStaffRosterWorkspace(config);
    }

    private void DrawEventShoutsTab()
    {
        var config = this.Configuration;
        config.EnsureEventShoutDefaults();
        this.DrawShoutsWorkspace(config);
    }

    private void OpenAddEventShoutEditor(Configuration config)
    {
        this.eventShoutEditorIsNew = true;
        this.eventShoutEditorOrder = 0;
        this.eventShoutEditorName = $"Custom Shout {config.EventShouts.Count + 1}";
        this.eventShoutEditorMacro = Configuration.DefaultEventShoutMacro;
        this.eventShoutEditorStartTime = NormalizeTimeText(config.EventStartTime);
        this.eventShoutEditorEndTime = NormalizeTimeText(config.EventEndTime);
        this.eventShoutEditorDelaySeconds = 2f;
        this.eventShoutEditorOpen = true;
        this.eventShoutOpeningDraft = this.BuildEventShoutPreviewDraft();
        this.eventShoutEditorFocusRequested = true;
    }

    private void OpenEditEventShoutEditor(EventShoutEntry entry)
    {
        this.eventShoutEditorIsNew = false;
        this.eventShoutEditorOrder = entry.Order;
        this.eventShoutEditorName = string.IsNullOrWhiteSpace(entry.Name) ? "New Shout" : entry.Name;
        this.eventShoutEditorMacro = entry.Macro ?? string.Empty;
        this.eventShoutEditorStartTime = NormalizeTimeText(entry.StartTime);
        this.eventShoutEditorEndTime = NormalizeTimeText(entry.EndTime);
        this.eventShoutEditorDelaySeconds = Math.Clamp(entry.DelaySeconds, 0f, 10f);
        this.eventShoutEditorOpen = true;
        this.eventShoutOpeningDraft = this.BuildEventShoutPreviewDraft();
        this.eventShoutEditorFocusRequested = true;
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
        entry.Macro = Configuration.NormalizeAnnouncementMacro(string.IsNullOrWhiteSpace(this.eventShoutEditorMacro) ? Configuration.DefaultEventShoutMacro : this.eventShoutEditorMacro);
        entry.Active = true;
        entry.StartTime = NormalizeTimeText(this.eventShoutEditorStartTime);
        entry.EndTime = NormalizeTimeText(this.eventShoutEditorEndTime);
        entry.DelaySeconds = Math.Clamp(this.eventShoutEditorDelaySeconds, 0f, 10f);

        config.NormalizeEventShoutOrders();
        config.SelectedEventShoutOrder = entry.Order;
        config.Save();
        this.EventShouts.Refresh();
    }

    /// <summary>
    /// Open the staff picker from the selected shift's assignment surface.
    /// The popup stays fixed-size to avoid ImGui auto-fit shrinkage.
    /// </summary>
    private void StaffPickerAndSave(StaffScheduleEntry entry, bool cardClicked)
    {
        if (cardClicked)
        {
            this.Configuration.SelectedStaffScheduleOrder = entry.Order;
            this.staffPickerSearchTextByOrder[entry.Order] = string.Empty;
            this.staffPickerCreatingNew = false;
            this.staffPickerNewName = string.Empty;
            this.staffPickerNewRole = this.Configuration.GetExistingOrDefaultStaffRole(entry.Role);
            this.Configuration.Save();
            ImGui.OpenPopup("StaffPickerPopup");
        }

        var pickerSize = new Vector2(460f, 454f);
        ImGui.SetNextWindowSize(pickerSize, ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(pickerSize, pickerSize);
        this.CenterNextFloatingWindow(pickerSize);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, BackstageDesign.Canvas);
        ImGui.PushStyleColor(ImGuiCol.Border, BackstageDesign.SoftLine);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f);
        if (!ImGui.BeginPopup("StaffPickerPopup", ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
            return;
        }

        var origin = ImGui.GetWindowPos();
        var draw = ImGui.GetWindowDrawList();
        const float width = 460f;
        BackstageDesign.Fill(draw, origin, new Vector2(width, 52f), BackstageDesign.Rail, 10f);
        BackstageDesign.Fill(draw, origin + new Vector2(0f, 34f), new Vector2(width, 18f), BackstageDesign.Rail);
        BackstageDesign.Rule(draw, origin + new Vector2(0f, 51f),
            origin + new Vector2(width, 51f), BackstageDesign.SoftLine);
        draw.AddText(origin + new Vector2(20f, 18f), BackstageDesign.Color(BackstageDesign.Gold),
            this.staffPickerCreatingNew ? "ADD NEW STAFF" : "SELECT STAFF");
        if (BackstageDesign.FlatButton("##vhStaffPickerClose", "X",
                origin + new Vector2(width - 45f, 10f), new Vector2(29f, 30f), BackstageDesign.Muted))
            ImGui.CloseCurrentPopup();

        if (!this.staffPickerSearchTextByOrder.TryGetValue(entry.Order, out var search))
            search = string.Empty;
        if (!this.staffPickerCreatingNew)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(18f, 68f));
            ImGui.PushStyleColor(ImGuiCol.FrameBg, BackstageDesign.Raised);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9f, 9f));
            ImGui.SetNextItemWidth(width - 36f);
            if (ImGui.InputTextWithHint("##vhStaffPickerSearch", "Search staff or roles...", ref search, 120))
                this.staffPickerSearchTextByOrder[entry.Order] = search;
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();

            var matches = this.Configuration.StaffDatabase
                .Where(person => MatchesStaffSearch(person, search))
                .OrderBy(person => person.Name, StringComparer.OrdinalIgnoreCase).ToList();
            draw.AddText(origin + new Vector2(20f, 119f), BackstageDesign.Color(BackstageDesign.Dim),
                $"{matches.Count} {(matches.Count == 1 ? "result" : "results")}");
            BackstageDesign.SurfacePanel(origin + new Vector2(15f, 145f),
                new Vector2(width - 30f, 229f), BackstageDesign.Surface, 9f);
            ImGui.SetCursorScreenPos(origin + new Vector2(23f, 152f));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2f, 2f));
            if (ImGui.BeginChild("##vhStaffPickerResults", new Vector2(width - 46f, 215f), false))
            {
                if (matches.Count == 0)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(BackstageDesign.Muted, "No matching staff found.");
                    ImGui.TextDisabled("Use + NEW STAFF to add someone.");
                }
                foreach (var person in matches)
                {
                    ImGui.PushID(person.RuntimeId);
                    var row = ImGui.GetCursorScreenPos();
                    var rowWidth = MathF.Max(120f, ImGui.GetContentRegionAvail().X - 4f);
                    var clicked = ImGui.InvisibleButton("##vhPickStaff", new Vector2(rowWidth, 41f));
                    var hovered = ImGui.IsItemHovered();
                    var selected = string.Equals(entry.Name, person.Name, StringComparison.OrdinalIgnoreCase);
                    var fill = hovered ? BackstageDesign.Raised :
                        selected ? new Vector4(0.240f, 0.208f, 0.187f, 1f) : BackstageDesign.Surface;
                    var rows = ImGui.GetWindowDrawList();
                    BackstageDesign.Fill(rows, row, new Vector2(rowWidth, 39f), fill, 6f);
                    if (selected)
                        BackstageDesign.Fill(rows, row, new Vector2(3f, 39f), BackstageDesign.Gold, 1f);
                    ImGui.PushClipRect(row + new Vector2(12f, 0f),
                        row + new Vector2(rowWidth - 12f, 39f), true);
                    rows.AddText(row + new Vector2(14f, 3f),
                        BackstageDesign.Color(selected || hovered ? BackstageDesign.Ivory : BackstageDesign.Muted),
                        person.Name ?? "Unnamed staff");
                    rows.AddText(row + new Vector2(14f, 21f),
                        BackstageDesign.Color(BackstageDesign.Dim), person.Role ?? string.Empty);
                    ImGui.PopClipRect();
                    if (clicked)
                    {
                        this.AssignSavedStaffToShift(entry, person);
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3f);
                    ImGui.PopID();
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }
        else
        {
            draw.AddText(origin + new Vector2(20f, 75f), BackstageDesign.Color(BackstageDesign.Dim), "STAFF NAME");
            draw.AddText(origin + new Vector2(20f, 150f), BackstageDesign.Color(BackstageDesign.Dim), "DEFAULT ROLE");
            ImGui.SetCursorScreenPos(origin + new Vector2(18f, 96f));
            ImGui.PushStyleColor(ImGuiCol.FrameBg, BackstageDesign.Raised);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f, 9f));
            ImGui.SetNextItemWidth(width - 36f);
            ImGui.InputTextWithHint("##vhStaffPickerNewName", "Staff name", ref this.staffPickerNewName, 120);
            if (BackstageDesign.RoleSelector("##vhStaffPickerNewRole", this.staffPickerNewRole,
                    GetKnownStaffRoles(this.Configuration), origin + new Vector2(18f, 171f),
                    new Vector2(width - 36f, 35f), out var quickRole))
                this.staffPickerNewRole = quickRole;
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();

            var duplicate = this.Configuration.StaffDatabase.Any(person =>
                string.Equals((person.Name ?? string.Empty).Trim(), this.staffPickerNewName.Trim(), StringComparison.OrdinalIgnoreCase));
            var valid = !string.IsNullOrWhiteSpace(this.staffPickerNewName) && !duplicate;
            if (!valid) ImGui.BeginDisabled();
            if (BackstageDesign.FlatButton("##vhStaffPickerAddAndAssign", "ADD & ASSIGN STAFF",
                    origin + new Vector2(18f, 247f), new Vector2(width - 36f, 38f), BackstageDesign.Mint, true))
            {
                var person = new StaffDatabaseEntry {
                    Name = this.staffPickerNewName.Trim(),
                    Role = this.Configuration.GetExistingOrDefaultStaffRole(this.staffPickerNewRole),
                };
                this.Configuration.StaffDatabase.Add(person);
                this.AssignSavedStaffToShift(entry, person);
                ImGui.CloseCurrentPopup();
            }
            if (!valid) ImGui.EndDisabled();
            ImGui.SetCursorScreenPos(origin + new Vector2(22f, 307f));
            ImGui.TextColored(duplicate ? BackstageDesign.Rose : BackstageDesign.Muted,
                duplicate ? "This name already exists. Select it from the list." :
                "Saves the person and assigns this shift.");
        }

        BackstageDesign.Rule(draw, origin + new Vector2(17f, 394f),
            origin + new Vector2(width - 17f, 394f), BackstageDesign.SoftLine);
        if (BackstageDesign.FlatButton("##vhStaffPickerNewToggle",
                this.staffPickerCreatingNew ? "BACK TO LIST" : "+ NEW STAFF",
                origin + new Vector2(17f, 405f), new Vector2(128f, 33f), BackstageDesign.Gold))
        {
            if (!this.staffPickerCreatingNew)
                this.staffPickerNewName = search.Trim();
            this.staffPickerCreatingNew = !this.staffPickerCreatingNew;
        }
        if (BackstageDesign.FlatButton("##vhStaffPickerOpenDatabase", "STAFF DATABASE",
                origin + new Vector2(width - 162f, 405f), new Vector2(144f, 33f), BackstageDesign.Mist))
        {
            this.Ui.ToggleStaffDatabaseUi();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }

    private void AssignSavedStaffToShift(StaffScheduleEntry shift, StaffDatabaseEntry person)
    {
        shift.Name = person.Name;
        shift.PersonId = person.Id;
        shift.Link = string.Empty;
        shift.Role = this.Configuration.GetExistingOrDefaultStaffRole(person.Role);
        this.Configuration.SelectedStaffScheduleOrder = shift.Order;
        this.staffCollapsedRoles.Remove(shift.Role);
        this.staffRosterSearch = string.Empty;
        this.Configuration.Save();
        this.StaffAutoShout.Refresh();
    }

    private void StaffRoleComboAndSave(StaffScheduleEntry entry, Vector2? size = null)
    {
        var currentRole = string.IsNullOrWhiteSpace(entry.Role) ? "Photographer" : entry.Role;
        var p = ImGui.GetCursorScreenPos();
        var fieldSize = size ?? new Vector2(MathF.Max(120f, ImGui.GetContentRegionAvail().X), 33f);
        if (BackstageDesign.RoleSelector("##StaffShiftRole", currentRole,
                GetKnownStaffRoles(this.Configuration), p, fieldSize, out var chosen))
        {
            this.Configuration.SelectedStaffScheduleOrder = entry.Order;
            entry.Role = chosen;
            this.staffCollapsedRoles.Remove(chosen);
            this.Configuration.Save();
            this.StaffAutoShout.Refresh();
        }
    }

    private static IReadOnlyList<string> GetKnownStaffRoles(Configuration config) => config.GetKnownStaffRoles();

    private static bool MatchesStaffSearch(StaffDatabaseEntry entry, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return true;

        return (entry.Name?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (entry.Role?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false);
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

        var linkColor = this.Configuration.OverviewHighContrastPreviewEnabled
            ? new Vector4(0.00f, 1.00f, 0.95f, 1.00f)
            : BackstageDesign.Mist;

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

    private void AddDjSlot(Configuration config)
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
        this.DjAutoShout.Refresh();
    }

    private bool showClearLineupConfirmation;

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
        this.DjAutoShout.Refresh();
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
        this.DjAutoShout.Refresh();
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
        var contrast = this.Configuration.OverviewHighContrastPreviewEnabled;
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
        if (this.Configuration.OverviewHighContrastPreviewEnabled)
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


    /// <summary>
    /// Compact VenueHost-styled DJ selector. Selecting a saved DJ assigns it
    /// immediately, while + NEW DJ reveals a separate quick-add form.
    /// The full DJ Database remains a distinct management window.
    /// </summary>
    private void DjPickerAndSave(DjScheduleEntry entry, bool autoOpen = false, bool cardClicked = false)
    {
        var popupRequested = cardClicked || autoOpen;
        if (popupRequested)
        {
            this.djPickerSearchTextByOrder[entry.Order] = string.Empty;
            this.djPickerQuickAddNameByOrder[entry.Order] = string.Empty;
            this.djPickerQuickAddLinkByOrder[entry.Order] = string.Empty;
            this.djPickerCreatingNew = false;
            ImGui.OpenPopup("DjPickerPopup");
        }
        // ImGui non-modal popups can auto-fit their contents *after* appearing.
        // Because our controls use absolute screen coordinates and derive their
        // widths from the popup, auto-fitting produced a self-reinforcing shrink
        // on subsequent frames. Enforce a fixed size every frame (including when
        // switching to + NEW DJ), rather than only on the initial appearance.
        var pickerSize = new Vector2(460f, 454f);
        ImGui.SetNextWindowSize(pickerSize, ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(pickerSize, pickerSize);
        this.CenterNextFloatingWindow(pickerSize);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, BackstageDesign.Canvas);
        ImGui.PushStyleColor(ImGuiCol.Border, BackstageDesign.SoftLine);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f);
        if (!ImGui.BeginPopup("DjPickerPopup", ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
            return;
        }

        var origin = ImGui.GetWindowPos();
        // Use the fixed design width, not the previously measured window width;
        // otherwise child control sizes can feed popup auto-sizing back into
        // themselves on some Dalamud/ImGui versions.
        var width = pickerSize.X;
        var draw = ImGui.GetWindowDrawList();
        // Header and footer share the same visual rhythm as DJ Database.
        BackstageDesign.Fill(draw, origin, new Vector2(width, 52f), BackstageDesign.Rail, 10f);
        BackstageDesign.Fill(draw, origin + new Vector2(0f, 34f), new Vector2(width, 18f), BackstageDesign.Rail);
        BackstageDesign.Rule(draw, origin + new Vector2(0f, 51f),
            origin + new Vector2(width, 51f), BackstageDesign.SoftLine);
        draw.AddText(origin + new Vector2(20f, 18f), BackstageDesign.Color(BackstageDesign.Gold),
            this.djPickerCreatingNew ? "ADD NEW DJ" : "SELECT DJ");
        if (BackstageDesign.FlatButton("##vhPickerClose", "X", origin + new Vector2(width - 45f, 10f),
                new Vector2(29f, 30f), BackstageDesign.Muted))
            ImGui.CloseCurrentPopup();

        this.Configuration.NormalizeDjDatabase(saveToStore: false);
        if (!this.djPickerSearchTextByOrder.TryGetValue(entry.Order, out var searchText))
            searchText = string.Empty;

        if (!this.djPickerCreatingNew)
        {
            // Search and select from saved records. No unnecessary metadata
            // or full stream URLs clutter the picker; the link is on hover.
            ImGui.SetCursorScreenPos(origin + new Vector2(18f, 68f));
            ImGui.PushStyleColor(ImGuiCol.FrameBg, BackstageDesign.Raised);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, BackstageDesign.Raised);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, BackstageDesign.Raised);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9f, 9f));
            ImGui.SetNextItemWidth(width - 36f);
            if (ImGui.InputTextWithHint("##vhDjPickerSearch", "Search saved DJs...", ref searchText, 120))
                this.djPickerSearchTextByOrder[entry.Order] = searchText;
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(3);

            var matches = this.Configuration.DjDatabase
                .Where(dbEntry => MatchesDjSearch(dbEntry, searchText))
                .OrderBy(dbEntry => dbEntry.Name, StringComparer.OrdinalIgnoreCase).ToList();
            draw.AddText(origin + new Vector2(20f, 119f), BackstageDesign.Color(BackstageDesign.Dim),
                $"{matches.Count} {(matches.Count == 1 ? "result" : "results")}");

            BackstageDesign.SurfacePanel(origin + new Vector2(15f, 145f),
                new Vector2(width - 30f, 229f), BackstageDesign.Surface, 9f);
            ImGui.SetCursorScreenPos(origin + new Vector2(23f, 152f));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2f, 2f));
            if (ImGui.BeginChild("##vhPickerResults", new Vector2(width - 46f, 215f), false))
            {
                if (matches.Count == 0)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(BackstageDesign.Muted, "No matching DJs found.");
                    ImGui.TextDisabled("Use + NEW DJ to add someone.");
                }
                foreach (var candidate in matches)
                {
                    ImGui.PushID(candidate.RuntimeId);
                    var row = ImGui.GetCursorScreenPos();
                    var rowWidth = MathF.Max(120f, ImGui.GetContentRegionAvail().X - 4f);
                    var picked = ImGui.InvisibleButton("##vhPickDj", new Vector2(rowWidth, 34f));
                    var hovered = ImGui.IsItemHovered();
                    var isAssigned = string.Equals(entry.DJName, candidate.Name, StringComparison.OrdinalIgnoreCase);
                    var bg = hovered ? BackstageDesign.Raised :
                        isAssigned ? new Vector4(0.240f, 0.208f, 0.187f, 1f) : BackstageDesign.Surface;
                    var listDraw = ImGui.GetWindowDrawList();
                    BackstageDesign.Fill(listDraw, row, new Vector2(rowWidth, 32f), bg, 6f);
                    if (isAssigned)
                        BackstageDesign.Fill(listDraw, row, new Vector2(3f, 32f), BackstageDesign.Gold, 1f);
                    ImGui.PushClipRect(row + new Vector2(12f, 0f), row + new Vector2(rowWidth - 12f, 34f), true);
                    listDraw.AddText(row + new Vector2(14f, 8f),
                        BackstageDesign.Color(hovered || isAssigned ? BackstageDesign.Ivory : BackstageDesign.Muted),
                        string.IsNullOrWhiteSpace(candidate.Name) ? "Unnamed DJ" : candidate.Name);
                    ImGui.PopClipRect();
                    if (hovered && !string.IsNullOrWhiteSpace(candidate.Link))
                        ImGui.SetTooltip(candidate.Link);
                    if (picked)
                    {
                        this.AssignSavedDjToSlot(entry, candidate);
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3f);
                    ImGui.PopID();
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }
        else
        {
            // The create form is shown only when requested, not under every
            // search result. It retains the old quick-add + use behavior.
            this.djPickerQuickAddNameByOrder.TryGetValue(entry.Order, out var quickName);
            this.djPickerQuickAddLinkByOrder.TryGetValue(entry.Order, out var quickLink);
            quickName ??= string.Empty;
            quickLink ??= string.Empty;
            draw.AddText(origin + new Vector2(20f, 75f), BackstageDesign.Color(BackstageDesign.Dim), "DJ NAME");
            draw.AddText(origin + new Vector2(20f, 150f), BackstageDesign.Color(BackstageDesign.Dim), "STREAM LINK");
            ImGui.PushStyleColor(ImGuiCol.FrameBg, BackstageDesign.Raised);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, BackstageDesign.Raised);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, BackstageDesign.Raised);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f, 9f));
            ImGui.SetCursorScreenPos(origin + new Vector2(18f, 96f));
            ImGui.SetNextItemWidth(width - 36f);
            if (ImGui.InputTextWithHint("##vhPickerNewName", "DJ name", ref quickName, 120))
                this.djPickerQuickAddNameByOrder[entry.Order] = quickName;
            ImGui.SetCursorScreenPos(origin + new Vector2(18f, 171f));
            ImGui.SetNextItemWidth(width - 36f);
            if (ImGui.InputTextWithHint("##vhPickerNewLink", "https://www.twitch.tv/...", ref quickLink, 400))
                this.djPickerQuickAddLinkByOrder[entry.Order] = quickLink;
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(3);

            if (string.IsNullOrWhiteSpace(quickName)) ImGui.BeginDisabled();
            if (BackstageDesign.FlatButton("##vhPickerAddAndUse", "ADD & ASSIGN DJ",
                    origin + new Vector2(18f, 245f), new Vector2(width - 36f, 38f), BackstageDesign.Mint, true))
            {
                this.QuickAddDjToSlot(entry, quickName, quickLink);
                ImGui.CloseCurrentPopup();
            }
            if (string.IsNullOrWhiteSpace(quickName)) ImGui.EndDisabled();
            ImGui.SetCursorScreenPos(origin + new Vector2(22f, 302f));
            ImGui.TextColored(BackstageDesign.Muted, "Saves to DJ Database and assigns this slot.");
        }

        BackstageDesign.Rule(draw, origin + new Vector2(17f, 394f),
            origin + new Vector2(width - 17f, 394f), BackstageDesign.SoftLine);
        if (BackstageDesign.FlatButton("##vhPickerToggleNew",
                this.djPickerCreatingNew ? "BACK TO LIST" : "+ NEW DJ",
                origin + new Vector2(17f, 405f), new Vector2(123f, 33f), BackstageDesign.Gold))
        {
            if (!this.djPickerCreatingNew && string.IsNullOrWhiteSpace(this.djPickerQuickAddNameByOrder.GetValueOrDefault(entry.Order)) &&
                !this.Configuration.DjDatabase.Any(dbEntry =>
                    string.Equals(dbEntry.Name?.Trim(), searchText.Trim(), StringComparison.OrdinalIgnoreCase)))
                this.djPickerQuickAddNameByOrder[entry.Order] = searchText.Trim();
            this.djPickerCreatingNew = !this.djPickerCreatingNew;
        }
        if (BackstageDesign.FlatButton("##vhPickerOpenDatabase", "DJ DATABASE",
                origin + new Vector2(width - 151f, 405f), new Vector2(133f, 33f), BackstageDesign.Mist))
        {
            this.Ui.ToggleDjDatabaseUi();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }

    private void AssignSavedDjToSlot(DjScheduleEntry slot, DjDatabaseEntry selected)
    {
        var hadUsableDjBefore = this.Configuration.DjSchedule.Any(Configuration.HasUsableDj);
        slot.DJName = selected.Name;
        slot.DJId = selected.Id;
        slot.DJLink = selected.Link;
        this.EnableAutoShoutWhenFirstDjIsAdded(hadUsableDjBefore);
        this.Configuration.Save();
        this.DjAutoShout.Refresh();
    }

    private void QuickAddDjToSlot(DjScheduleEntry slot, string name, string link)
    {
        var requestedName = (name ?? string.Empty).Trim();
        var requestedLink = (link ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(requestedName)) return;
        var existing = this.Configuration.DjDatabase.FirstOrDefault(dbEntry =>
            string.Equals((dbEntry.Name ?? string.Empty).Trim(), requestedName, StringComparison.OrdinalIgnoreCase));
        string selectedName;
        string selectedLink;
        if (existing is not null)
        {
            selectedName = existing.Name;
            if (string.IsNullOrWhiteSpace(existing.Link) && !string.IsNullOrWhiteSpace(requestedLink))
                existing.Link = requestedLink;
            selectedLink = existing.Link;
        }
        else
        {
            selectedName = this.Configuration.GetUniqueDjDatabaseName(requestedName);
            selectedLink = requestedLink;
            this.Configuration.DjDatabase.Add(new DjDatabaseEntry { Name = selectedName, Link = selectedLink });
        }
        this.Configuration.NormalizeDjDatabase();
        var hadUsableDjBefore = this.Configuration.DjSchedule.Any(Configuration.HasUsableDj);
        slot.DJName = selectedName;
        slot.DJId = this.Configuration.DjDatabase.FirstOrDefault(d => string.Equals(d.Name.Trim(), selectedName.Trim(), StringComparison.OrdinalIgnoreCase))?.Id ?? string.Empty;
        slot.DJLink = selectedLink;
        this.EnableAutoShoutWhenFirstDjIsAdded(hadUsableDjBefore);
        this.Configuration.Save();
        this.DjAutoShout.Refresh();
        this.djPickerQuickAddNameByOrder[slot.Order] = string.Empty;
        this.djPickerQuickAddLinkByOrder[slot.Order] = string.Empty;
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
            this.RefreshScheduleForEditedField(label);
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
            this.SetTimeAndSave(hour - 1, minute, setter, label);
        ImGui.SameLine();
        ImGui.TextUnformatted(hour.ToString("00"));
        ImGui.SameLine();
        if (ImGui.Button($"+##{label}HourUp", new Vector2(32, 24)))
            this.SetTimeAndSave(hour + 1, minute, setter, label);

        ImGui.TextUnformatted("Minute");
        ImGui.SameLine(80f);
        if (ImGui.Button($"-5##{label}MinuteDown", new Vector2(32, 24)))
            this.SetTimeAndSave(hour, minute - 5, setter, label);
        ImGui.SameLine();
        ImGui.TextUnformatted(minute.ToString("00"));
        ImGui.SameLine();
        if (ImGui.Button($"+5##{label}MinuteUp", new Vector2(32, 24)))
            this.SetTimeAndSave(hour, minute + 5, setter, label);

        ImGui.Spacing();
        ImGui.TextUnformatted("Quick set");
        if (ImGui.Button($"Now ST##{label}Now", new Vector2(80, 24)))
        {
            var now = DateTime.UtcNow.TimeOfDay;
            this.SetTimeAndSave(now.Hours, RoundToNearestFive(now.Minutes), setter, label);
        }

        ImGui.SameLine();
        if (ImGui.Button($"Next 15m##{label}Next15", new Vector2(90, 24)))
        {
            var now = DateTime.UtcNow.TimeOfDay;
            var roundedMinute = ((now.Minutes + 14) / 15) * 15;
            this.SetTimeAndSave(now.Hours, roundedMinute, setter, label);
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
                this.RefreshScheduleForEditedField(label);
            }
        }

        ImGui.Spacing();
        if (ImGui.Button($"Close##{label}Close", new Vector2(80, 24)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void SetTimeAndSave(int hour, int minute, Action<string> setter, string fieldId)
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
        this.RefreshScheduleForEditedField(fieldId);
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

    private static string NormalizeTimeText(string? value, string fallback)
    {
        return Configuration.TryParseServerTime(value, out var parsed)
            ? FormatTime(parsed.Hours, parsed.Minutes)
            : NormalizeTimeText(fallback);
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


}
