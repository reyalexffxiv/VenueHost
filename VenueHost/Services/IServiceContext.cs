using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace VenueHost.Services;

/// <summary>
/// Shared access point for Dalamud-provided services and plugin-owned services.
///
/// Plugin.cs remains the only class that receives <c>[PluginService]</c> injection.
/// Other services and windows should receive this context instead of depending on
/// the full plugin object. This keeps the Dalamud lifecycle predictable while the
/// codebase is gradually moved toward smaller services.
/// </summary>
public interface IServiceContext
{
    /// <summary>Dalamud plugin interface for configuration, UI hooks, and plugin paths.</summary>
    IDalamudPluginInterface PluginInterface { get; }

    /// <summary>Dalamud command manager used for slash command registration.</summary>
    ICommandManager CommandManager { get; }

    /// <summary>Dalamud framework service used for per-frame update hooks.</summary>
    IFramework Framework { get; }

    /// <summary>Dalamud plugin logger.</summary>
    IPluginLog Log { get; }

    /// <summary>Dalamud texture provider used for plugin image resources.</summary>
    ITextureProvider TextureProvider { get; }

    /// <summary>
    /// Register a plugin-owned service instance in the context.
    /// </summary>
    /// <typeparam name="T">The concrete service type or interface to register.</typeparam>
    /// <param name="service">The service instance.</param>
    void Add<T>(T service)
        where T : class;

    /// <summary>
    /// Resolve a previously registered plugin-owned service.
    /// </summary>
    /// <typeparam name="T">The concrete service type or interface to resolve.</typeparam>
    /// <returns>The registered service instance.</returns>
    T Get<T>()
        where T : class;
}
