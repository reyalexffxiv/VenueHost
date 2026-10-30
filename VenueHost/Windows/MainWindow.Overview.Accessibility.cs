using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueHost.Services;

namespace VenueHost.Windows;

/// <summary>
/// High-contrast Overview layout and shared accessible chrome for all pages.
/// Use real ImGui text and labelled buttons (not draw-list-only control labels)
/// wherever possible so contrast mode does not regress keyboard navigation.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly Vector4 AccessBlack = new(0.012f, 0.012f, 0.014f, 1f);
    private static readonly Vector4 AccessWhite = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 AccessYellow = new(1f, 0.91f, 0.05f, 1f);
    private static readonly Vector4 AccessCyan = new(0.34f, 0.95f, 1f, 1f);
    private static readonly Vector4 AccessGreen = new(0.45f, 1f, 0.42f, 1f);
    private static readonly Vector4 AccessMagenta = new(1f, 0.56f, 0.88f, 1f);
    private static readonly Vector4 StatusInactive = new(1f, 0.40f, 0.47f, 1f);

    private void DrawHighContrastOverview(VenueOverviewSnapshot state, IReadOnlyList<VenueActivity> timeline, DateTime nowUtc)
    {
        // Accessibility labels are larger, but the full Overview must still fit
        // ABOVE the fixed automation dock. The old fixed-size cards were clipped
        // at common 900px game resolutions even though the content had room to
        // become more compact. Use the actual available page height instead.
        ImGui.SetWindowFontScale(1.26f);
        try
        {
            var space = ImGui.GetContentRegionAvail();
            const float gap = 10f;
            // Leave room for both section headings, table bookkeeping, the
            // normal child padding and a little visual breathing space.
            const float nonCardHeight = 120f;
            const float minimumCardsHeight = 391f;
            const float maximumCardsHeight = 511f;
            var cardsHeight = Math.Clamp(space.Y - nonCardHeight,
                minimumCardsHeight, maximumCardsHeight);
            var room = (cardsHeight - minimumCardsHeight) /
                (maximumCardsHeight - minimumCardsHeight);
            var eventHeight = 112f + 31f * room;
            var liveHeight = 128f + 48f * room;
            var supportHeight = 151f + 41f * room;

            DrawAccessEvent(state, nowUtc, eventHeight);
            ImGui.Dummy(new Vector2(1f, gap));
            AccessSection("ON AIR", "CURRENT AND NEXT DJ");
            ImGui.Dummy(new Vector2(1f, 4f));

            if (ImGui.GetContentRegionAvail().X >= 675f)
            {
                if (ImGui.BeginTable("##accessLive", 2,
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
                {
                    ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthStretch, 1.55f);
                    ImGui.TableSetupColumn("Next", ImGuiTableColumnFlags.WidthStretch, 1f);
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    this.DrawAccessCurrentDj(state, liveHeight);
                    ImGui.TableSetColumnIndex(1);
                    DrawAccessNextDj(state, liveHeight);
                    ImGui.EndTable();
                }
            }
            else
            {
                this.DrawAccessCurrentDj(state, liveHeight);
                ImGui.Dummy(new Vector2(1f, 8f));
                DrawAccessNextDj(state, liveHeight);
            }

            ImGui.Dummy(new Vector2(1f, gap));
            AccessSection("STAFF & SHOUTS", "STAFF AND SCHEDULED SHOUTS");
            ImGui.Dummy(new Vector2(1f, 4f));
            if (ImGui.GetContentRegionAvail().X >= 675f)
            {
                if (ImGui.BeginTable("##accessSupport", 2,
                    ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    this.DrawAccessStaff(state, supportHeight);
                    ImGui.TableSetColumnIndex(1);
                    DrawAccessShouts(timeline, supportHeight);
                    ImGui.EndTable();
                }
            }
            else
            {
                this.DrawAccessStaff(state, supportHeight);
                ImGui.Dummy(new Vector2(1f, 8f));
                DrawAccessShouts(timeline, supportHeight);
            }
        }
        finally
        {
            ImGui.SetWindowFontScale(1.0f);
        }
    }

    private static void AccessSection(string title, string description)
    {
        var p = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        AccessLabel(p, title, AccessYellow, MathF.Max(90f, width * 0.56f));
        if (width > 790f)
            AccessLabel(p + new Vector2(width * 0.56f, 0f), description, AccessCyan,
                width * 0.44f - 4f, bold: false);
        ImGui.SetCursorScreenPos(p + new Vector2(0f, 23f));
        ImGui.Dummy(Vector2.Zero);
    }

    private static void AccessPanel(Vector2 p, Vector2 size, Vector4 stroke, string heading,
        float reserveRight = 23f)
    {
        var draw = ImGui.GetWindowDrawList();
        BackstageDesign.Fill(draw, p, size, AccessBlack, 9f);
        // 2.5px outlines and an opaque banner remain identifiable even against
        // bright, detailed in-game backgrounds.
        draw.AddRect(p, p + size, BackstageDesign.Color(stroke), 9f, ImDrawFlags.None, 2.5f);
        var title = FitCompactText(heading, MathF.Max(48f, size.X - reserveRight - 20f));
        var labelWidth = MathF.Min(size.X - 20f, ImGui.CalcTextSize(title).X + 26f);
        BackstageDesign.Fill(draw, p + new Vector2(10f, 10f), new Vector2(labelWidth, 28f), stroke, 5f);
        AccessLabel(p + new Vector2(19f, 13f), title, AccessBlack, labelWidth - 12f);
    }

    // Keep ImGui text items in the item tree. An additional offset draw pass
    // thickens the current font without requiring or distributing font files.
    private static void AccessLabel(Vector2 p, string value, Vector4 ink, float maxWidth, bool bold = true)
    {
        if (maxWidth <= 0f) return;
        var text = FitCompactText(value ?? string.Empty, maxWidth);
        ImGui.SetCursorScreenPos(p);
        ImGui.TextColored(ink, text);
        if (bold)
            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(),
                p + new Vector2(0.8f, 0f), BackstageDesign.Color(ink), text);
    }

    private static void AccessPrimaryText(Vector2 p, string text, float maxWidth, float scale = 1.40f)
    {
        ImGui.SetWindowFontScale(scale);
        try { AccessLabel(p, text, AccessWhite, maxWidth); }
        finally { ImGui.SetWindowFontScale(1.26f); }
    }

    private static bool AccessButton(string id, string text, Vector2 p, Vector2 size, Vector4 fill)
    {
        ImGui.SetCursorScreenPos(p);
        ImGui.PushStyleColor(ImGuiCol.Button, fill);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccessWhite);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccessCyan);
        ImGui.PushStyleColor(ImGuiCol.Text, AccessBlack);
        ImGui.PushStyleColor(ImGuiCol.Border, AccessWhite);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        // The readable action is part of the native ImGui button label.
        var clicked = ImGui.Button($"{text}##{id}", size);
        if (ImGui.IsItemFocused())
            ImGui.GetWindowDrawList().AddRect(p - new Vector2(2f), p + size + new Vector2(2f),
                BackstageDesign.Color(AccessMagenta), 7f, ImDrawFlags.None, 3f);
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(5);
        return clicked;
    }

    private void DrawAccessEvent(VenueOverviewSnapshot state, DateTime nowUtc, float height)
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var venueLabel = "TONIGHT AT " + (state.Venue == "Venue not configured" ? "THE VENUE" : state.Venue).ToUpperInvariant();
        AccessPanel(pos, new Vector2(width, height), AccessYellow, venueLabel, reserveRight: 204f);
        var title = state.EventTitle == "Untitled event" ? state.Venue : state.EventTitle;
        AccessPrimaryText(pos + new Vector2(18f, 47f), title, MathF.Max(55f, width - 210f), 1.40f);
        AccessLabel(pos + new Vector2(19f, height - 34f), state.EventWindow, AccessCyan,
            MathF.Max(50f, width - 211f));
        AccessLabel(pos + new Vector2(width - 185f, 15f), nowUtc.ToString("HH:mm:ss") + " ST",
            AccessWhite, 173f);
        if (AccessButton("AccessEditEvent", "EDIT EVENT", pos + new Vector2(width - 167f, height - 46f),
                new Vector2(147f, 35f), AccessYellow))
            this.RequestEventEditor();
        ReserveOverviewArea(pos, width, height);
    }

    private void DrawAccessCurrentDj(VenueOverviewSnapshot state, float height)
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        AccessPanel(pos, new Vector2(width, height), AccessMagenta, "ON STAGE");
        AccessPrimaryText(pos + new Vector2(18f, 49f), state.CurrentDj,
            MathF.Max(65f, width - 34f), 1.55f);
        AccessLabel(pos + new Vector2(19f, height - 33f), state.CurrentDjTime,
            AccessCyan, MathF.Max(60f, width - 167f));
        if (width >= 325f && AccessButton("AccessManageLineup", "MANAGE LINEUP",
                pos + new Vector2(width - 160f, height - 46f), new Vector2(145f, 35f), AccessYellow))
            this.activePage = BackstagePage.Djs;
        ReserveOverviewArea(pos, width, height);
    }

    private static void DrawAccessNextDj(VenueOverviewSnapshot state, float height)
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        AccessPanel(pos, new Vector2(width, height), AccessCyan, "NEXT DJ");
        AccessPrimaryText(pos + new Vector2(18f, 49f), state.NextDj,
            MathF.Max(65f, width - 33f), 1.48f);
        AccessLabel(pos + new Vector2(19f, height - 33f), state.NextDjTime,
            AccessWhite, MathF.Max(65f, width - 34f));
        ReserveOverviewArea(pos, width, height);
    }

    private void DrawAccessStaff(VenueOverviewSnapshot state, float height)
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        AccessPanel(pos, new Vector2(width, height), AccessGreen, "STAFF ON DUTY", reserveRight: 135f);
        var count = $"{state.ActiveStaff.Count} ACTIVE";
        AccessLabel(pos + new Vector2(MathF.Max(18f, width - 125f), 15f), count,
            AccessWhite, 111f);
        var rows = state.ActiveStaffRoleCounts;
        const float firstY = 49f;
        const float step = 27f;
        var maxRows = Math.Min(3, (int)MathF.Max(0f, MathF.Floor((height - 102f) / step) + 1f));
        if (state.ActiveStaff.Count == 0)
            AccessLabel(pos + new Vector2(18f, firstY), "No staff currently on shift",
                AccessWhite, width - 35f);
        else
            for (var i = 0; i < Math.Min(maxRows, rows.Count); i++)
            {
                var role = rows[i];
                AccessLabel(pos + new Vector2(19f, firstY + step * i),
                    $"{role.Value}  {role.Key}", AccessWhite, width - 38f);
            }
        if (rows.Count > maxRows)
            AccessLabel(pos + new Vector2(19f, height - 34f), $"+{rows.Count - maxRows} more roles",
                AccessCyan, MathF.Max(55f, width - 170f));
        if (width >= 275f && AccessButton("AccessManageStaff", "MANAGE STAFF",
                pos + new Vector2(width - 152f, height - 45f), new Vector2(137f, 35f), AccessGreen))
            this.activePage = BackstagePage.Staff;
        ReserveOverviewArea(pos, width, height);
    }

    private static void DrawAccessShouts(IReadOnlyList<VenueActivity> timeline, float height)
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        AccessPanel(pos, new Vector2(width, height), AccessYellow, "UPCOMING SHOUTS");
        if (timeline.Count == 0)
            AccessLabel(pos + new Vector2(18f, 57f), "No automatic shouts scheduled",
                AccessWhite, width - 35f);
        else
        {
            var rows = Math.Min(4, Math.Max(1, (int)((height - 49f) / 31f)));
            for (var i = 0; i < Math.Min(rows, timeline.Count); i++)
            {
                var activity = timeline[i];
                var y = 48f + 31f * i;
                var typeColor = activity.Kind switch
                {
                    VenueActivityKind.DjShout => AccessMagenta,
                    VenueActivityKind.StaffShout => AccessGreen,
                    _ => AccessCyan,
                };
                AccessLabel(pos + new Vector2(19f, y), activity.KindLabel,
                    typeColor, 72f);
                AccessLabel(pos + new Vector2(93f, y), activity.Title,
                    AccessWhite, MathF.Max(50f, width - 194f));
                AccessLabel(pos + new Vector2(width - 95f, y), activity.TimeUtc.ToString("HH:mm:ss"),
                    AccessCyan, 82f);
            }
        }
        ReserveOverviewArea(pos, width, height);
    }

    private void DrawAccessShellHeader()
    {
        ImGui.SetWindowFontScale(1.16f);
        var p = ImGui.GetWindowPos();
        var w = ImGui.GetWindowSize().X;
        var draw = ImGui.GetWindowDrawList();
        BackstageDesign.Fill(draw, p, new Vector2(w, 68f), AccessBlack, 11f);
        BackstageDesign.Rule(draw, p + new Vector2(0f, 67f), p + new Vector2(w, 67f), AccessYellow);
        var mascot = this.Ui.MascotTexture?.GetWrapOrDefault();
        if (mascot is not null)
            draw.AddImage(mascot.Handle, p + new Vector2(19f, 17f), p + new Vector2(53f, 51f));
        AccessLabel(p + new Vector2(65f, 24f), "VENUEHOST", AccessYellow, 145f);
        var status = this.GetAutomationHeaderStatus();
        AccessLabel(p + new Vector2(w - 372f, 25f), status,
            status == "PAUSED" ? AccessMagenta : status == "LIVE" ? AccessGreen : AccessYellow, 102f);
        if (AccessButton("AccessSetup", "SETUP", p + new Vector2(w - 264f, 17f),
                new Vector2(90f, 35f), AccessYellow))
            this.Ui.ToggleConfigUi();
        if (AccessButton("AccessCompact", "COMPACT", p + new Vector2(w - 166f, 17f),
                new Vector2(91f, 35f), AccessCyan))
            this.ToggleCompactMode();
        if (AccessButton("AccessClose", "CLOSE", p + new Vector2(w - 68f, 17f),
                new Vector2(61f, 35f), AccessMagenta))
            this.IsOpen = false;
        ImGui.SetCursorScreenPos(p + new Vector2(200f, 0f));
        ImGui.InvisibleButton("##AccessDrag", new Vector2(MathF.Max(1f, w - 600f), 67f));
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            this.ToggleCompactMode();
        if (ImGui.IsItemActive())
            this.shellDragDelta = ImGui.GetIO().MouseDelta;
        ImGui.SetWindowFontScale(1.0f);
    }

    private void DrawAccessShellRail(float width)
    {
        ImGui.SetWindowFontScale(1.18f);
        var p = ImGui.GetWindowPos();
        var height = ImGui.GetWindowSize().Y;
        var draw = ImGui.GetWindowDrawList();
        BackstageDesign.Fill(draw, p, new Vector2(width, height), AccessBlack);
        BackstageDesign.Rule(draw, p + new Vector2(width - 1f, 0f),
            p + new Vector2(width - 1f, height), AccessCyan);
        var items = new[]
        {
            (BackstagePage.Overview, "OVERVIEW"),
            (BackstagePage.Djs, "DJ LINEUP"),
            (BackstagePage.Staff, "STAFF"),
            (BackstagePage.Shouts, "SHOUTS"),
        };
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var isActive = this.activePage == item.Item1;
            var at = p + new Vector2(8f, 61f + i * 55f);
            if (AccessButton("AccessNav" + item.Item1, item.Item2, at,
                    new Vector2(width - 16f, 43f), isActive ? AccessYellow : AccessCyan))
            {
                if (this.activePage == BackstagePage.Djs && item.Item1 != BackstagePage.Djs)
                    this.CommitPendingDjTimeDrafts();
                this.activePage = item.Item1;
            }
            if (ImGui.IsItemHovered() && item.Item1 == BackstagePage.Staff)
                ImGui.SetTooltip("Staff Schedule");
            if (isActive)
                BackstageDesign.Fill(draw, at + new Vector2(0f, 43f),
                    new Vector2(width - 16f, 3f), AccessMagenta, 0f);
        }
        ImGui.SetWindowFontScale(1.0f);
    }

    private void DrawAccessShellDock()
    {
        ImGui.SetWindowFontScale(1.18f);
        var p = ImGui.GetCursorScreenPos();
        var width = ImGui.GetWindowSize().X;
        var draw = ImGui.GetWindowDrawList();
        var automation = this.Services.Get<AutomationControlService>();
        BackstageDesign.Rule(draw, p, p + new Vector2(width, 0f), AccessYellow);
        var rect = p + new Vector2(12f, 6f);
        var rw = width - 24f;
        BackstageDesign.Fill(draw, rect, new Vector2(rw, 54f), AccessBlack, 8f);
        draw.AddRect(rect, rect + new Vector2(rw, 54f), BackstageDesign.Color(AccessCyan),
            8f, ImDrawFlags.None, 2.5f);
        AccessLabel(rect + new Vector2(13f, 4f), "AUTOMATION CONTROL",
            AccessYellow, MathF.Max(130f, rw - 500f));
        DrawAccessDockToggle(rect, "DJ", automation.DjRunning, automation.DjAvailable, 13f, automation.ToggleDj);
        DrawAccessDockToggle(rect, "STAFF", automation.StaffRunning, automation.StaffAvailable, 153f, automation.ToggleStaff);
        DrawAccessDockToggle(rect, "EVENT", automation.EventRunning, automation.EventAvailable, 312f, automation.ToggleEvent);
        var buttonSize = new Vector2(133f, 35f);
        var at = rect + new Vector2(rw - 144f, 10f);
        AccessButton("AccessAllAutomation", automation.AnyRunning ? "PAUSE ALL" : "START ALL", at,
            buttonSize, AccessYellow);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Double-click to change global automation state");
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                automation.TogglePauseAll();
        }
        ImGui.SetWindowFontScale(1.0f);
    }

    private static void DrawAccessDockToggle(Vector2 root, string name, bool running, bool available, float x, Action toggle)
    {
        var p = root + new Vector2(x, 27f);
        ImGui.SetCursorScreenPos(p);
        ImGui.InvisibleButton("##AccessDock" + name, new Vector2(name == "DJ" ? 132f : 151f, 25f));
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        if (hovered)
            BackstageDesign.Fill(draw, p, new Vector2(name == "DJ" ? 132f : 151f, 25f),
                BackstageDesign.Raised, 5f);
        var statusColor = running ? AccessGreen : StatusInactive;
        draw.AddCircleFilled(p + new Vector2(7f, 13f), 3f, BackstageDesign.Color(statusColor));
        var titlePos = p + new Vector2(18f, 5f);
        draw.AddText(titlePos, BackstageDesign.Color(AccessWhite), name);
        draw.AddText(titlePos + new Vector2(ImGui.CalcTextSize(name).X + 7f, 0f),
            BackstageDesign.Color(statusColor), running ? "ACTIVE" : "INACTIVE");
        if (hovered)
        {
            ImGui.SetTooltip(AutoToggleTooltip(name, available));
            if (available && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                toggle();
        }
    }

}
