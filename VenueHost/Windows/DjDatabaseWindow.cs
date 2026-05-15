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

        if (ImGui.Button("Add DJ", new Vector2(100, 28)))
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
            ImGui.TableSetupColumn("Save", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("Remove", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableHeadersRow();

            var visibleEntries = config.DjDatabase
                .Select((entry, index) => new { Entry = entry, Index = index })
                .Where(item => MatchesSearch(item.Entry, this.searchText))
                .ToList();

            if (visibleEntries.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextDisabled("No DJs found.");
            }
            else
            {
                foreach (var item in visibleEntries)
                {
                    var entry = item.Entry;
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

                    if (ImGui.Button("Save", new Vector2(72, 24)))
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
                    if (ImGui.Button("Remove", new Vector2(75, 24)))
                    {
                        config.DjDatabase.RemoveAt(item.Index);
                        this.cleanSnapshots.Remove(entry.RuntimeId);
                        this.SaveDatabaseChanges();
                        ImGui.PopID();
                        break;
                    }

                    ImGui.PopID();
                }
            }

            ImGui.EndTable();
        }
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

}
