using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using VenueHost.Models;
using VenueHost.Services;

namespace VenueHost.Windows;

/// <summary>
/// Reusable DJ database. Staff can save DJ names and stream links once,
/// then pick them directly from DJ Lineup rows.
/// </summary>
public sealed class DjDatabaseWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly IServiceContext services;

    /// <summary>Shared plugin configuration used by this window.</summary>
    private Configuration Configuration => this.configuration;

    /// <summary>Shared service context used to resolve plugin services.</summary>
    private IServiceContext Services => this.services;

    /// <summary>Native file dialog helper that does not block Dalamud's UI draw thread.</summary>
    private FileDialogService FileDialogs => this.Services.Get<FileDialogService>();
    // Drafts stay separate from the stored entries until Save is pressed.
    // This also prevents a CSV operation from accidentally persisting half-typed data.
    private readonly Dictionary<string, (string Name, string Link)> cleanSnapshots = new();
    private string? selectedId;
    private string draftName = string.Empty;
    private string draftLink = string.Empty;
    private string originalName = string.Empty;
    private string originalLink = string.Empty;
    private bool creatingNew;
    private string? requestedSelectionId;
    private bool requestedCreate;
    private bool pendingDiscardPopup;
    private enum DiscardAction { SwitchSelection, CancelDraft, CloseWindow }
    private DiscardAction pendingDiscardAction;
    private bool discardPopupOpen;
    private bool pendingRemovePopup;
    private bool removePopupOpen;
    private string? pendingRemoveId;
    private string pendingRemoveName = string.Empty;
    private string searchText = string.Empty;
    private string csvStatusText = string.Empty;
    private PendingFileDialog? pendingCsvDialog;
    private bool pendingCsvDialogIsImport;
    private string editorError = string.Empty;

    private static readonly Vector2 WindowDimensions = new(944f, 548f);
    private const string RemovePopup = "Remove DJ?##VenueHostDjDbRemoveConfirm";
    private const string DiscardPopup = "Discard unsaved changes?##VenueHostDjDbDiscard";

    public DjDatabaseWindow(Configuration configuration, IServiceContext services)
        : base("DJ Database###VenueHostDjDatabase", ImGuiWindowFlags.NoTitleBar |
               ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize |
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
               ImGuiWindowFlags.NoBackground)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.Size = WindowDimensions;
        this.SizeCondition = ImGuiCond.Always;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = WindowDimensions,
            MaximumSize = WindowDimensions,
        };
        this.AllowPinning = false;
    }

    public void Dispose() { }

    // Draw custom rounded, opaque chrome BEFORE drawing content. No legacy
    // native title bar/transparent table is visible behind the editor.
    public override void PreDraw()
    {
        this.Size = WindowDimensions;
        this.SizeCondition = ImGuiCond.Always;
        ImGui.SetNextWindowPos(BackstageDesign.CenteredOverMainWindow(this.Services.Get<UiService>(), WindowDimensions), ImGuiCond.Appearing);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 14f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, BackstageDesign.Canvas);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
    }

    public override void Draw()
    {
        using var palette = BackstageDesign.PushHighContrast(this.Configuration.OverviewHighContrastPreviewEnabled);
        using var contrast = UiTheme.PushContrastIfEnabled(this.Configuration.OverviewHighContrastPreviewEnabled, preserveLayout: true);
        this.CompletePendingCsvDialog();
        this.Configuration.DjDatabase ??= [];
        this.EnsureSelection();

        var p = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var draw = ImGui.GetWindowDrawList();
        BackstageDesign.Fill(draw, p, size, BackstageDesign.Canvas, 14f);
        BackstageDesign.Fill(draw, p, new Vector2(size.X, 66f), BackstageDesign.Rail, 14f);
        // Fill the bottom half of the header's rounded strip to create a straight seam.
        BackstageDesign.Fill(draw, p + new Vector2(0f, 36f), new Vector2(size.X, 30f), BackstageDesign.Rail);
        BackstageDesign.Rule(draw, p + new Vector2(0f, 65f), p + new Vector2(size.X, 65f), BackstageDesign.SoftLine);
        draw.AddText(p + new Vector2(22f, 15f), BackstageDesign.Color(BackstageDesign.Gold), "DJ DATABASE");
        draw.AddText(p + new Vector2(22f, 36f), BackstageDesign.Color(BackstageDesign.Muted),
            $"{this.Configuration.DjDatabase.Count} saved DJs");
        if (this.ButtonAt("Close", "##vhDbClose", p + new Vector2(size.X - 82f, 15f),
                new Vector2(60f, 34f), BackstageDesign.Muted))
            this.RequestCloseWindow();
        ImGui.SetCursorScreenPos(p + new Vector2(225f, 0f));
        ImGui.InvisibleButton("##vhDbWindowDrag", new Vector2(size.X - 335f, 65f));
        if (ImGui.IsItemActive())
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta, ImGuiCond.Always);

        var left = p + new Vector2(18f, 82f);
        const float leftWidth = 393f;
        var right = p + new Vector2(423f, 82f);
        const float rightWidth = 503f;
        const float directoryHeight = 380f;
        const float editorHeight = 315f;
        BackstageDesign.SurfacePanel(left, new Vector2(leftWidth, directoryHeight));
        BackstageDesign.SurfacePanel(right, new Vector2(rightWidth, editorHeight));
        this.DrawDjDirectory(left, leftWidth, directoryHeight);
        this.DrawSelectedEditor(right, rightWidth, editorHeight);
        this.DrawCsvFooter(p + new Vector2(18f, 475f), size.X - 36f);
        this.DrawDialogs();
    }

    private void DrawDjDirectory(Vector2 p, float width, float height)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(p + new Vector2(18f, 17f), BackstageDesign.Color(BackstageDesign.Gold), "SAVED PERFORMERS");
        if (this.ButtonAt("+ ADD DJ", "##vhDbAdd", p + new Vector2(width - 113f, 9f),
                new Vector2(98f, 30f), BackstageDesign.Mint))
            this.RequestSelection(null, true);
        BackstageDesign.Rule(draw, p + new Vector2(15f, 49f),
            p + new Vector2(width - 15f, 49f), BackstageDesign.SoftLine);
        ImGui.SetCursorScreenPos(p + new Vector2(16f, 61f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, BackstageDesign.Raised);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, BackstageDesign.Raised);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        ImGui.SetNextItemWidth(width - 32f);
        ImGui.InputTextWithHint("##vhDjDbSearch", "Search DJs or stream links...", ref this.searchText, 120);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);
        var count = this.Configuration.DjDatabase.Count(e => MatchesSearch(e, this.searchText));
        draw.AddText(p + new Vector2(17f, 103f), BackstageDesign.Color(BackstageDesign.Dim),
            $"{count} {(count == 1 ? "result" : "results")}");

        ImGui.SetCursorScreenPos(p + new Vector2(10f, 125f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(5f, 2f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        if (ImGui.BeginChild("##vhDbScrollableList", new Vector2(width - 20f, height - 139f), false))
        {
            var names = this.Configuration.DjDatabase.Where(e => MatchesSearch(e, this.searchText))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(BackstageDesign.Muted, "No matching DJs.");
                ImGui.TextDisabled("Try another search or add a DJ.");
            }
            foreach (var entry in names)
            {
                var row = ImGui.GetCursorScreenPos();
                var rowWidth = ImGui.GetContentRegionAvail().X - 3f;
                ImGui.PushID(entry.RuntimeId);
                var clicked = ImGui.InvisibleButton("##select", new Vector2(rowWidth, 37f));
                var hovered = ImGui.IsItemHovered();
                var selected = !this.creatingNew && entry.RuntimeId == this.selectedId;
                var color = selected ? new Vector4(0.265f, 0.222f, 0.205f, 1f)
                    : hovered ? BackstageDesign.Raised : new Vector4(0.145f, 0.147f, 0.162f, 1f);
                BackstageDesign.Fill(ImGui.GetWindowDrawList(), row, new Vector2(rowWidth, 34f), color, 7f);
                if (selected)
                    BackstageDesign.Fill(ImGui.GetWindowDrawList(), row, new Vector2(3f, 34f), BackstageDesign.Gold, 1.5f);
                var name = string.IsNullOrWhiteSpace(entry.Name) ? "Unnamed DJ" : entry.Name;
                ImGui.GetWindowDrawList().AddText(row + new Vector2(15f, 9f),
                    BackstageDesign.Color(selected ? BackstageDesign.Ivory : BackstageDesign.Muted), name);
                ImGui.GetWindowDrawList().AddText(row + new Vector2(rowWidth - 21f, 9f),
                    BackstageDesign.Color(selected ? BackstageDesign.Gold : BackstageDesign.Dim), ">");
                if (clicked) this.RequestSelection(entry.RuntimeId, false);
                if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(entry.Link))
                    ImGui.SetTooltip(entry.Link);
                ImGui.PopID();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4f);
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void DrawSelectedEditor(Vector2 p, float width, float height)
    {
        var draw = ImGui.GetWindowDrawList();
        var entry = this.SelectedEntry();
        var newMode = this.creatingNew;
        var hasSelection = newMode || entry is not null;
        draw.AddText(p + new Vector2(19f, 18f), BackstageDesign.Color(BackstageDesign.Gold),
            newMode ? "ADD NEW DJ" : "EDIT DJ");
        draw.AddText(p + new Vector2(width - 129f, 18f), BackstageDesign.Color(BackstageDesign.Mint),
            newMode ? "NEW ENTRY" : hasSelection ? "SAVED DJ" : "NO SELECTION");
        BackstageDesign.Rule(draw, p + new Vector2(17f, 52f),
            p + new Vector2(width - 17f, 52f), BackstageDesign.SoftLine);
        if (!hasSelection)
        {
            ImGui.SetCursorScreenPos(p + new Vector2(22f, 90f));
            ImGui.TextColored(BackstageDesign.Muted, "Select a DJ from the list, or add a new one.");
            return;
        }
        draw.AddText(p + new Vector2(21f, 80f), BackstageDesign.Color(BackstageDesign.Dim), "DJ NAME");
        ImGui.PushStyleColor(ImGuiCol.FrameBg, BackstageDesign.Raised);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, BackstageDesign.Raised);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, BackstageDesign.Raised);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(11f, 9f));
        ImGui.SetCursorScreenPos(p + new Vector2(20f, 102f));
        ImGui.SetNextItemWidth(width - 40f);
        ImGui.InputText("##vhDbNameDraft", ref this.draftName, 120);
        draw.AddText(p + new Vector2(21f, 151f), BackstageDesign.Color(BackstageDesign.Dim), "STREAM LINK");
        ImGui.SetCursorScreenPos(p + new Vector2(20f, 172f));
        ImGui.SetNextItemWidth(width - 40f);
        ImGui.InputTextWithHint("##vhDbLinkDraft", "https://www.twitch.tv/...", ref this.draftLink, 700);
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(3);
        if (this.editorError.Length > 0)
        {
            ImGui.SetCursorScreenPos(p + new Vector2(21f, 216f));
            ImGui.TextColored(BackstageDesign.Rose, this.editorError);
        }
        BackstageDesign.Rule(draw, p + new Vector2(18f, height - 74f),
            p + new Vector2(width - 18f, height - 74f), BackstageDesign.SoftLine);
        var dirty = this.DraftIsDirty();
        if (this.ButtonAt("SAVE CHANGES", "##vhDbSave", p + new Vector2(20f, height - 58f),
                new Vector2(154f, 34f), BackstageDesign.Mint, true, !dirty))
            this.SaveDraft();
        if (this.ButtonAt("CANCEL", "##vhDbCancel", p + new Vector2(182f, height - 58f),
                new Vector2(86f, 34f), BackstageDesign.Muted))
            this.RequestCancelDraft();
        if (!newMode && this.ButtonAt("REMOVE DJ", "##vhDbRemove", p + new Vector2(width - 145f, height - 58f),
                new Vector2(125f, 34f), BackstageDesign.Rose))
            this.OpenRemoveConfirmation(entry!);
    }

    private void DrawCsvFooter(Vector2 p, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        BackstageDesign.Rule(draw, p, p + new Vector2(width, 0f), BackstageDesign.SoftLine);
        var disabled = this.pendingCsvDialog is not null;
        if (this.ButtonAt("EXPORT CSV", "##vhDbCsvExport", p + new Vector2(width - 244f, 10f),
                new Vector2(114f, 31f), BackstageDesign.Mist, false, disabled))
            this.ExportCsv();
        if (this.ButtonAt("IMPORT CSV", "##vhDbCsvImport", p + new Vector2(width - 122f, 10f),
                new Vector2(114f, 31f), BackstageDesign.Mist, false, disabled))
            this.ImportCsv();
        if (!string.IsNullOrWhiteSpace(this.csvStatusText))
        {
            var label = this.csvStatusText.Length > 108 ? this.csvStatusText[..105] + "..." : this.csvStatusText;
            draw.AddText(p + new Vector2(1f, 49f), BackstageDesign.Color(BackstageDesign.Dim), label);
            ImGui.SetCursorScreenPos(p + new Vector2(0f, 45f));
            ImGui.InvisibleButton("##vhDbCsvStatusHover", new Vector2(width - 12f, 15f));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(this.csvStatusText);
        }
    }

    private bool ButtonAt(string label, string id, Vector2 p, Vector2 size,
        Vector4 accent, bool featured = false, bool disabled = false)
    {
        ImGui.SetCursorScreenPos(p);
        if (disabled) ImGui.BeginDisabled();
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        if (disabled) ImGui.EndDisabled();
        var fill = featured && !disabled ? new Vector4(0.16f, 0.27f, 0.23f, 1f)
            : hovered && !disabled ? BackstageDesign.Raised : BackstageDesign.Surface;
        BackstageDesign.Fill(ImGui.GetWindowDrawList(), p, size, fill, 7f);
        BackstageDesign.Frame(ImGui.GetWindowDrawList(), p, size,
            featured && !disabled ? BackstageDesign.Mint : BackstageDesign.SoftLine, 7f);
        var dimensions = ImGui.CalcTextSize(label);
        ImGui.GetWindowDrawList().AddText(p + (size - dimensions) * 0.5f,
            BackstageDesign.Color(disabled ? BackstageDesign.Dim : accent), label);
        return clicked && !disabled;
    }

    private DjDatabaseEntry? SelectedEntry() => this.selectedId is null ? null
        : this.Configuration.DjDatabase.FirstOrDefault(e => e.RuntimeId == this.selectedId);

    private bool DraftIsDirty() => this.creatingNew
        ? !string.IsNullOrWhiteSpace(this.draftName) || !string.IsNullOrWhiteSpace(this.draftLink)
        : !string.Equals(this.draftName, this.originalName, StringComparison.Ordinal) ||
          !string.Equals(this.draftLink, this.originalLink, StringComparison.Ordinal);

    private void EnsureSelection()
    {
        if (this.creatingNew) return;
        var entry = this.SelectedEntry();
        if (entry is not null) return;
        this.LoadSelected(this.Configuration.DjDatabase.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?.RuntimeId);
    }

    private void LoadSelected(string? id)
    {
        this.creatingNew = false;
        this.selectedId = id;
        var entry = this.SelectedEntry();
        this.draftName = this.originalName = entry?.Name ?? string.Empty;
        this.draftLink = this.originalLink = entry?.Link ?? string.Empty;
        this.editorError = string.Empty;
    }

    private void RequestSelection(string? id, bool create)
    {
        if (this.creatingNew == create && (create || id == this.selectedId)) return;
        if (this.DraftIsDirty())
        {
            this.pendingDiscardAction = DiscardAction.SwitchSelection;
            this.requestedSelectionId = id;
            this.requestedCreate = create;
            this.pendingDiscardPopup = true;
            return;
        }
        this.ApplySelection(id, create);
    }

    private void ApplySelection(string? id, bool create)
    {
        if (create)
        {
            this.creatingNew = true;
            this.selectedId = null;
            this.draftName = this.draftLink = this.originalName = this.originalLink = string.Empty;
            this.editorError = string.Empty;
        }
        else this.LoadSelected(id);
    }

    private void RequestCancelDraft()
    {
        if (!this.DraftIsDirty()) { this.CancelDraft(); return; }
        this.pendingDiscardAction = DiscardAction.CancelDraft;
        this.pendingDiscardPopup = true;
    }

    private void RequestCloseWindow()
    {
        if (!this.DraftIsDirty()) { this.CancelDraft(); this.IsOpen = false; return; }
        this.pendingDiscardAction = DiscardAction.CloseWindow;
        this.pendingDiscardPopup = true;
    }

    private void ResolveDiscard()
    {
        switch (this.pendingDiscardAction)
        {
            case DiscardAction.CloseWindow:
                this.CancelDraft();
                this.IsOpen = false;
                break;
            case DiscardAction.CancelDraft:
                this.CancelDraft();
                break;
            default:
                this.ApplySelection(this.requestedSelectionId, this.requestedCreate);
                break;
        }
        this.requestedSelectionId = null;
    }

    private void CancelDraft()
    {
        if (this.creatingNew)
            this.LoadSelected(this.Configuration.DjDatabase.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()?.RuntimeId);
        else
        {
            this.draftName = this.originalName;
            this.draftLink = this.originalLink;
            this.editorError = string.Empty;
        }
    }

    private void SaveDraft()
    {
        var name = System.Text.RegularExpressions.Regex.Replace(this.draftName.Trim(), @"\s+", " ");
        var link = this.draftLink.Trim();
        if (name.Length == 0)
        {
            this.editorError = "Enter a DJ name before saving.";
            return;
        }
        var duplicate = this.Configuration.DjDatabase.Any(e =>
            e.RuntimeId != this.selectedId &&
            string.Equals(System.Text.RegularExpressions.Regex.Replace((e.Name ?? string.Empty).Trim(), @"\s+", " "), name, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            this.editorError = "That DJ name already exists.";
            return;
        }
        DjDatabaseEntry? saved;
        if (this.creatingNew)
        {
            saved = new DjDatabaseEntry { Name = name, Link = link };
            this.Configuration.DjDatabase.Add(saved);
        }
        else
        {
            saved = this.SelectedEntry();
            if (saved is null) return;
            saved.Name = name;
            saved.Link = link;
        }
        this.SaveDatabaseChanges();
        if (!this.creatingNew && !string.Equals(this.originalName, name, StringComparison.Ordinal))
            this.Services.Get<VenueProfileService>().RenameSharedPerson(this.originalName, name, true);
        this.LoadSelected(saved.RuntimeId);
        this.csvStatusText = $"Saved {name}.";
    }

    // Non-modal confirmations: never dim the FFXIV viewport or lock other windows.
    // Explicit choices preserve the database draft/selection safety rules.
    private void DrawDialogs()
    {
        var focusDiscard = this.pendingDiscardPopup;
        var focusRemove = this.pendingRemovePopup;
        if (focusDiscard)
        {
            this.discardPopupOpen = true;
            this.pendingDiscardPopup = false;
        }
        if (focusRemove)
        {
            this.removePopupOpen = true;
            this.pendingRemovePopup = false;
        }
        if (!this.discardPopupOpen && !this.removePopupOpen) return;

        var ownerPos = ImGui.GetWindowPos();
        var ownerSize = ImGui.GetWindowSize();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(20f, 18f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, BackstageDesign.Rail);
        ImGui.PushStyleColor(ImGuiCol.Border, BackstageDesign.SoftLine);

        if (this.discardPopupOpen)
        {
            var size = new Vector2(400f, 175f);
            ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
            ImGui.SetNextWindowPos(BackstageDesign.CenteredWindowPosition(ownerPos, ownerSize, size), ImGuiCond.Appearing);
            if (focusDiscard) ImGui.SetNextWindowFocus();
            if (ImGui.Begin(DiscardPopup, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoTitleBar))
            {
                ImGui.TextColored(BackstageDesign.Ivory, "Discard unsaved DJ changes?");
                ImGui.TextColored(BackstageDesign.Muted, "The current name and link edits have not been saved.");
                ImGui.Spacing();
                if (ImGui.Button("Discard changes", new Vector2(170f, 32f)))
                {
                    this.ResolveDiscard();
                    this.discardPopupOpen = false;
                }
                ImGui.SameLine();
                if (ImGui.Button("Keep editing", new Vector2(155f, 32f)))
                    this.discardPopupOpen = false;
            }
            ImGui.End();
        }
        if (this.removePopupOpen)
        {
            var size = new Vector2(420f, 175f);
            ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
            ImGui.SetNextWindowPos(BackstageDesign.CenteredWindowPosition(ownerPos, ownerSize, size), ImGuiCond.Appearing);
            if (focusRemove) ImGui.SetNextWindowFocus();
            if (ImGui.Begin(RemovePopup, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoTitleBar))
            {
                ImGui.TextColored(BackstageDesign.Ivory, $"Remove {this.pendingRemoveName}?");
                var references = this.Services.Get<VenueProfileService>().CountReferences(this.pendingRemoveName, true);
                ImGui.TextColored(BackstageDesign.Muted,
                    $"Global directory removal. {references} lineup slot(s) in venue profiles will retain their saved names.");
                ImGui.Spacing();
                if (ImGui.Button("Remove DJ", new Vector2(140f, 32f)))
                {
                    if (this.pendingRemoveId is { } id) this.RemoveEntry(id);
                    this.ClearPendingRemove();
                    this.removePopupOpen = false;
                    this.LoadSelected(this.Configuration.DjDatabase.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault()?.RuntimeId);
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel", new Vector2(140f, 32f)))
                {
                    this.ClearPendingRemove();
                    this.removePopupOpen = false;
                }
            }
            ImGui.End();
        }
        if (!this.removePopupOpen) this.ClearPendingRemove();
        if (!this.discardPopupOpen) this.requestedSelectionId = null;
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    private string GetDefaultCsvPath()
        => Path.Combine(this.Services.PluginInterface.ConfigDirectory.FullName, "dj_database.csv");

    private void ExportCsv()
    {
        this.StartCsvDialog(
            isImport: false,
            statusText: "Waiting for export location...",
            dialogFactory: () => this.FileDialogs.ShowSaveCsvDialog("Export DJ Database", this.GetDefaultCsvPath()));
    }

    private void ExportCsv(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

            var builder = new StringBuilder();
            builder.AppendLine("Name,Link");
            foreach (var entry in this.Configuration.DjDatabase.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
                builder.AppendLine($"{EscapeCsv(entry.Name)},{EscapeCsv(entry.Link)}");

            File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
            this.csvStatusText = $"Exported {this.Configuration.DjDatabase.Count} DJs to {path}.";
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
            dialogFactory: () => this.FileDialogs.ShowOpenCsvDialog("Import DJ Database", this.GetDefaultCsvPath()));
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

            var imported = 0;
            foreach (var row in File.ReadAllLines(path).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(row))
                    continue;

                var columns = ParseCsvLine(row);
                if (columns.Count == 0 || string.IsNullOrWhiteSpace(columns[0]))
                    continue;

                var name = columns[0].Trim();
                var link = columns.Count > 1 ? columns[1].Trim() : string.Empty;
                var existing = this.Configuration.DjDatabase.FirstOrDefault(entry =>
                    string.Equals((entry.Name ?? string.Empty).Trim(), name, StringComparison.OrdinalIgnoreCase));

                if (existing is null)
                {
                    this.Configuration.DjDatabase.Add(new DjDatabaseEntry { Name = name, Link = link });
                }
                else
                {
                    existing.Name = name;
                    existing.Link = link;
                }

                imported++;
            }

            this.SaveDatabaseChanges();
            if (!this.DraftIsDirty() && !this.creatingNew) this.LoadSelected(this.selectedId);
            this.csvStatusText = $"Imported/updated {imported} DJs from {path}.";
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

    private static bool MatchesSearch(DjDatabaseEntry entry, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return true;

        return (entry.Name?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (entry.Link?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void OpenRemoveConfirmation(DjDatabaseEntry entry)
    {
        this.pendingRemoveId = entry.RuntimeId;
        this.pendingRemoveName = string.IsNullOrWhiteSpace(entry.Name) ? "this DJ" : entry.Name.Trim();
        this.pendingRemovePopup = true;
    }

    private void RemoveEntry(string runtimeId)
    {
        var entry = this.Configuration.DjDatabase.FirstOrDefault(e => e.RuntimeId == runtimeId);
        if (entry is null) return;
        this.Configuration.DjDatabase.Remove(entry);
        this.cleanSnapshots.Remove(runtimeId);
        this.SaveDatabaseChanges();
    }

    private void ClearPendingRemove()
    {
        this.pendingRemoveId = null;
        this.pendingRemoveName = string.Empty;
        this.pendingRemovePopup = false;
    }

    private void SyncCleanSnapshots()
    {
        this.cleanSnapshots.Clear();
        foreach (var entry in this.Configuration.DjDatabase)
            this.cleanSnapshots[entry.RuntimeId] = (entry.Name, entry.Link);
    }

    private void SaveDatabaseChanges()
    {
        this.Configuration.CommitDjDatabaseChanges();
        this.SyncCleanSnapshots();
    }
}
