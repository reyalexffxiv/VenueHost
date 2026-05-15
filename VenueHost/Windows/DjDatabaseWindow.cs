using System;
using System.Linq;
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
            config.DjDatabase.Add(new DjDatabaseEntry { Name = config.GetUniqueDjDatabaseName("New DJ"), Link = string.Empty });
            config.CommitDjDatabaseChanges();
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
        if (ImGui.BeginTable("VenueHostDjDatabaseTable", 3, tableFlags))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Link", ImGuiTableColumnFlags.WidthStretch, 2.4f);
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
                    ImGui.PushID($"DjDatabaseRow{entry.RuntimeId}");
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    this.InputAndSave("##DbName", entry.Name, value => entry.Name = value, 120);

                    ImGui.TableNextColumn();
                    this.InputAndSave("##DbLink", entry.Link, value => entry.Link = value, 240);

                    ImGui.TableNextColumn();
                    if (ImGui.Button("Remove", new Vector2(75, 24)))
                    {
                        config.DjDatabase.RemoveAt(item.Index);
                        config.CommitDjDatabaseChanges();
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

    private void InputAndSave(string label, string value, Action<string> setter, int maxLength)
    {
        var editedValue = value ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText(label, ref editedValue, maxLength))
        {
            setter(editedValue);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            this.plugin.Configuration.CommitDjDatabaseChanges();
        }
    }

}
