using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using VenueHost.Services;

namespace VenueHost.Windows;

/// <summary>
/// Modern Setup: independent venue profiles, portable transfer, staff roles,
/// global staff stagger, and global accessibility controls.
/// Editing individual announcement macros belongs in Shouts.
/// </summary>
public sealed class ConfigWindow : Window, IDisposable
{
    private static readonly Vector2 WindowDimensions = new(850f, 748f);
    private readonly Configuration config;
    private readonly IServiceContext services;
    private string selectedProfileId = string.Empty;
    private string newProfileName = string.Empty;
    private string renameProfileName = string.Empty;
    private string newRoleName = string.Empty;
    private string renameRoleName = string.Empty;
    private string lastRole = string.Empty;
    private string status = string.Empty;
    private bool statusIsError;
    private enum ConfirmationView { None, ActivateVenue, DeleteVenue, RemoveRole, ImportVenue }
    private ConfirmationView confirmation = ConfirmationView.None;
    private bool confirmationFocusRequested;
    private string confirmationProfileId = string.Empty;
    private string confirmationProfileName = string.Empty;
    private bool showImportPreview;
    private string requestedRoleRemoval = string.Empty;
    private PendingFileDialog? pendingFileDialog;
    private bool fileDialogIsImport;
    private string? exportingProfileId;
    private string? importedFilePath;
    private ProfileImportPreview? importPreview;
    private bool updateImportedContacts;
    private bool showShoutPriority;
    private bool priorityFocusRequested;

    private VenueProfileService Profiles => this.services.Get<VenueProfileService>();
    private StaffAutoShoutService StaffAuto => this.services.Get<StaffAutoShoutService>();
    private FileDialogService FileDialogs => this.services.Get<FileDialogService>();

    public ConfigWindow(Configuration configuration, IServiceContext services)
        : base("VenueHost Setup###VenueHostSettings", ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground)
    {
        this.config = configuration;
        this.services = services;
        this.Size = WindowDimensions;
        this.SizeCondition = ImGuiCond.Always;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = WindowDimensions, MaximumSize = WindowDimensions,
        };
        this.AllowPinning = false;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        this.Size = WindowDimensions;
        this.SizeCondition = ImGuiCond.Always;
        ImGui.SetNextWindowPos(BackstageDesign.CenteredOverMainWindow(this.services.Get<UiService>(), WindowDimensions), ImGuiCond.Appearing);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, BackstageDesign.Canvas);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, BackstageDesign.Surface);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, BackstageDesign.Raised);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.25f, 0.24f, 0.26f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button, BackstageDesign.Raised);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.24f, 0.26f, 1f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, BackstageDesign.Mint);
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.26f, 0.21f, 0.19f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, BackstageDesign.Raised);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 14f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 7f));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(6);
        ImGui.PopStyleColor(9);
    }

    public override void Draw()
    {
        using var palette = BackstageDesign.PushHighContrast(this.config.OverviewHighContrastPreviewEnabled);
        using var contrast = UiTheme.PushContrastIfEnabled(this.config.OverviewHighContrastPreviewEnabled, preserveLayout: true);
        this.PollDialog();
        if (string.IsNullOrWhiteSpace(this.selectedProfileId) ||
            !this.Profiles.Profiles.Any(p => p.Id == this.selectedProfileId))
        {
            this.selectedProfileId = this.Profiles.ActiveId;
            this.renameProfileName = this.Profiles.Active.Name;
        }

        var origin = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var draw = ImGui.GetWindowDrawList();
        BackstageDesign.Fill(draw, origin, size, BackstageDesign.Canvas, 14f);
        BackstageDesign.Fill(draw, origin, new Vector2(size.X, 66f), BackstageDesign.Rail, 14f);
        BackstageDesign.Fill(draw, origin + new Vector2(0f, 30f),
            new Vector2(size.X, 36f), BackstageDesign.Rail);
        BackstageDesign.Rule(draw, origin + new Vector2(0f, 65f),
            origin + new Vector2(size.X, 65f), BackstageDesign.SoftLine);
        draw.AddText(origin + new Vector2(22f, 24f),
            BackstageDesign.Color(BackstageDesign.Gold), "VENUEHOST SETUP");
        if (BackstageDesign.FlatButton("##vhSetupClose", "CLOSE",
                origin + new Vector2(size.X - 100f, 16f), new Vector2(80f, 33f),
                BackstageDesign.Muted))
        {
            this.CloseConfirmation();
            this.showShoutPriority = false;
            this.IsOpen = false;
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(240f, 0f));
        ImGui.InvisibleButton("##vhSetupDrag", new Vector2(size.X - 370f, 65f));
        if (ImGui.IsItemActive())
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta, ImGuiCond.Always);

        BackstageDesign.SurfacePanel(origin + new Vector2(18f, 80f), new Vector2(size.X - 36f, 280f));
        BackstageDesign.SurfacePanel(origin + new Vector2(18f, 370f), new Vector2(size.X - 36f, 145f));
        // Two compact independent controls at the bottom: venue-specific staff
        // queue settings and the user-wide High Contrast display.
        BackstageDesign.SurfacePanel(origin + new Vector2(18f, 525f), new Vector2(548f, 91f));
        BackstageDesign.SurfacePanel(origin + new Vector2(578f, 525f), new Vector2(254f, 91f));
        this.DrawProfiles(origin);
        this.DrawRoles(origin);
        this.DrawQueue(origin);
        this.DrawAccessibility(origin);
        BackstageDesign.SurfacePanel(origin + new Vector2(18f, 625f), new Vector2(size.X - 36f, 91f));
        this.DrawAnnouncementControls(origin);

        if (!string.IsNullOrWhiteSpace(this.status))
        {
            var message = this.status.Length > 118 ? this.status[..115] + "..." : this.status;
            draw.AddText(origin + new Vector2(22f, 729f),
                BackstageDesign.Color(this.statusIsError ? BackstageDesign.Rose : BackstageDesign.Mint), message);
        }
        this.DrawConfirmations();
        this.DrawShoutPriorityWindow(origin, size);
    }

    private void DrawAnnouncementControls(Vector2 p)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(p + new Vector2(36f, 641f), BackstageDesign.Color(BackstageDesign.Gold),
            "ANNOUNCEMENT CONTROLS");
        if (BackstageDesign.FlatButton("##vhSetupPriority", "SHOUT PRIORITY",
                p + new Vector2(36f, 668f), new Vector2(161f, 32f), BackstageDesign.Gold))
        {
            this.showShoutPriority = true;
            this.priorityFocusRequested = true;
        }
        var smart = this.config.SmartChatChannelsEnabled;
        if (BackstageDesign.FlatButton("##vhSetupSmartChannels",
                smart ? "SMART CHANNELS ON" : "SMART CHANNELS OFF",
                p + new Vector2(207f, 668f), new Vector2(198f, 32f),
                smart ? BackstageDesign.Mint : BackstageDesign.Muted, smart))
        {
            this.config.SmartChatChannelsEnabled = !smart;
            this.config.Save();
        }
    }

    private void DrawProfiles(Vector2 p)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(p + new Vector2(36f, 97f), BackstageDesign.Color(BackstageDesign.Gold), "VENUE PROFILES");
        BackstageDesign.Rule(draw, p + new Vector2(34f, 119f), p + new Vector2(817f, 119f), BackstageDesign.SoftLine);

        ImGui.SetCursorScreenPos(p + new Vector2(35f, 129f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        if (ImGui.BeginChild("##vhProfilesDirectory", new Vector2(273f, 158f), false))
        {
            foreach (var profile in this.Profiles.Profiles)
            {
                ImGui.PushID(profile.Id);
                var row = ImGui.GetCursorScreenPos();
                var width = 253f;
                var selected = profile.Id == this.selectedProfileId;
                var active = profile.Id == this.Profiles.ActiveId;
                var clicked = ImGui.InvisibleButton("##profilePick", new Vector2(width, 42f));
                var hovered = ImGui.IsItemHovered();
                var color = selected ? new Vector4(0.265f, 0.222f, 0.205f, 1f)
                    : hovered ? BackstageDesign.Raised : BackstageDesign.Canvas;
                BackstageDesign.Fill(ImGui.GetWindowDrawList(), row, new Vector2(width, 40f), color, 7f);
                if (selected)
                    BackstageDesign.Fill(ImGui.GetWindowDrawList(), row, new Vector2(3f, 40f), BackstageDesign.Gold, 2f);
                var label = profile.Name.Length > 23 ? profile.Name[..22] + "..." : profile.Name;
                ImGui.GetWindowDrawList().AddText(row + new Vector2(12f, 12f),
                    BackstageDesign.Color(selected ? BackstageDesign.Ivory : BackstageDesign.Muted), label);
                if (active)
                {
                    var marker = "ACTIVE";
                    var measure = ImGui.CalcTextSize(marker);
                    ImGui.GetWindowDrawList().AddText(row + new Vector2(width - measure.X - 10f, 13f),
                        BackstageDesign.Color(BackstageDesign.Mint), marker);
                }
                if (clicked)
                {
                    this.selectedProfileId = profile.Id;
                    this.renameProfileName = profile.Name;
                }
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4f);
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        InputAt("##vhNewProfile", "New venue name", ref this.newProfileName, 80,
            p + new Vector2(36f, 300f), 174f);
        if (BackstageDesign.FlatButton("##vhNewProfileButton", "+ CREATE", p + new Vector2(219f, 299f),
                new Vector2(89f, 33f), BackstageDesign.Mint))
            this.Try(() =>
            {
                var created = this.Profiles.CreateEmpty(this.newProfileName);
                this.newProfileName = string.Empty;
                this.selectedProfileId = created.Id;
                this.renameProfileName = created.Name;
                return "New venue created. Select ACTIVATE to use it.";
            });

        var isActive = this.selectedProfileId == this.Profiles.ActiveId;
        draw.AddText(p + new Vector2(335f, 139f), BackstageDesign.Color(BackstageDesign.Dim),
            "RENAME SELECTED VENUE");
        InputAt("##vhRenameProfile", "New venue name", ref this.renameProfileName, 80,
            p + new Vector2(335f, 163f), 314f);
        if (BackstageDesign.FlatButton("##vhRenameProfileButton", "RENAME", p + new Vector2(661f, 162f),
                new Vector2(151f, 34f), BackstageDesign.Mist))
            this.Try(() => { this.Profiles.Rename(this.selectedProfileId, this.renameProfileName); return "Venue profile renamed."; });

        if (!isActive)
        {
            if (BackstageDesign.FlatButton("##vhActivateProfile", "ACTIVATE SELECTED", p + new Vector2(335f, 236f),
                    new Vector2(227f, 35f), BackstageDesign.Gold))
                this.OpenConfirmation(ConfirmationView.ActivateVenue);
            if (BackstageDesign.FlatButton("##vhDeleteProfile", "DELETE SELECTED", p + new Vector2(574f, 236f),
                    new Vector2(238f, 35f), BackstageDesign.Rose))
                this.OpenConfirmation(ConfirmationView.DeleteVenue);
        }

        if (BackstageDesign.FlatButton("##vhExportSelected", "EXPORT SELECTED", p + new Vector2(335f, 296f),
                new Vector2(227f, 35f), BackstageDesign.Mist))
            this.StartExport();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Exports the selected venue and the complete shared DJ and Staff directories.");
        if (BackstageDesign.FlatButton("##vhImportVenue", "IMPORT VENUE", p + new Vector2(574f, 296f),
                new Vector2(238f, 35f), BackstageDesign.Mint))
        {
            this.pendingFileDialog = this.FileDialogs.ShowOpenProfileDialog("Import VenueHost venue profile");
            this.fileDialogIsImport = true;
        }
    }

    private void DrawRoles(Vector2 p)
    {
        this.config.EnsureStaffRoles();
        this.config.EnsureStaffRoleShoutSettings();
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(p + new Vector2(36f, 386f), BackstageDesign.Color(BackstageDesign.Gold), "STAFF ROLES");
        BackstageDesign.Rule(draw, p + new Vector2(35f, 408f), p + new Vector2(817f, 408f), BackstageDesign.SoftLine);

        InputAt("##vhNewRole", "New role name", ref this.newRoleName, 80, p + new Vector2(36f, 420f), 260f);
        if (BackstageDesign.FlatButton("##vhAddRole", "+ ADD ROLE", p + new Vector2(305f, 419f),
                new Vector2(130f, 34f), BackstageDesign.Mint))
            this.Try(() =>
            {
                if (!this.config.AddStaffRole(this.newRoleName)) throw new InvalidOperationException("Role already exists or is invalid.");
                this.config.SelectedStaffRole = this.newRoleName.Trim();
                this.newRoleName = string.Empty;
                this.StaffAuto.Refresh();
                return "Role added to this venue.";
            });

        var roles = this.config.GetKnownStaffRoles();
        if (string.IsNullOrWhiteSpace(this.config.SelectedStaffRole) || !roles.Contains(this.config.SelectedStaffRole))
            this.config.SelectedStaffRole = roles.FirstOrDefault() ?? "Photographer";
        if (this.lastRole != this.config.SelectedStaffRole)
        {
            this.lastRole = this.config.SelectedStaffRole;
            this.renameRoleName = string.Empty;
        }
        if (BackstageDesign.RoleSelector("##vhSetupStaffRole", this.config.SelectedStaffRole,
                roles, p + new Vector2(36f, 465f), new Vector2(190f, 33f), out var chosenRole))
        {
            this.config.SelectedStaffRole = chosenRole;
            this.config.Save();
        }
        InputAt("##vhRenameRole", "New name for selected role", ref this.renameRoleName, 80,
            p + new Vector2(238f, 465f), 245f);
        if (BackstageDesign.FlatButton("##vhRenameRoleBtn", "RENAME", p + new Vector2(494f, 464f),
                new Vector2(146f, 34f), BackstageDesign.Mist))
            this.Try(() =>
            {
                if (!this.config.RenameStaffRole(this.config.SelectedStaffRole, this.renameRoleName))
                    throw new InvalidOperationException("Role rename was rejected. Check for duplicate names.");
                this.lastRole = this.config.SelectedStaffRole;
                this.renameRoleName = string.Empty;
                this.StaffAuto.Refresh();
                return "Role renamed for this venue.";
            });
        if (roles.Count > 1 && BackstageDesign.FlatButton("##vhRemoveRoleBtn", "REMOVE ROLE", p + new Vector2(650f, 464f),
                new Vector2(162f, 34f), BackstageDesign.Rose))
        {
            this.requestedRoleRemoval = this.config.SelectedStaffRole;
            this.OpenConfirmation(ConfirmationView.RemoveRole);
        }
    }

    private void DrawQueue(Vector2 p)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(p + new Vector2(36f, 541f), BackstageDesign.Color(BackstageDesign.Gold), "AUTOMATIC SHOUT STAGGER");
        var enabled = this.config.AutoStaffRoleShoutStaggerEnabled;
        if (BackstageDesign.FlatButton("##vhStaggerToggle", enabled ? "ON" : "OFF",
                p + new Vector2(36f, 567f), new Vector2(68f, 31f), enabled ? BackstageDesign.Mint : BackstageDesign.Muted, enabled))
        {
            this.config.AutoStaffRoleShoutStaggerEnabled = !enabled;
            this.config.Save();
            this.StaffAuto.Refresh();
        }
        draw.AddText(p + new Vector2(136f, 574f), BackstageDesign.Color(BackstageDesign.Dim), "DELAY");
        var delay = this.config.AutoStaffRoleShoutStaggerMinutes;
        if (BackstageDesign.FlatButton("##vhDelayLess", "-", p + new Vector2(183f, 567f),
                new Vector2(31f, 32f), BackstageDesign.Mist) && delay > 1)
        {
            this.config.AutoStaffRoleShoutStaggerMinutes = delay - 1;
            this.config.Save();
            this.StaffAuto.Refresh();
        }
        var delayText = $"{delay} min";
        draw.AddText(p + new Vector2(221f, 574f), BackstageDesign.Color(BackstageDesign.Ivory), delayText);
        if (BackstageDesign.FlatButton("##vhDelayMore", "+", p + new Vector2(287f, 567f),
                new Vector2(34f, 32f), BackstageDesign.Mist) && delay < 30)
        {
            this.config.AutoStaffRoleShoutStaggerMinutes = delay + 1;
            this.config.Save();
            this.StaffAuto.Refresh();
        }
    }

    private void DrawAccessibility(Vector2 p)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(p + new Vector2(596f, 541f), BackstageDesign.Color(BackstageDesign.Gold), "ACCESSIBILITY");

        // One user-wide High Contrast option, applied across VenueHost.
        // Venue profile switches and portable event exports exclude this setting.
        var enabled = this.config.OverviewHighContrastPreviewEnabled;
        if (BackstageDesign.FlatButton("##vhOverviewContrast",
                $"HIGH CONTRAST: {(enabled ? "ON" : "OFF")}",
                p + new Vector2(596f, 567f), new Vector2(216f, 32f),
                enabled ? BackstageDesign.Mint : BackstageDesign.Muted, enabled))
        {
            this.config.OverviewHighContrastPreviewEnabled = !enabled;
            this.config.Save();
        }
    }

    private void DrawShoutPriorityWindow(Vector2 ownerPosition, Vector2 ownerSize)
    {
        if (!this.showShoutPriority) return;
        var config = this.config;
        config.EnsureShoutPriority();
        var options = config.GetPriorityOptions().ToDictionary(x => x.Key, x => x.Label,
            StringComparer.OrdinalIgnoreCase);
        var rows = config.ShoutPriority.ToList();
        const float width = 545f;
        var height = MathF.Min(610f, 88f + rows.Count * 43f);
        height = MathF.Max(210f, height);
        var size = new Vector2(width, height);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowPos(BackstageDesign.CenteredWindowPosition(ownerPosition, ownerSize, size), ImGuiCond.Appearing);
        if (this.priorityFocusRequested)
        {
            ImGui.SetNextWindowFocus();
            this.priorityFocusRequested = false;
        }
        ImGui.PushStyleColor(ImGuiCol.WindowBg, BackstageDesign.Canvas);
        ImGui.PushStyleColor(ImGuiCol.Border, BackstageDesign.SoftLine);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 13f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        if (ImGui.Begin("Shout Priority##vhPriorityWindow",
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var p = ImGui.GetWindowPos();
            var dl = ImGui.GetWindowDrawList();
            BackstageDesign.Fill(dl, p, new Vector2(width, 58f), BackstageDesign.Rail, 12f);
            BackstageDesign.Rule(dl, p + new Vector2(0f, 57f), p + new Vector2(width, 57f), BackstageDesign.SoftLine);
            ImGui.SetCursorScreenPos(p + new Vector2(20f, 19f));
            ImGui.TextColored(BackstageDesign.Gold, "SHOUT PRIORITY");
            if (BackstageDesign.FlatButton("##vhPriorityClose", "CLOSE", p + new Vector2(width - 109f, 13f), new Vector2(90f, 30f), BackstageDesign.Muted))
                this.showShoutPriority = false;
            ImGui.SetCursorScreenPos(p + new Vector2(175f, 0f));
            ImGui.InvisibleButton("##vhPriorityDrag", new Vector2(width - 300f, 57f));
            if (ImGui.IsItemActive())
                ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);

            ImGui.SetCursorScreenPos(p + new Vector2(18f, 69f));
            if (ImGui.BeginChild("##vhPriorityRows", new Vector2(width - 36f, height - 83f), false))
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    ImGui.PushID(i);
                    var key = rows[i];
                    var row = ImGui.GetCursorScreenPos();
                    var rowWidth = ImGui.GetContentRegionAvail().X - 5f;
                    BackstageDesign.Fill(ImGui.GetWindowDrawList(), row,
                        new Vector2(rowWidth, 36f), BackstageDesign.Raised, 7f);
                    var label = options.TryGetValue(key, out var displayName) ? displayName : key;
                    ImGui.PushClipRect(row + new Vector2(12f, 2f),
                        row + new Vector2(rowWidth - 110f, 34f), true);
                    ImGui.GetWindowDrawList().AddText(row + new Vector2(12f, 10f),
                        BackstageDesign.Color(BackstageDesign.Ivory), $"{i + 1}.  {label}");
                    ImGui.PopClipRect();
                    if (i == 0) ImGui.BeginDisabled();
                    if (BackstageDesign.FlatButton("##vhPriorityUp", "UP", row + new Vector2(rowWidth - 94f, 4f), new Vector2(41f, 28f), BackstageDesign.Muted) && i > 0)
                    {
                        (config.ShoutPriority[i - 1], config.ShoutPriority[i]) =
                            (config.ShoutPriority[i], config.ShoutPriority[i - 1]);
                        config.Save();
                    }
                    if (i == 0) ImGui.EndDisabled();
                    if (i == rows.Count - 1) ImGui.BeginDisabled();
                    if (BackstageDesign.FlatButton("##vhPriorityDown", "DOWN", row + new Vector2(rowWidth - 48f, 4f), new Vector2(44f, 28f), BackstageDesign.Muted) && i < rows.Count - 1)
                    {
                        (config.ShoutPriority[i + 1], config.ShoutPriority[i]) =
                            (config.ShoutPriority[i], config.ShoutPriority[i + 1]);
                        config.Save();
                    }
                    if (i == rows.Count - 1) ImGui.EndDisabled();
                    ImGui.SetCursorScreenPos(row + new Vector2(0f, 42f));
                    ImGui.Dummy(new Vector2(1f, 1f));
                    ImGui.PopID();
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }

    private static void InputAt(string id, string hint, ref string value, int maxLength, Vector2 position, float width)
    {
        ImGui.SetCursorScreenPos(position);
        ImGui.SetNextItemWidth(width);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f, 9f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, BackstageDesign.Raised);
        ImGui.InputTextWithHint(id, hint, ref value, maxLength);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void OpenConfirmation(ConfirmationView view)
    {
        this.confirmation = view;
        this.confirmationFocusRequested = true;
        if (view == ConfirmationView.ActivateVenue || view == ConfirmationView.DeleteVenue)
        {
            // Keep a stable target even if the user changes the selected row while the
            // non-modal confirmation is visible.
            this.confirmationProfileId = this.selectedProfileId;
            this.confirmationProfileName = this.Profiles.Profiles
                .First(p => p.Id == this.selectedProfileId).Name;
        }
    }

    private void CloseConfirmation()
    {
        if (this.confirmation == ConfirmationView.ImportVenue)
            this.ClearImport();
        this.confirmation = ConfirmationView.None;
        this.confirmationFocusRequested = false;
    }

    /// <summary>
    /// Confirmation is a regular small ImGui window positioned over Setup, not a modal
    /// popup. The game and other windows stay usable; no full-screen dimming layer.
    /// </summary>
    private void DrawConfirmations()
    {
        if (this.showImportPreview && this.importPreview is not null && this.importedFilePath is not null)
        {
            this.showImportPreview = false;
            this.OpenConfirmation(ConfirmationView.ImportVenue);
        }
        if (this.confirmation == ConfirmationView.None) return;

        var isImport = this.confirmation == ConfirmationView.ImportVenue;
        var dimensions = isImport ? new Vector2(630f, 372f) : new Vector2(480f, 226f);
        var setupPosition = ImGui.GetWindowPos();
        var setupSize = ImGui.GetWindowSize();
        ImGui.SetNextWindowSize(dimensions, ImGuiCond.Always);
        ImGui.SetNextWindowPos(BackstageDesign.CenteredWindowPosition(setupPosition, setupSize, dimensions), ImGuiCond.Always);
        if (this.confirmationFocusRequested)
        {
            ImGui.SetNextWindowFocus();
            this.confirmationFocusRequested = false;
        }
        ImGui.PushStyleColor(ImGuiCol.WindowBg, BackstageDesign.Canvas);
        ImGui.PushStyleColor(ImGuiCol.Border, BackstageDesign.SoftLine);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoDocking;
        if (ImGui.Begin("Setup confirmation###vhNonModalSetupConfirmation", flags))
        {
            var p = ImGui.GetWindowPos();
            var draw = ImGui.GetWindowDrawList();
            BackstageDesign.Fill(draw, p, new Vector2(dimensions.X, 60f), BackstageDesign.Rail, 12f);
            BackstageDesign.Fill(draw, p + new Vector2(0f, 30f), new Vector2(dimensions.X, 30f), BackstageDesign.Rail);
            BackstageDesign.Rule(draw, p + new Vector2(0f, 59f),
                p + new Vector2(dimensions.X, 59f), BackstageDesign.SoftLine);
            var heading = this.confirmation switch
            {
                ConfirmationView.ActivateVenue => "ACTIVATE VENUE",
                ConfirmationView.DeleteVenue => "DELETE VENUE",
                ConfirmationView.RemoveRole => "REMOVE STAFF ROLE",
                ConfirmationView.ImportVenue => "IMPORT VENUE",
                _ => string.Empty,
            };
            draw.AddText(p + new Vector2(20f, 22f), BackstageDesign.Color(BackstageDesign.Gold), heading);
            if (BackstageDesign.FlatButton("##vhConfirmClose", "CLOSE",
                    p + new Vector2(dimensions.X - 94f, 13f), new Vector2(75f, 33f), BackstageDesign.Muted))
                this.CloseConfirmation();

            // Calling the action updates the state, but we finish drawing this window
            // before ImGui.End() so its window stack stays balanced.
            switch (this.confirmation)
            {
                case ConfirmationView.ActivateVenue:
                    this.DrawActivateConfirmation(p, dimensions);
                    break;
                case ConfirmationView.DeleteVenue:
                    this.DrawDeleteConfirmation(p, dimensions);
                    break;
                case ConfirmationView.RemoveRole:
                    this.DrawRemoveRoleConfirmation(p, dimensions);
                    break;
                case ConfirmationView.ImportVenue:
                    this.DrawImportConfirmation(p, dimensions);
                    break;
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }

    private static void ConfirmationText(Vector2 position, string message, float wrapWidth, Vector4 color)
    {
        ImGui.SetCursorScreenPos(position);
        ImGui.PushTextWrapPos(position.X + wrapWidth);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(message);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
    }

    private void DrawActivateConfirmation(Vector2 p, Vector2 size)
    {
        ConfirmationText(p + new Vector2(20f, 78f),
            $"Switch to {this.confirmationProfileName}?", size.X - 40f, BackstageDesign.Ivory);
        ConfirmationText(p + new Vector2(20f, 106f),
            "Pending chat commands will be cleared and automation paused. " +
            "The selected venue's schedules and macros will load; all profiles and shared people are preserved.",
            size.X - 40f, BackstageDesign.Muted);
        if (BackstageDesign.FlatButton("##vhConfirmSwitch", "SWITCH VENUE", p + new Vector2(20f, size.Y - 52f),
                new Vector2(205f, 34f), BackstageDesign.Gold))
        {
            if (this.Try(() =>
                {
                    this.Profiles.Switch(this.confirmationProfileId);
                    this.lastRole = string.Empty;
                    return string.Empty;
                }))
            {
                this.status = string.Empty;
                this.CloseConfirmation();
            }
        }
        if (BackstageDesign.FlatButton("##vhCancelSwitch", "CANCEL", p + new Vector2(size.X - 146f, size.Y - 52f),
                new Vector2(126f, 34f), BackstageDesign.Muted))
            this.CloseConfirmation();
    }

    private void DrawDeleteConfirmation(Vector2 p, Vector2 size)
    {
        ConfirmationText(p + new Vector2(20f, 78f),
            $"Delete {this.confirmationProfileName}?", size.X - 40f, BackstageDesign.Ivory);
        ConfirmationText(p + new Vector2(20f, 106f),
            "This permanently removes the inactive venue and its saved event data. " +
            "Shared DJ and Staff records remain unchanged. Export first if you may need this venue again.",
            size.X - 40f, BackstageDesign.Muted);
        if (BackstageDesign.FlatButton("##vhConfirmDelete", "DELETE VENUE", p + new Vector2(20f, size.Y - 52f),
                new Vector2(205f, 34f), BackstageDesign.Rose))
        {
            if (this.Try(() =>
                {
                    this.Profiles.Delete(this.confirmationProfileId);
                    this.selectedProfileId = this.Profiles.ActiveId;
                    this.renameProfileName = this.Profiles.Active.Name;
                    return "Inactive venue profile deleted.";
                }))
                this.CloseConfirmation();
        }
        if (BackstageDesign.FlatButton("##vhCancelDelete", "CANCEL", p + new Vector2(size.X - 146f, size.Y - 52f),
                new Vector2(126f, 34f), BackstageDesign.Muted))
            this.CloseConfirmation();
    }

    private void DrawRemoveRoleConfirmation(Vector2 p, Vector2 size)
    {
        ConfirmationText(p + new Vector2(20f, 78f),
            $"Remove '{this.requestedRoleRemoval}'?", size.X - 40f, BackstageDesign.Ivory);
        ConfirmationText(p + new Vector2(20f, 106f),
            "Existing shifts using this role will be reassigned. This venue's role macro and timer " +
            "will be removed, but the shared Staff Database will not change.",
            size.X - 40f, BackstageDesign.Muted);
        if (BackstageDesign.FlatButton("##vhConfirmRemoveRole", "REMOVE ROLE", p + new Vector2(20f, size.Y - 52f),
                new Vector2(205f, 34f), BackstageDesign.Rose))
        {
            if (this.Try(() =>
                {
                    if (!this.config.RemoveStaffRole(this.requestedRoleRemoval))
                        throw new InvalidOperationException("Could not remove role.");
                    this.lastRole = string.Empty;
                    this.StaffAuto.Refresh();
                    return "Venue role removed and affected shifts reassigned.";
                }))
                this.CloseConfirmation();
        }
        if (BackstageDesign.FlatButton("##vhCancelRole", "CANCEL", p + new Vector2(size.X - 146f, size.Y - 52f),
                new Vector2(126f, 34f), BackstageDesign.Muted))
            this.CloseConfirmation();
    }

    private void DrawImportConfirmation(Vector2 p, Vector2 size)
    {
        var preview = this.importPreview;
        if (preview is null || this.importedFilePath is null)
        {
            this.CloseConfirmation();
            return;
        }
        ConfirmationText(p + new Vector2(20f, 74f), preview.Name, size.X - 40f, BackstageDesign.Ivory);
        ConfirmationText(p + new Vector2(20f, 103f),
            $"{preview.DjCount} DJs  ·  {preview.DjSlotCount} DJ slots  ·  {preview.StaffCount} saved staff",
            size.X - 40f, BackstageDesign.Muted);
        ConfirmationText(p + new Vector2(20f, 126f),
            $"{preview.ShiftCount} shifts  ·  {preview.RoleCount} roles  ·  {preview.ShoutCount} custom shouts",
            size.X - 40f, BackstageDesign.Muted);
        ConfirmationText(p + new Vector2(20f, 157f),
            $"Contact differences: {preview.ConflictingDjs} DJ(s), {preview.ConflictingStaff} staff",
            size.X - 40f, BackstageDesign.Gold);
        ImGui.SetCursorScreenPos(p + new Vector2(20f, 188f));
        ImGui.Checkbox("Update existing contacts when names match", ref this.updateImportedContacts);
        ConfirmationText(p + new Vector2(20f, 219f),
            "Unchecked keeps your shared contacts. Checked replaces matching details with imported values.",
            size.X - 40f, BackstageDesign.Dim);
        BackstageDesign.Rule(ImGui.GetWindowDrawList(), p + new Vector2(20f, 256f),
            p + new Vector2(size.X - 20f, 256f), BackstageDesign.SoftLine);
        ConfirmationText(p + new Vector2(20f, 269f),
            "Import as New preserves other venues; Replace Active saves a recovery copy first. " +
            "Automation stays paused.", size.X - 40f, BackstageDesign.Muted);
        if (BackstageDesign.FlatButton("##vhImportAsNew", "IMPORT AS NEW", p + new Vector2(20f, size.Y - 52f),
                new Vector2(178f, 34f), BackstageDesign.Mint))
        {
            if (this.Try(() =>
                {
                    var imported = this.Profiles.ImportNew(this.importedFilePath!, this.updateImportedContacts);
                    this.selectedProfileId = imported.Id;
                    this.renameProfileName = imported.Name;
                    return "Imported as a separate venue. Automation PAUSED; use ACTIVATE to switch.";
                }))
                this.CloseConfirmation();
        }
        if (BackstageDesign.FlatButton("##vhImportReplace", "REPLACE ACTIVE", p + new Vector2(210f, size.Y - 52f),
                new Vector2(178f, 34f), BackstageDesign.Rose))
        {
            if (this.Try(() =>
                {
                    this.Profiles.ReplaceActiveFromImport(this.importedFilePath!, this.updateImportedContacts);
                    this.lastRole = string.Empty;
                    return "Active venue replaced; recovery backup saved. Automation PAUSED.";
                }))
                this.CloseConfirmation();
        }
        if (BackstageDesign.FlatButton("##vhImportCancel", "CANCEL", p + new Vector2(size.X - 140f, size.Y - 52f),
                new Vector2(120f, 34f), BackstageDesign.Muted))
            this.CloseConfirmation();
        if (this.statusIsError)
            ConfirmationText(p + new Vector2(20f, size.Y - 76f), this.status, size.X - 40f, BackstageDesign.Rose);
    }

    private void StartExport()
    {
        var selected = this.Profiles.Profiles.First(p => p.Id == this.selectedProfileId);
        this.exportingProfileId = selected.Id;
        var safeName = string.Concat(selected.Name.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        this.pendingFileDialog = this.FileDialogs.ShowSaveProfileDialog("Export selected VenueHost profile", safeName + ".venuehost");
        this.fileDialogIsImport = false;
    }

    private void PollDialog()
    {
        if (this.pendingFileDialog is null || !this.pendingFileDialog.TryGetResult(out var path, out var exception))
            return;
        this.pendingFileDialog = null;
        if (exception is not null)
        {
            this.SetStatus(exception.Message, true);
            return;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            this.exportingProfileId = null;
            return;
        }
        if (this.fileDialogIsImport)
            this.Try(() =>
            {
                this.importPreview = this.Profiles.InspectImport(path);
                this.importedFilePath = path;
                this.showImportPreview = true;
                return "Profile validated. Review the import confirmation.";
            });
        else
            this.Try(() =>
            {
                this.Profiles.ExportSelected(this.exportingProfileId ?? this.selectedProfileId, path);
                this.exportingProfileId = null;
                return "Selected venue exported with the shared DJ and staff directories.";
            });
    }

    private void ClearImport()
    {
        this.importPreview = null;
        this.importedFilePath = null;
        this.showImportPreview = false;
        this.updateImportedContacts = false;
    }

    private bool Try(Func<string> action)
    {
        try { this.SetStatus(action(), false); return true; }
        catch (Exception ex) { this.SetStatus(ex.Message, true); return false; }
    }

    private void SetStatus(string message, bool error)
    {
        this.status = message;
        this.statusIsError = error;
    }

}
