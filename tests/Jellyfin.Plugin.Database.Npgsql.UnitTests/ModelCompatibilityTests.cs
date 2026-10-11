using System.Data.Common;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.UnitTests;

public sealed class ModelCompatibilityTests
{
    private const string ConnectionString = "Host=127.0.0.1;Database=model_only;Username=model_only;Password=model_only";
    [Fact]
    public void LateModel_HasExactAuditedShapeAndNoUnboundedPhysicalTextBtree()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        Assert.True((bool)model[NpgsqlModelCustomizer.LateModelAnnotation]!);
        Assert.Equal(NpgsqlModelCustomizer.ModelContract, model[NpgsqlModelCustomizer.ModelContractAnnotation]);
        Assert.Equal(511, Property<BaseItemEntity>(model, nameof(BaseItemEntity.Type)).GetMaxLength());
        Assert.Equal(64, Property<BaseItemEntity>(model, nameof(BaseItemEntity.MediaType)).GetMaxLength());
        Assert.Equal("C", Property<People>(model, nameof(People.Name)).GetCollation());
        Assert.Equal(
            "translate(\"Name\", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')",
            Property<People>(model, "NameLower").GetComputedColumnSql());
        Assert.Equal(512, Property<People>(model, "NameLowerPrefix").GetMaxLength());
        var peoplePrefix = Assert.Single(
            Entity<People>(model).GetIndexes(),
            index => index.GetDatabaseName() == "IX_Peoples_Npgsql_NameLowerPrefix");
        Assert.Equal(["text_pattern_ops"], peoplePrefix.GetOperators());

        Assert.All(
            model.GetEntityTypes().SelectMany(entity => entity.GetIndexes()),
            index =>
            {
                if (index.GetMethod() != "hash")
                {
                    Assert.DoesNotContain(
                        index.Properties,
                        property => property.ClrType == typeof(string) && property.GetMaxLength() is null);
                }
            });

        AssertInternedLogicalKey<BaseItemProvider>(
            model,
            nameof(BaseItemProvider.ProviderId),
            nameof(BaseItemProvider.ItemId),
            nameof(BaseItemProvider.ProviderId));
        AssertInternedLogicalKey<PeopleBaseItemMap>(
            model,
            nameof(PeopleBaseItemMap.Role),
            nameof(PeopleBaseItemMap.ItemId),
            nameof(PeopleBaseItemMap.PeopleId),
            nameof(PeopleBaseItemMap.Role));
        AssertInternedLogicalKey<UserData>(
            model,
            nameof(UserData.CustomDataKey),
            nameof(UserData.ItemId),
            nameof(UserData.UserId),
            nameof(UserData.CustomDataKey));
        AssertInternedUniqueIndex<ItemValue>(
            model,
            nameof(ItemValue.Value),
            nameof(ItemValue.Type),
            "ValueInternId");
        AssertInternedUniqueIndex<CustomItemDisplayPreferences>(
            model,
            nameof(CustomItemDisplayPreferences.Key),
            nameof(CustomItemDisplayPreferences.UserId),
            nameof(CustomItemDisplayPreferences.ItemId),
            nameof(CustomItemDisplayPreferences.Client),
            "KeyInternId");
    }

    [Fact]
    public void FinalizedModel_MatchesRuntimeEnforcedReviewedGoldenFingerprint()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;

        Assert.Equal(
            "3A1CF5F43269EB4987B3E762B380BF8A2D39F639BB69049F3594AEC076257F5E",
            NpgsqlFinalizedModelContract.ExpectedSha256);
        Assert.Equal(NpgsqlModelCustomizer.ModelContract, model[NpgsqlModelCustomizer.ModelContractAnnotation]);
    }

    [Fact]
    public void RuntimeModelGuard_RejectsAnyFinalizedModelDriftBeforeDatabaseUse()
    {
        var commandAttempts = new CommandAttemptInterceptor();
        using var context = CreateDriftedContext(commandAttempts);

        var exception = Assert.Throws<InvalidOperationException>(() => context.BaseItems.Any());

        Assert.Contains("complete finalized Jellyfin database model", exception.Message, StringComparison.Ordinal);
        Assert.Contains("before any schema operation", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, commandAttempts.Count);
    }

    [Fact]
    public void ModelCache_ReusesTheValidatedCompleteModelWithoutLosingPluginMappings()
    {
        using var first = CreateContext();
        using var second = CreateContext();

        Assert.Same(first.Model, second.Model);
        Assert.Equal(512, Property<People>(second.Model, "NameLowerPrefix").GetMaxLength());
        Assert.NotNull(Entity<ItemValue>(second.Model).FindProperty("ValueInternId"));
    }

    [Fact]
    public void TextInterning_UsesBoundedExactChunksAndDatabaseCanonicalization()
    {
        Assert.Equal(128, NpgsqlTextInterning.ChunkSize);
        Assert.Contains("octet_length(\"Chunk\") <= 128", NpgsqlTextInterning.InstallSql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE (\"ParentId\", \"Chunk\", \"IsTerminal\")", NpgsqlTextInterning.InstallSql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (\"ParentId\", \"Chunk\", \"IsTerminal\")", NpgsqlTextInterning.InstallSql, StringComparison.Ordinal);
        Assert.Contains("DO NOTHING", NpgsqlTextInterning.InstallSql, StringComparison.Ordinal);
        Assert.DoesNotContain("DO UPDATE", NpgsqlTextInterning.InstallSql, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCK TABLE public.\"__JellyfinTextNodes\"", NpgsqlTextInterning.InstallSql, StringComparison.Ordinal);
        Assert.Contains("LOCK TABLE public.\"__JellyfinTextNodes\" IN SHARE ROW EXCLUSIVE MODE", NpgsqlTextInterning.InstallCurrentSql, StringComparison.Ordinal);
        Assert.Contains("IF path_exists THEN", NpgsqlTextInterning.InstallCurrentSql, StringComparison.Ordinal);
        Assert.Contains("BEFORE INSERT OR UPDATE ON", NpgsqlTextInterning.InstallSql, StringComparison.Ordinal);
        Assert.DoesNotContain("digest(", NpgsqlTextInterning.InstallSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pgcrypto", NpgsqlTextInterning.InstallSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("convert_to(pattern_character, 'UTF8') = convert_to(escape_value, 'UTF8')", NpgsqlTextInterning.InstallSql, StringComparison.Ordinal);
        Assert.DoesNotContain("ascii_lower\"(escape_value)", NpgsqlTextInterning.InstallSql, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryInterceptor_RewritesLikePeoplePrefixAndNullableOrdering()
    {
        using var context = CreateContext();

        var likeSql = context.ActivityLogs
            .Where(entry => EF.Functions.Like(entry.Name, "%scan\\%%"))
            .ToQueryString();
        Assert.Contains("__jellyfin_sqlite_like", likeSql, StringComparison.Ordinal);
        Assert.DoesNotContain("__jellyfin_sqlite_like_escape", likeSql, StringComparison.Ordinal);

        var explicitlyEscapedLikeSql = context.ActivityLogs
            .Where(entry => EF.Functions.Like(entry.Name, "%scanX%%", "X"))
            .ToQueryString();
        Assert.Contains("__jellyfin_sqlite_like_escape", explicitlyEscapedLikeSql, StringComparison.Ordinal);
        Assert.Contains("'X'", explicitlyEscapedLikeSql, StringComparison.Ordinal);

        var peopleSql = context.Peoples
            .Where(person => person.Name.StartsWith("MIXED"))
            .ToQueryString();
        Assert.Contains("\"NameLowerPrefix\"", peopleSql, StringComparison.Ordinal);
        Assert.Contains("\"NameLower\"", peopleSql, StringComparison.Ordinal);
        Assert.Contains("__jellyfin_ascii_prefix", peopleSql, StringComparison.Ordinal);
        Assert.Contains("__jellyfin_ascii_lower", peopleSql, StringComparison.Ordinal);
        Assert.Contains("LIKE", peopleSql, StringComparison.Ordinal);
        Assert.Contains("ESCAPE ''", peopleSql, StringComparison.Ordinal);

        var ascendingSql = context.BaseItems.OrderBy(item => item.SortName).ToQueryString();
        Assert.Contains("CASE", ascendingSql, StringComparison.Ordinal);
        Assert.Contains("IS NULL", ascendingSql, StringComparison.Ordinal);
        var descendingSql = context.BaseItems.OrderByDescending(item => item.SortName).ToQueryString();
        Assert.Contains("CASE", descendingSql, StringComparison.Ordinal);
        Assert.Contains("DESC", descendingSql, StringComparison.Ordinal);

        Assert.True(context.Database.CreateExecutionStrategy().RetriesOnFailure);
    }

    private static JellyfinDbContext CreateContext()
    {
        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        NpgsqlOptionsBootstrap.Configure(builder, ConnectionString, null);
        return new JellyfinDbContext(
            builder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private static JellyfinDbContext CreateDriftedContext(DbCommandInterceptor commandInterceptor)
    {
        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        NpgsqlOptionsBootstrap.Configure(builder, ConnectionString, null);
        builder.AddInterceptors(commandInterceptor);
        return new DriftedJellyfinDbContext(
            builder.Options,
            provider);
    }

    private sealed class CommandAttemptInterceptor : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Count++;
            return result;
        }
    }

    private static IReadOnlyEntityType Entity<TEntity>(IReadOnlyModel model)
        => Assert.IsAssignableFrom<IReadOnlyEntityType>(model.FindEntityType(typeof(TEntity)));

    private static IReadOnlyProperty Property<TEntity>(IReadOnlyModel model, string name)
        => Assert.IsAssignableFrom<IReadOnlyProperty>(Entity<TEntity>(model).FindProperty(name));

    private static void AssertInternedLogicalKey<TEntity>(
        IReadOnlyModel model,
        string sourceProperty,
        params string[] logicalKey)
    {
        var entity = Entity<TEntity>(model);
        Assert.Equal(logicalKey, entity.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.True((bool)entity.FindPrimaryKey()![NpgsqlTextInterning.LogicalKeyAnnotation]!);
        AssertInternedUniqueIndex<TEntity>(
            model,
            sourceProperty,
            logicalKey[..^1].Concat([sourceProperty + "InternId"]).ToArray());
    }

    private static void AssertInternedUniqueIndex<TEntity>(
        IReadOnlyModel model,
        string sourceProperty,
        params string[] physicalMembers)
    {
        var entity = Entity<TEntity>(model);
        var index = Assert.Single(
            entity.GetIndexes(),
            candidate => candidate.IsUnique
                && candidate.Properties.Select(property => property.Name).SequenceEqual(physicalMembers));
        Assert.Equal(sourceProperty, index[NpgsqlTextInterning.ExactIdentityAnnotation]);
        Assert.Equal(typeof(long), entity.FindProperty(sourceProperty + "InternId")!.ClrType);
    }

    private sealed class DriftedJellyfinDbContext(
        DbContextOptions<JellyfinDbContext> options,
        IJellyfinDatabaseProvider provider)
        : JellyfinDbContext(
            options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance))
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Model.SetAnnotation("ReviewTest:UnreviewedDrift", true);
        }
    }
}
