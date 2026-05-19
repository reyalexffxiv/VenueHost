using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace VenueHost.Services;

/// <summary>
/// Runtime service container for Venue Host.
///
/// This is intentionally small and explicit. It is not a general dependency
/// injection framework; it is a stable bridge that lets the plugin move logic
/// out of <see cref="Plugin"/> without changing Dalamud's construction model.
/// </summary>
public sealed class ServiceContext : IServiceContext
{
    private readonly Dictionary<Type, object> services = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceContext"/> class.
    /// </summary>
    public ServiceContext(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IPluginLog log,
        ITextureProvider textureProvider)
    {
        this.PluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        this.CommandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        this.Framework = framework ?? throw new ArgumentNullException(nameof(framework));
        this.Log = log ?? throw new ArgumentNullException(nameof(log));
        this.TextureProvider = textureProvider ?? throw new ArgumentNullException(nameof(textureProvider));
    }

    /// <inheritdoc />
    public IDalamudPluginInterface PluginInterface { get; }

    /// <inheritdoc />
    public ICommandManager CommandManager { get; }

    /// <inheritdoc />
    public IFramework Framework { get; }

    /// <inheritdoc />
    public IPluginLog Log { get; }

    /// <inheritdoc />
    public ITextureProvider TextureProvider { get; }

    /// <inheritdoc />
    public void Add<T>(T service)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        this.services[typeof(T)] = service;
    }

    /// <inheritdoc />
    public T Get<T>()
        where T : class
    {
        if (this.services.TryGetValue(typeof(T), out var service))
            return (T)service;

        throw new InvalidOperationException($"Service '{typeof(T).FullName}' has not been registered.");
    }
}
