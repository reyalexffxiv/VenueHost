using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueHost.Models;
using VenueHost.Services;

namespace VenueHost.Windows;

/// <summary>
/// DJ lineup workspace: build and edit the schedule, without duplicating the
/// live overview or the outgoing shout controls in this page.
/// Scheduling and chat dispatch are still owned by their existing services.
/// </summary>
public sealed partial class MainWindow
{
    private bool showRemoveDjConfirmation;
    // Set by selecting an empty row or adding a slot. The existing DJ picker
    // opens from the editor in the SAME ImGui popup scope on this frame.
    private int djPickerRequestedSlotOrder;

    // Inline HH:mm drafts are kept separate from persisted schedule values.
    // Invalid or half-typed inputs never reach the scheduler or configuration.
    private DjScheduleEntry? djInlineTimeEntry;
    private int djInlineTimeSlotOrder = -1;
    private string djInlineStartDraft = string.Empty;
    private string djInlineEndDraft = string.Empty;
    private string djInlineStartSource = string.Empty;
    private string djInlineEndSource = string.Empty;
    private bool djInlineStartInvalid;
    private bool djInlineEndInvalid;

    private void DrawDjLineupRedesigned(Configuration config)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var remainingHeight = ImGui.GetContentRegionAvail().Y;
        // Keep short lineups compact while retaining room for the selected-slot
        // editor. Beyond ten slots, the list scrolls rather than growing the UI.
        var desiredHeight = MathF.Max(430f,
            MathF.Min(508f, 92f + MathF.Min(config.DjSchedule.Count, 10) * 42f));
        var editorHeight = MathF.Min(desiredHeight, MathF.Max(380f, remainingHeight - 91f));
        const float gap = 12f;
        var scheduleWidth = MathF.Floor((width - gap) * 0.62f);
        var detailsWidth = MathF.Max(300f, width - scheduleWidth - gap);

        var warnings = config.GetScheduleWarnings();
        var warningRows = GetScheduleWarningRows(config);
        AddUnassignedWindowWarnings(config, warnings, warningRows);

        this.DrawDjLineupList(config, new Vector2(scheduleWidth, editorHeight), warningRows, warnings);
        ImGui.SameLine(0f, gap);
        // The right-hand editor has its own content height; only the lineup
        // list needs the full scrolling height.
        this.DrawDjSlotEditor(config, new Vector2(detailsWidth, 355f));

        // Schedule issues are indicated only by icons/tooltips on affected
        // DJ rows; avoid a second, redundant status line below the board.

    }

    // The production timeline only contains DJs that have been assigned.
    // The lineup editor must also warn about EMPTY slots outside event hours,
    // without changing which entries the live scheduler considers runnable.
    private static void AddUnassignedWindowWarnings(Configuration config,
        System.Collections.Generic.List<string> warnings,
        System.Collections.Generic.HashSet<int> warningRows)
    {
        if (!config.TryGetEventWindow(out var eventStart, out var eventEnd))
            return;

        foreach (var entry in config.DjSchedule.Where(e => !Configuration.HasUsableDj(e)))
        {
            if (!Configuration.TryParseServerTime(entry.StartTime, out var start) ||
                !Configuration.TryParseServerTime(entry.EndTime, out var end) || start == end)
                continue;

            var slotStart = eventStart.Date.Add(start);
            while (slotStart < eventStart)
                slotStart = slotStart.AddDays(1);
            var slotEnd = slotStart.Date.Add(end);
            if (slotEnd <= slotStart)
                slotEnd = slotEnd.AddDays(1);
            if (slotEnd <= eventEnd)
                continue;

            warningRows.Add(entry.Order);
            warnings.Add($"DJ {entry.Order} (unassigned) is outside the event window: " +
                         $"{slotStart:ddd HH:mm}–{slotEnd:ddd HH:mm} ST. " +
                         "The slot will not trigger an automatic DJ shout.");
        }
    }

    private void DrawDjLineupList(Configuration config, Vector2 size,
        System.Collections.Generic.HashSet<int> warningRows,
        System.Collections.Generic.List<string> warnings)
    {
        var origin = ImGui.GetCursorScreenPos();
        BackstageDesign.SurfacePanel(origin, size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f));
        if (ImGui.BeginChild("##vhDjScheduleListPanel", size, false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            // This workspace already has a DJ Lineup page title.
            // Keep the schedule panel toolbar minimal: actions only, no extra
            // heading/counts/accent competing with the DJ slots beneath.
            // One compact toolbar for lineup-wide operations, ordered from
            // schedule creation to management. Leave a larger gap before the
            // destructive Clear action, which still requires confirmation.
            const float addWidth = 102f;
            const float dbWidth = 117f;
            const float autoWidth = 102f;
            const float clearWidth = 123f;
            const float buttonGap = 8f;
            const float clearGap = 16f;
            const float buttonHeight = 29f;
            var toolbarWidth = addWidth + dbWidth + autoWidth + clearWidth +
                               buttonGap * 2f + clearGap;
            var actionsAt = origin + new Vector2(size.X - 14f - toolbarWidth, 11f);
            ImGui.SetCursorScreenPos(actionsAt);
            if (this.WorkspaceButton("+ ADD SLOT", new Vector2(addWidth, buttonHeight), BackstageDesign.Mint))
            {
                this.CommitPendingDjTimeDrafts();
                this.AddDjSlot(config);
                this.djPickerRequestedSlotOrder = config.SelectedDjOrder;
            }
            ImGui.SameLine(0f, buttonGap);
            if (this.WorkspaceButton("DJ DATABASE", new Vector2(dbWidth, buttonHeight), BackstageDesign.Mist))
                this.Ui.ToggleDjDatabaseUi();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Open saved DJ names and stream links.");
            ImGui.SameLine(0f, buttonGap);
            if (this.WorkspaceButton("AUTO TIMES", new Vector2(autoWidth, buttonHeight), BackstageDesign.Muted))
                this.AutoFillTimesFromSelected(config);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Update times from the selected slot downward.");
            ImGui.SameLine(0f, clearGap);
            if (this.WorkspaceButton("CLEAR LINEUP", new Vector2(clearWidth, buttonHeight), BackstageDesign.Gold))
                this.showClearLineupConfirmation = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Clear all DJ slots after confirmation.");

            var draw = ImGui.GetWindowDrawList();
            BackstageDesign.Rule(draw, origin + new Vector2(14f, 48f),
                origin + new Vector2(size.X - 14f, 48f), BackstageDesign.SoftLine);
            ImGui.SetCursorScreenPos(origin + new Vector2(14f, 58f));

            if (config.DjSchedule.Count == 0)
            {
                ImGui.Dummy(new Vector2(1f, 24f));
                ImGui.TextColored(BackstageDesign.Ivory, "Your lineup is empty");
                ImGui.TextDisabled("Add a slot to assign the first DJ.");
            }
            else
            {
                var listHeight = MathF.Max(90f, ImGui.GetContentRegionAvail().Y - 7f);
                if (ImGui.BeginChild("##vhDjScheduleScroll", new Vector2(0f, listHeight), false))
                {
                    foreach (var entry in config.DjSchedule.OrderBy(e => e.Order).ToArray())
                    {
                        ImGui.PushID($"LineupSlot{entry.Order}");
                        this.DrawDjSlotInList(config, entry, warningRows.Contains(entry.Order), warnings);
                        ImGui.PopID();
                    }
                }
                ImGui.EndChild();
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private void DrawDjSlotInList(Configuration config, DjScheduleEntry entry, bool hasWarning,
        System.Collections.Generic.List<string> warnings)
    {
        var selected = config.SelectedDjOrder == entry.Order;
        var unassigned = !Configuration.HasUsableDj(entry);
        var name = unassigned ? "+ Assign a DJ" : entry.DJName.Trim();
        var rowWidth = MathF.Max(190f, ImGui.GetContentRegionAvail().X - 5f);
        var rowHeight = unassigned ? 36f : 39f;
        var visibleHeight = rowHeight - 3f;
        var at = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##SelectDjSlot", new Vector2(rowWidth, rowHeight)))
        {
            if (config.SelectedDjOrder != entry.Order)
                this.CommitPendingDjTimeDrafts();
            config.SelectedDjOrder = entry.Order;
            config.Save();
            if (unassigned)
                this.djPickerRequestedSlotOrder = entry.Order;
        }
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var rowSize = new Vector2(rowWidth, visibleHeight);

        // Filled rows are solid and visually prominent. Empty slots are
        // quieter, subtly dashed placeholders; they never look booked.
        var background = selected
            ? new Vector4(0.245f, 0.210f, 0.194f, 1f)
            : unassigned
                ? (hovered ? new Vector4(0.142f, 0.132f, 0.136f, 1f)
                           : new Vector4(0.091f, 0.096f, 0.111f, 1f))
                : (hovered ? new Vector4(0.205f, 0.194f, 0.202f, 1f)
                           : new Vector4(0.162f, 0.158f, 0.175f, 1f));
        BackstageDesign.Fill(draw, at, rowSize, background, 7f);
        if (selected)
        {
            BackstageDesign.Fill(draw, at + new Vector2(0f, 3f),
                new Vector2(3f, visibleHeight - 6f), BackstageDesign.Gold, 1f);
            BackstageDesign.Frame(draw, at, rowSize,
                BackstageDesign.Gold with { W = 0.28f }, 7f);
        }
        else if (unassigned)
        {
            DrawOpenSlotOutline(draw, at, rowSize,
                (hovered ? BackstageDesign.Gold : BackstageDesign.SoftLine) with { W = hovered ? 0.42f : 0.28f });
        }
        else
        {
            BackstageDesign.Frame(draw, at, rowSize,
                BackstageDesign.SoftLine with { W = 0.28f }, 7f);
            // Small mint marker signals a filled slot without repeating the
            // Overview's LIVE/NEXT states.
            draw.AddCircleFilled(at + new Vector2(57f, visibleHeight / 2f), 2.4f,
                BackstageDesign.Color(BackstageDesign.Mint with { W = 0.72f }));
        }

        var textY = (visibleHeight - ImGui.GetTextLineHeight()) / 2f;
        draw.AddText(at + new Vector2(13f, textY),
            BackstageDesign.Color(selected ? BackstageDesign.Gold : BackstageDesign.Dim),
            entry.Order.ToString("00"));
        BackstageDesign.Rule(draw, at + new Vector2(45f, 9f),
            at + new Vector2(45f, visibleHeight - 9f),
            BackstageDesign.SoftLine with { W = 0.37f });

        var time = $"{NormalizeTimeText(entry.StartTime)}  –  {NormalizeTimeText(entry.EndTime)}";
        var timeWidth = ImGui.CalcTextSize(time).X;
        var rightTextX = rowWidth - timeWidth - (hasWarning ? 31f : 15f);
        var labelX = unassigned || selected ? 63f : 69f;
        var nameWidth = MathF.Max(55f, rightTextX - labelX - 18f);
        var labelColor = selected ? BackstageDesign.Ivory
            : unassigned ? new Vector4(0.70f, 0.61f, 0.51f, 1f)
                         : BackstageDesign.Ivory;
        draw.AddText(at + new Vector2(labelX, textY),
            BackstageDesign.Color(labelColor), TrimTextToWidth(name, nameWidth));

        // Assigned times are warm and readable, not boxed controls. Open
        // placeholders keep their times muted to emphasize the missing DJ.
        var timeColor = selected ? BackstageDesign.Ivory
            : unassigned ? BackstageDesign.Dim
                         : new Vector4(0.87f, 0.75f, 0.62f, 1f);
        draw.AddText(at + new Vector2(rightTextX, textY),
            BackstageDesign.Color(timeColor), time);
        if (hasWarning)
        {
            var center = at + new Vector2(rowWidth - 14f, visibleHeight * 0.5f);
            draw.AddCircleFilled(center, 7f,
                BackstageDesign.Color(new Vector4(0.42f, 0.30f, 0.15f, 1f)));
            draw.AddText(center + new Vector2(-2f, -8f),
                BackstageDesign.Color(BackstageDesign.Gold), "!");
        }
        if (hovered)
        {
            if (hasWarning)
            {
                var matching = FindSlotWarnings(warnings, entry.Order);
                ImGui.SetTooltip(matching.Count > 0
                    ? string.Join("\n", matching)
                    : "This slot has a schedule warning.");
            }
            else if (unassigned)
                ImGui.SetTooltip("Click to assign a DJ from the database.");
            else
                ImGui.SetTooltip("Select this DJ to edit the lineup slot.");
        }
    }

    // Light dashed borders communicate that empty rows are placeholders.
    // Avoid opaque ImGui button styling and avoid introducing new textures.
    private static void DrawOpenSlotOutline(ImDrawListPtr draw, Vector2 p, Vector2 size, Vector4 tint)
    {
        var color = BackstageDesign.Color(tint);
        const float dash = 5f;
        const float step = 12f;
        var right = p.X + size.X;
        var bottom = p.Y + size.Y;
        for (var x = p.X + 9f; x < right - 9f; x += step)
        {
            var xEnd = MathF.Min(x + dash, right - 9f);
            draw.AddLine(new Vector2(x, p.Y), new Vector2(xEnd, p.Y), color);
            draw.AddLine(new Vector2(x, bottom), new Vector2(xEnd, bottom), color);
        }
        for (var y = p.Y + 8f; y < bottom - 8f; y += step)
        {
            var yEnd = MathF.Min(y + dash, bottom - 8f);
            draw.AddLine(new Vector2(p.X, y), new Vector2(p.X, yEnd), color);
            draw.AddLine(new Vector2(right, y), new Vector2(right, yEnd), color);
        }
    }

    private static System.Collections.Generic.List<string> FindSlotWarnings(
        System.Collections.Generic.List<string> warnings, int order)
        => warnings.Where(w => w.Contains($"DJ {order} (", StringComparison.OrdinalIgnoreCase) ||
                               w.Contains($"DJ {order} ", StringComparison.OrdinalIgnoreCase)).ToList();

    // Right-hand DJ editor deliberately shares the running-order panel's
    // rhythm: one title, a focused performer card, a small set of settings,
    // and lightweight slot operations along the bottom. Layout is anchored
    // within this child so a short lineup never produces a pile of separators.
    private void DrawDjSlotEditor(Configuration config, Vector2 size)
    {
        var at = ImGui.GetCursorScreenPos();
        BackstageDesign.SurfacePanel(at, size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f, 12f));
        if (ImGui.BeginChild("##vhDjSlotEditorPanel", size, false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var draw = ImGui.GetWindowDrawList();
            var entry = config.GetSelectedDj();
            var unassigned = entry is null || !Configuration.HasUsableDj(entry);

            // One clear header. It identifies the slot being edited; there is
            // no repeated 'Slot 08 of 08' subsection beneath it.
            ImGui.SetCursorScreenPos(at + new Vector2(18f, 15f));
            ImGui.TextColored(BackstageDesign.Gold, entry is null
                ? "SELECT A SLOT" : $"EDIT SLOT {entry.Order:00}");
            // This header identifies assignment state only. Schedule warnings
            // remain on the corresponding lineup rows, without duplication.
            var status = entry is null ? "NO SELECTION" :
                unassigned ? "OPEN SLOT" : "ASSIGNED";
            var statusColor = unassigned || entry is null
                ? BackstageDesign.Muted : BackstageDesign.Mint;
            var statusWidth = ImGui.CalcTextSize(status).X;
            ImGui.SetCursorScreenPos(at + new Vector2(size.X - 18f - statusWidth, 15f));
            ImGui.TextColored(statusColor, status);
            BackstageDesign.Rule(draw, at + new Vector2(18f, 44f),
                at + new Vector2(size.X - 18f, 44f), BackstageDesign.SoftLine);

            if (entry is null)
            {
                // A real empty editor state, not a disconnected input form.
                ImGui.SetCursorScreenPos(at + new Vector2(18f, 74f));
                ImGui.TextColored(BackstageDesign.Ivory, "Choose a lineup slot");
                ImGui.SetCursorScreenPos(at + new Vector2(18f, 98f));
                ImGui.TextColored(BackstageDesign.Muted, "Select any row on the left to edit it.");
            }
            else
            {
                ImGui.PushID($"EditDjSlot{entry.Order}");

                // One shared assignment card (also used by Staff). The whole
                // surface opens the existing DJ picker; no separate huge button.
                var performerClicked = BackstageDesign.AssignmentSelector(
                    "##vhDjAssignment", "PERFORMER", unassigned ? string.Empty : entry.DJName,
                    at + new Vector2(18f, 57f), new Vector2(size.X - 36f, 73f));
                var autoOpen = this.djPickerRequestedSlotOrder == entry.Order;
                if (autoOpen)
                    this.djPickerRequestedSlotOrder = 0;
                this.DjPickerAndSave(entry, autoOpen, performerClicked);

                // DJ time editing is inline rather than opening the legacy
                // pop-up. Persist only complete, valid HH:mm values on Enter
                // or focus loss; 23:00 -> 00:00 is handled by the existing
                // overnight schedule logic, not by this UI control.
                this.PrepareDjInlineTimeDrafts(entry);
                ImGui.SetCursorScreenPos(at + new Vector2(18f, 147f));
                ImGui.TextColored(BackstageDesign.Dim, "TIME ST");
                if (this.djInlineStartInvalid || this.djInlineEndInvalid)
                {
                    const string warningText = "INVALID HH:mm";
                    var warningWidth = ImGui.CalcTextSize(warningText).X;
                    ImGui.SetCursorScreenPos(at + new Vector2(size.X - 18f - warningWidth, 147f));
                    ImGui.TextColored(BackstageDesign.Rose, warningText);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Use 24-hour HH:mm from 00:00 to 23:59. Invalid edits are not saved.");
                }
                var timeWidth = MathF.Max(100f, (size.X - 48f) * 0.5f);
                var secondTimeX = size.X - 18f - timeWidth;
                ImGui.SetCursorScreenPos(at + new Vector2(18f, 170f));
                ImGui.TextColored(BackstageDesign.Muted, "START");
                ImGui.SetCursorScreenPos(at + new Vector2(secondTimeX, 170f));
                ImGui.TextColored(BackstageDesign.Muted, "END");
                ImGui.SetCursorScreenPos(at + new Vector2(18f, 191f));
                this.DrawDjInlineTimeField("DjStartTime", ref this.djInlineStartDraft,
                    ref this.djInlineStartSource, ref this.djInlineStartInvalid,
                    entry.StartTime, value => entry.StartTime = value, timeWidth);
                ImGui.SetCursorScreenPos(at + new Vector2(secondTimeX, 191f));
                this.DrawDjInlineTimeField("DjEndTime", ref this.djInlineEndDraft,
                    ref this.djInlineEndSource, ref this.djInlineEndInvalid,
                    entry.EndTime, value => entry.EndTime = value, timeWidth);

                // Stream links belong to the DJ Database, not to the event
                // slot itself. Keep this information visible without inventing
                // a second place to edit it.
                ImGui.SetCursorScreenPos(at + new Vector2(18f, 240f));
                this.DrawLinkCell(entry);

                // Match the unframed Staff availability checkbox.
                ImGui.SetCursorScreenPos(at + new Vector2(18f, 272f));
                var enabled = entry.GiveawayEnabled;
                if (ImGui.Checkbox("Include event giveaway", ref enabled))
                {
                    entry.GiveawayEnabled = enabled;
                    config.Save();
                }

                // Deliberately secondary: reorder and remove the selected
                // slot without letting these controls dominate the editor.
                const float actionsButtonY = 316f;
                BackstageDesign.Rule(draw, at + new Vector2(18f, actionsButtonY - 10f),
                    at + new Vector2(size.X - 18f, actionsButtonY - 10f), BackstageDesign.SoftLine);
                ImGui.SetCursorScreenPos(at + new Vector2(18f, actionsButtonY));
                if (this.WorkspaceButton("UP", new Vector2(61f, 28f), BackstageDesign.Muted))
                    this.MoveSelected(config, -1);
                ImGui.SetCursorScreenPos(at + new Vector2(87f, actionsButtonY));
                if (this.WorkspaceButton("DOWN", new Vector2(69f, 28f), BackstageDesign.Muted))
                    this.MoveSelected(config, 1);
                ImGui.SetCursorScreenPos(at + new Vector2(size.X - 138f, actionsButtonY));
                if (this.WorkspaceButton("REMOVE SLOT", new Vector2(120f, 28f), BackstageDesign.Rose))
                    this.showRemoveDjConfirmation = true;
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    /// <summary>
    /// Initialize drafts when selection changes and pick up external schedule
    /// edits (e.g. Auto Times) without overwriting an in-progress text edit.
    /// </summary>
    private void PrepareDjInlineTimeDrafts(DjScheduleEntry entry)
    {
        if (!ReferenceEquals(this.djInlineTimeEntry, entry) ||
            this.djInlineTimeSlotOrder != entry.Order)
        {
            this.djInlineTimeEntry = entry;
            this.djInlineTimeSlotOrder = entry.Order;
            this.djInlineStartSource = entry.StartTime;
            this.djInlineEndSource = entry.EndTime;
            this.djInlineStartDraft = NormalizeTimeText(entry.StartTime);
            this.djInlineEndDraft = NormalizeTimeText(entry.EndTime);
            this.djInlineStartInvalid = false;
            this.djInlineEndInvalid = false;
            return;
        }

        if (!string.Equals(this.djInlineStartSource, entry.StartTime, StringComparison.Ordinal))
        {
            this.djInlineStartSource = entry.StartTime;
            this.djInlineStartDraft = NormalizeTimeText(entry.StartTime);
            this.djInlineStartInvalid = false;
        }
        if (!string.Equals(this.djInlineEndSource, entry.EndTime, StringComparison.Ordinal))
        {
            this.djInlineEndSource = entry.EndTime;
            this.djInlineEndDraft = NormalizeTimeText(entry.EndTime);
            this.djInlineEndInvalid = false;
        }
    }

    /// <summary>
    /// Editing text updates only the draft. Enter or losing focus commits the
    /// value if it is precisely HH:mm (00:00 through 23:59). Invalid drafts
    /// stay visible for correction while the persisted value remains intact.
    /// </summary>
    private void DrawDjInlineTimeField(string id, ref string draft, ref string source,
        ref bool invalid, string persistedValue, Action<string> setter, float width)
    {
        ImGui.SetNextItemWidth(width);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f, 6f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, BackstageDesign.Raised);
        ImGui.PushStyleColor(ImGuiCol.Text, invalid ? BackstageDesign.Rose : BackstageDesign.Ivory);
        var enterPressed = ImGui.InputText($"##{id}", ref draft, 6,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var leftField = ImGui.IsItemDeactivatedAfterEdit();
        var hovered = ImGui.IsItemHovered();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);

        if (enterPressed || leftField)
            this.CommitDjInlineTimeDraft(id, ref draft, ref source, ref invalid,
                persistedValue, setter);

        if (hovered)
            ImGui.SetTooltip(invalid
                ? "Invalid time. Enter HH:mm from 00:00 to 23:59; previous saved time is unchanged."
                : "Type HH:mm (24-hour ST). Press Enter or click away to save.");
    }

    private void CommitDjInlineTimeDraft(string id, ref string draft,
        ref string source, ref bool invalid, string persistedValue, Action<string> setter)
    {
        if (!TryParseDjInlineTime(draft, out var normalized))
        {
            invalid = true;
            return;
        }

        if (!string.Equals(persistedValue, normalized, StringComparison.Ordinal))
        {
            setter(normalized);
            this.Configuration.Save();
            this.RefreshScheduleForEditedField(id);
        }
        source = normalized;
        draft = normalized;
        invalid = false;
    }

    // A click on a different lineup row or workspace can replace the input
    // widget before ImGui reports its focus loss. Commit the old row's drafts
    // first, using exactly the same validation as Enter/focus-loss.
    private void CommitPendingDjTimeDrafts()
    {
        var entry = this.djInlineTimeEntry;
        if (entry is null)
            return;

        if (!string.Equals(this.djInlineStartDraft, NormalizeTimeText(entry.StartTime),
                StringComparison.Ordinal))
            this.CommitDjInlineTimeDraft("DjStartTime", ref this.djInlineStartDraft,
                ref this.djInlineStartSource, ref this.djInlineStartInvalid,
                entry.StartTime, value => entry.StartTime = value);

        if (!string.Equals(this.djInlineEndDraft, NormalizeTimeText(entry.EndTime),
                StringComparison.Ordinal))
            this.CommitDjInlineTimeDraft("DjEndTime", ref this.djInlineEndDraft,
                ref this.djInlineEndSource, ref this.djInlineEndInvalid,
                entry.EndTime, value => entry.EndTime = value);
    }

    private static bool TryParseDjInlineTime(string input, out string normalized)
    {
        normalized = string.Empty;
        if (input.Length != 5 || input[2] != ':' ||
            !char.IsAsciiDigit(input[0]) || !char.IsAsciiDigit(input[1]) ||
            !char.IsAsciiDigit(input[3]) || !char.IsAsciiDigit(input[4]))
            return false;

        var hour = (input[0] - '0') * 10 + input[1] - '0';
        var minute = (input[3] - '0') * 10 + input[4] - '0';
        if (hour > 23 || minute > 59)
            return false;

        normalized = FormatTime(hour, minute);
        return true;
    }

    /// <summary>
    /// Only rendered on the Shouts page. Previews use the live schedule,
    /// never the DJ row selected for editing.
    /// </summary>
    /// <summary>VenueHost outlined action button shared by DJ and Staff workspaces and shout controls.</summary>
    private bool WorkspaceButton(string label, Vector2 size, Vector4 accent)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, BackstageDesign.Surface);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, BackstageDesign.Raised);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.22f, 0.22f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, accent);
        ImGui.PushStyleColor(ImGuiCol.Border, BackstageDesign.SoftLine);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        var pressed = ImGui.Button(label, size);
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(5);
        return pressed;
    }

}
