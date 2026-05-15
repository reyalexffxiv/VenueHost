using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using VenueHost.Models;

namespace VenueHost.Windows;

/// <summary>
/// Main control panel for venue staff during an event night.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("Venue Host###VenueHostMain")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(920, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            ShowTooltip = () => ImGui.SetTooltip("Setup / Macros"),
            Click = _ => this.plugin.ToggleConfigUi(),
            Priority = 10,
        });
    }

    private readonly Dictionary<int, string> djPickerSearchTextByOrder = new();
    private readonly Dictionary<int, string> djPickerQuickAddNameByOrder = new();
    private readonly Dictionary<int, string> djPickerQuickAddLinkByOrder = new();

    public void Dispose()
    {
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("VenueHostTabs"))
            return;

        if (ImGui.BeginTabItem("DJ Lineup"))
        {
            this.DrawDjLineupTab();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawDjLineupTab()
    {
        var config = this.plugin.Configuration;
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

        var tableHeight = Math.Min(330f, Math.Max(230f, ImGui.GetTextLineHeightWithSpacing() * 12f));
        if (ImGui.BeginChild("DjScheduleScrollRegion", new Vector2(0, tableHeight), false, ImGuiWindowFlags.None))
            this.DrawDjScheduleTable(config);
        ImGui.EndChild();

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

        ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 105f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Venue:");
        ImGui.TableNextColumn();
        this.InputAndSave("##VenueName", config.VenueName, v => config.VenueName = v, 160);

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

        this.InputAndSave("##GiveawayText", config.GiveawayText, v => config.GiveawayText = v, 220);

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

        this.InputAndSave("##GiveawayCommand", config.GiveawayCommand, v => config.GiveawayCommand = v, 80);

        if (!config.GiveawayEnabled || !config.GiveawayCommandEnabled)
            ImGui.EndDisabled();

        ImGui.EndTable();
    }

    private void DrawDjScheduleTable(Configuration config)
    {
        var warningRows = GetScheduleWarningRows(config);
        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("VenueHostDjSchedule", 6, tableFlags))
            return;

        ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, 58f);
        ImGui.TableSetupColumn("DJ", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Link", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("Start", ImGuiTableColumnFlags.WidthFixed, 125f);
        ImGui.TableSetupColumn("End", ImGuiTableColumnFlags.WidthFixed, 125f);
        ImGui.TableSetupColumn("Giveaway", ImGuiTableColumnFlags.WidthFixed, 72f);
        ImGui.TableHeadersRow();

        for (var i = 0; i < config.DjSchedule.Count; i++)
        {
            var entry = config.DjSchedule[i];
            ImGui.PushID($"DjScheduleRow{i}");
            ImGui.TableNextRow();
            var hasWarning = warningRows.Contains(entry.Order);
            var selected = config.SelectedDjOrder == entry.Order;
            if (selected)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ToImGuiColor(new Vector4(0.16f, 0.32f, 0.52f, 0.22f)));

            ImGui.TableNextColumn();
            if (hasWarning)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, ToImGuiColor(new Vector4(0.70f, 0.42f, 0.12f, 0.26f)));

            if (ImGui.RadioButton($"{entry.Order}##SelectedDj", selected))
            {
                config.SelectedDjOrder = entry.Order;
                config.Save();
            }

            ImGui.TableNextColumn();
            this.DjPickerAndSave(entry);

            ImGui.TableNextColumn();
            this.DrawLinkEditor(entry, i);

            ImGui.TableNextColumn();
            this.TimePickerAndSave("Start", entry.StartTime, value => entry.StartTime = value);

            ImGui.TableNextColumn();
            this.TimePickerAndSave("End", entry.EndTime, value => entry.EndTime = value);

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
    /// Draws the editable stream link cell with a compact browser button.
    ///
    /// The input remains a normal editable field, so hosts can still paste or tweak links
    /// directly. The adjacent button opens valid http/https links for quick stream checks.
    /// </summary>
    private void DrawLinkEditor(DjScheduleEntry entry, int index)
    {
        var openButtonWidth = 52f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var inputWidth = Math.Max(80f, ImGui.GetContentRegionAvail().X - openButtonWidth - spacing);

        ImGui.SetNextItemWidth(inputWidth);
        this.InputEntryAndSave($"##DJLink{index}", entry.DJLink, value => entry.DJLink = value);

        ImGui.SameLine();

        var canOpen = IsHttpLink(entry.DJLink);
        if (!canOpen)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"Open##DJLinkOpen{index}", new Vector2(openButtonWidth, 0f)))
        {
            OpenExternalLink(entry.DJLink);
        }

        if (!canOpen)
        {
            ImGui.EndDisabled();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(canOpen ? "Open stream link in browser." : "Enter a full http:// or https:// link first.");
        }
    }

    private static bool IsHttpLink(string? link)
    {
        return Uri.TryCreate(link, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static void OpenExternalLink(string link)
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
            this.plugin.RefreshAutoShoutSchedule();
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
            this.plugin.RefreshAutoShoutSchedule();
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
            this.plugin.ToggleDjDatabaseUi();

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
        if (!this.showClearLineupConfirmation)
            return;

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.20f, 0.12f, 0.04f, 0.78f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.70f, 0.42f, 0.15f, 1f));
        if (ImGui.BeginChild("ClearLineupInlineConfirm", new Vector2(0, 86), true, ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.TextUnformatted("Clear the full DJ lineup?");
            ImGui.TextDisabled("This removes every DJ row. Use Add DJ to start a new lineup.");
            ImGui.Spacing();

            if (this.ColoredButton("Yes, clear lineup", new Vector2(150, 28), ButtonTone.Danger))
            {
                config.DjSchedule.Clear();
                config.SelectedDjOrder = 0;
                config.AutoCurrentDjShoutEnabled = false;
                config.Save();
                this.plugin.RefreshAutoShoutSchedule();
                this.showClearLineupConfirmation = false;
            }

            ImGui.SameLine();

            if (this.ColoredButton("Cancel", new Vector2(90, 28), ButtonTone.Neutral))
                this.showClearLineupConfirmation = false;
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
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
        this.plugin.RefreshAutoShoutSchedule();
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
        this.plugin.RefreshAutoShoutSchedule();
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
            this.plugin.SendCurrentDjShout();

        if (!hasSelectedDj)
            ImGui.EndDisabled();

        ImGui.SameLine();

        if (!hasSelectedDj || !hasNextDj)
            ImGui.BeginDisabled();

        if (this.ColoredButton("Thank Current / Welcome Next", new Vector2(270, 32), ButtonTone.Primary))
            this.plugin.SendTransitionShout();

        if (!hasSelectedDj || !hasNextDj)
            ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(hasNextDj ? "Send a transition shout from the selected DJ to the next DJ." : "Disabled because there is no next DJ after the selected row.");

        ImGui.SameLine();

        if (!hasSelectedDj)
            ImGui.BeginDisabled();

        if (this.ColoredButton("Tell DJ to Target", new Vector2(230, 32), ButtonTone.Positive))
            this.plugin.TellCurrentDjToTarget();

        if (!hasSelectedDj)
            ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(hasSelectedDj ? "Sends the selected DJ info to your current target using /tell <t>." : "Disabled because the selected row has no DJ yet.");
    }

    private void DrawStatusPanel(Configuration config)
    {
        var serverNow = DateTime.UtcNow;
        var scheduledCurrent = config.GetCurrentServerTimeDj(serverNow);
        var nextAutoAt = this.plugin.NextAutoShoutAtUtc;
        var nextAutoText = nextAutoAt.HasValue ? FormatStatusTime(nextAutoAt.Value, includeSeconds: true) : "none";
        var nextAutoDj = nextAutoAt.HasValue ? config.GetCurrentServerTimeDj(nextAutoAt.Value) : null;
        var autoDjText = scheduledCurrent is null ? "none active now" : DisplayDjWithRow(scheduledCurrent);
        var currentSlot = scheduledCurrent is null ? null : config.GetTimelineSlotForEntry(scheduledCurrent);
        var currentSlotText = currentSlot is null ? "none" : FormatStatusRange(currentSlot.StartUtc, currentSlot.EndUtc);
        var eventWindowText = config.TryGetEventWindow(out var eventStartUtc, out var eventEndUtc)
            ? $"{eventStartUtc:yyyy-MM-dd HH:mm} → {eventEndUtc:yyyy-MM-dd HH:mm} ST"
            : "invalid";
        var nextTransitionText = GetNextTransitionText(config, scheduledCurrent);

        ImGui.TextUnformatted("Auto schedule");
        if (!ImGui.BeginTable("VenueHostAutoScheduleLayout", 2, ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 470f);
        ImGui.TableSetupColumn("Mascot", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        if (ImGui.BeginTable("VenueHostStatusPanel", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 135f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);

            this.DrawStatusRow("Auto Shout", config.AutoCurrentDjShoutEnabled ? "ON" : "OFF");
            this.DrawStatusRow("Server Time", $"{serverNow:yyyy-MM-dd HH:mm:ss} ST / UTC");
            this.DrawStatusRow("Event Window", eventWindowText);
            this.DrawStatusRow("Auto DJ", autoDjText);
            this.DrawStatusRow("Current Slot", currentSlotText);
            if (scheduledCurrent is null && nextAutoDj is not null)
                this.DrawStatusRow("Next DJ", $"{DisplayDjWithRow(nextAutoDj)} at {nextAutoText}");
            this.DrawStatusRow("Next Transition", nextTransitionText);
            this.DrawStatusRow("Next Shout", nextAutoText);
            this.DrawStatusRow("Next Shout Type", this.plugin.GetNextAutoShoutTypeDescription());

            ImGui.EndTable();
        }

        ImGui.TableNextColumn();
        this.DrawMascotDecoration();

        ImGui.EndTable();
    }


    private void DrawMascotDecoration()
    {
        var texture = this.plugin.MascotTexture;
        if (texture is null)
            return;

        var wrap = texture.GetWrapOrDefault();
        if (wrap is null)
            return;

        var available = ImGui.GetContentRegionAvail();
        if (available.X < 170f || available.Y < 150f)
            return;

        // Decorative mascot, intentionally small so it does not steal focus from the live schedule.
        // It is placed in the empty status-panel space, lower and closer to center than the first pass.
        const float mascotSize = 90f;
        var size = new Vector2(mascotSize, mascotSize);
        var start = ImGui.GetCursorScreenPos();
        var x = start.X + Math.Max(0f, (available.X - size.X) * 0.42f);
        var y = start.Y + 72f;

        ImGui.SetCursorScreenPos(new Vector2(x, y));
        ImGui.Image(wrap.Handle, size);
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
        ImGui.PushStyleColor(ImGuiCol.Button, normal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    private static (Vector4 Normal, Vector4 Hovered, Vector4 Active) GetButtonColors(ButtonTone tone)
    {
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

        this.plugin.Configuration.NormalizeDjDatabase();

        var matches = this.plugin.Configuration.DjDatabase
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
                        var hadUsableDjBefore = this.plugin.Configuration.DjSchedule.Any(Configuration.HasUsableDj);
                        entry.DJName = dbEntry.Name;
                        entry.DJLink = dbEntry.Link;
                        this.EnableAutoShoutWhenFirstDjIsAdded(hadUsableDjBefore);
                        this.plugin.Configuration.Save();
                        this.plugin.RefreshAutoShoutSchedule();
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
            var existingEntry = this.plugin.Configuration.DjDatabase.FirstOrDefault(dbEntry =>
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
                nameToSave = this.plugin.Configuration.GetUniqueDjDatabaseName(requestedName);
                linkToSave = requestedLink;

                this.plugin.Configuration.DjDatabase.Add(new DjDatabaseEntry
                {
                    Name = nameToSave,
                    Link = linkToSave,
                });
            }

            this.plugin.Configuration.NormalizeDjDatabase();

            var hadUsableDjBefore = this.plugin.Configuration.DjSchedule.Any(Configuration.HasUsableDj);
            entry.DJName = nameToSave;
            entry.DJLink = linkToSave;
            this.EnableAutoShoutWhenFirstDjIsAdded(hadUsableDjBefore);
            this.plugin.Configuration.Save();
            this.plugin.RefreshAutoShoutSchedule();

            this.djPickerQuickAddNameByOrder[entry.Order] = string.Empty;
            this.djPickerQuickAddLinkByOrder[entry.Order] = string.Empty;

            ImGui.CloseCurrentPopup();
        }

        if (!canQuickAdd)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Open DJ Database", new Vector2(140, 24)))
            this.plugin.ToggleDjDatabaseUi();

        ImGui.SameLine();
        if (ImGui.Button("Close", new Vector2(80, 24)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }


    private void EnableAutoShoutWhenFirstDjIsAdded(bool hadUsableDjBefore)
    {
        if (hadUsableDjBefore)
            return;

        if (this.plugin.Configuration.DjSchedule.Any(Configuration.HasUsableDj))
            this.plugin.Configuration.AutoCurrentDjShoutEnabled = true;
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
            this.plugin.Configuration.Save();
            this.plugin.RefreshAutoShoutSchedule();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use yyyy-MM-dd, for example 2026-05-16.");
    }

    private void TimePickerAndSave(string label, string value, Action<string> setter)
    {
        var displayValue = NormalizeTimeText(value);
        var popupId = $"{label}TimePickerPopup";

        if (!string.Equals(displayValue, value ?? string.Empty, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(value))
        {
            setter(displayValue);
            this.plugin.Configuration.Save();
        }

        ImGui.SetNextItemWidth(110f);
        if (ImGui.Button($"{displayValue} ▼##{label}TimePickerButton", new Vector2(110f, 0)))
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
                this.plugin.Configuration.Save();
                this.plugin.RefreshAutoShoutSchedule();
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
        this.plugin.Configuration.Save();
        this.plugin.RefreshAutoShoutSchedule();
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

    private void InputAndSave(string label, string value, Action<string> setter, int maxLength)
    {
        var editedValue = value ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText(label, ref editedValue, maxLength))
        {
            setter(editedValue);
            this.plugin.Configuration.Save();
        }
    }

    private void InputEntryAndSave(string label, string value, Action<string> setter, int maxLength = 240)
    {
        var editedValue = value ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText(label, ref editedValue, maxLength))
        {
            setter(editedValue);
            this.plugin.Configuration.Save();
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
