using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueHost.Services;

namespace VenueHost.Windows;

/// <summary>
/// A custom app shell around the existing production tools. This file never
/// changes schedule state. Legacy editors are preserved as selectable pages.
/// </summary>
public sealed partial class MainWindow
{
    private enum BackstagePage { Overview, Djs, Staff, Shouts }
    private BackstagePage activePage = BackstagePage.Overview;
    private Vector2 shellDragDelta;
    private float animatedNavY = -1f;

    private void DrawBackstageApp()
    {
        this.shellDragDelta = Vector2.Zero;

        // In compact mode the header IS the application. No hidden workspace,
        // child window, editor or persistent dock is drawn behind the strip.
        if (this.compactAmount > 0.001f)
        {
            this.DrawCompactBackstageBar();
            return;
        }

        var origin = ImGui.GetCursorScreenPos();
        var total = ImGui.GetContentRegionAvail();
        if (total.X < 100f || total.Y < 150f)
            return;

        // Single owner for the whole outer silhouette, including the BOTTOM
        // two corners. No native WindowBg and no second dock/window background.
        // Both would otherwise fill the pixels outside this rounded path.
        var rootDraw = ImGui.GetWindowDrawList();
        var accessibilityOverview = this.Configuration.OverviewHighContrastPreviewEnabled;
        BackstageDesign.Fill(rootDraw, ImGui.GetWindowPos(),
            ImGui.GetWindowSize(), accessibilityOverview ? AccessBlack : BackstageDesign.Canvas,
            BackstageDesign.WindowCornerRadius);
        if (!accessibilityOverview)
            BackstageDesign.Ambient(origin, total);

        // All shell children are borderless, including in optional high-contrast mode.
        // The root window's native border is disabled earlier in PreDraw().
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 0f);
        const float headerHeight = 68f;
        const float dockHeight = 70f; // 17 px below the inset dock for visible bottom breathing room
        var bodyHeight = MathF.Max(300f, total.Y - headerHeight - dockHeight);
        if (ImGui.BeginChild("##vhShellHeader", new Vector2(0, headerHeight), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            this.DrawShellHeader();
        ImGui.EndChild();

        if (ImGui.BeginChild("##vhShellBody", new Vector2(0, bodyHeight), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            // Keep the navigation a permanent icon-only rail. The narrower
            // expanded window retains the same usable workspace width because
            // the previous 166px labelled rail is now just 72px.
            var railWidth = accessibilityOverview ? 130f : 72f;
            if (ImGui.BeginChild("##vhShellRail", new Vector2(railWidth, 0), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (accessibilityOverview)
                    this.DrawAccessShellRail(railWidth);
                else
                    this.DrawShellRail(railWidth);
            }
            ImGui.EndChild();
            ImGui.SameLine(0f, 0f);

            if (ImGui.BeginChild("##vhShellWorkspace", new Vector2(0, 0), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                this.DrawWorkspaceTitle();
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22f, 18f));
                // Existing table editors can be taller than the available area.
                // Keep the page scroll independent of the header and footer.
                // Horizontal scrolling belongs to the table editors. The
                // accessibility Overview is responsive and should never grow a
                // scrollbar beneath its cards, where it appears as a clipping bug.
                var pageFlags = ImGuiWindowFlags.AlwaysUseWindowPadding;
                if (!accessibilityOverview)
                    pageFlags |= ImGuiWindowFlags.HorizontalScrollbar;
                if (ImGui.BeginChild("##vhWorkspacePage", new Vector2(0, 0), false,
                    pageFlags))
                {
                    switch (this.activePage)
                    {
                        case BackstagePage.Overview: this.DrawBackstageOverview(); break;
                        case BackstagePage.Djs: this.DrawDjLineupTab(); break;
                        case BackstagePage.Staff: this.DrawStaffScheduleTab(); break;
                        case BackstagePage.Shouts: this.DrawEventShoutsTab(); break;
                    }
                }
                ImGui.EndChild();
                ImGui.PopStyleVar();
            }
            ImGui.EndChild();
        }
        ImGui.EndChild();

        // Render the dock INSIDE THE ROOT, not a separate ImGui child window.
        // The other plugins use the root draw list for their bottom dock;
        // this also prevents nested window backgrounds and rectangular child
        // clipping from covering the outside corner pixels.
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, headerHeight + bodyHeight));
        if (accessibilityOverview)
            this.DrawAccessShellDock();
        else
            this.DrawShellDock();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
        // Apply dragging to the root Dalamud window, not the nested header child.
        if (this.shellDragDelta != Vector2.Zero)
            ImGui.SetWindowPos(ImGui.GetWindowPos() + this.shellDragDelta, ImGuiCond.Always);
    }

    private void DrawShellHeader()
    {
        if (this.Configuration.OverviewHighContrastPreviewEnabled)
        {
            this.DrawAccessShellHeader();
            return;
        }
        var p = ImGui.GetWindowPos();
        var width = ImGui.GetWindowSize().X;
        var dl = ImGui.GetWindowDrawList();
        BackstageDesign.Fill(dl, p, new Vector2(width, 68f), BackstageDesign.Rail, BackstageDesign.WindowCornerRadius);
        BackstageDesign.Fill(dl, p + new Vector2(0f, 12f), new Vector2(width, 56f), BackstageDesign.Rail);
        BackstageDesign.Rule(dl, p + new Vector2(0, 67f), p + new Vector2(width, 67f), BackstageDesign.SoftLine);
        // The embedded peepo PNG already has alpha transparency. Don't paint
        // a square plaque behind it; let the mascot blend into the shell.
        var mascot = this.Ui.MascotTexture?.GetWrapOrDefault();
        if (mascot is not null)
            dl.AddImage(mascot.Handle, p + new Vector2(19f, 17f), p + new Vector2(53f, 51f));
        else
            BackstageDesign.LabelAt(p + new Vector2(28f, 25f), "V", BackstageDesign.Gold);
        // One brand line, vertically centered. The second tagline added clutter.
        BackstageDesign.LabelAt(p + new Vector2(67f, 25f), "VENUEHOST", BackstageDesign.Ivory);
        var status = this.GetAutomationHeaderStatus();
        var accent = status == "LIVE" ? BackstageDesign.Mint :
            status == "PAUSED" ? BackstageDesign.Rose :
            status == "UPCOMING" ? BackstageDesign.Gold : BackstageDesign.Muted;
        var indicatorX = width - 269f;
        BackstageDesign.StatusDot(p + new Vector2(indicatorX, 34f), accent, 3.5f);
        BackstageDesign.LabelAt(p + new Vector2(indicatorX + 11f, 25f), status, accent);
        if (BackstageDesign.FlatButton("##vhShellSetup", "SETUP", p + new Vector2(width - 177f, 18f),
                new Vector2(74f, 32f), BackstageDesign.Gold))
            this.Ui.ToggleConfigUi();
        if (BackstageDesign.ChevronButton("##vhShellCompact", p + new Vector2(width - 91f, 18f),
                new Vector2(34f, 32f), BackstageDesign.Gold, up: false))
            this.ToggleCompactMode();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Compact mode (or double-click the title bar)");
        if (BackstageDesign.FlatButton("##vhShellClose", "X", p + new Vector2(width - 47f, 18f),
                new Vector2(30f, 32f), BackstageDesign.Rose))
            this.IsOpen = false;

        // The unoccupied title strip supports dragging AND double-click collapse.
        // No interaction overlaps with the Setup / Collapse / Close buttons.
        ImGui.SetCursorScreenPos(p + new Vector2(190f, 0f));
        ImGui.InvisibleButton("##vhShellDrag", new Vector2(MathF.Max(10f, width - 505f), 67f));
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            this.ToggleCompactMode();
        if (ImGui.IsItemActive())
            this.shellDragDelta = ImGui.GetIO().MouseDelta;
    }

    private string GetAutomationHeaderStatus()
    {
        var automation = this.Services.Get<AutomationControlService>();
        return automation.AnyRunning ? "LIVE" : "IDLE";
    }

    private string GetShellEventState()
    {
        var now = DateTime.UtcNow;
        if (this.overviewSnapshot is null || (now - this.overviewSnapshotAtUtc).TotalSeconds > 1)
        {
            this.overviewSnapshot = VenueOverviewSnapshot.Build(this.Configuration, now);
            this.overviewSnapshotAtUtc = now;
        }
        return this.overviewSnapshot.EventState;
    }

    private void DrawShellRail(float railWidth)
    {
        var p = ImGui.GetWindowPos();
        var height = ImGui.GetWindowSize().Y;
        var dl = ImGui.GetWindowDrawList();
        BackstageDesign.Fill(dl, p, new Vector2(railWidth, height), BackstageDesign.Rail);
        BackstageDesign.Rule(dl, p + new Vector2(railWidth - 1, 0f),
            p + new Vector2(railWidth - 1, height), BackstageDesign.SoftLine);
        DrawNavItem(BackstagePage.Overview, "Overview", 61f);
        DrawNavItem(BackstagePage.Djs, "DJ lineup", 113f);
        DrawNavItem(BackstagePage.Staff, "Staff Schedule", 165f);
        DrawNavItem(BackstagePage.Shouts, "Shouts", 217f);
        // One persistent, softly eased indicator makes the rail feel like a
        // small application rather than a stack of default ImGui tabs.
        var target = 61f + 52f * (int)this.activePage + 10f;
        if (this.animatedNavY < 0f || Plugin.PluginInterface.UiBuilder.ShouldUseReducedMotion)
            this.animatedNavY = target;
        else
            this.animatedNavY += (target - this.animatedNavY) *
                (1f - MathF.Exp(-15f * MathF.Min(ImGui.GetIO().DeltaTime, 0.05f)));
        BackstageDesign.Fill(dl, p + new Vector2(10f, this.animatedNavY),
            new Vector2(3f, 23f), BackstageDesign.Gold, 1.5f);
    }

    private void DrawNavItem(BackstagePage page, string title, float y)
    {
        var pos = ImGui.GetWindowPos();
        var width = ImGui.GetWindowSize().X;
        var selected = this.activePage == page;
        var x = pos.X + 10f;
        var rect = new Vector2(width - 20f, 43f);
        ImGui.SetCursorScreenPos(new Vector2(x, pos.Y + y));
        var clicked = ImGui.InvisibleButton($"##vhNav{page}", rect);
        var hover = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        if (selected || hover)
            BackstageDesign.Fill(dl, new Vector2(x, pos.Y + y), rect,
                selected ? new Vector4(0.25f, 0.205f, 0.195f, 1f) : BackstageDesign.Raised, 9f);
        var color = selected ? BackstageDesign.Gold : hover ? BackstageDesign.Ivory : BackstageDesign.Muted;
        // Always center the icon. A hover tooltip replaces the old label.
        var iconX = x + rect.X * 0.5f;
        DrawNavGlyph(dl, page, new Vector2(iconX, pos.Y + y + 21.5f), color);
        if (hover)
            ImGui.SetTooltip(title);
        if (clicked)
        {
            if (this.activePage == BackstagePage.Djs && page != BackstagePage.Djs)
                this.CommitPendingDjTimeDrafts();
            this.activePage = page;
        }
    }

    // Scalable vector icons, drawn locally. No font/icon dependency and no
    // stock ImGui glyphs leaking into the Backstage art direction.
    private static void DrawNavGlyph(ImDrawListPtr draw, BackstagePage page, Vector2 center, Vector4 ink)
    {
        var color = BackstageDesign.Color(ink);
        switch (page)
        {
            case BackstagePage.Overview:
                for (var y = -1; y <= 1; y += 2)
                for (var x = -1; x <= 1; x += 2)
                    BackstageDesign.Fill(draw, center + new Vector2(x * 4.7f - 3.2f, y * 4.7f - 3.2f),
                        new Vector2(6.4f, 6.4f), ink, 1.5f);
                break;
            case BackstagePage.Djs:
                draw.AddCircle(center, 8.5f, color, 28, 1.6f);
                draw.AddCircleFilled(center, 2.4f, color, 18);
                draw.AddLine(center + new Vector2(3f, -5f), center + new Vector2(6f, -7f), color, 1.3f);
                break;
            case BackstagePage.Staff:
                draw.AddCircleFilled(center + new Vector2(-4.7f, -4f), 3.2f, color, 18);
                draw.AddCircleFilled(center + new Vector2(4.7f, -4f), 3.2f, color, 18);
                draw.AddLine(center + new Vector2(-10f, 7f), center + new Vector2(-10f, 3f), color, 1.5f);
                draw.AddLine(center + new Vector2(-10f, 3f), center + new Vector2(-1f, 1f), color, 1.5f);
                draw.AddLine(center + new Vector2(10f, 7f), center + new Vector2(10f, 3f), color, 1.5f);
                draw.AddLine(center + new Vector2(10f, 3f), center + new Vector2(1f, 1f), color, 1.5f);
                break;
            case BackstagePage.Shouts:
                draw.AddLine(center + new Vector2(-8f, -4f), center + new Vector2(1f, -4f), color, 1.6f);
                draw.AddLine(center + new Vector2(-8f, 3f), center + new Vector2(1f, 3f), color, 1.6f);
                draw.AddLine(center + new Vector2(1f, -4f), center + new Vector2(6f, -8f), color, 1.6f);
                draw.AddLine(center + new Vector2(1f, 3f), center + new Vector2(6f, 7f), color, 1.6f);
                draw.AddLine(center + new Vector2(6f, -8f), center + new Vector2(6f, 7f), color, 1.6f);
                draw.AddLine(center + new Vector2(-5f, 4f), center + new Vector2(-3f, 9f), color, 1.8f);
                break;
        }
    }

    private void DrawWorkspaceTitle()
    {
        var p = ImGui.GetWindowPos();
        var w = ImGui.GetWindowSize().X;
        var dl = ImGui.GetWindowDrawList();
        const float headerHeight = 56f;
        var accessOverview = this.Configuration.OverviewHighContrastPreviewEnabled;
        BackstageDesign.Fill(dl, p, new Vector2(w, headerHeight),
            accessOverview ? AccessBlack : new Vector4(0.088f, 0.088f, 0.102f, 0.78f));
        BackstageDesign.Rule(dl, p + new Vector2(0, headerHeight - 1f),
            p + new Vector2(w, headerHeight - 1f), accessOverview ? AccessCyan : BackstageDesign.SoftLine);
        var heading = this.activePage switch
        {
            BackstagePage.Overview => "OVERVIEW",
            BackstagePage.Staff => "STAFF SCHEDULE",
            BackstagePage.Shouts => "SHOUTS",
            _ => "DJ LINEUP",
        };
        if (accessOverview)
        {
            ImGui.SetWindowFontScale(1.16f);
            AccessLabel(p + new Vector2(22f, 16f), heading, AccessYellow, 260f);
            ImGui.SetWindowFontScale(1.0f);
        }
        else
            BackstageDesign.LabelAt(p + new Vector2(22f, 19f), heading, BackstageDesign.Gold);

        ImGui.SetCursorScreenPos(p + new Vector2(0, headerHeight));
        ImGui.Dummy(new Vector2(0f, 0f));
    }

    private void DrawShellDock()
    {
        // This method runs in the ROOT ImGui window. Cursor is positioned
        // at the dock's top edge; GetWindowPos() would point to the app's top.
        var p = ImGui.GetCursorScreenPos();
        var width = ImGui.GetWindowSize().X;
        var draw = ImGui.GetWindowDrawList();
        var automation = this.Services.Get<AutomationControlService>();

        // No full-width dock background is needed. The ROOT already drew an
        // opaque rounded background in one pass, and the dock uses the same
        // Canvas color. Painting a second (even rounded) surface here can hide
        // the application's real lower-corner silhouette.
        BackstageDesign.Rule(draw, p, p + new Vector2(width, 0f), BackstageDesign.SoftLine);

        var rect = p + new Vector2(12f, 8f);
        var rectWidth = width - 24f;
        BackstageDesign.SurfacePanel(rect, new Vector2(rectWidth, 45f), BackstageDesign.Raised, 10f);
        BackstageDesign.Fill(draw, rect + new Vector2(0f, 10f), new Vector2(3f, 25f),
            BackstageDesign.Gold, 1.5f);
        BackstageDesign.LabelAt(rect + new Vector2(16f, 3f),
            "AUTOMATION CONTROL", BackstageDesign.Gold);

        // All three toggles are double-click actions. Their indicator and
        // label always represent the actual runnable automation state.
        this.DrawDockToggle(rect + new Vector2(12f, 17f), 116f, "DJ", automation.DjRunning,
            automation.DjAvailable, automation.ToggleDj);
        this.DrawDockToggle(rect + new Vector2(132f, 17f), 141f, "STAFF", automation.StaffRunning,
            automation.StaffAvailable, automation.ToggleStaff);
        this.DrawDockToggle(rect + new Vector2(278f, 17f), 155f, "EVENT", automation.EventRunning,
            automation.EventAvailable, automation.ToggleEvent);

        // The footer is a dedicated automation control surface. Keep the
        // individual toggles on the left and the global pause on the right;
        // scheduled messages and queue details belong in their workspaces.
        const float pauseWidth = 130f;
        var pausePos = rect + new Vector2(rectWidth - pauseWidth - 11f, 7f);
        var pauseLabel = automation.AnyRunning ? "PAUSE ALL" : "START ALL";
        this.DrawDockDoubleAction("##vhPauseAll", pauseLabel, pausePos,
            new Vector2(pauseWidth, 31f), automation.TogglePauseAll);
    }

    private static string AutoToggleTooltip(string name, bool available) => available
        ? "Double-click to toggle"
        : name switch
        {
            "DJ" => "No DJs scheduled",
            "STAFF" => "No staff role auto enabled",
            _ => "No event shouts auto enabled",
        };

    private void DrawDockToggle(Vector2 pos, float width, string label, bool running,
        bool available, Action toggle)
    {
        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton($"##vhToggle{label}", new Vector2(width, 26f));
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        if (hovered)
            BackstageDesign.Fill(draw, pos, new Vector2(width, 26f), BackstageDesign.Surface, 6f);
        var ink = running ? BackstageDesign.Mint : StatusInactive;
        draw.AddCircleFilled(pos + new Vector2(8f, 13f), 3f, BackstageDesign.Color(ink), 14);
        // The category stays white; only the indicator and its status carry state color.
        var titlePos = pos + new Vector2(18f, 6f);
        draw.AddText(titlePos, BackstageDesign.Color(AccessWhite), label);
        draw.AddText(titlePos + new Vector2(ImGui.CalcTextSize(label).X + 7f, 0f),
            BackstageDesign.Color(ink), running ? "ACTIVE" : "INACTIVE");
        if (hovered)
        {
            ImGui.SetTooltip(AutoToggleTooltip(label, available));
            if (available && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                toggle();
        }
    }

    private void DrawDockDoubleAction(string id, string label, Vector2 pos, Vector2 size,
        Action toggle)
    {
        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        BackstageDesign.Fill(draw, pos, size, hovered ? BackstageDesign.Raised : BackstageDesign.Surface, 8f);
        BackstageDesign.Frame(draw, pos, size, BackstageDesign.SoftLine, 8f);
        var textSize = ImGui.CalcTextSize(label);
        draw.AddText(pos + new Vector2((size.X - textSize.X) * 0.5f, (size.Y - textSize.Y) * 0.5f),
            BackstageDesign.Color(BackstageDesign.Ivory), label);
        if (hovered)
        {
            ImGui.SetTooltip(label == "START ALL" ? "Double-click to start configured automatic shouts" :
                "Double-click to pause all automatic shouts");
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) toggle();
        }
    }
}
