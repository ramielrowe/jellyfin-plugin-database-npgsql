using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Database.Npgsql.Migrations.Design;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.UnitTests;

public sealed class MigrationContractTests
{
    private const string ConnectionString =
        "Host=127.0.0.1;Port=1;Database=migration_contract;Username=migration_contract;Password=not-used;Pooling=false;Timeout=1";

    [Fact]
    public void RuntimeContext_OwnsFreshMigrationLineageSnapshotAndIdempotentScript()
    {
        using var context = CreateRuntimeContext();

        Assert.Equal(
            [
                "20261009061101_InitialPostgreSqlBaseline",
                "20261009061446_AddActivityLogTypeDateIndex",
                "20261009113458_AddUuidMinMaxAggregates",
            ],
            context.Database.GetMigrations());
        Assert.False(context.Database.HasPendingModelChanges());

        var script = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("20261009061101_InitialPostgreSqlBaseline", script, StringComparison.Ordinal);
        Assert.Contains("20261009061446_AddActivityLogTypeDateIndex", script, StringComparison.Ordinal);
        Assert.Contains("20261009113458_AddUuidMinMaxAggregates", script, StringComparison.Ordinal);
        Assert.Contains("__JellyfinTextNodes", script, StringComparison.Ordinal);
        Assert.Contains("CREATE AGGREGATE public.min(uuid)", script, StringComparison.Ordinal);
        Assert.Contains("CREATE AGGREGATE public.max(uuid)", script, StringComparison.Ordinal);
        Assert.Contains("IX_ActivityLogs_Npgsql_Type_DateCreated", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Digest", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DesignFactory_RecreatesTheExactReviewedModelWithoutParentCheckout()
    {
        using var context = new NpgsqlDesignTimeDbContextFactory().CreateDbContext([]);
        var model = context.Model;

        Assert.Equal(NpgsqlModelCustomizer.ModelContract, model[NpgsqlModelCustomizer.ModelContractAnnotation]);
        Assert.Equal(NpgsqlFinalizedModelContract.ExpectedSha256, NpgsqlFinalizedModelContract.CalculateFingerprint(model));
    }

    [Fact]
    public void HostMigrationAudit_AcceptsOnlyTheExactPinnedSet()
    {
        Assert.Equal(
            "20260522092304",
            new DateTime(2026, 5, 22, 9, 23, 4, DateTimeKind.Unspecified)
                .ToString("yyyyMMddHHmmsss", System.Globalization.CultureInfo.InvariantCulture));
        NpgsqlHostMigrationAudit.ValidateMigrationIds(NpgsqlHostMigrationAudit.AuditedMigrationIds);

        var unknown = NpgsqlHostMigrationAudit.AuditedMigrationIds.Append("20990101000000_UnknownMigration");
        var exception = Assert.Throws<NotSupportedException>(() => NpgsqlHostMigrationAudit.ValidateMigrationIds(unknown));
        Assert.Contains("20990101000000_UnknownMigration", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No database connection was attempted", exception.Message, StringComparison.Ordinal);

        var duplicate = NpgsqlHostMigrationAudit.AuditedMigrationIds.Append(NpgsqlHostMigrationAudit.AuditedMigrationIds[0]);
        Assert.Throws<NotSupportedException>(() => NpgsqlHostMigrationAudit.ValidateMigrationIds(duplicate));
    }

    [Fact]
    public void HostMigrationAudit_ClassifiesEveryPinnedMigrationForFreshSetup()
    {
        Assert.Equal(49, NpgsqlHostMigrationAudit.AuditedMigrationIds.Length);
        Assert.Equal(
            49,
            NpgsqlHostMigrationAudit.AuditedMigrationIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            [
                "20250420090000_AddDefaultPluginRepository",
                "20250420110000_ReaddDefaultPluginRepository",
                "20250420160000_AddDefaultCastReceivers",
                "20250420170000_UpdateDefaultPluginRepository",
                "20250420230000_MoveTrickplayFiles",
            ],
            NpgsqlHostMigrationAudit.AuditedSetupMigrationIds);
        Assert.Equal(
            [
                "20260522092304_UpdateNormalizedUsername",
                "20260531160000_DisableLegacyAuthorization",
            ],
            NpgsqlHostMigrationAudit.AuditedCoreInitialisationMigrationIds);

        var setup = NpgsqlHostMigrationAudit.AuditedSetupMigrationIds.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(5, setup.Count);
        Assert.All(setup, migrationId => Assert.Contains(migrationId, NpgsqlHostMigrationAudit.AuditedMigrationIds));

        var seeded = NpgsqlHostMigrationAudit.AuditedMigrationIds
            .Where(migrationId => !setup.Contains(migrationId))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(44, seeded.Count);
        Assert.All(
            NpgsqlHostMigrationAudit.AuditedCoreInitialisationMigrationIds,
            migrationId => Assert.Contains(migrationId, seeded));
        Assert.Equal(49, setup.Concat(seeded).Distinct(StringComparer.Ordinal).Count());
    }

    private static JellyfinDbContext CreateRuntimeContext()
    {
        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
        var options = new DbContextOptionsBuilder<JellyfinDbContext>();
        NpgsqlOptionsBootstrap.Configure(options, ConnectionString, null);
        return new JellyfinDbContext(
            options.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
