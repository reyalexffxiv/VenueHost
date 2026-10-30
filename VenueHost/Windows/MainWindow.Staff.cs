using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueHost.Models;
using VenueHost.Services;

namespace VenueHost.Windows;

/// <summary>
/// Staff workspace: event-bound, overlapping shifts with a role-grouped roster.
/// The database, shared configuration format and automatic role schedulers are
/// retained. Shout controls belong to the Shouts workspace instead.
/// </summary>
public sealed partial class MainWindow
{
    private string staffRosterSearch = string.Empty;
    private readonly HashSet<string> staffCollapsedRoles = new(StringComparer.OrdinalIgnoreCase);
    private bool requestRemoveStaffShift;

    private StaffScheduleEntry? staffTimeDraftEntry;
    private string staffStartDraft = string.Empty;
    private string staffEndDraft = string.Empty;
    private bool staffStartInvalid;
    private bool staffEndInvalid;

    private void DrawStaffRosterWorkspace(Configuration config)
    {
        var width = ImGui.GetContentRegionAvail().X;
        if (width < 620f)
        {
            ImGui.TextColored(BackstageDesign.Rose, "Expand VenueHost to edit staff shifts.");
            return;
        }

        const float gap = 12f;
        var rosterWidth = MathF.Floor((width - gap) * 0.60f);
        var editorWidth = width - rosterWidth - gap;
        var available = ImGui.GetContentRegionAvail().Y;
        var panelHeight = MathF.Min(490f, MathF.Max(405f, available - 10f));
        // The roster needs height for scrolling; the selected editor does not.
        // Allow extra room only when an inline shift warning is visible.
        var selectedShift = config.GetSelectedStaffScheduleEntry();
        var editorHeight = selectedShift is not null && GetStaffShiftWarning(config, selectedShift) is not null
            ? 457f : 388f;
        this.DrawStaffRosterPanel(config, new Vector2(rosterWidth, panelHeight));
        ImGui.SameLine(0f, gap);
        this.DrawSelectedStaffShiftEditor(config, new Vector2(editorWidth, editorHeight));


    }

    private void DrawStaffRosterPanel(Configuration config, Vector2 size)
    {
        var p = ImGui.GetCursorScreenPos();
        BackstageDesign.SurfacePanel(p, size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(13f, 11f));
        if (ImGui.BeginChild("##vhStaffRosterPanel", size, false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var dl = ImGui.GetWindowDrawList();
            // Like DJ Lineup, the page heading already identifies this panel.
            // Keep the toolbar compact: actions only, right-aligned, with
            // subdued accent colours rather than solid coloured buttons.
            const float addWidth = 110f;
            const float dbWidth = 131f;
            const float clearWidth = 111f;
            const float buttonGap = 8f;
            const float clearGap = 16f;
            const float buttonHeight = 29f;
            var toolbarWidth = addWidth + dbWidth + clearWidth + buttonGap + clearGap;
            ImGui.SetCursorScreenPos(p + new Vector2(size.X - 14f - toolbarWidth, 11f));
            // Adding a shift must not change any other person's working time.
            if (this.WorkspaceButton("+ ADD SHIFT", new Vector2(addWidth, buttonHeight), BackstageDesign.Mint))
                this.AddStaffShift(config);
            ImGui.SameLine(0f, buttonGap);
            if (this.WorkspaceButton("STAFF DATABASE", new Vector2(dbWidth, buttonHeight), BackstageDesign.Mist))
                this.Ui.ToggleStaffDatabaseUi();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Open and manage saved staff members.");
            ImGui.SameLine(0f, clearGap);
            if (this.WorkspaceButton("CLEAR LIST", new Vector2(clearWidth, buttonHeight), BackstageDesign.Gold))
                this.showClearStaffListConfirmation = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Clear all staff shifts after confirmation.");
            BackstageDesign.Rule(dl, p + new Vector2(14f, 48f),
                p + new Vector2(size.X - 14f, 48f), BackstageDesign.SoftLine);

            ImGui.SetCursorScreenPos(p + new Vector2(14f, 59f));
            ImGui.SetNextItemWidth(size.X - 28f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f, 7f));
            ImGui.PushStyleColor(ImGuiCol.FrameBg, BackstageDesign.Raised);
            ImGui.InputTextWithHint("##vhStaffSearch", "Search staff or roles...", ref this.staffRosterSearch, 100);
            ImGui.PopStyleColor();
            ImGui.PopStyleVar(2);

            ImGui.SetCursorScreenPos(p + new Vector2(14f, 101f));
            var scrollHeight = MathF.Max(120f, size.Y - 112f);
            if (ImGui.BeginChild("##vhStaffRosterScroll", new Vector2(size.X - 27f, scrollHeight), false))
            {
                var filtered = config.StaffSchedule.Where(e =>
                    string.IsNullOrWhiteSpace(this.staffRosterSearch) ||
                    (e.Name?.Contains(this.staffRosterSearch, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (e.Role?.Contains(this.staffRosterSearch, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();
                if (filtered.Count == 0)
                {
                    ImGui.TextColored(BackstageDesign.Muted,
                        config.StaffSchedule.Count == 0 ? "No staff shifts yet. Add a shift to begin." : "No matching staff shifts.");
                }
                else
                {
                    foreach (var group in filtered.GroupBy(e => e.Role ?? "Unassigned", StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        ImGui.PushID(group.Key);
                        // Search should reveal matching shifts even if their
                        // role section was previously collapsed.
                        var searchActive = !string.IsNullOrWhiteSpace(this.staffRosterSearch);
                        this.DrawStaffRoleHeader(group.Key, group.Count(), searchActive);
                        if (searchActive || !this.staffCollapsedRoles.Contains(group.Key))
                        {
                            foreach (var entry in group.OrderBy(e => e.StartTime, StringComparer.Ordinal)
                                         .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                            {
                                ImGui.PushID($"StaffShift{entry.Order}");
                                this.DrawStaffRosterRow(config, entry);
                                ImGui.PopID();
                            }
                        }
                        ImGui.PopID();
                    }
                }
            }
            ImGui.EndChild();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private void DrawStaffRoleHeader(string role, int count, bool searchActive)
    {
        var p = ImGui.GetCursorScreenPos();
        var w = MathF.Max(140f, ImGui.GetContentRegionAvail().X - 5f);
        const float headerHeight = 36f;
        // Header plus a little whitespace separates each role without
        // adding a tree widget or nesting/indentation to the actual roster.
        var clicked = ImGui.InvisibleButton("##vhStaffRoleCollapse", new Vector2(w, headerHeight + 6f));
        var hovered = ImGui.IsItemHovered();
        if (clicked && !searchActive)
        {
            if (!this.staffCollapsedRoles.Add(role))
                this.staffCollapsedRoles.Remove(role);
        }

        var expanded = searchActive || !this.staffCollapsedRoles.Contains(role);
        var dl = ImGui.GetWindowDrawList();
        var fill = hovered ? new Vector4(0.190f, 0.186f, 0.202f, 1f)
            : new Vector4(0.143f, 0.141f, 0.159f, 1f);
        BackstageDesign.Fill(dl, p, new Vector2(w, headerHeight), fill, 7f);
        BackstageDesign.Frame(dl, p, new Vector2(w, headerHeight), BackstageDesign.SoftLine, 7f);

        // Role name on the left, count and a real chevron on the right.
        // No literal 'v'/'>' markers: these look like old ImGui tree controls.
        var title = FitCompactText(role.ToUpperInvariant(), MathF.Max(65f, w - 145f));
        dl.AddText(p + new Vector2(12f, 10f), BackstageDesign.Color(BackstageDesign.Gold), title);
        var countLabel = $"{count} {(count == 1 ? "SHIFT" : "SHIFTS")}";
        var countWidth = ImGui.CalcTextSize(countLabel).X;
        dl.AddText(p + new Vector2(w - 40f - countWidth, 10f),
            BackstageDesign.Color(BackstageDesign.Muted), countLabel);
        var center = p + new Vector2(w - 20f, 18f);
        var accent = BackstageDesign.Color(hovered ? BackstageDesign.Ivory : BackstageDesign.Muted);
        if (expanded)
        {
            dl.AddLine(center + new Vector2(-4f, -2f), center + new Vector2(0f, 2f), accent, 1.6f);
            dl.AddLine(center + new Vector2(0f, 2f), center + new Vector2(4f, -2f), accent, 1.6f);
        }
        else
        {
            dl.AddLine(center + new Vector2(-2f, -4f), center + new Vector2(2f, 0f), accent, 1.6f);
            dl.AddLine(center + new Vector2(2f, 0f), center + new Vector2(-2f, 4f), accent, 1.6f);
        }
        if (hovered)
            ImGui.SetTooltip(searchActive ? "Showing all matching shifts while searching."
                : expanded ? "Collapse this role" : "Expand this role");
    }

    private void DrawStaffRosterRow(Configuration config, StaffScheduleEntry entry)
    {
        var p = ImGui.GetCursorScreenPos();
        var w = MathF.Max(140f, ImGui.GetContentRegionAvail().X - 5f);
        const float rowHeight = 48f;
        var selected = config.SelectedStaffScheduleOrder == entry.Order;
        var blank = string.IsNullOrWhiteSpace(entry.Name);
        if (ImGui.InvisibleButton("##vhStaffSelectShift", new Vector2(w, rowHeight)))
        {
            this.CommitStaffTimeDrafts();
            config.SelectedStaffScheduleOrder = entry.Order;
            config.Save();
        }
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        // Unselected rows are intentionally lighter than the panel. Without
        // this contrast they look like loose text rather than clickable rows.
        var fill = selected ? new Vector4(0.245f, 0.210f, 0.194f, 1f)
            : hovered ? new Vector4(0.205f, 0.194f, 0.202f, 1f)
            : new Vector4(0.162f, 0.158f, 0.175f, 1f);
        BackstageDesign.Fill(dl, p, new Vector2(w, rowHeight - 4f), fill, 7f);
        if (selected)
        {
            BackstageDesign.Fill(dl, p + new Vector2(0f, 4f), new Vector2(3f, rowHeight - 12f), BackstageDesign.Gold, 2f);
            BackstageDesign.Frame(dl, p, new Vector2(w, rowHeight - 4f),
                BackstageDesign.Gold with { W = 0.33f }, 7f);
        }
        var name = blank ? "+ Assign a staff member" : entry.Name.Trim();
        var accent = selected ? BackstageDesign.Ivory : blank ? BackstageDesign.Gold : BackstageDesign.Ivory;
        dl.AddText(p + new Vector2(13f, 7f), BackstageDesign.Color(accent),
            FitCompactText(name, MathF.Max(70f, w - (blank ? 50f : 168f))));
        dl.AddText(p + new Vector2(13f, 26f), BackstageDesign.Color(BackstageDesign.Muted),
            $"{entry.StartTime} – {entry.EndTime} ST");
        if (!entry.Available)
            dl.AddText(p + new Vector2(w - (blank ? 115f : 80f), 15f),
                BackstageDesign.Color(BackstageDesign.Rose), "INACTIVE");

        // Match the DJ Lineup's amber warning marker. Empty staff shifts
        // remain editable but are excluded from automatic staff announcements.
        if (blank)
        {
            var center = p + new Vector2(w - 16f, (rowHeight - 4f) * 0.5f);
            dl.AddCircleFilled(center, 7f,
                BackstageDesign.Color(new Vector4(0.42f, 0.30f, 0.15f, 1f)));
            dl.AddText(center + new Vector2(-2f, -8f),
                BackstageDesign.Color(BackstageDesign.Gold), "!");
            if (hovered && ImGui.IsMouseHoveringRect(center - new Vector2(9f, 9f),
                    center + new Vector2(9f, 9f)))
                ImGui.SetTooltip("No staff member assigned to this shift.");
            else if (hovered)
                ImGui.SetTooltip("Select this empty shift to assign a staff member.");
        }
        else if (hovered)
            ImGui.SetTooltip("Select this shift to edit the staff member, role, times, or availability.");
    }

    private void DrawSelectedStaffShiftEditor(Configuration config, Vector2 size)
    {
        var p = ImGui.GetCursorScreenPos();
        BackstageDesign.SurfacePanel(p, size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f, 12f));
        if (ImGui.BeginChild("##vhStaffShiftEditor", size, false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var dl = ImGui.GetWindowDrawList();
            var entry = config.GetSelectedStaffScheduleEntry();
            ImGui.SetCursorScreenPos(p + new Vector2(17f, 15f));
            ImGui.TextColored(BackstageDesign.Gold, entry is null ? "SELECT A SHIFT" : "EDIT SHIFT");
            if (entry is not null)
            {
                var status = entry.Available ? "AVAILABLE" : "INACTIVE";
                var tint = entry.Available ? BackstageDesign.Mint : BackstageDesign.Rose;
                ImGui.SetCursorScreenPos(p + new Vector2(size.X - ImGui.CalcTextSize(status).X - 17f, 15f));
                ImGui.TextColored(tint, status);
            }
            BackstageDesign.Rule(dl, p + new Vector2(17f, 45f),
                p + new Vector2(size.X - 17f, 45f), BackstageDesign.SoftLine);
            if (entry is null)
            {
                ImGui.SetCursorScreenPos(p + new Vector2(17f, 75f));
                ImGui.TextColored(BackstageDesign.Muted, "Add a shift to begin scheduling staff.");
            }
            else
            {
                ImGui.PushID($"StaffEditor{entry.Order}");
                var staffCardClicked = BackstageDesign.AssignmentSelector(
                    "##vhStaffAssignment", "STAFF MEMBER", entry.Name,
                    p + new Vector2(17f, 63f), new Vector2(size.X - 34f, 68f));
                this.StaffPickerAndSave(entry, staffCardClicked);
                ImGui.SetCursorScreenPos(p + new Vector2(17f, 144f));
                ImGui.TextColored(BackstageDesign.Muted, "ROLE");
                ImGui.SetCursorScreenPos(p + new Vector2(17f, 165f));
                this.StaffRoleComboAndSave(entry, new Vector2(size.X - 34f, 34f));

                this.EnsureStaffTimeDrafts(entry);
                ImGui.SetCursorScreenPos(p + new Vector2(17f, 208f));
                ImGui.TextColored(BackstageDesign.Muted, "START  /  ST");
                ImGui.SetCursorScreenPos(p + new Vector2(17f + (size.X - 42f) * 0.52f, 208f));
                ImGui.TextColored(BackstageDesign.Muted, "END  /  ST");
                var timeWidth = MathF.Max(75f, (size.X - 54f) * 0.48f);
                ImGui.SetCursorScreenPos(p + new Vector2(17f, 229f));
                this.DrawStaffTimeInput("Start", entry, true, timeWidth);
                ImGui.SetCursorScreenPos(p + new Vector2(17f + (size.X - 42f) * 0.52f, 229f));
                this.DrawStaffTimeInput("End", entry, false, timeWidth);
                ImGui.SetCursorScreenPos(p + new Vector2(17f, 279f));
                var available = entry.Available;
                if (ImGui.Checkbox("Available for staff shouts", ref available))
                {
                    entry.Available = available;
                    config.Save();
                    this.StaffAutoShout.Refresh();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Temporarily exclude this shift from staff announcements without deleting it.");

                // Inline validation remains adjacent to the relevant editor.
                // Only invalid shifts need a taller panel; the normal editor
                // ends shortly after its controls instead of filling the roster.
                var warning = GetStaffShiftWarning(config, entry);
                if (warning is not null)
                {
                    ImGui.SetCursorScreenPos(p + new Vector2(17f, 318f));
                    ImGui.PushTextWrapPos(p.X + size.X - 17f);
                    ImGui.TextColored(BackstageDesign.Gold, warning);
                    ImGui.PopTextWrapPos();
                }
                var actionsY = warning is null ? 326f : 394f;
                BackstageDesign.Rule(dl, p + new Vector2(17f, actionsY),
                    p + new Vector2(size.X - 17f, actionsY), BackstageDesign.SoftLine);
                ImGui.SetCursorScreenPos(p + new Vector2(17f, actionsY + 17f));
                ImGui.TextColored(BackstageDesign.Muted, "Edits save automatically");
                if (BackstageDesign.FlatButton("##vhStaffRemoveSelected", "REMOVE SHIFT",
                        new Vector2(p.X + size.X - 146f, p.Y + actionsY + 11f),
                        new Vector2(128f, 31f), BackstageDesign.Rose))
                    this.requestRemoveStaffShift = true;
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private static string? GetStaffShiftWarning(Configuration config, StaffScheduleEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name))
            return "Choose a staff member to include this shift in announcements.";
        if (!Configuration.TryParseServerTime(entry.StartTime, out _) ||
            !Configuration.TryParseServerTime(entry.EndTime, out _) || entry.StartTime == entry.EndTime)
            return "Enter different, valid start and end times.";
        if (!config.TryGetEventWindow(out _, out _))
            return "The event schedule needs valid dates and times.";
        var timeline = config.BuildStaffScheduleTimeline();
        var shift = timeline.FirstOrDefault(slot => ReferenceEquals(slot.Entry, entry));
        return shift is not null && !shift.IsInsideEventWindow
            ? "This shift extends outside the event schedule and won't trigger automatic announcements."
            : null;
    }

    private void AddStaffShift(Configuration config)
    {
        this.CommitStaffTimeDrafts();
        var start = NormalizeTimeText(config.EventStartTime, "18:00");
        config.StaffSchedule.Add(new StaffScheduleEntry
        {
            Order = config.StaffSchedule.Count + 1,
            Role = config.GetExistingOrDefaultStaffRole(config.SelectedStaffRole),
            StartTime = start,
            EndTime = AddMinutesToServerTime(start, 60),
            Available = true,
        });
        config.NormalizeStaffScheduleOrders();
        config.SelectedStaffScheduleOrder = config.StaffSchedule[^1].Order;
        // New shifts must remain visible even if the current filter was narrow.
        this.staffRosterSearch = string.Empty;
        this.staffCollapsedRoles.Remove(config.StaffSchedule[^1].Role);
        this.staffTimeDraftEntry = null;
        config.Save();
        this.StaffAutoShout.Refresh();
    }

    private void EnsureStaffTimeDrafts(StaffScheduleEntry entry)
    {
        if (!ReferenceEquals(this.staffTimeDraftEntry, entry))
        {
            this.staffTimeDraftEntry = entry;
            this.staffStartDraft = entry.StartTime;
            this.staffEndDraft = entry.EndTime;
            this.staffStartInvalid = false;
            this.staffEndInvalid = false;
        }
    }

    private void DrawStaffTimeInput(string id, StaffScheduleEntry entry, bool start, float width)
    {
        var value = start ? this.staffStartDraft : this.staffEndDraft;
        ImGui.SetNextItemWidth(width);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, BackstageDesign.Raised);
        ImGui.PushStyleColor(ImGuiCol.Text,
            (start ? this.staffStartInvalid : this.staffEndInvalid) ? BackstageDesign.Rose : BackstageDesign.Ivory);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9f, 7f));
        var pressedEnter = ImGui.InputText($"##vhStaffTime{id}", ref value, 6, ImGuiInputTextFlags.EnterReturnsTrue);
        var lostFocus = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);
        if (start) this.staffStartDraft = value;
        else this.staffEndDraft = value;
        if (pressedEnter || lostFocus)
            this.CommitStaffTimeField(entry, start);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Type HH:mm in server time; press Enter or click away to save.");
    }

    private void CommitStaffTimeField(StaffScheduleEntry entry, bool start)
    {
        var draft = start ? this.staffStartDraft : this.staffEndDraft;
        if (!TryParseDjInlineTime(draft, out var parsed))
        {
            if (start) this.staffStartInvalid = true;
            else this.staffEndInvalid = true;
            return;
        }
        var before = start ? entry.StartTime : entry.EndTime;
        if (start)
        {
            entry.StartTime = parsed;
            this.staffStartDraft = parsed;
            this.staffStartInvalid = false;
        }
        else
        {
            entry.EndTime = parsed;
            this.staffEndDraft = parsed;
            this.staffEndInvalid = false;
        }
        if (before == parsed) return;
        this.Configuration.Save();
        this.StaffAutoShout.Refresh();
    }

    private void CommitStaffTimeDrafts()
    {
        var entry = this.staffTimeDraftEntry;
        if (entry is null) return;
        if (this.staffStartDraft != entry.StartTime) this.CommitStaffTimeField(entry, true);
        if (this.staffEndDraft != entry.EndTime) this.CommitStaffTimeField(entry, false);
    }

}
