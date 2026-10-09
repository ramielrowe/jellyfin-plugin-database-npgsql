using System.Security.Cryptography;
using System.Text;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.UnitTests;

public sealed class ModelCompatibilityTests
{
    private const string ConnectionString = "Host=127.0.0.1;Database=model_only;Username=model_only;Password=model_only";
    private const string GoldenDebugModelSha256 = "5253BCCAD1F7E190C991A9F9926917EBE3CBCD5F2413AD079DB07E9CF7393C6D";

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
    public void DebugModel_MatchesReviewedGoldenFingerprint()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var debugModel = model.ToDebugString(MetadataDebugStringOptions.LongDefault);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(debugModel)));

        Assert.Equal(GoldenDebugModelSha256, fingerprint);
    }

    [Fact]
    public void TextInterning_UsesBoundedExactChunksAndDatabaseCanonicalization()
    {
        Assert.Equal(128, NpgsqlTextInterning.ChunkSize);
        Assert.Contains("octet_length(\"Chunk\") <= 128", NpgsqlTextInterning.InstallSql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE (\"ParentId\", \"Chunk\", \"IsTerminal\")", NpgsqlTextInterning.InstallSql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (\"ParentId\", \"Chunk\", \"IsTerminal\")", NpgsqlTextInterning.InstallSql, StringComparison.Ordinal);
        Assert.DoesNotContain("digest(", NpgsqlTextInterning.InstallSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pgcrypto", NpgsqlTextInterning.InstallSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QueryInterceptor_RewritesLikePeoplePrefixAndNullableOrdering()
    {
        using var context = CreateContext();

        var likeSql = context.ActivityLogs
            .Where(entry => EF.Functions.Like(entry.Name, "%scan\\%%"))
            .ToQueryString();
        Assert.Contains("__jellyfin_sqlite_like", likeSql, StringComparison.Ordinal);
        Assert.Contains("'\\'", likeSql, StringComparison.Ordinal);

        var peopleSql = context.Peoples
            .Where(person => person.Name.StartsWith("MIXED"))
            .ToQueryString();
        Assert.Contains("\"NameLower\"", peopleSql, StringComparison.Ordinal);
        Assert.Contains("__jellyfin_ascii_lower", peopleSql, StringComparison.Ordinal);

        var ascendingSql = context.BaseItems.OrderBy(item => item.SortName).ToQueryString();
        Assert.Contains("CASE", ascendingSql, StringComparison.Ordinal);
        Assert.Contains("IS NULL", ascendingSql, StringComparison.Ordinal);
        var descendingSql = context.BaseItems.OrderByDescending(item => item.SortName).ToQueryString();
        Assert.Contains("CASE", descendingSql, StringComparison.Ordinal);
        Assert.Contains("DESC", descendingSql, StringComparison.Ordinal);
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
}
