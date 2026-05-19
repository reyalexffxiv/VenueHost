using System;
using System.Reflection;
using Dalamud.Interface.Textures;

namespace VenueHost.Services;

/// <summary>
/// Coordinates UI-only actions that should not require windows to depend on the
/// full <see cref="Plugin"/> object.
/// </summary>
/// <remarks>
/// The service intentionally exposes small, explicit methods instead of giving
/// windows direct access to the plugin entry point. Plugin.cs still owns the
/// actual Dalamud window instances and wires these callbacks during startup.
/// </remarks>
public sealed class UiService : BaseService, IDisposable
{
    private const string MascotResourceName = "VenueHost.Assets.cute_peepo.png";

    private Action? toggleMainUi;
    private Action? toggleConfigUi;
    private Action? toggleDjDatabaseUi;
    private Action? toggleStaffDatabaseUi;

    /// <summary>
    /// Initializes a new instance of the <see cref="UiService"/> class.
    /// </summary>
    public UiService(Configuration configuration, IServiceContext services)
        : base(configuration, services)
    {
        this.MascotTexture = this.LoadMascotTexture();
    }

    /// <summary>Optional mascot texture used by the main window.</summary>
    public ISharedImmediateTexture? MascotTexture { get; private set; }

    /// <summary>
    /// Connects the service to the window instances owned by <see cref="Plugin"/>.
    /// </summary>
    /// <remarks>
    /// Keeping this as explicit delegate wiring avoids turning this small service
    /// into a second window manager. Dalamud window lifetime remains centralized
    /// in Plugin.cs.
    /// </remarks>
    public void BindWindowToggles(
        Action toggleMainUi,
        Action toggleConfigUi,
        Action toggleDjDatabaseUi,
        Action toggleStaffDatabaseUi)
    {
        this.toggleMainUi = toggleMainUi ?? throw new ArgumentNullException(nameof(toggleMainUi));
        this.toggleConfigUi = toggleConfigUi ?? throw new ArgumentNullException(nameof(toggleConfigUi));
        this.toggleDjDatabaseUi = toggleDjDatabaseUi ?? throw new ArgumentNullException(nameof(toggleDjDatabaseUi));
        this.toggleStaffDatabaseUi = toggleStaffDatabaseUi ?? throw new ArgumentNullException(nameof(toggleStaffDatabaseUi));
    }

    /// <summary>Toggle the main Venue Host window.</summary>
    public void ToggleMainUi() => this.InvokeIfBound(this.toggleMainUi, nameof(this.ToggleMainUi));

    /// <summary>Toggle the settings window.</summary>
    public void ToggleConfigUi() => this.InvokeIfBound(this.toggleConfigUi, nameof(this.ToggleConfigUi));

    /// <summary>Toggle the DJ database window.</summary>
    public void ToggleDjDatabaseUi() => this.InvokeIfBound(this.toggleDjDatabaseUi, nameof(this.ToggleDjDatabaseUi));

    /// <summary>Toggle the staff database window.</summary>
    public void ToggleStaffDatabaseUi() => this.InvokeIfBound(this.toggleStaffDatabaseUi, nameof(this.ToggleStaffDatabaseUi));

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.MascotTexture is IDisposable disposable)
            disposable.Dispose();

        this.MascotTexture = null;
        this.toggleMainUi = null;
        this.toggleConfigUi = null;
        this.toggleDjDatabaseUi = null;
        this.toggleStaffDatabaseUi = null;
    }

    private ISharedImmediateTexture? LoadMascotTexture()
    {
        try
        {
            return this.Services.TextureProvider.GetFromManifestResource(
                Assembly.GetExecutingAssembly(),
                MascotResourceName);
        }
        catch (Exception ex)
        {
            this.Log.Warning(ex, "Could not load Venue Host mascot texture.");
            return null;
        }
    }

    private void InvokeIfBound(Action? action, string actionName)
    {
        if (action is not null)
        {
            action();
            return;
        }

        this.Log.Warning("UI action {ActionName} was requested before window toggles were bound.", actionName);
    }
}
