using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using VenueHost.Models;

namespace VenueHost.Windows;

/// <summary>
/// Reusable DJ database. Staff can save DJ names and stream links once,
/// then pick them directly from DJ Lineup rows.
/// </summary>
public sealed class DjDatabaseWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Dictionary<string, (string Name, string Link)> cleanSnapshots = new();
    private string? pendingRemoveId;
    private string pendingRemoveName = string.Empty;
    private string searchText = string.Empty;

    public DjDatabaseWindow(Plugin plugin)
        : base("DJ Database###VenueHostDjDatabase")
    {
        this.plugin = plugin;
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
        var config = this.plugin.Configuration;
        config.DjDatabase ??= [];
        ImGui.TextUnformatted("DJ Database");
        ImGui.TextDisabled("Save unique DJ names and stream links here. The DJ Lineup picker uses this list.");
        ImGui.Spacing();

        if (this.ColoredButton("Add DJ", ButtonTone.Green, new Vector2(100, 28)))
        {
            var entry = new DjDatabaseEntry { Name = config.GetUniqueDjDatabaseName("New DJ"), Link = string.Empty };

            // Keep new rows at the top while staff fill them in. The row is sorted
            // into the permanent database order only after its Save button is used.
            config.DjDatabase.Insert(0, entry);
            this.cleanSnapshots[entry.RuntimeId] = (entry.Name, entry.Link);
        }

        ImGui.SameLine();
        ImGui.TextDisabled("DJ names are kept unique automatically.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Search DJ:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("##DjDatabaseSearch", ref this.searchText, 80);
        ImGui.Spacing();

        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("VenueHostDjDatabaseTable", 4, tableFlags))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Link", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableSetupColumn("Save", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Remove", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableHeadersRow();

            var visibleEntries = config.DjDatabase
                .Where(entry => MatchesSearch(entry, this.searchText))
                .ToList();

            if (visibleEntries.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextDisabled("No DJs found.");
            }
            else
            {
                foreach (var entry in visibleEntries)
                {
                    this.EnsureCleanSnapshot(entry);
                    ImGui.PushID($"DjDatabaseRow{entry.RuntimeId}");
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    this.InputWithoutAutoSave("##DbName", entry.Name, value => entry.Name = value, 120);

                    ImGui.TableNextColumn();
                    this.InputWithoutAutoSave("##DbLink", entry.Link, value => entry.Link = value, 240);

                    ImGui.TableNextColumn();
                    var dirty = this.IsDirty(entry);
                    if (!dirty)
                    {
                        ImGui.BeginDisabled();
                    }

                    if (this.ColoredButton("Save", ButtonTone.Blue, new Vector2(78, 24)))
                    {
                        this.SaveDatabaseChanges();
                        ImGui.PopID();
                        break;
                    }

                    if (!dirty)
                    {
                        ImGui.EndDisabled();
                    }

                    ImGui.TableNextColumn();
                    if (this.ColoredButton("Remove", ButtonTone.Red, new Vector2(82, 24)))
                    {
                        this.OpenRemoveConfirmation(entry);
                    }

                    ImGui.PopID();
                }
            }

            ImGui.EndTable();
        }

        this.DrawRemoveConfirmationPopup();
    }

    private static bool MatchesSearch(DjDatabaseEntry entry, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return true;

        return (entry.Name?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (entry.Link?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void InputWithoutAutoSave(string label, string value, Action<string> setter, int maxLength)
    {
        var editedValue = value ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText(label, ref editedValue, maxLength))
        {
            setter(editedValue);
        }
    }

    private void EnsureCleanSnapshot(DjDatabaseEntry entry)
    {
        if (!this.cleanSnapshots.ContainsKey(entry.RuntimeId))
        {
            this.cleanSnapshots[entry.RuntimeId] = (entry.Name, entry.Link);
        }
    }

    private bool IsDirty(DjDatabaseEntry entry)
    {
        this.EnsureCleanSnapshot(entry);
        var snapshot = this.cleanSnapshots[entry.RuntimeId];
        return !string.Equals(entry.Name, snapshot.Name, StringComparison.Ordinal)
            || !string.Equals(entry.Link, snapshot.Link, StringComparison.Ordinal);
    }

    private void OpenRemoveConfirmation(DjDatabaseEntry entry)
    {
        this.pendingRemoveId = entry.RuntimeId;
        this.pendingRemoveName = string.IsNullOrWhiteSpace(entry.Name) ? "this DJ" : entry.Name.Trim();
        ImGui.OpenPopup("Remove DJ?##VenueHostDjDbRemoveConfirm");
    }

    private void DrawRemoveConfirmationPopup()
    {
        if (this.pendingRemoveId is not null)
        {
            var windowPos = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            var popupSize = new Vector2(390, 145);
            ImGui.SetNextWindowSize(popupSize, ImGuiCond.Appearing);
            ImGui.SetNextWindowPos(windowPos + (windowSize - popupSize) * 0.5f, ImGuiCond.Appearing);
        }

        var popupOpen = true;
        if (!ImGui.BeginPopupModal("Remove DJ?##VenueHostDjDbRemoveConfirm", ref popupOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        ImGui.TextWrapped($"Remove {this.pendingRemoveName} from the DJ Database?");
        ImGui.TextDisabled("Existing lineup rows using this DJ will stay unchanged.");
        ImGui.Spacing();

        if (this.ColoredButton("Remove##ConfirmRemoveDj", ButtonTone.Red, new Vector2(120, 28)))
        {
            if (this.pendingRemoveId is { } removeId)
            {
                this.RemoveEntry(removeId);
            }

            this.ClearPendingRemove();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##CancelRemoveDj", new Vector2(120, 28)))
        {
            this.ClearPendingRemove();
            ImGui.CloseCurrentPopup();
        }

        if (!popupOpen)
        {
            this.ClearPendingRemove();
        }

        ImGui.EndPopup();
    }

    private void RemoveEntry(string runtimeId)
    {
        var entry = this.plugin.Configuration.DjDatabase.FirstOrDefault(databaseEntry => databaseEntry.RuntimeId == runtimeId);
        if (entry is null)
        {
            return;
        }

        this.plugin.Configuration.DjDatabase.Remove(entry);
        this.cleanSnapshots.Remove(entry.RuntimeId);
        this.SaveDatabaseChanges();
    }

    private void ClearPendingRemove()
    {
        this.pendingRemoveId = null;
        this.pendingRemoveName = string.Empty;
    }

    private void SyncCleanSnapshots()
    {
        this.cleanSnapshots.Clear();
        foreach (var entry in this.plugin.Configuration.DjDatabase)
        {
            this.cleanSnapshots[entry.RuntimeId] = (entry.Name, entry.Link);
        }
    }

    private void SaveDatabaseChanges()
    {
        this.plugin.Configuration.CommitDjDatabaseChanges();
        this.SyncCleanSnapshots();
    }

    private bool ColoredButton(string label, ButtonTone tone, Vector2 size)
    {
        var (normal, hovered, active) = tone switch
        {
            ButtonTone.Green => (new Vector4(0.10f, 0.46f, 0.24f, 1.00f), new Vector4(0.14f, 0.56f, 0.30f, 1.00f), new Vector4(0.08f, 0.36f, 0.19f, 1.00f)),
            ButtonTone.Red => (new Vector4(0.56f, 0.14f, 0.14f, 1.00f), new Vector4(0.68f, 0.18f, 0.18f, 1.00f), new Vector4(0.45f, 0.10f, 0.10f, 1.00f)),
            ButtonTone.Blue => (new Vector4(0.12f, 0.34f, 0.61f, 1.00f), new Vector4(0.16f, 0.42f, 0.73f, 1.00f), new Vector4(0.09f, 0.27f, 0.50f, 1.00f)),
            _ => (new Vector4(0.32f, 0.32f, 0.32f, 1.00f), new Vector4(0.39f, 0.39f, 0.39f, 1.00f), new Vector4(0.25f, 0.25f, 0.25f, 1.00f)),
        };

        ImGui.PushStyleColor(ImGuiCol.Button, normal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
        var clicked = ImGui.Button(label, size);
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
