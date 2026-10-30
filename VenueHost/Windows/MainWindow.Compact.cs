using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using VenueHost.Services;

namespace VenueHost.Windows;

/// <summary>
/// Window chrome and compact event monitor. The old scheduler and editors have
/// no dependency on this presentation state; collapse never pauses automation.
/// </summary>
public sealed partial class MainWindow
{
    private const float CompactStripHeight = 60f;
    private const float CompactStripWidth = 590f;
    private const float CompactDurationMs = 240f;

    private const ImGuiWindowFlags AppWindowFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
        // All background pixels are drawn exactly once in our rounded shell.
        // Native WindowBg behind a rounded dock makes the corners look square.
        ImGuiWindowFlags.NoBackground;

    // v0.5.6: icon-only 72px rail replaces the former 166px labelled rail.
    // Shrinking 1325 -> 1232 preserves essentially the same workspace width.
    // Window is draggable, but never manually resizable. Compact is independent.
    private static readonly Vector2 ExpandedFixedSize = new(1232f, 832f);

    private static readonly WindowSizeConstraints ExpandedWindowConstraints = new()
    {
        MinimumSize = ExpandedFixedSize,
        MaximumSize = ExpandedFixedSize,
    };

    private static readonly WindowSizeConstraints CompactWindowConstraints = new()
    {
        MinimumSize = new Vector2(CompactStripWidth, CompactStripHeight),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };

    private bool compactRequested;
    private float compactAmount;
    private float transitionFrom;
    private long transitionAt;

    private void ToggleCompactMode()
    {
        if (!this.compactRequested && this.activePage == BackstagePage.Djs)
            this.CommitPendingDjTimeDrafts();
        this.compactRequested = !this.compactRequested;
        this.transitionFrom = this.compactAmount;
        this.transitionAt = Environment.TickCount64;
    }

    private void AdvanceCompactTransition()
    {
        var target = this.compactRequested ? 1f : 0f;
        if (Plugin.PluginInterface.UiBuilder.ShouldUseReducedMotion)
        {
            this.compactAmount = target;
            return;
        }

        var range = MathF.Abs(target - this.transitionFrom);
        if (range <= 0.0001f)
        {
            this.compactAmount = target;
            return;
        }

        var duration = CompactDurationMs * range;
        var t = Math.Clamp((Environment.TickCount64 - this.transitionAt) / duration, 0f, 1f);
        var eased = t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) * 0.5f;
        this.compactAmount = this.transitionFrom + (target - this.transitionFrom) * eased;
    }

    // PreDraw runs before the root Begin. The root is transparent; expanded
    // and compact backgrounds are both drawn with genuinely rounded geometry.
    // Don't depend on the native opaque WindowBg behind a rounded footer.
    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, BackstageDesign.WindowCornerRadius);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, BackstageDesign.Canvas);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        // No resize grip: the expanded layout now has a fixed approved size.
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, Vector4.Zero);

        this.AdvanceCompactTransition();
        var sizeDriven = this.compactRequested || this.compactAmount > 0.001f;
        this.Flags = AppWindowFlags | ImGuiWindowFlags.NoResize;
        this.SizeConstraints = sizeDriven ? CompactWindowConstraints : ExpandedWindowConstraints;
        if (sizeDriven)
        {
            // Fit the visible text with tight but safe spacing, keeping the
            // restore/close controls free even for long DJ names or status text.
            this.GetShellEventState();
            var status = this.GetAutomationHeaderStatus();
            var djName = this.overviewSnapshot?.CurrentDj;
            var summary = !string.IsNullOrWhiteSpace(djName) && djName != "No DJ on stage"
                ? "On stage: " + djName : "No DJ on stage";
            var desired = GetCompactSummaryX(status) + ImGui.CalcTextSize(summary).X + 113f;
            var compactWidth = Math.Clamp(desired, CompactStripWidth, 900f);
            this.Size = Vector2.Lerp(ExpandedFixedSize,
                new Vector2(compactWidth, CompactStripHeight), this.compactAmount);
            this.SizeCondition = ImGuiCond.Always;
        }
        else
        {
            // Ignore old Dalamud saved sizes as well as prior user drags.
            this.Size = ExpandedFixedSize;
            this.SizeCondition = ImGuiCond.Always;
        }
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar(4);
    }

    // Space the status immediately after the brand and the DJ summary after
    // the actual status text instead of using hard-coded wide columns.
    private static float GetCompactStatusDotX() =>
        62f + ImGui.CalcTextSize("VENUEHOST").X + 23f;

    private static float GetCompactSummaryX(string status) =>
        GetCompactStatusDotX() + 10f + ImGui.CalcTextSize(status).X + 25f;

    private void DrawCompactBackstageBar()
    {
        // The compact window is only 60 px high. A same-height nested child
        // can acquire a near-empty content clip when Dalamud/ImGui applies
        // viewport and inner padding rules. Keep hit testing in the ROOT
        // window and draw compact chrome in the foreground draw list after
        // the hit regions have been submitted, clipped to our own rectangle.
        var p = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var centerY = p.Y + size.Y * 0.5f;
        var mouseDelta = Vector2.Zero;

        var restorePos = p + new Vector2(size.X - 86f, centerY - p.Y - 16f);
        var closePos = p + new Vector2(size.X - 45f, centerY - p.Y - 16f);

        ImGui.SetCursorScreenPos(restorePos);
        var restore = ImGui.InvisibleButton("##vhCompactRestore", new Vector2(34f, 32f));
        var restoreHover = ImGui.IsItemHovered();
        if (restoreHover) ImGui.SetTooltip("Restore VenueHost");
        ImGui.SetCursorScreenPos(closePos);
        var close = ImGui.InvisibleButton("##vhCompactClose", new Vector2(30f, 32f));
        var closeHover = ImGui.IsItemHovered();
        ImGui.SetCursorScreenPos(p);
        ImGui.InvisibleButton("##vhCompactTitleDrag", new Vector2(MathF.Max(1f, size.X - 104f), size.Y));
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            restore = true;
        if (ImGui.IsItemActive())
            mouseDelta = ImGui.GetIO().MouseDelta;

        // Foreground rendering is intentional: it bypasses the root content
        // clipping that hid our labels and buttons in both earlier builds.
        // Explicit clipping prevents painting outside the VenueHost window.
        var draw = ImGui.GetForegroundDrawList();
        draw.PushClipRect(p, p + size, true);
        BackstageDesign.Fill(draw, p, size, BackstageDesign.Rail, BackstageDesign.WindowCornerRadius);
        var mascot = this.Ui.MascotTexture?.GetWrapOrDefault();
        if (mascot is not null)
            draw.AddImage(mascot.Handle, p + new Vector2(17f, centerY - p.Y - 16f),
                p + new Vector2(49f, centerY - p.Y + 16f));
        else
            draw.AddText(p + new Vector2(28f, centerY - p.Y - 8f),
                BackstageDesign.Color(BackstageDesign.Gold), "V");

        draw.AddText(p + new Vector2(62f, centerY - p.Y - 9f),
            BackstageDesign.Color(BackstageDesign.Ivory), "VENUEHOST");
        this.GetShellEventState();
        var status = this.GetAutomationHeaderStatus();
        var statusColor = status == "LIVE" ? BackstageDesign.Mint :
            status == "PAUSED" ? BackstageDesign.Rose :
            status == "UPCOMING" ? BackstageDesign.Gold : BackstageDesign.Muted;
        var statusDotX = GetCompactStatusDotX();
        draw.AddCircleFilled(p + new Vector2(statusDotX, centerY - p.Y), 3f,
            BackstageDesign.Color(statusColor), 12);
        draw.AddText(p + new Vector2(statusDotX + 10f, centerY - p.Y - 9f),
            BackstageDesign.Color(statusColor), status);

        // Compact mode must refresh its own DJ snapshot. Otherwise the label
        // could stay on the DJ who was on stage when the user minimized.
        this.GetShellEventState();
        var snapshot = this.overviewSnapshot;
        var chat = this.Services.Get<GameChatService>();
        var summaryX = GetCompactSummaryX(status);
        var room = size.X - summaryX - 103f;
        if (room > 40f)
        {
            var origin = p + new Vector2(summaryX, centerY - p.Y - 9f);
            if (chat.HasFailedCommand)
            {
                draw.AddText(origin, BackstageDesign.Color(BackstageDesign.Rose),
                    FitCompactText("Local chat send failed", room));
            }
            else if (snapshot is not null && snapshot.CurrentDj != "No DJ on stage")
            {
                const string label = "On stage: ";
                draw.AddText(origin, BackstageDesign.Color(BackstageDesign.Muted), label);
                var labelWidth = ImGui.CalcTextSize(label).X;
                var djWidth = MathF.Max(24f, room - labelWidth);
                draw.AddText(origin + new Vector2(labelWidth, 0f),
                    BackstageDesign.Color(BackstageDesign.Gold),
                    FitCompactText(snapshot.CurrentDj, djWidth));
            }
            else
            {
                var fallback = snapshot is null ? "Waiting for event" : "No DJ on stage";
                draw.AddText(origin, BackstageDesign.Color(BackstageDesign.Muted),
                    FitCompactText(fallback, room));
            }
        }
        var buttonFill = BackstageDesign.Surface;
        BackstageDesign.Fill(draw, restorePos, new Vector2(34f, 32f),
            restoreHover ? BackstageDesign.Raised : buttonFill, 8f);
        BackstageDesign.Frame(draw, restorePos, new Vector2(34f, 32f), BackstageDesign.SoftLine, 8f);
        var middle = restorePos + new Vector2(17f, 16f);
        var gold = BackstageDesign.Color(BackstageDesign.Gold);
        draw.AddLine(middle + new Vector2(-5f, 2f), middle + new Vector2(0f, -2f), gold, 1.7f);
        draw.AddLine(middle + new Vector2(0f, -2f), middle + new Vector2(5f, 2f), gold, 1.7f);
        BackstageDesign.Fill(draw, closePos, new Vector2(30f, 32f),
            closeHover ? BackstageDesign.Raised : buttonFill, 8f);
        BackstageDesign.Frame(draw, closePos, new Vector2(30f, 32f), BackstageDesign.SoftLine, 8f);
        draw.AddText(closePos + new Vector2(9f, 8f), BackstageDesign.Color(BackstageDesign.Rose), "X");
        draw.PopClipRect();

        if (mouseDelta != Vector2.Zero)
            ImGui.SetWindowPos(p + mouseDelta, ImGuiCond.Always);
        if (restore) this.ToggleCompactMode();
        if (close) this.IsOpen = false;
    }

    private static string FitCompactText(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width) return text;
        const string ellipsis = "...";
        while (text.Length > 0 && ImGui.CalcTextSize(text + ellipsis).X > width)
            text = text[..^1];
        return text.Length == 0 ? ellipsis : text + ellipsis;
    }
}
