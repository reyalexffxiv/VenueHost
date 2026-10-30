using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueHost.Services;

namespace VenueHost.Windows;

/// <summary>
/// Single, deliberately styled non-modal event editor. This is the sole Overview entry
/// point for event name, date range, venue and giveaway settings. All fields
/// are drafts: Cancel cannot persist a half-edited event or reset schedulers.
/// </summary>
public sealed partial class MainWindow
{
    private const string EventEditorWindowId = "EDIT EVENT###VenueHostEventEditor";
    private bool eventEditorPendingOpen;
    private bool eventEditorFocusRequested;
    private bool eventEditorVisible;
    private bool eventEditorDiscardRequested;
    private string editName = string.Empty;
    private string editVenue = string.Empty;
    private string editStartDate = string.Empty;
    private string editStartTime = string.Empty;
    private string editEndDate = string.Empty;
    private string editEndTime = string.Empty;
    private bool editGiveawayEnabled;
    private string editGiveawayText = string.Empty;
    private bool editCommandEnabled;
    private string editCommand = string.Empty;

    private void RequestEventEditor()
    {
        if (this.eventEditorVisible || this.eventEditorPendingOpen)
        {
            this.eventEditorFocusRequested = true; // Raise the existing draft without replacing it.
            return;
        }
        this.eventEditorDiscardRequested = false;
        var config = this.Configuration;
        this.editName = config.EventName ?? string.Empty;
        this.editVenue = config.VenueName ?? string.Empty;
        this.editStartDate = config.EventStartDate ?? string.Empty;
        this.editStartTime = config.EventStartTime ?? string.Empty;
        this.editEndDate = config.EventEndDate ?? string.Empty;
        this.editEndTime = config.EventEndTime ?? string.Empty;
        this.editGiveawayEnabled = config.GiveawayEnabled;
        this.editGiveawayText = config.GiveawayText ?? string.Empty;
        this.editCommandEnabled = config.GiveawayCommandEnabled;
        this.editCommand = config.GiveawayCommand ?? string.Empty;
        this.eventEditorPendingOpen = true;
    }

    private void DrawEventEditorWindow()
    {
        // Draw a regular floating window from the root, not an ImGui modal.
        // Modal popups dim the entire game and block unrelated interactions.
        if (this.eventEditorPendingOpen)
        {
            this.eventEditorPendingOpen = false;
            this.eventEditorVisible = true;
            this.eventEditorFocusRequested = true;
        }

        if (!this.eventEditorVisible) return;

        const float editorWidth = 670f;
        const float editorHeight = 560f;
        this.CenterNextFloatingWindow(new Vector2(editorWidth, editorHeight));
        ImGui.SetNextWindowSize(new Vector2(editorWidth, editorHeight), ImGuiCond.Appearing);
        if (this.eventEditorFocusRequested)
        {
            ImGui.SetNextWindowFocus();
            this.eventEditorFocusRequested = false;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(24f, 20f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9f, 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(10f, 7f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, BackstageDesign.Rail);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, BackstageDesign.Rail);
        ImGui.PushStyleColor(ImGuiCol.Border, BackstageDesign.SoftLine);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, BackstageDesign.Surface);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, BackstageDesign.Raised);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, BackstageDesign.Raised);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, BackstageDesign.Mint);
        ImGui.PushStyleColor(ImGuiCol.Text, BackstageDesign.Ivory);
        try
        {
            var drawn = ImGui.Begin(EventEditorWindowId,
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar);
            if (!drawn)
            {
                ImGui.End();
                return;
            }

            ImGui.TextColored(BackstageDesign.Gold, "EVENT SETTINGS");
            ImGui.TextColored(BackstageDesign.Muted, "Edit your venue, schedule and event information.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextColored(BackstageDesign.Gold, "EVENT SCHEDULE");
            ImGui.TextColored(BackstageDesign.Dim, "All dates and times use FFXIV Server Time (ST / UTC).");
            if (ImGui.BeginTable("##vhEditEventDates", 2,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextColored(BackstageDesign.Muted, "START  ·  yyyy-MM-dd  /  HH:mm");
                ImGui.SetNextItemWidth(164f);
                ImGui.InputText("##vhEditStartDate", ref this.editStartDate, 11);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(90f);
                ImGui.InputText("##vhEditStartTime", ref this.editStartTime, 6);

                ImGui.TableSetColumnIndex(1);
                ImGui.TextColored(BackstageDesign.Muted, "END  ·  yyyy-MM-dd  /  HH:mm");
                ImGui.SetNextItemWidth(164f);
                ImGui.InputText("##vhEditEndDate", ref this.editEndDate, 11);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(90f);
                ImGui.InputText("##vhEditEndTime", ref this.editEndTime, 6);
                ImGui.EndTable();
            }
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextColored(BackstageDesign.Gold, "EVENT DETAILS");
            ImGui.TextColored(BackstageDesign.Muted, "Event name (optional)");
            ImGui.SetNextItemWidth(610f);
            ImGui.InputText("##vhEditEventName", ref this.editName, 120);
            ImGui.TextColored(BackstageDesign.Muted, "Venue");
            ImGui.SetNextItemWidth(610f);
            ImGui.InputText("##vhEditVenue", ref this.editVenue, 160);
            ImGui.Spacing();

            ImGui.Checkbox("Giveaway##vhEventGiveaway", ref this.editGiveawayEnabled);
            ImGui.SameLine(150f);
            if (!this.editGiveawayEnabled) ImGui.BeginDisabled();
            ImGui.SetNextItemWidth(475f);
            ImGui.InputText("##vhEditGiveawayText", ref this.editGiveawayText, 220);
            if (!this.editGiveawayEnabled) ImGui.EndDisabled();

            if (!this.editGiveawayEnabled) ImGui.BeginDisabled();
            ImGui.Checkbox("Command##vhEventCommandEnabled", ref this.editCommandEnabled);
            if (!this.editGiveawayEnabled) ImGui.EndDisabled();
            ImGui.SameLine(150f);
            if (!this.editGiveawayEnabled || !this.editCommandEnabled) ImGui.BeginDisabled();
            ImGui.SetNextItemWidth(475f);
            ImGui.InputText("##vhEditGiveawayCommand", ref this.editCommand, 80);
            if (!this.editGiveawayEnabled || !this.editCommandEnabled) ImGui.EndDisabled();

            var validWindow = VenueOverviewSnapshot.TryWindow(
                this.editStartDate.Trim(), this.editStartTime.Trim(),
                this.editEndDate.Trim(), this.editEndTime.Trim(), out _, out _);
            if (!validWindow)
            {
                ImGui.Spacing();
                ImGui.TextColored(BackstageDesign.Rose,
                    "Enter valid dates/times; event end must be after its start.");
            }

            // One discard policy across editors: clean drafts close immediately;
            // dirty drafts require explicit Discard or Keep Editing.
            var footerY = ImGui.GetWindowPos().Y + editorHeight - 53f;
            var rightX = ImGui.GetWindowPos().X + editorWidth - 24f;
            if (this.eventEditorDiscardRequested)
            {
                ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X + 24f, footerY + 6f));
                ImGui.TextColored(BackstageDesign.Gold, "Discard unsaved changes?");
                var discard = BackstageDesign.FlatButton("##vhEventEditorDiscard", "DISCARD",
                    new Vector2(rightX - 260f, footerY), new Vector2(117f, 32f), BackstageDesign.Rose);
                var keep = BackstageDesign.FlatButton("##vhEventEditorKeep", "KEEP EDITING",
                    new Vector2(rightX - 132f, footerY), new Vector2(132f, 32f), BackstageDesign.Muted);
                if (discard)
                {
                    this.eventEditorDiscardRequested = false;
                    this.eventEditorVisible = false;
                }
                else if (keep) this.eventEditorDiscardRequested = false;
            }
            else
            {
                if (!validWindow) ImGui.BeginDisabled();
                var save = BackstageDesign.FlatButton("##vhEventEditorSave", "SAVE EVENT",
                    new Vector2(rightX - 134f, footerY), new Vector2(134f, 32f),
                    BackstageDesign.Gold, highlighted: true);
                if (!validWindow) ImGui.EndDisabled();
                if (save && validWindow)
                {
                    this.SaveEventEditor();
                    this.eventEditorVisible = false;
                }
                var cancel = BackstageDesign.FlatButton("##vhEventEditorCancel", "CANCEL",
                    new Vector2(rightX - 265f, footerY), new Vector2(119f, 32f),
                    BackstageDesign.Muted);
                if (cancel)
                {
                    if (this.EventEditorDraftDirty()) this.eventEditorDiscardRequested = true;
                    else
                    {
                        this.eventEditorVisible = false;
                    }
                }
            }
            ImGui.End();
        }
        finally
        {
            ImGui.PopStyleColor(8);
            ImGui.PopStyleVar(6);
        }
    }

    private bool EventEditorDraftDirty()
    {
        var config = this.Configuration;
        return this.editName != (config.EventName ?? string.Empty) ||
               this.editVenue != (config.VenueName ?? string.Empty) ||
               this.editStartDate != (config.EventStartDate ?? string.Empty) ||
               this.editStartTime != (config.EventStartTime ?? string.Empty) ||
               this.editEndDate != (config.EventEndDate ?? string.Empty) ||
               this.editEndTime != (config.EventEndTime ?? string.Empty) ||
               this.editGiveawayEnabled != config.GiveawayEnabled ||
               this.editGiveawayText != (config.GiveawayText ?? string.Empty) ||
               this.editCommandEnabled != config.GiveawayCommandEnabled ||
               this.editCommand != (config.GiveawayCommand ?? string.Empty);
    }

    private void SaveEventEditor()
    {
        var config = this.Configuration;
        var changedWindow = config.EventStartDate != this.editStartDate.Trim() ||
                            config.EventStartTime != this.editStartTime.Trim() ||
                            config.EventEndDate != this.editEndDate.Trim() ||
                            config.EventEndTime != this.editEndTime.Trim();
        config.EventName = this.editName.Trim();
        config.VenueName = this.editVenue.Trim();
        config.EventStartDate = this.editStartDate.Trim();
        config.EventStartTime = this.editStartTime.Trim();
        config.EventEndDate = this.editEndDate.Trim();
        config.EventEndTime = this.editEndTime.Trim();
        config.GiveawayEnabled = this.editGiveawayEnabled;
        config.GiveawayText = this.editGiveawayText.Trim();
        config.GiveawayCommandEnabled = this.editCommandEnabled;
        config.GiveawayCommand = this.editCommand.Trim();
        config.Save();
        if (changedWindow) this.RefreshAutoShoutSchedule();
        this.overviewSnapshotAtUtc = DateTime.MinValue;
        this.overviewTimelineAtUtc = DateTime.MinValue;
    }
}
