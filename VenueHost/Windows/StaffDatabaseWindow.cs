using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using VenueHost.Models;
using VenueHost.Services;

namespace VenueHost.Windows;

/// <summary>
/// Reusable staff database. Roles are managed in Settings -> Staff Schedule,
/// then selected here so staff data stays normalized.
/// </summary>
public sealed class StaffDatabaseWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly IServiceContext services;

    /// <summary>Shared plugin configuration used by this window.</summary>
    private Configuration Configuration => this.configuration;

    /// <summary>Shared service context used to resolve plugin services.</summary>
    private IServiceContext Services => this.services;

    /// <summary>Native file dialog helper that does not block Dalamud's UI draw thread.</summary>
    private FileDialogService FileDialogs => this.Services.Get<FileDialogService>();
    private readonly Dictionary<string, (string Name, string Role)> cleanSnapshots = new();
    private string? pendingRemoveId;
    private string pendingRemoveName = string.Empty;
    private bool shouldOpenRemoveConfirmation;
    private string searchText = string.Empty;
    private string csvStatusText = string.Empty;
    private PendingFileDialog? pendingCsvDialog;
    private bool pendingCsvDialogIsImport;

    public StaffDatabaseWindow(Configuration configuration, IServiceContext services)
        : base("Staff Database###VenueHostStaffDatabase")
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        using var contrast = UiTheme.PushContrastIfEnabled(this.Configuration.ContrastModeEnabled);
        this.CompletePendingCsvDialog();

        var config = this.Configuration;
        config.StaffDatabase ??= [];
        config.EnsureStaffRoles();

        ImGui.TextUnformatted("Staff Database");
        ImGui.TextDisabled("Save reusable staff names here. Roles are created in Settings -> Staff Schedule, then picked here.");
        ImGui.Spacing();

        if (this.ColoredButton("Add Staff", ButtonTone.Green, new Vector2(116, 32)))
        {
            var entry = new StaffDatabaseEntry
            {
                Name = config.GetUniqueStaffName("New Staff"),
                Role = config.GetExistingOrDefaultStaffRole(config.SelectedStaffRole),
            };

            // Keep new rows at the top while staff fill them in. The row is sorted
            // into the permanent database order only after its Save button is used.
            config.StaffDatabase.Insert(0, entry);
            this.cleanSnapshots[entry.RuntimeId] = (entry.Name, entry.Role);
        }

        var csvDialogPending = this.pendingCsvDialog is not null;
        if (csvDialogPending)
            ImGui.BeginDisabled();

        ImGui.SameLine();
        if (this.ColoredButton("Export CSV", ButtonTone.Blue, new Vector2(128, 32)))
            this.ExportCsv();

        ImGui.SameLine();
        if (this.ColoredButton("Import CSV", ButtonTone.Blue, new Vector2(128, 32)))
            this.ImportCsv();

        if (csvDialogPending)
            ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled("CSV format: Name,Role. Roles must already exist in Staff Schedule settings.");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Export opens a Save dialog. Import opens a file picker. Unknown imported roles use the selected/default role.");

        if (!string.IsNullOrWhiteSpace(this.csvStatusText))
            ImGui.TextDisabled(this.csvStatusText);

        ImGui.Spacing();
        ImGui.TextUnformatted("Search Staff:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("##StaffDatabaseSearch", ref this.searchText, 80);
        ImGui.Spacing();

        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("VenueHostStaffDatabaseTable", 4, tableFlags))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Save", ImGuiTableColumnFlags.WidthFixed, 106f);
            ImGui.TableSetupColumn("Remove", ImGuiTableColumnFlags.WidthFixed, 118f);
            ImGui.TableHeadersRow();

            var visibleEntries = config.StaffDatabase
                .Where(entry => MatchesSearch(entry, this.searchText))
                .ToList();

            if (visibleEntries.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextDisabled("No staff found.");
            }
            else
            {
                foreach (var entry in visibleEntries)
                {
                    this.EnsureCleanSnapshot(entry);
                    ImGui.PushID($"StaffDatabaseRow{entry.RuntimeId}");
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    this.InputWithoutAutoSave("##StaffName", entry.Name, value => entry.Name = value, 120);

                    ImGui.TableNextColumn();
                    this.RoleComboWithoutAutoSave(entry);

                    ImGui.TableNextColumn();
                    var dirty = this.IsDirty(entry);
                    if (!dirty)
                        ImGui.BeginDisabled();

                    if (this.ColoredButton("Save", ButtonTone.Blue, new Vector2(90, 28)))
                    {
                        this.SaveDatabaseChanges();
                        ImGui.PopID();
                        break;
                    }

                    if (!dirty)
                        ImGui.EndDisabled();

                    ImGui.TableNextColumn();
                    if (this.ColoredButton("Remove", ButtonTone.Red, new Vector2(100, 28)))
                        this.OpenRemoveConfirmation(entry);

                    ImGui.PopID();
                }
            }

            ImGui.EndTable();
        }

        this.DrawRemoveConfirmationPopup();
    }

    private string GetDefaultCsvPath()
        => Path.Combine(this.Services.PluginInterface.ConfigDirectory.FullName, "staff_database.csv");

    private void ExportCsv()
    {
        this.StartCsvDialog(
            isImport: false,
            statusText: "Waiting for export location...",
            dialogFactory: () => this.FileDialogs.ShowSaveCsvDialog("Export Staff Database", this.GetDefaultCsvPath()));
    }

    private void ExportCsv(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

            var builder = new StringBuilder();
            builder.AppendLine("Name,Role");
            foreach (var entry in this.Configuration.StaffDatabase.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
                builder.AppendLine($"{EscapeCsv(entry.Name)},{EscapeCsv(entry.Role)}");

            File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
            this.csvStatusText = $"Exported {this.Configuration.StaffDatabase.Count} staff entries to {path}.";
        }
        catch (Exception ex)
        {
            this.csvStatusText = $"Export failed: {ex.Message}";
        }
    }

    private void ImportCsv()
    {
        this.StartCsvDialog(
            isImport: true,
            statusText: "Waiting for import file...",
            dialogFactory: () => this.FileDialogs.ShowOpenCsvDialog("Import Staff Database", this.GetDefaultCsvPath()));
    }

    private void ImportCsv(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                this.csvStatusText = $"Import failed: file not found, {path}";
                return;
            }

            var config = this.Configuration;
            config.EnsureStaffRoles();
            var imported = 0;
            var remappedRoles = 0;
            foreach (var row in File.ReadAllLines(path).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(row))
                    continue;

                var columns = ParseCsvLine(row);
                if (columns.Count == 0 || string.IsNullOrWhiteSpace(columns[0]))
                    continue;

                var name = columns[0].Trim();
                var importedRole = columns.Count > 1 ? columns[1].Trim() : string.Empty;
                var role = config.GetExistingOrDefaultStaffRole(importedRole);
                if (!string.IsNullOrWhiteSpace(importedRole) && !role.Equals(importedRole, StringComparison.OrdinalIgnoreCase))
                    remappedRoles++;

                var existing = config.StaffDatabase.FirstOrDefault(entry =>
                    string.Equals((entry.Name ?? string.Empty).Trim(), name, StringComparison.OrdinalIgnoreCase));

                if (existing is null)
                    config.StaffDatabase.Add(new StaffDatabaseEntry { Name = name, Role = role });
                else
                {
                    existing.Name = name;
                    existing.Role = role;
                }

                imported++;
            }

            this.SaveDatabaseChanges();
            this.csvStatusText = remappedRoles > 0
                ? $"Imported/updated {imported} staff entries from {path}. {remappedRoles} unknown role(s) used the default selected role."
                : $"Imported/updated {imported} staff entries from {path}.";
        }
        catch (Exception ex)
        {
            this.csvStatusText = $"Import failed: {ex.Message}";
        }
    }

    private void StartCsvDialog(bool isImport, string statusText, Func<PendingFileDialog> dialogFactory)
    {
        if (this.pendingCsvDialog is not null)
            return;

        try
        {
            this.pendingCsvDialogIsImport = isImport;
            this.pendingCsvDialog = dialogFactory();
            this.csvStatusText = statusText;
        }
        catch (Exception ex)
        {
            this.pendingCsvDialog = null;
            this.csvStatusText = $"CSV dialog failed: {ex.Message}";
        }
    }

    private void CompletePendingCsvDialog()
    {
        if (this.pendingCsvDialog is not { } dialog)
            return;

        if (!dialog.TryGetResult(out var path, out var exception))
            return;

        var wasImport = this.pendingCsvDialogIsImport;
        this.pendingCsvDialog = null;
        this.pendingCsvDialogIsImport = false;

        if (exception is not null)
        {
            this.csvStatusText = $"CSV dialog failed: {exception.Message}";
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            this.csvStatusText = wasImport ? "Import cancelled." : "Export cancelled.";
            return;
        }

        if (wasImport)
            this.ImportCsv(path);
        else
            this.ExportCsv(path);
    }

    private static string EscapeCsv(string? value)
    {
        value ??= string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var current = line[i];
            if (current == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    builder.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (current == ',' && !inQuotes)
            {
                values.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(current);
            }
        }

        values.Add(builder.ToString());
        return values;
    }

    private void RoleComboWithoutAutoSave(StaffDatabaseEntry entry)
    {
        var config = this.Configuration;
        var currentRole = config.GetExistingOrDefaultStaffRole(entry.Role);
        entry.Role = currentRole;

        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##StaffDbRole", currentRole))
            return;

        foreach (var role in config.GetKnownStaffRoles())
        {
            var selected = string.Equals(currentRole, role, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(role, selected))
                entry.Role = role;

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static bool MatchesSearch(StaffDatabaseEntry entry, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return true;

        return (entry.Name?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (entry.Role?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void InputWithoutAutoSave(string label, string value, Action<string> setter, int maxLength)
    {
        var editedValue = value ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText(label, ref editedValue, maxLength))
            setter(editedValue);
    }

    private void EnsureCleanSnapshot(StaffDatabaseEntry entry)
    {
        if (!this.cleanSnapshots.ContainsKey(entry.RuntimeId))
            this.cleanSnapshots[entry.RuntimeId] = (entry.Name, entry.Role);
    }

    private bool IsDirty(StaffDatabaseEntry entry)
    {
        this.EnsureCleanSnapshot(entry);
        var snapshot = this.cleanSnapshots[entry.RuntimeId];
        return !string.Equals(entry.Name, snapshot.Name, StringComparison.Ordinal) ||
               !string.Equals(entry.Role, snapshot.Role, StringComparison.Ordinal);
    }

    private void SaveDatabaseChanges()
    {
        var config = this.Configuration;
        config.EnsureStaffRoles();

        foreach (var entry in config.StaffDatabase)
        {
            entry.Name = (entry.Name ?? string.Empty).Trim();
            entry.Role = config.GetExistingOrDefaultStaffRole(entry.Role);
            entry.Link = string.Empty;
        }

        config.StaffDatabase = config.StaffDatabase
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .GroupBy(entry => entry.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        config.Save();
        this.SyncCleanSnapshots();
    }

    private void OpenRemoveConfirmation(StaffDatabaseEntry entry)
    {
        this.pendingRemoveId = entry.RuntimeId;
        this.pendingRemoveName = string.IsNullOrWhiteSpace(entry.Name) ? "this staff member" : entry.Name.Trim();
        this.shouldOpenRemoveConfirmation = true;
    }

    private void DrawRemoveConfirmationPopup()
    {
        if (this.shouldOpenRemoveConfirmation)
        {
            ImGui.OpenPopup("Remove Staff?##VenueHostStaffDbRemoveConfirm");
            this.shouldOpenRemoveConfirmation = false;
        }

        if (this.pendingRemoveId is not null)
        {
            var windowPos = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            var popupSize = new Vector2(410, 145);
            ImGui.SetNextWindowSize(popupSize, ImGuiCond.Appearing);
            ImGui.SetNextWindowPos(windowPos + (windowSize - popupSize) * 0.5f, ImGuiCond.Appearing);
        }

        var popupOpen = true;
        if (!ImGui.BeginPopupModal("Remove Staff?##VenueHostStaffDbRemoveConfirm", ref popupOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings))
            return;

        ImGui.TextWrapped($"Remove {this.pendingRemoveName} from the Staff Database?");
        ImGui.TextDisabled("Existing Staff Schedule rows using this name will stay unchanged.");
        ImGui.Spacing();

        if (this.ColoredButton("Remove##ConfirmRemoveStaff", ButtonTone.Red, new Vector2(120, 28)))
        {
            if (this.pendingRemoveId is { } removeId)
                this.RemoveEntry(removeId);

            this.ClearPendingRemove();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##CancelRemoveStaff", new Vector2(120, 28)))
        {
            this.ClearPendingRemove();
            ImGui.CloseCurrentPopup();
        }

        if (!popupOpen)
            this.ClearPendingRemove();

        ImGui.EndPopup();
    }

    private void RemoveEntry(string runtimeId)
    {
        var entry = this.Configuration.StaffDatabase.FirstOrDefault(databaseEntry => databaseEntry.RuntimeId == runtimeId);
        if (entry is null)
            return;

        this.Configuration.StaffDatabase.Remove(entry);
        this.cleanSnapshots.Remove(entry.RuntimeId);
        this.SaveDatabaseChanges();
    }

    private void ClearPendingRemove()
    {
        this.pendingRemoveId = null;
        this.pendingRemoveName = string.Empty;
        this.shouldOpenRemoveConfirmation = false;
    }

    private void SyncCleanSnapshots()
    {
        this.cleanSnapshots.Clear();
        foreach (var entry in this.Configuration.StaffDatabase)
            this.cleanSnapshots[entry.RuntimeId] = (entry.Name, entry.Role);
    }

    private bool ColoredButton(string label, ButtonTone tone, Vector2 size)
    {
        var (normal, hovered, active) = this.Configuration.ContrastModeEnabled
            ? tone switch
            {
                ButtonTone.Green => (new Vector4(0.00f, 0.030f, 0.010f, 1.00f), new Vector4(0.00f, 0.12f, 0.04f, 1.00f), new Vector4(0.00f, 0.22f, 0.08f, 1.00f)),
                ButtonTone.Red => (new Vector4(0.045f, 0.000f, 0.038f, 1.00f), new Vector4(0.15f, 0.00f, 0.125f, 1.00f), new Vector4(0.26f, 0.00f, 0.22f, 1.00f)),
                ButtonTone.Blue => (new Vector4(0.00f, 0.025f, 0.030f, 1.00f), new Vector4(0.00f, 0.11f, 0.12f, 1.00f), new Vector4(0.00f, 0.20f, 0.22f, 1.00f)),
                _ => (new Vector4(0.00f, 0.025f, 0.018f, 1.00f), new Vector4(0.00f, 0.10f, 0.07f, 1.00f), new Vector4(0.00f, 0.18f, 0.12f, 1.00f)),
            }
            : tone switch
            {
                ButtonTone.Green => (new Vector4(0.10f, 0.46f, 0.24f, 1.00f), new Vector4(0.14f, 0.56f, 0.30f, 1.00f), new Vector4(0.08f, 0.36f, 0.19f, 1.00f)),
                ButtonTone.Red => (new Vector4(0.56f, 0.14f, 0.14f, 1.00f), new Vector4(0.68f, 0.18f, 0.18f, 1.00f), new Vector4(0.45f, 0.10f, 0.10f, 1.00f)),
                ButtonTone.Blue => (new Vector4(0.12f, 0.34f, 0.61f, 1.00f), new Vector4(0.16f, 0.42f, 0.73f, 1.00f), new Vector4(0.09f, 0.27f, 0.50f, 1.00f)),
                _ => (new Vector4(0.32f, 0.32f, 0.32f, 1.00f), new Vector4(0.39f, 0.39f, 0.39f, 1.00f), new Vector4(0.25f, 0.25f, 0.25f, 1.00f)),
            };

        var contrast = this.Configuration.ContrastModeEnabled;
        ImGui.PushStyleColor(ImGuiCol.Button, normal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
        if (contrast)
        {
            var accent = tone switch
            {
                ButtonTone.Green => new Vector4(0.10f, 1.00f, 0.25f, 1.00f),
                ButtonTone.Red => new Vector4(1.00f, 0.05f, 0.85f, 1.00f),
                ButtonTone.Blue => new Vector4(0.00f, 1.00f, 0.95f, 1.00f),
                _ => new Vector4(0.58f, 1.00f, 0.78f, 1.00f),
            };
            ImGui.PushStyleColor(ImGuiCol.Text, accent);
            ImGui.PushStyleColor(ImGuiCol.Border, accent);
        }

        ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.5f, 0.5f));
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleVar();

        if (contrast)
            ImGui.PopStyleColor(2);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    private enum ButtonTone
    {
        Green,
        Red,
        Blue,
    }
}
