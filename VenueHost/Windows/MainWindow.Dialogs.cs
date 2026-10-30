using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueHost.Models;
using VenueHost.Services;

namespace VenueHost.Windows;

// One non-modal confirmation surface for the four schedule deletion actions.
// Capture the target when requested; selection may change while it is open.
public sealed partial class MainWindow
{
    private enum ScheduleConfirmation { None, ClearDjs, ClearStaff, RemoveDj, RemoveStaff }
    private ScheduleConfirmation scheduleConfirmation;
    private int confirmationTargetOrder;
    private string confirmationProfileId = string.Empty;
    private DjScheduleEntry? confirmationDj;
    private StaffScheduleEntry? confirmationStaff;
    private bool confirmationOpen;

    private string previewHeading = string.Empty;
    private string previewMessage = string.Empty;
    private bool previewOpen;
    private bool previewFocusRequested;

    private void RequestSharedPreview(string title, string message)
    {
        this.previewHeading = title;
        this.previewMessage = message;
        this.previewOpen = true;
        this.previewFocusRequested = true;
    }

    private void DrawConfirmationWindow()
    {
        var config = this.Configuration;
        ScheduleConfirmation requested = ScheduleConfirmation.None;
        if (this.showClearLineupConfirmation)
        {
            requested = ScheduleConfirmation.ClearDjs;
            this.showClearLineupConfirmation = false;
        }
        if (this.showClearStaffListConfirmation)
        {
            requested = ScheduleConfirmation.ClearStaff;
            this.showClearStaffListConfirmation = false;
        }
        if (this.showRemoveDjConfirmation)
        {
            requested = ScheduleConfirmation.RemoveDj;
            this.showRemoveDjConfirmation = false;
        }
        if (this.requestRemoveStaffShift)
        {
            requested = ScheduleConfirmation.RemoveStaff;
            this.requestRemoveStaffShift = false;
        }
        if (requested != ScheduleConfirmation.None)
        {
            this.scheduleConfirmation = requested;
            this.confirmationProfileId = this.Services.Get<VenueProfileService>().ActiveId;
            this.confirmationTargetOrder = requested == ScheduleConfirmation.RemoveDj
                ? config.SelectedDjOrder : requested == ScheduleConfirmation.RemoveStaff
                ? config.SelectedStaffScheduleOrder : 0;
            this.confirmationDj = requested == ScheduleConfirmation.RemoveDj ? config.GetSelectedDj() : null;
            this.confirmationStaff = requested == ScheduleConfirmation.RemoveStaff
                ? config.GetSelectedStaffScheduleEntry() : null;
            this.confirmationOpen = true;
            ImGui.SetNextWindowFocus();
        }
        if (!this.confirmationOpen || this.scheduleConfirmation == ScheduleConfirmation.None)
            return;

        const float width = 385f;
        const float height = 165f;
        var size = new Vector2(width, height);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        this.CenterNextFloatingWindow(size);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, BackstageDesign.Canvas);
        ImGui.PushStyleColor(ImGuiCol.Border, BackstageDesign.SoftLine);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var open = this.confirmationOpen;
        if (ImGui.Begin("Confirm schedule change##vhScheduleConfirmation", ref open,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var p = ImGui.GetWindowPos();
            var dl = ImGui.GetWindowDrawList();
            BackstageDesign.Fill(dl, p, new Vector2(width, 50f), BackstageDesign.Rail, 12f);
            BackstageDesign.Rule(dl, p + new Vector2(0f, 49f), p + new Vector2(width, 49f), BackstageDesign.SoftLine);
            var title = this.scheduleConfirmation switch
            {
                ScheduleConfirmation.ClearDjs => "CLEAR DJ LINEUP?",
                ScheduleConfirmation.ClearStaff => "CLEAR STAFF SCHEDULE?",
                ScheduleConfirmation.RemoveDj => "REMOVE DJ SLOT?",
                _ => "REMOVE STAFF SHIFT?",
            };
            var message = this.scheduleConfirmation switch
            {
                ScheduleConfirmation.ClearDjs => "Remove all DJ slots?",
                ScheduleConfirmation.ClearStaff => "Remove all staff shifts?",
                ScheduleConfirmation.RemoveDj => $"Remove DJ slot {this.confirmationTargetOrder}?",
                _ => string.IsNullOrWhiteSpace(this.confirmationStaff?.Name)
                    ? "Remove this staff shift?" : $"Remove {this.confirmationStaff.Name}'s shift?",
            };
            ImGui.SetCursorScreenPos(p + new Vector2(18f, 16f));
            ImGui.TextColored(BackstageDesign.Gold, title);
            ImGui.SetCursorScreenPos(p + new Vector2(width - 48f, 10f));
            if (this.WorkspaceButton("X##vhConfirmClose", new Vector2(30f, 29f), BackstageDesign.Muted))
                open = false;
            ImGui.SetCursorScreenPos(p + new Vector2(18f, 67f));
            ImGui.TextColored(BackstageDesign.Ivory, message);
            var action = this.scheduleConfirmation switch
            {
                ScheduleConfirmation.ClearDjs => "CLEAR LINEUP",
                ScheduleConfirmation.ClearStaff => "CLEAR SCHEDULE",
                _ => "REMOVE",
            };
            ImGui.SetCursorScreenPos(p + new Vector2(18f, 114f));
            if (this.WorkspaceButton(action + "##vhConfirmAccept", new Vector2(145f, 31f), BackstageDesign.Rose))
            {
                this.ApplyScheduleConfirmation(config);
                open = false;
            }
            ImGui.SetCursorScreenPos(p + new Vector2(174f, 114f));
            if (this.WorkspaceButton("CANCEL##vhConfirmCancel", new Vector2(98f, 31f), BackstageDesign.Muted))
                open = false;
            ImGui.SetCursorScreenPos(p + new Vector2(270f, 0f));
            ImGui.InvisibleButton("##vhConfirmDrag", new Vector2(60f, 48f));
            if (ImGui.IsItemActive()) ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
        this.confirmationOpen = open;
        if (!open) this.scheduleConfirmation = ScheduleConfirmation.None;
    }

    private void ApplyScheduleConfirmation(Configuration config)
    {
        // A non-modal confirmation must never clear a different venue after a profile switch.
        if (this.confirmationProfileId != this.Services.Get<VenueProfileService>().ActiveId)
            return;
        switch (this.scheduleConfirmation)
        {
            case ScheduleConfirmation.ClearDjs:
                config.DjSchedule.Clear();
                config.SelectedDjOrder = 0;
                config.AutoCurrentDjShoutEnabled = false;
                this.DjAutoShout.Refresh();
                break;
            case ScheduleConfirmation.ClearStaff:
                config.StaffSchedule.Clear();
                config.SelectedStaffScheduleOrder = 0;
                config.AutoPhotographerShoutEnabled = false;
                foreach (var setting in config.StaffRoleShoutSettings)
                    setting.AutoEnabled = false;
                this.staffTimeDraftEntry = null;
                this.StaffAutoShout.Refresh();
                break;
            case ScheduleConfirmation.RemoveDj:
                if (this.confirmationDj is null || !config.DjSchedule.Contains(this.confirmationDj)) break;
                config.DjSchedule.Remove(this.confirmationDj);
                config.NormalizeScheduleOrders();
                config.SelectedDjOrder = config.DjSchedule.Count == 0 ? 0
                    : Math.Clamp(this.confirmationTargetOrder, 1, config.DjSchedule.Count);
                config.AutoCurrentDjShoutEnabled = config.DjSchedule.Any(Configuration.HasUsableDj);
                this.DjAutoShout.Refresh();
                break;
            case ScheduleConfirmation.RemoveStaff:
                var entry = this.confirmationStaff;
                if (entry is not null && !config.StaffSchedule.Contains(entry)) entry = null;
                if (entry is null) break;
                // Discard pending drafts for this removed entry rather than applying them to another row.
                config.StaffSchedule.Remove(entry);
                config.NormalizeStaffScheduleOrders();
                config.SelectedStaffScheduleOrder = config.StaffSchedule.FirstOrDefault()?.Order ?? 0;
                this.staffTimeDraftEntry = null;
                this.StaffAutoShout.Refresh();
                break;
        }
        config.Save();
    }

    private void DrawSharedPreviewWindow()
    {
        if (!this.previewOpen) return;
        const float width = 650f;
        var lines = this.previewMessage.Split('\n').Length;
        var contentHeight = Math.Clamp(lines * 21f + 12f, 80f, 335f);
        var size = new Vector2(width, contentHeight + 78f);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        this.CenterNextFloatingWindow(size);
        if (this.previewFocusRequested)
        {
            ImGui.SetNextWindowFocus();
            this.previewFocusRequested = false;
        }
        ImGui.PushStyleColor(ImGuiCol.WindowBg, BackstageDesign.Canvas);
        ImGui.PushStyleColor(ImGuiCol.Border, BackstageDesign.SoftLine);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("Shout preview##vhSharedPreview", ref this.previewOpen,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var p = ImGui.GetWindowPos();
            var dl = ImGui.GetWindowDrawList();
            BackstageDesign.Fill(dl, p, new Vector2(width, 53f), BackstageDesign.Rail, 12f);
            BackstageDesign.Rule(dl, p + new Vector2(0f, 52f), p + new Vector2(width, 52f), BackstageDesign.SoftLine);
            ImGui.SetCursorScreenPos(p + new Vector2(19f, 17f));
            ImGui.TextColored(BackstageDesign.Gold, this.previewHeading);
            ImGui.SetCursorScreenPos(p + new Vector2(width - 114f, 11f));
            if (this.WorkspaceButton("CLOSE", new Vector2(96f, 30f), BackstageDesign.Muted))
                this.previewOpen = false;
            ImGui.SetCursorScreenPos(p + new Vector2(235f, 0f));
            ImGui.InvisibleButton("##vhPreviewDrag", new Vector2(width - 375f, 51f));
            if (ImGui.IsItemActive()) ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);
            ImGui.SetCursorScreenPos(p + new Vector2(19f, 66f));
            if (ImGui.BeginChild("##vhPreviewText", new Vector2(width - 38f, contentHeight), false))
                ImGui.TextWrapped(this.previewMessage);
            ImGui.EndChild();
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }
}
