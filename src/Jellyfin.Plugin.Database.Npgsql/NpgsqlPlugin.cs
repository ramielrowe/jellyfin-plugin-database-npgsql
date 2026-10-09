using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Exposes the early database-provider assembly to Jellyfin's ordinary plugin lifecycle.
/// </summary>
public sealed class NpgsqlPlugin : BasePlugin<BasePluginConfiguration>
{
    /// <summary>Initializes a new instance of the <see cref="NpgsqlPlugin"/> class.</summary>
    /// <param name="applicationPaths">The stock application paths.</param>
    /// <param name="xmlSerializer">The stock configuration serializer.</param>
    public NpgsqlPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    /// <inheritdoc />
    public override string Name => PluginMetadata.Name;

    /// <inheritdoc />
    public override string Description
        => "PostgreSQL database provider with provider-bound stock backup and restore.";

    /// <inheritdoc />
    public override Guid Id => PluginMetadata.Id;
}
