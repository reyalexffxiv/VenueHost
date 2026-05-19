using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using VenueHost.Services;

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
    private const float SettingsWindowWidth = 940f;
    private const float SettingsWindowMinHeight = 720f;
    private const float MacroEditorHeight = 115f;
    private const float MacroEditorWidth = 760f;
    private const float MacroPreviewWidth = 760f;

    private readonly Configuration configuration;
    private readonly IServiceContext services;

    /// <summary>Shared plugin configuration used by this window.</summary>
    private Configuration Configuration => this.configuration;

    /// <summary>Shared service context used to resolve plugin services.</summary>
    private IServiceContext Services => this.services;

    /// <summary>UI helper service for cross-window actions and shared UI assets.</summary>
    private UiService Ui => this.Services.Get<UiService>();

    /// <summary>Macro preview helper service.</summary>
    private ShoutService Shout => this.Services.Get<ShoutService>();

    /// <summary>Automatic DJ shout scheduler and status provider.</summary>
    private DjAutoShoutService DjAutoShout => this.Services.Get<DjAutoShoutService>();

    /// <summary>Automatic staff role shout scheduler and status provider.</summary>
    private StaffAutoShoutService StaffAutoShout => this.Services.Get<StaffAutoShoutService>();
    private bool showVariables;
    private string newStaffRoleName = string.Empty;
    private string renameStaffRoleName = string.Empty;
    private string renameStaffRoleSource = string.Empty;

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
        "{StaffSchedule}",
        "{PhotographerNames}",
        "{Role}",
        "{StaffNames}",
        "{StaffCount}",
        "{IsAre}",
    ];

    public ConfigWindow(Configuration configuration, IServiceContext services)
        : base("Venue Host Settings###VenueHostSettings")
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        // Keep the settings layout stable horizontally while still allowing
        // vertical expansion for longer macro and role configuration panels.
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(SettingsWindowWidth, SettingsWindowMinHeight),
            MaximumSize = new Vector2(SettingsWindowWidth, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// Keeps Settings at the designed width and minimum height.
    /// Dalamud/ImGui may restore an older saved size before constraints apply,
    /// so enforce the final visual contract at draw time without blocking taller layouts.
    /// </summary>
    private void EnforceSettingsWindowSize()
    {
        var currentSize = ImGui.GetWindowSize();
        var targetHeight = Math.Max(currentSize.Y, SettingsWindowMinHeight);

        if (Math.Abs(currentSize.X - SettingsWindowWidth) <= 0.5f &&
            Math.Abs(currentSize.Y - targetHeight) <= 0.5f)
        {
            return;
        }

        ImGui.SetWindowSize(new Vector2(SettingsWindowWidth, targetHeight));
    }

    /// <summary>Refreshes automatic shout timers after schedule or settings edits.</summary>
    private void RefreshAutoShoutSchedule()
    {
        this.DjAutoShout.Refresh();
        this.StaffAutoShout.Refresh();
    }

    public override void Draw()
    {
        this.EnforceSettingsWindowSize();

        using var contrast = UiTheme.PushContrastIfEnabled(this.Configuration.ContrastModeEnabled);

        // Keep Settings as separate tabs instead of side-by-side panels.
        // General is intentionally drawn first. The tab bar ID includes SetupFirst so old ImGui tab-order state from earlier beta builds cannot keep Macros first.
        if (ImGui.BeginTabBar("VenueHostSettingsTabsGeneralFirst"))
        {
            if (ImGui.BeginTabItem("General"))
            {
                this.DrawSetupPanel();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("DJ Lineup"))
            {
                this.DrawMacrosPanel();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Staff Schedule"))
            {
                this.DrawStaffSchedulePanel();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawSetupPanel()
    {
        var config = this.Configuration;

        ImGui.TextUnformatted("Display");
        ImGui.Separator();

        var contrastMode = config.ContrastModeEnabled;
        if (ImGui.Checkbox("Contrast Mode", ref contrastMode))
        {
            config.ContrastModeEnabled = contrastMode;
            config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses darker panels, brighter accents, and slightly larger text across Venue Host windows.");

        ImGui.TextDisabled("Applies to Setup, Macros, DJ Lineup, and DJ Database windows.");
        ImGui.TextDisabled("Uses neon-style accents and slightly larger text for stronger readability.");

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
            this.Ui.ToggleMainUi();
    }

    private void DrawMacrosPanel()
    {
        var config = this.Configuration;

        ImGui.TextUnformatted("DJ Auto Shout");
        ImGui.Separator();
        ImGui.TextWrapped("Uses the DJ lineup schedule as FFXIV server time, which is UTC. Shouts are aligned to each DJ slot start, not to the moment you enable the option.");

        var autoEnabled = config.AutoCurrentDjShoutEnabled;
        if (ImGui.Checkbox("Enable automatic DJ shouts", ref autoEnabled))
        {
            config.AutoCurrentDjShoutEnabled = autoEnabled;
            config.Save();
            this.RefreshAutoShoutSchedule();
        }

        var intervalMinutes = config.AutoCurrentDjShoutIntervalMinutes;
        if (this.DrawIntStepper("DJ auto-shout interval", ref intervalMinutes, 1, 120, 1, "min"))
        {
            config.AutoCurrentDjShoutIntervalMinutes = intervalMinutes;
            config.ClampAutoShoutSettings();
            config.Save();
            this.RefreshAutoShoutSchedule();
        }

        ImGui.TextDisabled("Controls how often the scheduled current DJ is announced inside their slot.");
        ImGui.TextWrapped($"Auto-shout status: {this.DjAutoShout.Status}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("DJ Lineup Macros");
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


    private void DrawStaffSchedulePanel()
    {
        var config = this.Configuration;
        config.EnsureStaffRoleShoutSettings();

        if (string.IsNullOrWhiteSpace(config.SelectedStaffRole))
            config.SelectedStaffRole = config.GetKnownStaffRoles().FirstOrDefault() ?? "Photographer";

        var selectedRole = config.SelectedStaffRole;
        var roleSetting = config.GetOrCreateStaffRoleShoutSetting(selectedRole);

        ImGui.TextUnformatted("Staff Schedule");
        ImGui.Separator();
        ImGui.TextWrapped("Create roles, tune role auto-shouts, and edit the shout macro used for each staff role.");

        this.DrawStaffRoleManager(config);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Role Auto Shouts");
        ImGui.Separator();

        var staggerEnabled = config.AutoStaffRoleShoutStaggerEnabled;
        if (ImGui.Checkbox("Stagger / queue automatic role shouts", ref staggerEnabled))
        {
            config.AutoStaffRoleShoutStaggerEnabled = staggerEnabled;
            config.Save();
            this.RefreshAutoShoutSchedule();
        }

        var staggerMinutes = config.AutoStaffRoleShoutStaggerMinutes;
        if (this.DrawIntStepper("Stagger delay##StaffRoleStaggerDelay", ref staggerMinutes, 1, 30, 1, "min"))
        {
            config.AutoStaffRoleShoutStaggerMinutes = staggerMinutes;
            config.Save();
            this.RefreshAutoShoutSchedule();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Role overview");
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(4f, 2f));
        if (ImGui.BeginTable(
                "StaffRoleAutoSummary",
                3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoHostExtendX))
        {
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("Auto", ImGuiTableColumnFlags.WidthFixed, 55f);
            ImGui.TableSetupColumn("Interval", ImGuiTableColumnFlags.WidthFixed, 75f);
            ImGui.TableHeadersRow();
            foreach (var setting in config.GetKnownStaffRoles().Select(config.GetOrCreateStaffRoleShoutSetting))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable(setting.Role, string.Equals(config.SelectedStaffRole, setting.Role, StringComparison.OrdinalIgnoreCase), ImGuiSelectableFlags.SpanAllColumns))
                {
                    config.SelectedStaffRole = setting.Role;
                    config.Save();
                }
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(setting.AutoEnabled ? "ON" : "OFF");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{setting.IntervalMinutes} min");
            }
            ImGui.EndTable();
        }
        ImGui.PopStyleVar();

        ImGui.Spacing();
        ImGui.TextUnformatted("Edit selected role");

        ImGui.SetNextItemWidth(180f);
        if (ImGui.BeginCombo("Role##StaffAutoRole", config.SelectedStaffRole))
        {
            foreach (var role in config.GetKnownStaffRoles())
            {
                var selected = string.Equals(config.SelectedStaffRole, role, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(role, selected))
                {
                    config.SelectedStaffRole = role;
                    config.GetOrCreateStaffRoleShoutSetting(role);
                    config.Save();
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        selectedRole = config.SelectedStaffRole;
        roleSetting = config.GetOrCreateStaffRoleShoutSetting(selectedRole);

        var autoEnabled = roleSetting.AutoEnabled;
        if (ImGui.Checkbox($"Enable automatic {roleSetting.Role} shouts", ref autoEnabled))
        {
            roleSetting.AutoEnabled = autoEnabled;
            // Keep the dedicated Photographer compatibility fields aligned with role settings.
            if (roleSetting.Role.Equals("Photographer", StringComparison.OrdinalIgnoreCase))
                config.AutoPhotographerShoutEnabled = autoEnabled;
            config.Save();
            this.RefreshAutoShoutSchedule();
        }

        var intervalMinutes = roleSetting.IntervalMinutes;
        if (this.DrawIntStepper($"Interval##{roleSetting.Role}StaffAutoInterval", ref intervalMinutes, 1, 120, 1, "min"))
        {
            roleSetting.IntervalMinutes = intervalMinutes;
            if (roleSetting.Role.Equals("Photographer", StringComparison.OrdinalIgnoreCase))
                config.AutoPhotographerShoutIntervalMinutes = intervalMinutes;
            config.Save();
            this.RefreshAutoShoutSchedule();
        }

        var next = this.StaffAutoShout.GetNextShoutAtUtc(roleSetting.Role);
        ImGui.TextDisabled(next.HasValue ? $"Next shout: {next.Value:HH:mm:ss} ST / UTC" : "Next shout: none");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted($"Role Shout Macro: {config.SelectedStaffRole}");
        ImGui.SameLine();
        if (ImGui.Button($"Reset {config.SelectedStaffRole} Macro##ResetStaffRoleMacro", new Vector2(180, 24)))
        {
            config.SetStaffShoutMacro(config.SelectedStaffRole, config.GetDefaultStaffShoutMacro(config.SelectedStaffRole));
            config.SetStaffShoutDelaySeconds(config.SelectedStaffRole, 2f);
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Show Variables##StaffRoleVariables", new Vector2(130, 24)))
            this.showVariables = !this.showVariables;

        this.DrawMacroEditor(
            $"##StaffRoleMacroEditorTitle{config.SelectedStaffRole}",
            config.GetStaffShoutMacro(config.SelectedStaffRole),
            value => config.SetStaffShoutMacro(config.SelectedStaffRole, value),
            config.GetStaffShoutDelaySeconds(config.SelectedStaffRole),
            value => config.SetStaffShoutDelaySeconds(config.SelectedStaffRole, value),
            "##StaffRoleShoutMacro");

        ImGui.TextDisabled("Available variables: {VenueName}, {EventName}, {Role}, {StaffNames}, {StaffCount}, {IsAre}");
        if (this.showVariables)
            this.DrawVariablesHelp();

        ImGui.Spacing();
        ImGui.TextUnformatted($"Preview using current active {config.SelectedStaffRole} staff");
        var previewWidth = MathF.Min(ImGui.GetContentRegionAvail().X, MacroPreviewWidth);
        ImGui.BeginChild("StaffRoleMacroPreview", new Vector2(previewWidth, 88), true, ImGuiWindowFlags.None);
        ImGui.TextWrapped(this.Shout.PreviewSelectedStaffRoleShout());
        ImGui.EndChild();
    }


    private void DrawStaffRoleManager(Configuration config)
    {
        config.EnsureStaffRoles();

        ImGui.TextUnformatted("Staff Roles");
        ImGui.Separator();
        ImGui.TextWrapped("Create, rename, or remove roles used by Staff Database, Staff Schedule rows, and role shout macros.");

        ImGui.TextUnformatted("New role");
        ImGui.SetNextItemWidth(220f);
        ImGui.InputText("##NewStaffRole", ref this.newStaffRoleName, 80);
        ImGui.SameLine();
        if (ImGui.Button("Add Role", new Vector2(110, 24)))
        {
            if (config.AddStaffRole(this.newStaffRoleName))
            {
                config.SelectedStaffRole = this.newStaffRoleName.Trim();
                this.newStaffRoleName = string.Empty;
                this.RefreshAutoShoutSchedule();
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Selected role");

        if (string.IsNullOrWhiteSpace(config.SelectedStaffRole))
            config.SelectedStaffRole = config.GetKnownStaffRoles().FirstOrDefault() ?? "Photographer";

        ImGui.SetNextItemWidth(180f);
        if (ImGui.BeginCombo("##SelectedStaffRoleForRename", config.SelectedStaffRole))
        {
            foreach (var role in config.GetKnownStaffRoles())
            {
                var selected = string.Equals(config.SelectedStaffRole, role, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(role, selected))
                {
                    config.SelectedStaffRole = role;
                    config.Save();
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        if (!string.Equals(this.renameStaffRoleSource, config.SelectedStaffRole, StringComparison.Ordinal))
        {
            this.renameStaffRoleSource = config.SelectedStaffRole;
            this.renameStaffRoleName = config.SelectedStaffRole;
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("Rename to");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f);
        ImGui.InputText("##RenameStaffRole", ref this.renameStaffRoleName, 80);
        ImGui.SameLine();
        if (ImGui.Button("Save Rename", new Vector2(125, 24)))
        {
            if (config.RenameStaffRole(this.renameStaffRoleSource, this.renameStaffRoleName))
            {
                this.renameStaffRoleSource = config.SelectedStaffRole;
                this.renameStaffRoleName = config.SelectedStaffRole;
                this.RefreshAutoShoutSchedule();
            }
        }

        ImGui.SameLine();
        var canRemove = config.GetKnownStaffRoles().Count > 1;
        if (!canRemove)
            ImGui.BeginDisabled();

        if (ImGui.Button("Remove Role", new Vector2(120, 24)))
        {
            if (config.RemoveStaffRole(config.SelectedStaffRole))
            {
                this.renameStaffRoleSource = config.SelectedStaffRole;
                this.renameStaffRoleName = config.SelectedStaffRole;
                this.RefreshAutoShoutSchedule();
            }
        }

        if (!canRemove)
            ImGui.EndDisabled();

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
        if (!title.StartsWith("##", StringComparison.Ordinal))
            ImGui.TextUnformatted(title);

        var editedDelay = delaySeconds;
        if (this.DrawFloatStepper($"Wait between lines##{inputId}", ref editedDelay, 0f, 10f, 0.5f, "sec"))
        {
            setDelaySeconds(editedDelay);
            this.Configuration.Save();
        }

        var editedMacro = macroText ?? string.Empty;
        var editorWidth = MathF.Min(ImGui.GetContentRegionAvail().X, MacroEditorWidth);
        ImGui.SetNextItemWidth(editorWidth);
        if (ImGui.InputTextMultiline(inputId, ref editedMacro, 8000, new Vector2(editorWidth, MacroEditorHeight)))
        {
            setMacroText(editedMacro);
            this.Configuration.Save();
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
