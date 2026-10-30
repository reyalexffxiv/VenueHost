using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueHost.Services;

namespace VenueHost.Windows;

/// <summary>
/// Live event overview and launch point for the unified Edit Event dialog.
/// Scheduling projections never alter the live queues.
/// </summary>
public sealed partial class MainWindow
{
    private VenueOverviewSnapshot? overviewSnapshot;
    private DateTime overviewSnapshotAtUtc = DateTime.MinValue;
    private IReadOnlyList<VenueActivity> overviewTimeline = Array.Empty<VenueActivity>();
    private DateTime overviewTimelineAtUtc = DateTime.MinValue;

    private void DrawBackstageOverview()
    {
        var nowUtc = DateTime.UtcNow;
        if (this.overviewSnapshot is null || (nowUtc - this.overviewSnapshotAtUtc).TotalSeconds >= 1)
        {
            this.overviewSnapshot = VenueOverviewSnapshot.Build(this.Configuration, nowUtc);
            this.overviewSnapshotAtUtc = nowUtc;
        }

        var state = this.overviewSnapshot!;
        // Rebuild only once per second: the forecast can include many recurring
        // shouts and should not allocate a fresh timeline on every ImGui frame.
        if ((nowUtc - this.overviewTimelineAtUtc).TotalSeconds >= 1)
        {
            this.overviewTimeline = VenueActivityTimeline.Build(this.Configuration, this.DjAutoShout, this.StaffAutoShout, this.EventShouts, nowUtc);
            this.overviewTimelineAtUtc = nowUtc;
        }

        // Preview stays strictly on the Overview page. The normal Overview's
        // custom art and all other VenueHost windows are left untouched.
        if (this.Configuration.OverviewHighContrastPreviewEnabled)
        {
            this.DrawHighContrastOverview(state, this.overviewTimeline, nowUtc);
            return;
        }

        // Next DJ is independent of the chronological outgoing shout forecast.
        var timeline = this.overviewTimeline;
        var availableHeight = ImGui.GetContentRegionAvail().Y;
        var dense = availableHeight < 510f;
        var veryDense = availableHeight < 425f;
        var contextHeight = veryDense ? 74f : dense ? 85f : 101f;
        var liveHeight = veryDense ? 125f : dense ? 149f : 177f;
        // Match the two support cards' height. Give the staff list enough
        // room for rows plus its Manage Staff action, without imposing tall
        // empty panels when only one or two roles are present.
        var roleCount = state.ActiveStaffRoleCounts.Count;
        var supportingHeight = veryDense ? 118f : dense ? 148f :
            MathF.Max(152f, MathF.Min(196f, 100f + 24f * Math.Min(roleCount, 4)));
        var gap = veryDense ? 10f : 15f;

        this.DrawEventContext(state, nowUtc, contextHeight);
        ImGui.Dummy(new Vector2(1f, gap));
        DrawSectionHeading("ON AIR");
        ImGui.Dummy(new Vector2(1f, veryDense ? 3f : 6f));

        var space = ImGui.GetContentRegionAvail().X;
        if (space >= 625f)
        {
            if (ImGui.BeginTable("##vhLiveProduction", 2,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
            {
                ImGui.TableSetupColumn("Now", ImGuiTableColumnFlags.WidthStretch, 1.55f);
                ImGui.TableSetupColumn("Next", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                this.DrawOnAirCard(state, liveHeight);
                ImGui.TableSetColumnIndex(1);
                DrawNextDjCard(state, liveHeight);
                ImGui.EndTable();
            }
        }
        else
        {
            this.DrawOnAirCard(state, liveHeight);
            ImGui.Dummy(new Vector2(1f, 8f));
            DrawNextDjCard(state, liveHeight);
        }

        ImGui.Dummy(new Vector2(1f, gap));
        DrawSectionHeading("STAFF & SHOUTS");
        ImGui.Dummy(new Vector2(1f, veryDense ? 3f : 6f));
        if (ImGui.GetContentRegionAvail().X >= 625f)
        {
            if (ImGui.BeginTable("##vhProductionSupport", 2,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                DrawCrewCard(state, supportingHeight);
                ImGui.TableSetColumnIndex(1);
                DrawUpcomingCard(timeline, supportingHeight);
                ImGui.EndTable();
            }
        }
        else
        {
            DrawCrewCard(state, supportingHeight);
            ImGui.Dummy(new Vector2(1f, 8f));
            DrawUpcomingCard(timeline, supportingHeight);
        }
    }

    private void DrawEventContext(VenueOverviewSnapshot state, DateTime nowUtc, float height)
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        BackstageDesign.SurfacePanel(pos, new Vector2(width, height), BackstageDesign.Raised, 11f);
        BackstageDesign.Fill(draw, pos + new Vector2(0f, 11f),
            new Vector2(3f, height - 22f), BackstageDesign.Gold, 1.5f);

        var unnamed = state.EventTitle == "Untitled event";
        var title = unnamed ? state.Venue : state.EventTitle;
        var usableTitleWidth = MathF.Max(100f, width - 390f);
        title = FitCompactText(title, usableTitleWidth);
        // Use the configured venue name; keep the section label fully uppercase.
        // The original generic heading remains the fallback for an unnamed venue.
        var venueHeading = "TONIGHT AT " +
            (state.Venue == "Venue not configured" ? "THE VENUE" : state.Venue).ToUpperInvariant();
        BackstageDesign.LabelAt(pos + new Vector2(18f, 8f),
            FitCompactText(venueHeading, MathF.Max(100f, width - 310f)), BackstageDesign.Gold);
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * 1.25f,
            pos + new Vector2(18f, 28f), BackstageDesign.Color(BackstageDesign.Ivory), title);
        // The venue already appears in the heading; keep the metadata line
        // limited to the event window instead of repeating the venue name.
        if (height >= 83f)
            BackstageDesign.LabelAt(pos + new Vector2(19f, height - 26f),
                state.EventWindow, BackstageDesign.Muted);

        // All event metadata now lives together in one styled editor.
        if (width > 570f && BackstageDesign.FlatButton("##vhContextEditEvent", "EDIT EVENT",
                pos + new Vector2(width - 269f, 14f), new Vector2(130f, 29f), BackstageDesign.Gold))
            this.RequestEventEditor();

        if (width >= 610f)
        {
            BackstageDesign.LabelAt(pos + new Vector2(width - 116f, 10f), "SERVER TIME", BackstageDesign.Dim);
            BackstageDesign.LabelAt(pos + new Vector2(width - 115f, 32f),
                nowUtc.ToString("HH:mm:ss") + " ST", BackstageDesign.Ivory);
        }
        ReserveOverviewArea(pos, width, height);
    }

    private static void DrawSectionHeading(string label)
    {
        var pos = ImGui.GetCursorScreenPos();
        BackstageDesign.LabelAt(pos, label, BackstageDesign.Gold);
        ImGui.SetCursorScreenPos(pos + new Vector2(0f, 16f));
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawOnAirCard(VenueOverviewSnapshot state, float height)
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        BackstageDesign.SurfacePanel(pos, new Vector2(width, height),
            new Vector4(0.18f, 0.146f, 0.166f, 1f), 12f);
        BackstageDesign.Fill(draw, pos + new Vector2(0f, 15f),
            new Vector2(4f, height - 30f), BackstageDesign.Rose, 2f);
        BackstageDesign.StatusDot(pos + new Vector2(24f, 27f), BackstageDesign.Rose, 3f);
        BackstageDesign.LabelAt(pos + new Vector2(35f, 17f), "ON STAGE", BackstageDesign.Rose);
        var name = FitCompactText(state.CurrentDj, MathF.Max(90f, width - 48f));
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * (height < 140f ? 1.50f : 1.85f),
            pos + new Vector2(22f, height < 140f ? 42f : 47f), BackstageDesign.Color(BackstageDesign.Ivory), name);
        BackstageDesign.LabelAt(pos + new Vector2(23f, height - 33f),
            state.CurrentDjTime, BackstageDesign.Muted);
        // This links to existing DJ controls. It never sends a message.
        if (width >= 380f && BackstageDesign.FlatButton("##vhLiveDjControls", "MANAGE LINEUP",
                pos + new Vector2(width - 153f, height - 41f),
                new Vector2(137f, 28f), BackstageDesign.Gold))
            this.activePage = BackstagePage.Djs;
        ReserveOverviewArea(pos, width, height);
    }

    private static void DrawNextDjCard(VenueOverviewSnapshot state, float height)
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        BackstageDesign.SurfacePanel(pos, new Vector2(width, height));
        BackstageDesign.Fill(draw, pos + new Vector2(0f, 15f),
            new Vector2(3f, height - 30f), BackstageDesign.Gold, 1.5f);
        BackstageDesign.LabelAt(pos + new Vector2(20f, 17f), "NEXT DJ", BackstageDesign.Gold);
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * (height < 140f ? 1.22f : 1.38f),
            pos + new Vector2(20f, height < 140f ? 46f : 58f),
            BackstageDesign.Color(BackstageDesign.Ivory),
            FitCompactText(state.NextDj, MathF.Max(60f, width - 43f)));
        BackstageDesign.LabelAt(pos + new Vector2(21f, height - 33f),
            FitCompactText(state.NextDjTime, MathF.Max(60f, width - 40f)), BackstageDesign.Muted);
        ReserveOverviewArea(pos, width, height);
    }

    private void DrawCrewCard(VenueOverviewSnapshot state, float height)
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        BackstageDesign.SurfacePanel(pos, new Vector2(width, height));
        BackstageDesign.StatusDot(pos + new Vector2(21f, 24f), BackstageDesign.Mint, 3f);
        BackstageDesign.LabelAt(pos + new Vector2(33f, 14f), "STAFF ON DUTY", BackstageDesign.Ivory);
        var staffCount = state.ActiveStaff.Count;
        var countLabel = $"{staffCount} ACTIVE";
        BackstageDesign.LabelAt(pos + new Vector2(width - ImGui.CalcTextSize(countLabel).X - 19f, 14f),
            countLabel, BackstageDesign.Mint);
        BackstageDesign.Rule(draw, pos + new Vector2(17f, 44f),
            pos + new Vector2(width - 17f, 44f), BackstageDesign.SoftLine);

        // Mirror Upcoming Shouts: one role per 24 px row. Put the count
        // FIRST, in a compact leading column (e.g. "6 Photographer"), so the
        // breakdown scans naturally from left to right. Never parse the
        // old formatted summary string. The button has reserved space.
        const float rowStartY = 54f;
        const float rowStep = 24f;
        const float buttonWidth = 119f;
        var buttonY = height - 38f;
        var rowsBeforeButton = (int)MathF.Floor(
            (buttonY - 10f - rowStartY - ImGui.GetFontSize()) / rowStep) + 1;
        var roles = state.ActiveStaffRoleCounts;
        var visibleRows = Math.Min(roles.Count, Math.Clamp(rowsBeforeButton, 0, 4));

        if (staffCount == 0)
            BackstageDesign.LabelAt(pos + new Vector2(19f, rowStartY),
                FitCompactText("No staff currently on shift",
                    MathF.Max(45f, width - buttonWidth - 52f)), BackstageDesign.Muted);
        else if (roles.Count == 0)
            BackstageDesign.LabelAt(pos + new Vector2(19f, rowStartY),
                FitCompactText("Open Staff for shift details",
                    MathF.Max(45f, width - buttonWidth - 52f)), BackstageDesign.Muted);

        for (var i = 0; i < visibleRows; i++)
        {
            var role = roles[i];
            var y = rowStartY + i * rowStep;
            var countText = role.Value.ToString();
            // Like the colored type label on the shout rows, the count has
            // a small leading column and the role follows on the same row.
            // The width grows for double/triple-digit counts without overlap.
            var countColumnWidth = MathF.Max(20f, ImGui.CalcTextSize(countText).X + 11f);
            var roleX = 19f + countColumnWidth;
            BackstageDesign.LabelAt(pos + new Vector2(19f, y),
                countText, BackstageDesign.Mint);
            BackstageDesign.LabelAt(pos + new Vector2(roleX, y),
                FitCompactText(role.Key, MathF.Max(45f, width - roleX - 19f)),
                BackstageDesign.Ivory);
        }

        if (roles.Count > visibleRows)
        {
            var remaining = roles.Count - visibleRows;
            var moreText = $"+{remaining} more {(remaining == 1 ? "role" : "roles")}";
            BackstageDesign.LabelAt(pos + new Vector2(19f, height - 30f),
                FitCompactText(moreText, MathF.Max(45f, width - buttonWidth - 53f)),
                BackstageDesign.Dim);
        }

        if (BackstageDesign.FlatButton("##vhManageStaff", "MANAGE STAFF",
                pos + new Vector2(width - buttonWidth - 17f, buttonY),
                new Vector2(buttonWidth, 28f), BackstageDesign.Mint))
            this.activePage = BackstagePage.Staff;
        ReserveOverviewArea(pos, width, height);
    }

    private static void DrawUpcomingCard(IReadOnlyList<VenueActivity> timeline, float height)
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        BackstageDesign.SurfacePanel(pos, new Vector2(width, height));
        BackstageDesign.StatusDot(pos + new Vector2(21f, 24f), BackstageDesign.Gold, 3f);
        BackstageDesign.LabelAt(pos + new Vector2(33f, 14f), "UPCOMING SHOUTS", BackstageDesign.Ivory);
        BackstageDesign.Rule(draw, pos + new Vector2(17f, 44f),
            pos + new Vector2(width - 17f, 44f), BackstageDesign.SoftLine);

        // This is now the ONLY shout forecast on the Overview: include the
        // very next outgoing shout at index zero, then three that follow.
        if (timeline.Count == 0)
        {
            BackstageDesign.LabelAt(pos + new Vector2(20f, 57f),
                "No automatic shouts scheduled", BackstageDesign.Muted);
        }
        else
        {
            var visibleRows = Math.Max(1, Math.Min(4, (int)((height - 53f) / 24f)));
            var displayed = Math.Min(visibleRows, timeline.Count);
            for (var i = 0; i < displayed; i++)
            {
                var activity = timeline[i];
                var y = 54f + 24f * i;
                // The first row is the next actual shout. A very subtle tint
                // distinguishes it without adding a duplicate Next Cue card.
                if (i == 0)
                    BackstageDesign.Fill(draw, pos + new Vector2(12f, y - 3f),
                        new Vector2(width - 24f, 23f), new Vector4(0.94f, 0.735f, 0.47f, 0.055f), 4f);
                var accent = activity.Kind switch
                {
                    VenueActivityKind.DjShout => BackstageDesign.Rose,
                    VenueActivityKind.StaffShout => BackstageDesign.Mint,
                    _ => BackstageDesign.Gold,
                };
                var typeWidth = MathF.Max(62f, ImGui.CalcTextSize("EVENT").X + 18f);
                var timeWidth = 83f;
                BackstageDesign.LabelAt(pos + new Vector2(19f, y), activity.KindLabel, accent);
                BackstageDesign.LabelAt(pos + new Vector2(19f + typeWidth, y),
                    FitCompactText(activity.Title, MathF.Max(45f, width - typeWidth - timeWidth - 51f)),
                    BackstageDesign.Ivory);
                // All times are ST; the page's server-time header provides the
                // timezone. Preserve seconds for staggered announcements.
                var timeText = activity.TimeUtc.ToString("HH:mm:ss");
                BackstageDesign.LabelAt(pos + new Vector2(width - timeWidth - 15f, y),
                    timeText, BackstageDesign.Muted);
            }
        }
        ReserveOverviewArea(pos, width, height);
    }

    private static void ReserveOverviewArea(Vector2 position, float width, float height)
    {
        ImGui.SetCursorScreenPos(position + new Vector2(0f, height));
        ImGui.Dummy(new Vector2(MathF.Max(1f, width), 0f));
    }
}
