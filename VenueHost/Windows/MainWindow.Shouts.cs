using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueHost.Models;
using VenueHost.Services;

namespace VenueHost.Windows;

/// <summary>
/// v0.8.1: distinct announcement panels and a non-modal shout editor.
/// Scheduler, message expansion, queueing, and configuration persistence stay in services.
/// </summary>
public sealed partial class MainWindow
{
    private const string ShoutEditorId = "Edit event shout##vhShoutEditorFloating";
    private string eventShoutSearch = string.Empty;
    private bool eventShoutDiscardRequested;
    private bool eventShoutRemoveRequested;

    private void DrawShoutsWorkspace(Configuration config)
    {
        var available = ImGui.GetContentRegionAvail();
        const float gap = 12f;
        const float djHeight = 89f;
        const float staffHeight = 89f;
        this.DrawDjAnnouncementPanel(config, new Vector2(available.X, djHeight));
        ImGui.Dummy(new Vector2(0f, gap));
        this.DrawStaffAnnouncementPanel(config, new Vector2(available.X, staffHeight));
        ImGui.Dummy(new Vector2(0f, gap));
        var listHeight = MathF.Min(MathF.Max(125f, available.Y - djHeight - staffHeight - gap * 3f - 40f),
            91f + MathF.Min(7, Math.Max(1, config.EventShouts.Count)) * 58f +
            (config.EventShouts.Count > 7 ? 38f : 0f));
        this.DrawCompactShoutList(config, new Vector2(ImGui.GetContentRegionAvail().X, listHeight));
    }

    // Floating editors are drawn by MainWindow.Draw, even if the user switches
    // tabs or collapses the main workspace while editing a message.
    private void DrawFloatingShoutEditors(Configuration config)
    {
        this.DrawShoutEditorWindow(config);
        this.DrawAnnouncementMacroEditor(config);
    }

    private void DrawDjAnnouncementPanel(Configuration config, Vector2 size)
    {
        var p = ImGui.GetCursorScreenPos();
        BackstageDesign.SurfacePanel(p, size);
        if (ImGui.BeginChild("##vhDjAnnouncementCard", size, false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var nowUtc = DateTime.UtcNow;
            var currentValid = Configuration.HasUsableDj(config.GetCurrentServerTimeDj(nowUtc));
            var nextValid = Configuration.HasUsableDj(config.GetNextServerTimeDj(nowUtc));
            ImGui.SetCursorScreenPos(p + new Vector2(15f, 13f));
            ImGui.TextColored(BackstageDesign.Gold, "DJ ANNOUNCEMENTS");

            BackstageDesign.Rule(ImGui.GetWindowDrawList(), p + new Vector2(15f, 45f),
                p + new Vector2(size.X - 15f, 45f), BackstageDesign.SoftLine);
            var x = p.X + 16f;
            var y = p.Y + 52f;
            ImGui.SetCursorScreenPos(new Vector2(x, y));
            if (!currentValid) ImGui.BeginDisabled();
            if (this.WorkspaceButton("SHOUT DJ", new Vector2(100f, 29f), BackstageDesign.Rose))
                this.Shout.SendCurrentDjShout();
            if (!currentValid) ImGui.EndDisabled();
            ImGui.SameLine(0f, 8f);
            if (!currentValid || !nextValid) ImGui.BeginDisabled();
            if (this.WorkspaceButton("HANDOFF", new Vector2(99f, 29f), BackstageDesign.Gold))
                this.Shout.SendTransitionShout();
            if (!currentValid || !nextValid) ImGui.EndDisabled();
            ImGui.SameLine(0f, 8f);
            if (!currentValid) ImGui.BeginDisabled();
            if (this.WorkspaceButton("TELL TARGET", new Vector2(115f, 29f), BackstageDesign.Mint))
                this.Shout.TellCurrentDjToTarget();
            if (!currentValid) ImGui.EndDisabled();
            ImGui.SameLine(0f, 8f);
            if (!currentValid) ImGui.BeginDisabled();
            if (this.WorkspaceButton("PREVIEW", new Vector2(90f, 29f), BackstageDesign.Mist))
            {
                var current = this.Shout.PreviewCurrentDjShout();
                var hasHandoff = Configuration.HasUsableDj(config.GetNextServerTimeDj(DateTime.UtcNow));
                var handoff = hasHandoff ? this.Shout.PreviewTransitionShout() : "No scheduled handoff";
                this.RequestSharedPreview("DJ SHOUT PREVIEW", $"CURRENT DJ\n{current}\n\nHANDOFF\n{handoff}");
            }
            if (!currentValid) ImGui.EndDisabled();
            var automation = this.Services.Get<AutomationControlService>();
            ImGui.SetCursorScreenPos(p + new Vector2(460f, 52f));
            if (this.WorkspaceButton(automation.DjRunning ? "AUTO ON" : "AUTO OFF",
                    new Vector2(96f, 30f), automation.DjRunning ? BackstageDesign.Mint : BackstageDesign.Rose))
                automation.ToggleDj();
            var djInterval = this.DrawAnnouncementInterval(p + new Vector2(568f, 59f),
                config.AutoCurrentDjShoutIntervalMinutes, 120);
            if (djInterval != config.AutoCurrentDjShoutIntervalMinutes)
            {
                config.AutoCurrentDjShoutIntervalMinutes = djInterval;
                this.DjAutoShout.Refresh();
                config.Save();
            }
            // Keep the editor action right aligned and independent of DJ selection.
            if (this.DrawAnnouncementEditAction(p, size, 52f, "EDIT MACROS"))
                this.OpenDjMacroEditor(config);
        }
        ImGui.EndChild();
    }

    private void DrawStaffAnnouncementPanel(Configuration config, Vector2 size)
    {
        var p = ImGui.GetCursorScreenPos();
        BackstageDesign.SurfacePanel(p, size);
        if (ImGui.BeginChild("##vhStaffAnnouncementCard", size, false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.SetCursorScreenPos(p + new Vector2(15f, 13f));
            ImGui.TextColored(BackstageDesign.Gold, "STAFF ANNOUNCEMENTS");
            BackstageDesign.Rule(ImGui.GetWindowDrawList(), p + new Vector2(15f, 45f),
                p + new Vector2(size.X - 15f, 45f), BackstageDesign.SoftLine);
            var roles = config.GetKnownStaffRoles().ToList();
            if (BackstageDesign.RoleSelector("##vhQuickStaffRole", config.SelectedStaffRole,
                    roles, p + new Vector2(16f, 51f), new Vector2(229f, 30f), out var chosen))
            {
                config.SelectedStaffRole = chosen;
                config.Save();
            }
            var count = config.GetCurrentServerTimeStaffForRole(DateTime.UtcNow, config.SelectedStaffRole)
                .Where(e => e.Available && !string.IsNullOrWhiteSpace(e.Name))
                .Select(e => e.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            // Keep the availability guard; the redundant availability counter is not displayed.
            ImGui.SetCursorScreenPos(p + new Vector2(253f, 51f));
            if (count == 0) ImGui.BeginDisabled();
            if (this.WorkspaceButton("SHOUT ROLE", new Vector2(132f, 30f), BackstageDesign.Mint))
                this.Shout.SendSelectedStaffRoleShout();
            if (count == 0) ImGui.EndDisabled();
            ImGui.SetCursorScreenPos(p + new Vector2(393f, 51f));
            if (this.WorkspaceButton("PREVIEW", new Vector2(88f, 30f), BackstageDesign.Mist))
                this.RequestSharedPreview("STAFF SHOUT PREVIEW", this.Shout.PreviewSelectedStaffRoleShout());
            var setting = config.GetOrCreateStaffRoleShoutSetting(config.SelectedStaffRole);
            var automation = this.Services.Get<AutomationControlService>();
            ImGui.SetCursorScreenPos(p + new Vector2(489f, 51f));
            var roleRunning = setting.AutoEnabled && automation.StaffRunning;
            if (this.WorkspaceButton(roleRunning ? "AUTO ON" : "AUTO OFF",
                    new Vector2(96f, 30f), roleRunning ? BackstageDesign.Mint : BackstageDesign.Rose))
                automation.SetStaffRoleAuto(config.SelectedStaffRole, !roleRunning, setting.IntervalMinutes, fromToggle: true);

            var staffInterval = this.DrawAnnouncementInterval(p + new Vector2(597f, 58f),
                setting.IntervalMinutes, 120);
            if (staffInterval != setting.IntervalMinutes)
                automation.SetStaffRoleAuto(config.SelectedStaffRole, setting.AutoEnabled, staffInterval);

            // Manual shout and macro editing remain independent of automation.
            if (this.DrawAnnouncementEditAction(p, size, 51f, "EDIT MACRO"))
                this.OpenStaffMacroEditor(config);
        }
        ImGui.EndChild();
    }

    // Shared, compact interval control: highlighted EVERY, value, adjacent -/+.
    // A value is returned rather than saved here; each announcement owner handles its scheduler.
    private int DrawAnnouncementInterval(Vector2 pos, int minutes, int maxMinutes)
    {
        ImGui.SetCursorScreenPos(pos);
        ImGui.TextColored(BackstageDesign.Gold, "EVERY");
        ImGui.SetCursorScreenPos(pos + new Vector2(46f, 0f));
        ImGui.TextColored(BackstageDesign.Ivory, $"{minutes} MIN");
        ImGui.SetCursorScreenPos(pos + new Vector2(112f, -7f));
        if (this.WorkspaceButton("-", new Vector2(27f, 30f), BackstageDesign.Muted))
            minutes--;
        ImGui.SetCursorScreenPos(pos + new Vector2(141f, -7f));
        if (this.WorkspaceButton("+", new Vector2(27f, 30f), BackstageDesign.Muted))
            minutes++;
        return Math.Clamp(minutes, 1, maxMinutes);
    }

    /// <summary>
    /// Right-align an announcement editor button within its card. All positions
    /// passed to SetCursorScreenPos must be in screen coordinates; card-relative
    /// offsets are added to the card origin exactly once.
    /// </summary>
    private bool DrawAnnouncementEditAction(Vector2 cardOrigin, Vector2 cardSize,
        float rowOffsetY, string label)
    {
        ImGui.SetCursorScreenPos(cardOrigin + new Vector2(cardSize.X - 145f, rowOffsetY));
        return this.WorkspaceButton(label, new Vector2(128f, 30f), BackstageDesign.Mist);
    }

    private void RequestEditShout(EventShoutEntry entry)
    {
        if (this.eventShoutEditorOpen)
        {
            if (!this.eventShoutEditorIsNew && this.eventShoutEditorOrder == entry.Order)
            {
                this.eventShoutEditorFocusRequested = true;
                return;
            }
            this.eventShoutDiscardRequested = true;
            return;
        }
        if (this.announcementMacroKind != AnnouncementMacroKind.None)
        {
            this.announcementMacroDiscardRequested = true;
            return;
        }
        this.OpenEditEventShoutEditor(entry);
        this.eventShoutDiscardRequested = false;
        this.eventShoutRemoveRequested = false;
    }

    private void RequestNewShout(Configuration config)
    {
        if (this.eventShoutEditorOpen)
        {
            if (this.eventShoutEditorIsNew)
            {
                this.eventShoutEditorFocusRequested = true;
                return;
            }
            this.eventShoutDiscardRequested = true;
            return;
        }
        if (this.announcementMacroKind != AnnouncementMacroKind.None)
        {
            this.announcementMacroDiscardRequested = true;
            return;
        }
        this.OpenAddEventShoutEditor(config);
        this.eventShoutDiscardRequested = false;
        this.eventShoutRemoveRequested = false;
    }

    private void SelectSavedShout(Configuration config, EventShoutEntry entry)
    {
        if (config.SelectedEventShoutOrder == entry.Order) return;
        config.SelectedEventShoutOrder = entry.Order;
        config.Save();
    }

    private void DrawCompactShoutList(Configuration config, Vector2 size)
    {
        var p = ImGui.GetCursorScreenPos();
        BackstageDesign.SurfacePanel(p, size);
        if (ImGui.BeginChild("##vhCompactShoutDirectory", size, false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.SetCursorScreenPos(p + new Vector2(16f, 14f));
            ImGui.TextColored(BackstageDesign.Gold, "CUSTOM EVENT SHOUTS");
            ImGui.SetCursorScreenPos(p + new Vector2(size.X - 132f, 8f));
            if (this.WorkspaceButton("+ ADD SHOUT", new Vector2(116f, 30f), BackstageDesign.Mint))
                this.RequestNewShout(config);
            BackstageDesign.Rule(ImGui.GetWindowDrawList(), p + new Vector2(15f, 47f),
                p + new Vector2(size.X - 15f, 47f), BackstageDesign.SoftLine);

            var searchShown = config.EventShouts.Count > 7;
            var listTop = searchShown ? 92f : 57f;
            if (searchShown)
            {
                ImGui.SetCursorScreenPos(p + new Vector2(15f, 56f));
                ImGui.SetNextItemWidth(size.X - 30f);
                ImGui.InputTextWithHint("##vhSearchSavedShouts", "Search saved shouts...", ref this.eventShoutSearch, 100);
            }
            ImGui.SetCursorScreenPos(p + new Vector2(14f, listTop));
            var listHeight = MathF.Max(65f, size.Y - listTop - 11f);
            if (ImGui.BeginChild("##vhSavedShoutsScroll", new Vector2(size.X - 28f, listHeight), false))
            {
                if (config.EventShouts.Count == 0)
                    ImGui.TextColored(BackstageDesign.Muted, "No saved announcements. Use + ADD SHOUT to create one.");

                // Fixed saved order also breaks ties between simultaneous timers.
                foreach (var entry in config.EventShouts.OrderBy(e => e.Order).ToList())
                {
                    if (searchShown && !string.IsNullOrWhiteSpace(this.eventShoutSearch) &&
                        !entry.Name.Contains(this.eventShoutSearch, StringComparison.OrdinalIgnoreCase))
                        continue;
                    ImGui.PushID(entry.Order);
                    this.DrawCompactShoutRow(config, entry);
                    ImGui.PopID();
                }
            }
            ImGui.EndChild();
        }
        ImGui.EndChild();
    }

    private void DrawCompactShoutRow(Configuration config, EventShoutEntry entry)
    {
        var p = ImGui.GetCursorScreenPos();
        var width = MathF.Max(600f, ImGui.GetContentRegionAvail().X - 5f);
        const float height = 58f;
        var selected = config.SelectedEventShoutOrder == entry.Order;
        var draw = ImGui.GetWindowDrawList();
        var fill = selected ? new Vector4(0.245f, 0.210f, 0.194f, 1f) : BackstageDesign.Raised;
        BackstageDesign.Fill(draw, p, new Vector2(width, 50f), fill, 8f);
        BackstageDesign.Frame(draw, p, new Vector2(width, 50f),
            selected ? BackstageDesign.Gold with { W = 0.44f } : BackstageDesign.SoftLine, 8f);
        if (selected)
            BackstageDesign.Fill(draw, p + new Vector2(0f, 4f), new Vector2(3f, 42f), BackstageDesign.Gold, 2f);

        // Each shout owns its automation controls; no list-order buttons needed.
        var actionX = p.X + width - 306f;
        var autoX = actionX - 286f;
        ImGui.SetCursorScreenPos(p + new Vector2(8f, 3f));
        if (ImGui.InvisibleButton("##vhShoutSelect", new Vector2(MathF.Max(100f, autoX - p.X - 16f), 44f)))
            this.SelectSavedShout(config, entry);
        ImGui.PushClipRect(p + new Vector2(15f, 4f),
            new Vector2(autoX - 12f, p.Y + 48f), true);
        draw.AddText(p + new Vector2(17f, 18f), BackstageDesign.Color(BackstageDesign.Ivory), entry.Name);
        ImGui.PopClipRect();

        var automation = this.Services.Get<AutomationControlService>();
        var eventRunning = entry.AutoEnabled && automation.EventRunning;
        if (this.ShoutRowButton("auto", eventRunning ? "AUTO ON" : "AUTO OFF",
                new Vector2(autoX, p.Y + 10f), new Vector2(96f, 30f),
                eventRunning ? BackstageDesign.Mint : BackstageDesign.Rose))
            this.SetEventShoutAutomation(config, entry, !eventRunning, entry.IntervalMinutes, fromToggle: true);

        ImGui.PushID("interval");
        var interval = this.DrawAnnouncementInterval(new Vector2(autoX + 107f, p.Y + 17f),
            entry.IntervalMinutes, 240);
        ImGui.PopID();
        if (interval != entry.IntervalMinutes)
            this.SetEventShoutAutomation(config, entry, entry.AutoEnabled, interval);

        if (this.ShoutRowButton("edit", "EDIT", new Vector2(actionX, p.Y + 10f),
                new Vector2(78f, 30f), BackstageDesign.Mist))
        {
            this.SelectSavedShout(config, entry);
            this.RequestEditShout(entry);
        }
        if (this.ShoutRowButton("preview", "PREVIEW", new Vector2(actionX + 86f, p.Y + 10f),
                new Vector2(85f, 30f), BackstageDesign.Mist))
        {
            this.RequestSharedPreview("SHOUT PREVIEW", this.EventShouts.PreviewEventShout(entry));
        }
        if (this.ShoutRowButton("send", "SHOUT NOW", new Vector2(actionX + 179f, p.Y + 10f),
                new Vector2(112f, 30f), BackstageDesign.Gold))
        {
            this.SelectSavedShout(config, entry);
            this.EventShouts.SendSelectedEventShout();
        }
        // Register the row height for scrolling, not only the drawn rectangle.
        ImGui.SetCursorScreenPos(p + new Vector2(0f, height - 1f));
        ImGui.Dummy(new Vector2(1f, 1f));
        ImGui.SetCursorScreenPos(p + new Vector2(0f, height));
    }

    private void SetEventShoutAutomation(Configuration config, EventShoutEntry entry, bool enabled,
        int interval, bool fromToggle = false)
    {
        entry.AutoEnabled = enabled;
        entry.IntervalMinutes = Math.Clamp(interval, 1, 240);
        if (fromToggle && enabled)
            this.Services.Get<AutomationControlService>().StartEventFromRow();
        if (!config.EventShouts.Any(shout => shout.AutoEnabled && Configuration.HasUsableEventShout(shout)))
            config.EventAutomationMasterEnabled = false;
        config.Save();
        this.EventShouts.Refresh();
    }

    private bool ShoutRowButton(string id, string label, Vector2 pos, Vector2 size, Vector4 accent)
    {
        ImGui.PushID(id);
        ImGui.SetCursorScreenPos(pos);
        var clicked = this.WorkspaceButton(label, size, accent);
        ImGui.PopID();
        return clicked;
    }

    private EventShoutEntry BuildEventShoutPreviewDraft()
        => new()
        {
            Order = this.eventShoutEditorOrder,
            Name = this.eventShoutEditorName,
            Macro = this.eventShoutEditorMacro,
            Active = true,
            AutoEnabled = this.Configuration.EventShouts.FirstOrDefault(e => e.Order == this.eventShoutEditorOrder)?.AutoEnabled ?? false,
            IntervalMinutes = this.Configuration.EventShouts.FirstOrDefault(e => e.Order == this.eventShoutEditorOrder)?.IntervalMinutes ?? 15,
            StartTime = this.eventShoutEditorStartTime,
            EndTime = this.eventShoutEditorEndTime,
            DelaySeconds = this.eventShoutEditorDelaySeconds,
        };

    private void RequestCloseShoutEditor(Configuration config)
    {
        if (this.ShoutDraftDirty(config))
            this.eventShoutDiscardRequested = true;
        else
        {
            this.eventShoutEditorOpen = false;
            this.eventShoutDiscardRequested = false;
        }
    }

    private void DrawShoutEditorWindow(Configuration config)
    {
        if (!this.eventShoutEditorOpen) return;
        const float width = 846f;
        const float height = 655f;
        var popupSize = new Vector2(width, height);
        ImGui.SetNextWindowSize(popupSize, ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(popupSize, popupSize);
        this.CenterNextFloatingWindow(popupSize);
        if (this.eventShoutEditorFocusRequested)
        {
            ImGui.SetNextWindowFocus();
            this.eventShoutEditorFocusRequested = false;
        }
        ImGui.PushStyleColor(ImGuiCol.WindowBg, BackstageDesign.Canvas);
        ImGui.PushStyleColor(ImGuiCol.Border, BackstageDesign.SoftLine);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 13f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        if (ImGui.Begin(ShoutEditorId,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var p = ImGui.GetWindowPos();
            var dl = ImGui.GetWindowDrawList();
            BackstageDesign.Fill(dl, p, new Vector2(width, 61f), BackstageDesign.Rail, 12f);
            BackstageDesign.Rule(dl, p + new Vector2(0f, 60f), p + new Vector2(width, 60f), BackstageDesign.SoftLine);
            ImGui.SetCursorScreenPos(p + new Vector2(22f, 17f));
            ImGui.TextColored(BackstageDesign.Gold,
                this.eventShoutEditorIsNew ? "NEW EVENT SHOUT" : "EDIT EVENT SHOUT");
            ImGui.SetCursorScreenPos(p + new Vector2(width - 112f, 14f));
            if (this.WorkspaceButton("CLOSE", new Vector2(90f, 30f), BackstageDesign.Muted))
                this.RequestCloseShoutEditor(config);
            // Drag the editor by the unoccupied title strip, not its controls.
            ImGui.SetCursorScreenPos(p + new Vector2(160f, 0f));
            ImGui.InvisibleButton("##vhShoutEditorDrag", new Vector2(width - 300f, 59f));
            if (ImGui.IsItemActive())
                ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);

            // The content area scrolls independently, while Save/Cancel remain fixed.
            ImGui.SetCursorScreenPos(p + new Vector2(23f, 76f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            if (ImGui.BeginChild("##vhShoutModalBody", new Vector2(width - 46f, 475f), false))
            {
                ImGui.TextColored(BackstageDesign.Muted, "NAME");
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("##vhModalShoutName", "Announcement name", ref this.eventShoutEditorName, 80);
                ImGui.Dummy(new Vector2(0f, 9f));
                ImGui.TextColored(BackstageDesign.Muted, "MESSAGE / MACRO");
                ImGui.SetNextItemWidth(-1f);
                // Keep the macro large; automation lives on the shout row.
                ImGui.InputTextMultiline("##vhModalShoutMacro", ref this.eventShoutEditorMacro, 2000,
                    new Vector2(-1f, 218f));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("One message per line.");
                ImGui.Dummy(new Vector2(0f, 8f));
                ImGui.TextColored(BackstageDesign.Muted, "START / ST");
                ImGui.SameLine(410f);
                ImGui.TextColored(BackstageDesign.Muted, "END / ST");
                ImGui.SetNextItemWidth(386f);
                ImGui.InputText("##vhModalStart", ref this.eventShoutEditorStartTime, 6);
                ImGui.SameLine(0f, 14f);
                ImGui.SetNextItemWidth(386f);
                ImGui.InputText("##vhModalEnd", ref this.eventShoutEditorEndTime, 6);
                ImGui.TextColored(BackstageDesign.Muted, MacroDelayLabel);
                ImGui.SameLine(0f, 12f);
                ImGui.SetNextItemWidth(150f);
                ImGui.SliderFloat("##vhModalDelay", ref this.eventShoutEditorDelaySeconds, 0f, 10f, "%.1f");
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();

            BackstageDesign.Rule(dl, p + new Vector2(22f, 568f),
                p + new Vector2(width - 22f, 568f), BackstageDesign.SoftLine);
            var validTimes = Configuration.TryParseServerTime(this.eventShoutEditorStartTime, out var start) &&
                             Configuration.TryParseServerTime(this.eventShoutEditorEndTime, out var end) && start != end;
            var canSave = !string.IsNullOrWhiteSpace(this.eventShoutEditorName) &&
                          !string.IsNullOrWhiteSpace(this.eventShoutEditorMacro) && validTimes;
            ImGui.SetCursorScreenPos(p + new Vector2(22f, 581f));
            if (!canSave) ImGui.BeginDisabled();
            if (this.WorkspaceButton("SAVE CHANGES", new Vector2(142f, 32f), BackstageDesign.Mint))
            {
                this.SaveEventShoutEditor(config);
                this.eventShoutEditorOpen = false;
                this.eventShoutDiscardRequested = false;
            }
            if (!canSave) ImGui.EndDisabled();
            if (!this.eventShoutEditorIsNew)
            {
                ImGui.SetCursorScreenPos(p + new Vector2(width - 127f, 581f));
                if (this.WorkspaceButton("REMOVE", new Vector2(105f, 32f), BackstageDesign.Rose))
                {
                    this.eventShoutRemoveRequested = true;
                    this.eventShoutDiscardRequested = false;
                }
            }

            if (this.eventShoutDiscardRequested)
            {
                ImGui.SetCursorScreenPos(p + new Vector2(22f, 622f));
                ImGui.TextColored(BackstageDesign.Gold, "Discard unsaved changes?");
                ImGui.SameLine(0f, 10f);
                if (this.WorkspaceButton("DISCARD", new Vector2(90f, 27f), BackstageDesign.Rose))
                {
                    this.eventShoutEditorOpen = false;
                    this.eventShoutDiscardRequested = false;
                }
                ImGui.SameLine(0f, 8f);
                if (this.WorkspaceButton("KEEP EDITING", new Vector2(118f, 27f), BackstageDesign.Muted))
                    this.eventShoutDiscardRequested = false;
            }
            else if (this.eventShoutRemoveRequested)
            {
                ImGui.SetCursorScreenPos(p + new Vector2(22f, 622f));
                ImGui.TextColored(BackstageDesign.Rose, "Remove this saved shout?");
                ImGui.SameLine(0f, 10f);
                if (this.WorkspaceButton("CONFIRM REMOVE", new Vector2(144f, 27f), BackstageDesign.Rose))
                {
                    var selected = config.EventShouts.FirstOrDefault(e => e.Order == this.eventShoutEditorOrder);
                    if (selected is not null)
                    {
                        config.EventShouts.Remove(selected);
                        config.NormalizeEventShoutOrders();
                        config.SelectedEventShoutOrder = config.EventShouts.FirstOrDefault()?.Order ?? 0;
                        config.Save();
                        this.EventShouts.Refresh();
                    }
                    this.eventShoutEditorOpen = false;
                    this.eventShoutRemoveRequested = false;
                }
                ImGui.SameLine(0f, 8f);
                if (this.WorkspaceButton("KEEP", new Vector2(75f, 27f), BackstageDesign.Muted))
                    this.eventShoutRemoveRequested = false;
            }
            else if (!validTimes)
            {
                ImGui.SetCursorScreenPos(p + new Vector2(22f, 624f));
                ImGui.TextColored(BackstageDesign.Rose, "Start and end must be distinct HH:mm times.");
            }

        }
        ImGui.End();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }

    private bool ShoutDraftDirty(Configuration config)
    {
        if (!this.eventShoutEditorOpen || this.eventShoutOpeningDraft is null) return false;
        // Match the values displayed when the window opened (including normalized
        // times/defaults). Opening and closing without editing must never warn.
        var original = this.eventShoutOpeningDraft;
        return !string.Equals(original.Name, this.eventShoutEditorName, StringComparison.Ordinal) ||
               !string.Equals(original.Macro, this.eventShoutEditorMacro, StringComparison.Ordinal) ||
               !string.Equals(original.StartTime, this.eventShoutEditorStartTime, StringComparison.Ordinal) ||
               !string.Equals(original.EndTime, this.eventShoutEditorEndTime, StringComparison.Ordinal) ||
               MathF.Abs(original.DelaySeconds - this.eventShoutEditorDelaySeconds) > 0.001f;
    }
}
