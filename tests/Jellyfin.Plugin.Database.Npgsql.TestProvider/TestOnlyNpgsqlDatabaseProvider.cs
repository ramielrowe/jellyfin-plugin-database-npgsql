using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Database.Npgsql.TestProvider;

/// <summary>
/// Test-only provider entry point which bridges the unfinished backup bead when a
/// disposable database has already been created at the current model.
/// </summary>
/// <remarks>
/// This assembly is never included in the release package. Its no-op backup token is
/// safe only because the full-host test owns and destroys the entire database.
/// </remarks>
public sealed class TestOnlyNpgsqlDatabaseProvider(ILogger<NpgsqlDatabaseProvider> logger)
    : NpgsqlDatabaseProvider(logger)
{
    /// <summary>The test-only provider assembly file name.</summary>
    public const string AssemblyFileName = "Jellyfin.Plugin.Database.Npgsql.TestProvider.dll";

    /// <inheritdoc />
    protected override string RequiredPluginAssembly => AssemblyFileName;

    /// <inheritdoc />
    public override Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult("test-only-disposable-precreated-model");
    }

    /// <inheritdoc />
    public override Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task DeleteBackup(string key) => Task.CompletedTask;
}
