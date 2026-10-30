using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace VenueHost.Windows;

/// <summary>
/// The message editors live beside the actions that use them, not in Setup.
/// All message fields use unsaved drafts and a non-modal, draggable window.
/// </summary>
public sealed partial class MainWindow
{
    private enum AnnouncementMacroKind { None, Dj, Staff }
    private AnnouncementMacroKind announcementMacroKind;
    private int djMacroSelected;
    private readonly string[] djMacroDrafts = new string[3];
    private readonly float[] djMacroDelayDrafts = new float[3];
    private string staffMacroDraft = string.Empty;
    private float staffMacroDelayDraft;
    private string staffMacroRole = string.Empty;
    private bool announcementMacroDiscardRequested;
    private bool announcementMacroFocusRequested;

    private void OpenDjMacroEditor(Configuration config)
    {
        if (this.announcementMacroKind == AnnouncementMacroKind.Dj)
        {
            this.announcementMacroFocusRequested = true;
            return; // Preserve the already-open draft.
        }
        if (this.eventShoutEditorOpen)
        {
            this.eventShoutDiscardRequested = true;
            return;
        }
        if (this.announcementMacroKind != AnnouncementMacroKind.None && this.AnnouncementMacroDirty(config))
        {
            this.announcementMacroDiscardRequested = true;
            return;
        }
        this.djMacroDrafts[0] = config.CurrentDjMacro;
        this.djMacroDrafts[1] = config.TransitionMacro;
        this.djMacroDrafts[2] = config.TellCurrentDjMacro;
        this.djMacroDelayDrafts[0] = config.CurrentDjMacroDelaySeconds;
        this.djMacroDelayDrafts[1] = config.TransitionMacroDelaySeconds;
        this.djMacroDelayDrafts[2] = config.TellCurrentDjMacroDelaySeconds;
        this.djMacroSelected = 0;
        this.announcementMacroKind = AnnouncementMacroKind.Dj;
        this.announcementMacroFocusRequested = true;
        this.announcementMacroDiscardRequested = false;
    }

    private void OpenStaffMacroEditor(Configuration config)
    {
        if (this.announcementMacroKind == AnnouncementMacroKind.Staff &&
            string.Equals(this.staffMacroRole, config.SelectedStaffRole, StringComparison.OrdinalIgnoreCase))
        {
            this.announcementMacroFocusRequested = true;
            return; // Preserve the current role's draft.
        }
        if (this.eventShoutEditorOpen)
        {
            this.eventShoutDiscardRequested = true;
            return;
        }
        if (this.announcementMacroKind != AnnouncementMacroKind.None && this.AnnouncementMacroDirty(config))
        {
            this.announcementMacroDiscardRequested = true;
            return;
        }
        this.staffMacroRole = config.SelectedStaffRole;
        this.staffMacroDraft = config.GetStaffShoutMacro(this.staffMacroRole);
        this.staffMacroDelayDraft = config.GetStaffShoutDelaySeconds(this.staffMacroRole);
        this.announcementMacroKind = AnnouncementMacroKind.Staff;
        this.announcementMacroFocusRequested = true;
        this.announcementMacroDiscardRequested = false;
    }

    private bool AnnouncementMacroDirty(Configuration config)
    {
        if (this.announcementMacroKind == AnnouncementMacroKind.Dj)
            return !string.Equals(this.djMacroDrafts[0], config.CurrentDjMacro, StringComparison.Ordinal) ||
                   !string.Equals(this.djMacroDrafts[1], config.TransitionMacro, StringComparison.Ordinal) ||
                   !string.Equals(this.djMacroDrafts[2], config.TellCurrentDjMacro, StringComparison.Ordinal) ||
                   Math.Abs(this.djMacroDelayDrafts[0] - config.CurrentDjMacroDelaySeconds) > 0.001f ||
                   Math.Abs(this.djMacroDelayDrafts[1] - config.TransitionMacroDelaySeconds) > 0.001f ||
                   Math.Abs(this.djMacroDelayDrafts[2] - config.TellCurrentDjMacroDelaySeconds) > 0.001f;
        if (this.announcementMacroKind == AnnouncementMacroKind.Staff)
        {
            return !string.Equals(this.staffMacroDraft, config.GetStaffShoutMacro(this.staffMacroRole), StringComparison.Ordinal) ||
                   Math.Abs(this.staffMacroDelayDraft - config.GetStaffShoutDelaySeconds(this.staffMacroRole)) > 0.001f;
        }
        return false;
    }

    private void CloseAnnouncementMacroEditor(Configuration config)
    {
        if (this.AnnouncementMacroDirty(config))
            this.announcementMacroDiscardRequested = true;
        else
            this.announcementMacroKind = AnnouncementMacroKind.None;
    }

    private void SaveAnnouncementMacroEditor(Configuration config)
    {
        if (this.announcementMacroKind == AnnouncementMacroKind.Dj)
        {
            config.CurrentDjMacro = Configuration.NormalizeAnnouncementMacro(this.djMacroDrafts[0]);
            config.TransitionMacro = Configuration.NormalizeAnnouncementMacro(this.djMacroDrafts[1]);
            config.TellCurrentDjMacro = this.djMacroDrafts[2];
            config.CurrentDjMacroDelaySeconds = this.djMacroDelayDrafts[0];
            config.TransitionMacroDelaySeconds = this.djMacroDelayDrafts[1];
            config.TellCurrentDjMacroDelaySeconds = this.djMacroDelayDrafts[2];
        }
        else if (this.announcementMacroKind == AnnouncementMacroKind.Staff)
        {
            config.SetStaffShoutMacro(this.staffMacroRole, Configuration.NormalizeAnnouncementMacro(this.staffMacroDraft));
            config.SetStaffShoutDelaySeconds(this.staffMacroRole, this.staffMacroDelayDraft);
        }
        config.Save();
        this.announcementMacroKind = AnnouncementMacroKind.None;
        this.announcementMacroDiscardRequested = false;
    }

    private void DrawAnnouncementMacroEditor(Configuration config)
    {
        if (this.announcementMacroKind == AnnouncementMacroKind.None) return;
        const float width = 800f;
        const float height = 490f;
        var windowSize = new Vector2(width, height);
        ImGui.SetNextWindowSize(windowSize, ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(windowSize, windowSize);
        this.CenterNextFloatingWindow(windowSize);
        if (this.announcementMacroFocusRequested)
        {
            ImGui.SetNextWindowFocus();
            this.announcementMacroFocusRequested = false;
        }
        ImGui.PushStyleColor(ImGuiCol.WindowBg, BackstageDesign.Canvas);
        ImGui.PushStyleColor(ImGuiCol.Border, BackstageDesign.SoftLine);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 13f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        if (ImGui.Begin("Edit announcement macros##vhFloatingAnnouncementMacroEditor",
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var p = ImGui.GetWindowPos();
            var dl = ImGui.GetWindowDrawList();
            BackstageDesign.Fill(dl, p, new Vector2(width, 61f), BackstageDesign.Rail, 12f);
            BackstageDesign.Rule(dl, p + new Vector2(0f, 60f), p + new Vector2(width, 60f), BackstageDesign.SoftLine);
            ImGui.SetCursorScreenPos(p + new Vector2(22f, 18f));
            ImGui.TextColored(BackstageDesign.Gold, this.announcementMacroKind == AnnouncementMacroKind.Dj
                ? "EDIT DJ ANNOUNCEMENT MACROS" : $"EDIT STAFF MACRO · {this.staffMacroRole.ToUpperInvariant()}");
            ImGui.SetCursorScreenPos(p + new Vector2(width - 111f, 14f));
            if (this.WorkspaceButton("CLOSE", new Vector2(90f, 30f), BackstageDesign.Muted))
                this.CloseAnnouncementMacroEditor(config);
            ImGui.SetCursorScreenPos(p + new Vector2(240f, 0f));
            ImGui.InvisibleButton("##vhAnnouncementMacroDrag", new Vector2(width - 370f, 59f));
            if (ImGui.IsItemActive())
                ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);

            ImGui.SetCursorScreenPos(p + new Vector2(22f, 79f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            if (ImGui.BeginChild("##vhAnnouncementMacroBody", new Vector2(width - 44f, height - 175f), false))
            {
                if (this.announcementMacroKind == AnnouncementMacroKind.Dj)
                {
                    if (this.WorkspaceButton("CURRENT DJ", new Vector2(128f, 30f),
                            this.djMacroSelected == 0 ? BackstageDesign.Gold : BackstageDesign.Muted)) this.djMacroSelected = 0;
                    ImGui.SameLine(0f, 8f);
                    if (this.WorkspaceButton("HANDOFF", new Vector2(120f, 30f),
                            this.djMacroSelected == 1 ? BackstageDesign.Gold : BackstageDesign.Muted)) this.djMacroSelected = 1;
                    ImGui.SameLine(0f, 8f);
                    if (this.WorkspaceButton("TELL TARGET", new Vector2(130f, 30f),
                            this.djMacroSelected == 2 ? BackstageDesign.Gold : BackstageDesign.Muted)) this.djMacroSelected = 2;
                    ImGui.Dummy(new Vector2(0f, 13f));
                }
                ImGui.TextColored(BackstageDesign.Muted, "MESSAGE / MACRO");
                ImGui.SetNextItemWidth(-1f);
                if (this.announcementMacroKind == AnnouncementMacroKind.Dj)
                {
                    var text = this.djMacroDrafts[this.djMacroSelected];
                    if (ImGui.InputTextMultiline("##vhDjMacroText", ref text, 8000, new Vector2(-1f, 190f)))
                        this.djMacroDrafts[this.djMacroSelected] = text;
                }
                else
                {
                    ImGui.InputTextMultiline("##vhStaffMacroText", ref this.staffMacroDraft, 8000,
                        new Vector2(-1f, 228f));
                }
                ImGui.Dummy(new Vector2(0f, 9f));
                ImGui.TextColored(BackstageDesign.Muted, MacroDelayLabel);
                ImGui.SameLine(0f, 12f);
                ImGui.SetNextItemWidth(150f);
                if (this.announcementMacroKind == AnnouncementMacroKind.Dj)
                {
                    var delay = this.djMacroDelayDrafts[this.djMacroSelected];
                    if (ImGui.SliderFloat("##vhDjMacroWait", ref delay, 0f, 10f, "%.1f"))
                        this.djMacroDelayDrafts[this.djMacroSelected] = delay;
                }
                else
                    ImGui.SliderFloat("##vhStaffMacroWait", ref this.staffMacroDelayDraft, 0f, 10f, "%.1f");

            }
            ImGui.EndChild();
            ImGui.PopStyleVar();

            BackstageDesign.Rule(dl, p + new Vector2(22f, height - 92f), p + new Vector2(width - 22f, height - 92f), BackstageDesign.SoftLine);
            ImGui.SetCursorScreenPos(p + new Vector2(22f, height - 79f));
            if (this.WorkspaceButton("SAVE CHANGES", new Vector2(140f, 32f), BackstageDesign.Mint))
                this.SaveAnnouncementMacroEditor(config);
            if (this.announcementMacroDiscardRequested)
            {
                ImGui.SetCursorScreenPos(p + new Vector2(22f, height - 38f));
                ImGui.TextColored(BackstageDesign.Gold, "Discard unsaved macro changes?");
                ImGui.SameLine(0f, 10f);
                if (this.WorkspaceButton("DISCARD", new Vector2(92f, 27f), BackstageDesign.Rose))
                {
                    this.announcementMacroKind = AnnouncementMacroKind.None;
                    this.announcementMacroDiscardRequested = false;
                }
                ImGui.SameLine(0f, 8f);
                if (this.WorkspaceButton("KEEP EDITING", new Vector2(121f, 27f), BackstageDesign.Muted))
                    this.announcementMacroDiscardRequested = false;
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }
}
