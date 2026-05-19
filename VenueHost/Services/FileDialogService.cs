using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace VenueHost.Services;

/// <summary>
/// Opens native Windows file dialogs away from Dalamud's UI draw thread.
///
/// Windows Forms file dialogs must run on an STA thread. Blocking the ImGui
/// draw call while that thread is open makes Dalamud report a UiBuilder hitch,
/// especially when the user spends a few seconds choosing an import file.
/// This service starts the dialog on a short-lived STA thread and lets windows
/// poll the result on later frames.
/// </summary>
public sealed class FileDialogService : BaseService
{
    public FileDialogService(Configuration configuration, IServiceContext services)
        : base(configuration, services)
    {
    }

    /// <summary>
    /// Starts a native Save File dialog and returns a pending result handle.
    /// </summary>
    public PendingFileDialog ShowSaveCsvDialog(string title, string defaultPath)
        => PendingFileDialog.Start(() =>
        {
            using var dialog = new SaveFileDialog
            {
                Title = title,
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                AddExtension = true,
                DefaultExt = "csv",
                FileName = Path.GetFileName(defaultPath),
                InitialDirectory = Path.GetDirectoryName(defaultPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                OverwritePrompt = true,
            };

            return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
        });

    /// <summary>
    /// Starts a native Open File dialog and returns a pending result handle.
    /// </summary>
    public PendingFileDialog ShowOpenCsvDialog(string title, string defaultPath)
        => PendingFileDialog.Start(() =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                FileName = Path.GetFileName(defaultPath),
                InitialDirectory = Path.GetDirectoryName(defaultPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };

            return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
        });
}

/// <summary>
/// Represents a file dialog that is still running on its STA dialog thread.
/// </summary>
public sealed class PendingFileDialog
{
    private readonly object syncRoot = new();
    private string? result;
    private Exception? exception;
    private bool completed;

    private PendingFileDialog()
    {
    }

    /// <summary>
    /// Starts a dialog factory on an STA background thread.
    /// </summary>
    public static PendingFileDialog Start(Func<string?> dialogFactory)
    {
        ArgumentNullException.ThrowIfNull(dialogFactory);
        var pending = new PendingFileDialog();

        var thread = new Thread(() =>
        {
            try
            {
                var dialogResult = dialogFactory();
                pending.Complete(dialogResult, null);
            }
            catch (Exception ex)
            {
                pending.Complete(null, ex);
            }
        })
        {
            IsBackground = true,
            Name = "VenueHostFileDialog",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return pending;
    }

    /// <summary>
    /// Returns true once the dialog has completed. This property is safe to poll
    /// from the Dalamud UI thread.
    /// </summary>
    public bool IsCompleted
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.completed;
            }
        }
    }

    /// <summary>
    /// Tries to consume the dialog result. Returns false while the dialog is
    /// still open, true once a result, cancellation, or exception is available.
    /// </summary>
    public bool TryGetResult(out string? filePath, out Exception? dialogException)
    {
        lock (this.syncRoot)
        {
            filePath = this.result;
            dialogException = this.exception;
            return this.completed;
        }
    }

    private void Complete(string? filePath, Exception? dialogException)
    {
        lock (this.syncRoot)
        {
            this.result = filePath;
            this.exception = dialogException;
            this.completed = true;
        }
    }
}
