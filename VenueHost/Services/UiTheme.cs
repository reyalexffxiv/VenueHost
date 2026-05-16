using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace VenueHost.Services;

/// <summary>
/// Small immediate-mode theme helper used to apply Venue Host display options.
///
/// Contrast Mode intentionally changes only colors and only while a Venue Host
/// window is drawing. It does not modify global Dalamud style permanently, so
/// other plugins and the rest of the game UI are left untouched.
/// </summary>
public sealed class UiTheme : IDisposable
{
    private readonly int pushedColors;
    private readonly int pushedStyleVars;

    private UiTheme(int pushedColors, int pushedStyleVars = 0)
    {
        this.pushedColors = pushedColors;
        this.pushedStyleVars = pushedStyleVars;
    }

    public static UiTheme PushContrastIfEnabled(bool enabled)
    {
        if (!enabled)
            return new UiTheme(0);

        var count = 0;
        var pushedStyleVars = 0;

        // Contrast Mode:
        // - Avoids blue/white as the main readability pair.
        // - Uses a black base with neon yellow, cyan, green, and magenta accents.
        // - Makes text and hit targets a little larger inside Venue Host windows.
        // - Keeps controls dark with neon outlines so buttons remain readable.
        var neonYellow = new Vector4(1.00f, 0.95f, 0.00f, 1.00f);
        var neonCyan = new Vector4(0.00f, 1.00f, 0.95f, 1.00f);
        var neonMagenta = new Vector4(1.00f, 0.05f, 0.85f, 1.00f);
        var neonGreen = new Vector4(0.10f, 1.00f, 0.25f, 1.00f);
        var neonMint = new Vector4(0.58f, 1.00f, 0.88f, 1.00f);
        var dimGold = new Vector4(0.68f, 0.62f, 0.00f, 0.70f);
        var black = new Vector4(0.00f, 0.00f, 0.00f, 1.00f);

        ImGui.SetWindowFontScale(1.15f);

        // Default text uses mint/cyan instead of yellow so long help blocks do not
        // become a wall of yellow. Yellow is reserved for structure, checks, and key accents.
        Push(ImGuiCol.Text, neonMint);
        Push(ImGuiCol.TextDisabled, new Vector4(0.36f, 0.70f, 0.64f, 1.00f));
        Push(ImGuiCol.WindowBg, new Vector4(0.00f, 0.00f, 0.00f, 0.985f));
        Push(ImGuiCol.ChildBg, new Vector4(0.00f, 0.00f, 0.00f, 0.96f));
        Push(ImGuiCol.PopupBg, black);

        // High-visibility structure. The thicker style vars make these borders
        // feel less like faint UI decoration and more like actual landmarks.
        // Frame/input borders use cyan, while separators/table structure stay yellow.
        // This keeps editable fields readable without making every grid line compete.
        Push(ImGuiCol.Border, neonCyan);
        Push(ImGuiCol.Separator, neonYellow);
        Push(ImGuiCol.SeparatorHovered, neonCyan);
        Push(ImGuiCol.SeparatorActive, neonMagenta);

        // Dark fields with neon text/focus. This keeps the background quiet while
        // making the typed values stand out.
        Push(ImGuiCol.FrameBg, black);
        Push(ImGuiCol.FrameBgHovered, new Vector4(0.03f, 0.10f, 0.08f, 1.00f));
        Push(ImGuiCol.FrameBgActive, new Vector4(0.08f, 0.04f, 0.09f, 1.00f));

        // Default buttons stay dark. Window-level button helpers add neon text and borders
        // per action type so controls remain high-contrast without bright solid fills.
        Push(ImGuiCol.Button, new Vector4(0.02f, 0.02f, 0.02f, 1.00f));
        Push(ImGuiCol.ButtonHovered, new Vector4(0.00f, 0.35f, 0.32f, 1.00f));
        Push(ImGuiCol.ButtonActive, new Vector4(0.50f, 0.00f, 0.42f, 1.00f));

        // Headers/selected states use magenta. Links/focus use cyan. Active
        // checkmarks remain neon yellow for quick scanning.
        Push(ImGuiCol.Header, new Vector4(0.34f, 0.00f, 0.28f, 1.00f));
        Push(ImGuiCol.HeaderHovered, new Vector4(0.62f, 0.00f, 0.52f, 1.00f));
        Push(ImGuiCol.HeaderActive, new Vector4(0.86f, 0.00f, 0.72f, 1.00f));

        Push(ImGuiCol.TableHeaderBg, new Vector4(0.08f, 0.00f, 0.07f, 1.00f));
        Push(ImGuiCol.TableBorderStrong, neonYellow);
        Push(ImGuiCol.TableBorderLight, dimGold);
        Push(ImGuiCol.TableRowBg, black);
        Push(ImGuiCol.TableRowBgAlt, new Vector4(0.025f, 0.025f, 0.025f, 1.00f));

        Push(ImGuiCol.CheckMark, neonYellow);
        Push(ImGuiCol.SliderGrab, neonCyan);
        Push(ImGuiCol.SliderGrabActive, neonMagenta);

        Push(ImGuiCol.Tab, black);
        Push(ImGuiCol.TabHovered, new Vector4(0.80f, 0.00f, 0.66f, 1.00f));
        Push(ImGuiCol.TabActive, new Vector4(0.55f, 0.00f, 0.45f, 1.00f));

        Push(ImGuiCol.TitleBg, black);
        Push(ImGuiCol.TitleBgActive, new Vector4(0.18f, 0.00f, 0.13f, 1.00f));

        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 3.0f);
        pushedStyleVars++;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2.0f);
        pushedStyleVars++;
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2.5f);
        pushedStyleVars++;
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 3.0f);
        pushedStyleVars++;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8f, 6f));
        pushedStyleVars++;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f));
        pushedStyleVars++;

        return new UiTheme(count, pushedStyleVars);

        void Push(ImGuiCol color, Vector4 value)
        {
            ImGui.PushStyleColor(color, value);
            count++;
        }
    }

    public void Dispose()
    {
        if (this.pushedColors > 0)
            ImGui.PopStyleColor(this.pushedColors);

        if (this.pushedStyleVars > 0)
            ImGui.PopStyleVar(this.pushedStyleVars);

        // Reset after drawing the current Venue Host window. This keeps the
        // larger contrast text local to Venue Host instead of changing Dalamud globally.
        ImGui.SetWindowFontScale(1.0f);
    }
}
