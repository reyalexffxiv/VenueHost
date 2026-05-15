using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace VenueHost.Windows;

/// <summary>
/// Settings and macro editor window.
///
/// This window intentionally keeps all configuration editing in one place.
/// Setup and Macros are separate tabs because setup is quick operational state,
/// while macros are larger text editors. Any setting changed here is saved
/// immediately through Dalamud's plugin config path.
/// </summary>
public sealed class ConfigWindow : Window, IDisposable
{
    private const float MacroEditorHeight = 115f;

    private readonly Plugin plugin;
    private bool showVariables;

    private static readonly string[] AvailableVariables =
    [
        "{CurrentDJName}",
        "{CurrentDJLink}",
        "{DJName}",
        "{DJLink}",
        "{NextDJName}",
        "{NextDJLink}",
        "{VenueName}",
        "{EventName}",
        "{GiveawayText}",
        "{GiveawayCommand}",
        "{CommandText}",
        "{GiveawayCommandLine}",
        "{GiveawayLine}",
        "{StartTime}",
        "{EndTime}",
    ];

    public ConfigWindow(Plugin plugin)
        : base("Venue Host Settings###VenueHostSettings")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(920, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        // Keep Settings as separate tabs instead of side-by-side panels.
        // Setup is intentionally drawn first. The tab bar ID includes SetupFirst so old ImGui tab-order state from earlier beta builds cannot keep Macros first.
        if (ImGui.BeginTabBar("VenueHostSettingsTabsSetupFirst"))
        {
            if (ImGui.BeginTabItem("Setup"))
            {
                this.DrawSetupPanel();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Macros"))
            {
                this.DrawMacrosPanel();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawSetupPanel()
    {
        var config = this.plugin.Configuration;

        ImGui.TextUnformatted("Auto Shout");
        ImGui.Separator();
        ImGui.TextWrapped("Uses the DJ lineup schedule as FFXIV server time, which is UTC. Shouts are aligned to each DJ slot start, not to the moment you enable the option.");

        var autoEnabled = config.AutoCurrentDjShoutEnabled;
        if (ImGui.Checkbox("Enable auto-shout", ref autoEnabled))
        {
            config.AutoCurrentDjShoutEnabled = autoEnabled;
            config.Save();
        }

        var intervalMinutes = config.AutoCurrentDjShoutIntervalMinutes;
        if (this.DrawIntStepper("Auto-shout interval", ref intervalMinutes, 1, 120, 1, "min"))
        {
            config.AutoCurrentDjShoutIntervalMinutes = intervalMinutes;
            config.ClampAutoShoutSettings();
            config.Save();
        }

        ImGui.TextDisabled("Controls how often the scheduled current DJ is announced inside their slot.");
        ImGui.Spacing();
        ImGui.TextWrapped($"Auto-shout status: {this.plugin.AutoShoutStatus}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Commands");
        ImGui.BulletText("/venuehost");
        ImGui.BulletText("/vhost");
        ImGui.BulletText("/venuehost settings");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Chat Sending");
        ImGui.TextWrapped("Venue Host sends queued native chat commands. Each macro line should usually be a complete FFXIV command like /y, /sh, /s, /em, or /tell.");
        ImGui.TextWrapped("Use the compact wait controls for normal timing. Manual <wait.N> lines are still supported as one-off overrides.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Rules / Notes");
        ImGui.BulletText("FFXIV server time is treated as UTC.");
        ImGui.BulletText("Blank Pick DJ rows are ignored by auto-shout and schedule checks.");
        ImGui.BulletText("Manual buttons use the selected lineup row.");
        ImGui.BulletText("Auto-shout uses the scheduled current DJ, not the manually selected row.");
        ImGui.BulletText("Transition shouts are sent at the next DJ start time when a previous DJ exists.");

        ImGui.Spacing();
        if (ImGui.Button("Open DJ Lineup", new Vector2(150, 24)))
            this.plugin.ToggleMainUi();
    }

    private void DrawMacrosPanel()
    {
        var config = this.plugin.Configuration;

        ImGui.TextUnformatted("Macros");
        ImGui.Separator();

        // Keep this guidance close to the editors. Macro editing is powerful,
        // but most venue staff should only need to paste one chat command per line.
        ImGui.TextWrapped("Edit the messages sent by DJ lineup buttons and auto-shouts. Use one full chat command per line, for example /y message or /sh message.");
        ImGui.TextWrapped("Wait controls delay between macro lines. You do not need to add <wait.N> manually, though manual wait lines are still supported as one-off overrides.");
        ImGui.TextWrapped("Variables in {curly braces} are replaced automatically when the macro runs.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Quick Actions");
        if (ImGui.Button("Reset Default Macros", new Vector2(170, 24)))
        {
            config.ResetDefaultMacros();
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Remove Old Wait Lines", new Vector2(180, 24)))
        {
            if (config.RemoveManualWaitLinesFromMacros())
                config.Save();
        }

        ImGui.SameLine();
        var variablesButtonText = this.showVariables ? "Hide Variables" : "Show Variables";
        if (ImGui.Button(variablesButtonText, new Vector2(130, 24)))
            this.showVariables = !this.showVariables;

        ImGui.TextDisabled("Remove Old Wait Lines removes standalone <wait.N> lines. The wait controls handle normal spacing now.");

        if (this.showVariables)
            this.DrawVariablesHelp();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawMacroEditor(
            "Shout Current DJ Macro",
            config.CurrentDjMacro,
            value => config.CurrentDjMacro = value,
            config.CurrentDjMacroDelaySeconds,
            value => config.CurrentDjMacroDelaySeconds = value,
            "##CurrentDjMacro");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawMacroEditor(
            "Thank Current / Welcome Next Macro",
            config.TransitionMacro,
            value => config.TransitionMacro = value,
            config.TransitionMacroDelaySeconds,
            value => config.TransitionMacroDelaySeconds = value,
            "##TransitionMacro");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawMacroEditor(
            "Tell DJ to Target Macro",
            config.TellCurrentDjMacro,
            value => config.TellCurrentDjMacro = value,
            config.TellCurrentDjMacroDelaySeconds,
            value => config.TellCurrentDjMacroDelaySeconds = value,
            "##TellCurrentDjMacro");

        ImGui.Dummy(new Vector2(1, 14));
    }

    private void DrawVariablesHelp()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Available variables");

        if (ImGui.BeginTable("##VariablesTable", 3, ImGuiTableFlags.SizingStretchSame))
        {
            for (var i = 0; i < AvailableVariables.Length; i++)
            {
                if (i % 3 == 0)
                    ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(i % 3);
                ImGui.TextUnformatted(AvailableVariables[i]);
            }

            ImGui.EndTable();
        }
    }

    private void DrawMacroEditor(
        string title,
        string macroText,
        Action<string> setMacroText,
        float delaySeconds,
        Action<float> setDelaySeconds,
        string inputId)
    {
        ImGui.TextUnformatted(title);

        var editedDelay = delaySeconds;
        if (this.DrawFloatStepper($"Wait between lines##{inputId}", ref editedDelay, 0f, 10f, 0.5f, "sec"))
        {
            setDelaySeconds(editedDelay);
            this.plugin.Configuration.Save();
        }

        var editedMacro = macroText ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextMultiline(inputId, ref editedMacro, 8000, new Vector2(-1, MacroEditorHeight)))
        {
            setMacroText(editedMacro);
            this.plugin.Configuration.Save();
        }
    }

    private static string StripImGuiId(string label)
    {
        var markerIndex = label.IndexOf("##", StringComparison.Ordinal);
        return markerIndex >= 0 ? label[..markerIndex] : label;
    }

    /// <summary>
    /// Draws a compact numeric stepper for integer settings.
    /// This avoids wide sliders for simple values like the auto-shout interval.
    /// </summary>
    private bool DrawIntStepper(string label, ref int value, int min, int max, int step, string suffix)
    {
        var changed = false;
        var inputValue = value;
        var displayLabel = StripImGuiId(label);

        ImGui.PushID(label);
        ImGui.SetNextItemWidth(70);
        if (ImGui.InputInt("##value", ref inputValue, 0, 0))
        {
            value = Math.Clamp(inputValue, min, max);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("-", new Vector2(28, 0)))
        {
            value = Math.Clamp(value - step, min, max);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("+", new Vector2(28, 0)))
        {
            value = Math.Clamp(value + step, min, max);
            changed = true;
        }

        ImGui.SameLine();
        ImGui.TextUnformatted($"{displayLabel} ({suffix})");
        ImGui.PopID();

        return changed;
    }

    /// <summary>
    /// Draws a compact numeric stepper for macro wait values.
    /// Macro waits intentionally use half-second steps because venue shouts
    /// rarely need fine-grained timing.
    /// </summary>
    private bool DrawFloatStepper(string label, ref float value, float min, float max, float step, string suffix)
    {
        var changed = false;
        var inputValue = value;
        var displayLabel = StripImGuiId(label);

        ImGui.PushID(label);
        ImGui.SetNextItemWidth(70);
        if (ImGui.InputFloat("##value", ref inputValue, 0f, 0f, "%.1f"))
        {
            value = Math.Clamp(inputValue, min, max);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("-", new Vector2(28, 0)))
        {
            value = Math.Clamp(value - step, min, max);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("+", new Vector2(28, 0)))
        {
            value = Math.Clamp(value + step, min, max);
            changed = true;
        }

        ImGui.SameLine();
        ImGui.TextUnformatted($"{displayLabel} ({suffix})");
        ImGui.PopID();

        return changed;
    }
}
