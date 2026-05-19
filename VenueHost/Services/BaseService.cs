using System;
using Dalamud.Plugin.Services;

namespace VenueHost.Services;

/// <summary>
/// Common base type for Venue Host services.
///
/// Services should keep domain logic out of windows and out of <see cref="Plugin"/>.
/// They receive the shared configuration and service context explicitly so their
/// dependencies stay easy to see and easy to test during future refactors.
/// </summary>
public abstract class BaseService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BaseService"/> class.
    /// </summary>
    protected BaseService(Configuration configuration, IServiceContext services)
    {
        this.Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>Shared plugin configuration.</summary>
    protected Configuration Configuration { get; }

    /// <summary>Shared Dalamud and plugin service context.</summary>
    protected IServiceContext Services { get; }

    /// <summary>Convenience logger accessor for service implementations.</summary>
    protected IPluginLog Log => this.Services.Log;
}
