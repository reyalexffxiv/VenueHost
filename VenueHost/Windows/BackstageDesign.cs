using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using VenueHost.Services;

namespace VenueHost.Windows;

/// <summary>
/// VenueHost's own editorial/stagecraft art direction. Draws only within the
/// current Dalamud/ImGui window; it does not install global styles or textures.
/// All rendering is immediate-mode and produces no persistent GPU allocations.
/// </summary>
internal static class BackstageDesign
{
    public const float WindowCornerRadius = 14f;
    // The Overview's accessible palette is shared by all VenueHost surfaces.
    // A drawing scope restores the previous palette before another plugin/window draws.
    [ThreadStatic] private static bool highContrast;

    public static IDisposable PushHighContrast(bool enabled) => new ContrastScope(enabled);

    private sealed class ContrastScope : IDisposable
    {
        private readonly bool previous;
        public ContrastScope(bool enabled)
        {
            this.previous = highContrast;
            highContrast = enabled;
        }
        public void Dispose() => highContrast = this.previous;
    }

    public static Vector4 Canvas => highContrast ? new(0.012f, 0.012f, 0.014f, 1f) : new(0.055f, 0.063f, 0.077f, 1f);
    public static Vector4 Rail => highContrast ? new(0.012f, 0.012f, 0.014f, 1f) : new(0.076f, 0.083f, 0.095f, 1f);
    public static Vector4 Surface => highContrast ? new(0.027f, 0.027f, 0.031f, 1f) : new(0.105f, 0.107f, 0.123f, 1f);
    public static Vector4 Raised => highContrast ? new(0.075f, 0.075f, 0.083f, 1f) : new(0.135f, 0.132f, 0.148f, 1f);
    public static Vector4 Line => highContrast ? new(0.34f, 0.95f, 1f, 1f) : new(0.24f, 0.23f, 0.255f, 1f);
    public static Vector4 SoftLine => highContrast ? new(0.34f, 0.95f, 1f, 0.85f) : new(0.21f, 0.21f, 0.23f, 0.70f);
    public static Vector4 Ivory => highContrast ? new(1f, 1f, 1f, 1f) : new(0.963f, 0.945f, 0.912f, 1f);
    public static Vector4 Muted => highContrast ? new(0.88f, 0.93f, 0.95f, 1f) : new(0.69f, 0.69f, 0.72f, 1f);
    public static Vector4 Dim => highContrast ? new(0.75f, 0.83f, 0.86f, 1f) : new(0.50f, 0.51f, 0.55f, 1f);
    public static Vector4 Gold => highContrast ? new(1f, 0.91f, 0.05f, 1f) : new(0.94f, 0.735f, 0.47f, 1f);
    public static Vector4 Rose => highContrast ? new(1f, 0.56f, 0.88f, 1f) : new(0.94f, 0.60f, 0.67f, 1f);
    public static Vector4 Mint => highContrast ? new(0.45f, 1f, 0.42f, 1f) : new(0.45f, 0.84f, 0.72f, 1f);
    public static Vector4 Mist => highContrast ? new(0.34f, 0.95f, 1f, 1f) : new(0.55f, 0.72f, 0.81f, 1f);

    public static uint Color(Vector4 value) => ImGui.ColorConvertFloat4ToU32(value);

    public static void Fill(ImDrawListPtr draw, Vector2 p, Vector2 size, Vector4 color, float round = 0f)
        => draw.AddRectFilled(p, p + size, Color(color), round);

    public static void Frame(ImDrawListPtr draw, Vector2 p, Vector2 size, Vector4 stroke, float round = 0f)
        => draw.AddRect(p, p + size, Color(stroke), round, ImDrawFlags.None, highContrast ? 2f : 1f);

    public static void Rule(ImDrawListPtr draw, Vector2 a, Vector2 b, Vector4 color)
        => draw.AddLine(a, b, Color(color));

    public static void Ambient(Vector2 p, Vector2 size)
    {
        var draw = ImGui.GetWindowDrawList();
        // The root window's native rounded fill supplies Canvas. Drawing
        // another rectangular background here would obscure that silhouette.
        ImGui.PushClipRect(p, p + size, true);
        // Original VenueHost visual language: dim theater spotlights and
        // brass architectural linework, rather than a combat-tech dashboard.
        var a = p + new Vector2(size.X * 0.86f, size.Y * 0.24f);
        var b = p + new Vector2(size.X * 0.23f, size.Y * 0.93f);
        draw.AddCircleFilled(a, 300f, Color(new Vector4(0.45f, 0.25f, 0.22f, 0.055f)), 72);
        draw.AddCircleFilled(a, 214f, Color(new Vector4(0.57f, 0.34f, 0.22f, 0.043f)), 72);
        draw.AddCircleFilled(a, 118f, Color(new Vector4(0.63f, 0.41f, 0.26f, 0.045f)), 72);
        draw.AddCircleFilled(b, 260f, Color(new Vector4(0.26f, 0.39f, 0.42f, 0.035f)), 72);
        draw.AddCircleFilled(b, 145f, Color(new Vector4(0.25f, 0.47f, 0.44f, 0.03f)), 72);
        for (var i = 1; i <= 5; i++)
        {
            var x = p.X + i * size.X / 6f;
            Rule(draw, new Vector2(x, p.Y), new Vector2(x, p.Y + size.Y),
                new Vector4(0.56f, 0.43f, 0.37f, 0.022f));
        }
        ImGui.PopClipRect();
    }

    // Floaters open over their owner, never beyond the viewport when they can fit.
    // They remain draggable: only apply this position when a window first appears.
    public static Vector2 CenteredOverMainWindow(UiService ui, Vector2 popupSize)
    {
        if (ui.TryGetMainWindowBounds(out var pos, out var size))
            return CenteredWindowPosition(pos, size, popupSize);

        // Settings may be opened directly by a slash command, with no main window yet.
        var viewport = ImGui.GetMainViewport();
        return CenteredWindowPosition(viewport.WorkPos, viewport.WorkSize, popupSize);
    }

    public static Vector2 CenteredWindowPosition(Vector2 ownerPosition, Vector2 ownerSize, Vector2 popupSize)
    {
        var requested = ownerPosition + (ownerSize - popupSize) * 0.5f;
        var viewport = ImGui.GetMainViewport();
        var min = viewport.WorkPos;
        var max = viewport.WorkPos + viewport.WorkSize - popupSize;
        return new Vector2(Math.Clamp(requested.X, min.X, MathF.Max(min.X, max.X)),
            Math.Clamp(requested.Y, min.Y, MathF.Max(min.Y, max.Y)));
    }

    public static void SurfacePanel(Vector2 p, Vector2 size, Vector4? fill = null, float rounding = 12f)
    {
        var draw = ImGui.GetWindowDrawList();
        Fill(draw, p, size, fill ?? Surface, rounding);
        Frame(draw, p, size, SoftLine, rounding);
        if (!highContrast)
            Rule(draw, p + new Vector2(rounding, 1f),
                p + new Vector2(size.X - rounding, 1f),
                new Vector4(1f, 0.84f, 0.73f, 0.05f));
    }

    public static void StatusDot(Vector2 center, Vector4 color, float radius = 4f)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(center, radius + 3f, Color(color with { W = 0.085f }), 20);
        draw.AddCircleFilled(center, radius, Color(color), 20);
    }

    public static bool FlatButton(string id, string label, Vector2 p, Vector2 size,
        Vector4 accent, bool highlighted = false)
    {
        ImGui.SetCursorScreenPos(p);
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var fill = highlighted ? new Vector4(accent.X * 0.27f, accent.Y * 0.27f, accent.Z * 0.27f, 1f)
            : hovered ? Raised : Surface;
        var draw = ImGui.GetWindowDrawList();
        Fill(draw, p, size, fill, 8f);
        Frame(draw, p, size, highlighted ? accent with { W = 0.72f } : SoftLine, 8f);
        var textSize = ImGui.CalcTextSize(label);
        draw.AddText(p + new Vector2(MathF.Max(4f, (size.X - textSize.X) * 0.5f),
            (size.Y - textSize.Y) * 0.5f), Color(highlighted ? Ivory : (hovered ? Ivory : Muted)), label);
        return clicked;
    }

    public static bool ChevronButton(string id, Vector2 p, Vector2 size, Vector4 accent, bool up)
    {
        var clicked = FlatButton(id, string.Empty, p, size, accent);
        var middle = p + size * 0.5f;
        var y = up ? 2f : -2f;
        var a = new Vector2(middle.X - 5f, middle.Y + y);
        var b = new Vector2(middle.X, middle.Y - y);
        var c = new Vector2(middle.X + 5f, middle.Y + y);
        var dl = ImGui.GetWindowDrawList();
        dl.AddLine(a, b, Color(accent), 1.7f);
        dl.AddLine(b, c, Color(accent), 1.7f);
        return clicked;
    }

    /// <summary>
    /// Shared assignment surface for DJ and Staff. The entire card is one
    /// clickable hit target; the caller retains ownership of its picker popup.
    /// </summary>
    public static bool AssignmentSelector(string id, string caption, string? value,
        Vector2 p, Vector2 size)
    {
        ImGui.SetCursorScreenPos(p);
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var empty = string.IsNullOrWhiteSpace(value);
        var draw = ImGui.GetWindowDrawList();
        var fill = hovered ? new Vector4(0.195f, 0.190f, 0.208f, 1f)
            : empty ? new Vector4(0.155f, 0.139f, 0.132f, 1f) : Raised;
        Fill(draw, p, size, fill, 8f);
        Frame(draw, p, size, hovered ? Gold with { W = 0.55f }
            : empty ? Gold with { W = 0.32f } : SoftLine, 8f);
        draw.AddText(p + new Vector2(13f, 10f), Color(Dim), caption);
        var name = empty ? "Not assigned" : value!.Trim();
        // Long saved names must never draw over the CHANGE affordance.
        ImGui.PushClipRect(p + new Vector2(12f, 31f),
            p + new Vector2(MathF.Max(42f, size.X - 108f), size.Y - 6f), true);
        draw.AddText(p + new Vector2(13f, 35f), Color(empty ? Gold : Ivory), name);
        ImGui.PopClipRect();
        var action = empty ? "ASSIGN" : "CHANGE";
        var actionSize = ImGui.CalcTextSize(action);
        var actionX = p.X + size.X - actionSize.X - 18f;
        draw.AddText(new Vector2(actionX, p.Y + (size.Y - actionSize.Y) * 0.5f),
            Color(hovered ? Ivory : Gold), action);
        if (hovered)
            ImGui.SetTooltip(empty ? "Choose someone from the saved database" : "Change this assignment");
        return clicked;
    }

    /// <summary>
    /// Compact VenueHost role menu with rounded rows and selected-state accent.
    /// Returns true only when a different role was chosen. Persistence belongs
    /// to the caller (shift, quick-add draft or database draft).
    /// </summary>
    public static bool RoleSelector(string id, string currentRole,
        IReadOnlyList<string> roles, Vector2 p, Vector2 size, out string chosenRole)
    {
        chosenRole = currentRole;
        ImGui.PushID(id);
        ImGui.SetCursorScreenPos(p);
        var clicked = ImGui.InvisibleButton("##RoleField", size);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        Fill(draw, p, size, hovered ? new Vector4(0.195f, 0.190f, 0.208f, 1f) : Raised, 7f);
        Frame(draw, p, size, hovered ? Gold with { W = 0.45f } : SoftLine, 7f);
        ImGui.PushClipRect(p + new Vector2(10f, 1f), p + new Vector2(size.X - 32f, size.Y - 1f), true);
        draw.AddText(p + new Vector2(11f, (size.Y - ImGui.GetTextLineHeight()) * 0.5f),
            Color(Ivory), currentRole);
        ImGui.PopClipRect();
        var mid = p + new Vector2(size.X - 17f, size.Y * 0.5f);
        var chevronColor = Color(hovered ? Gold : Muted);
        draw.AddLine(mid + new Vector2(-4f, -2f), mid + new Vector2(0f, 2f), chevronColor, 1.7f);
        draw.AddLine(mid + new Vector2(0f, 2f), mid + new Vector2(4f, -2f), chevronColor, 1.7f);
        if (clicked)
            ImGui.OpenPopup("##RoleMenu");

        var menuHeight = MathF.Min(262f, 12f + roles.Count * 32f);
        var menuSize = new Vector2(size.X, MathF.Max(45f, menuHeight));
        ImGui.SetNextWindowSize(menuSize, ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(menuSize, menuSize);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Canvas);
        ImGui.PushStyleColor(ImGuiCol.Border, SoftLine);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 9f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 6f);
        var changed = false;
        if (ImGui.BeginPopup("##RoleMenu", ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar))
        {
            if (ImGui.BeginChild("##RoleItems", new Vector2(size.X - 12f, menuSize.Y - 12f), false))
            {
                foreach (var role in roles)
                {
                    ImGui.PushID(role);
                    var rp = ImGui.GetCursorScreenPos();
                    var rw = MathF.Max(80f, ImGui.GetContentRegionAvail().X - 4f);
                    var pick = ImGui.InvisibleButton("##RoleItem", new Vector2(rw, 31f));
                    var over = ImGui.IsItemHovered();
                    var selected = string.Equals(currentRole, role, StringComparison.OrdinalIgnoreCase);
                    var surface = over ? Raised
                        : selected ? new Vector4(0.205f, 0.182f, 0.166f, 1f) : Canvas;
                    var rd = ImGui.GetWindowDrawList();
                    Fill(rd, rp, new Vector2(rw, 30f), surface, 5f);
                    if (selected)
                        Fill(rd, rp + new Vector2(0f, 3f), new Vector2(2f, 24f), Gold, 1f);
                    ImGui.PushClipRect(rp + new Vector2(10f, 0f), rp + new Vector2(rw - 8f, 30f), true);
                    rd.AddText(rp + new Vector2(11f, 6f), Color(selected || over ? Ivory : Muted), role);
                    ImGui.PopClipRect();
                    if (pick)
                    {
                        chosenRole = role;
                        changed = !string.Equals(currentRole, role, StringComparison.OrdinalIgnoreCase);
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 1f);
                    ImGui.PopID();
                }
            }
            ImGui.EndChild();
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(2);
        ImGui.PopID();
        return changed;
    }

    public static void LabelAt(Vector2 p, string label, Vector4 color)
    {
        ImGui.SetCursorScreenPos(p);
        ImGui.TextColored(color, label);
    }
}
