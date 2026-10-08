namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Contains validated Npgsql settings. Callers must never log <see cref="ConnectionString"/>.
/// </summary>
/// <param name="ConnectionString">The normalized private connection string.</param>
/// <param name="Host">The endpoint host.</param>
/// <param name="Port">The endpoint port.</param>
/// <param name="Database">The endpoint database.</param>
/// <param name="SslMode">The non-secret TLS mode.</param>
/// <param name="Pooling">Whether pooling is enabled.</param>
/// <param name="PassfileConfigured">Whether a passfile was configured.</param>
/// <param name="CommandTimeout">The bounded command timeout.</param>
internal sealed record NpgsqlConnectionSettings(
    string ConnectionString,
    string Host,
    int Port,
    string Database,
    string SslMode,
    bool Pooling,
    bool PassfileConfigured,
    int CommandTimeout);
