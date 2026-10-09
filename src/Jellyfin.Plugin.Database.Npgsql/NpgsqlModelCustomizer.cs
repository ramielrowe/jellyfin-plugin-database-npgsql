using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Applies Npgsql model changes after Jellyfin has applied its shared configurations.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="NpgsqlModelCustomizer"/> class.
/// </remarks>
/// <param name="dependencies">The EF model customizer dependencies.</param>
public sealed class NpgsqlModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    /// <summary>The annotation proving the late model hook ran.</summary>
    internal const string LateModelAnnotation = "JellyfinNpgsql:LateModelHook";

    /// <summary>The annotation identifying the audited vanilla model contract.</summary>
    internal const string ModelContractAnnotation = "JellyfinNpgsql:ModelContract";

    /// <summary>The exact vanilla model contract supported by this release.</summary>
    internal const string ModelContract = "jellyfin-12.2-model-v1";

    /// <inheritdoc />
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);
        base.Customize(modelBuilder, context);

        ConfigureBaseItems(modelBuilder);
        ConfigurePeople(modelBuilder);
        ConfigureBoundedIdentifiers(modelBuilder);
        ConfigureLosslessTextKeys(modelBuilder);
        ConfigureOrdinalText(modelBuilder);
        ConfigureQueryFunctions(modelBuilder);
        AssertSafeFinalModel(modelBuilder.Model);

        modelBuilder.Model.SetAnnotation(LateModelAnnotation, true);
        modelBuilder.Model.SetAnnotation(ModelContractAnnotation, ModelContract);
    }

    private static void ConfigureBaseItems(ModelBuilder modelBuilder)
    {
        var items = modelBuilder.Entity<BaseItemEntity>();
        RequireProperty(items.Metadata, nameof(BaseItemEntity.Type), typeof(string));
        RequireProperty(items.Metadata, nameof(BaseItemEntity.MediaType), typeof(string));
        items.Property(entity => entity.Type).HasMaxLength(511);
        items.Property(entity => entity.MediaType).HasMaxLength(64);

        RemoveIndex(items.Metadata, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.SeriesPresentationUniqueKey), nameof(BaseItemEntity.PresentationUniqueKey), nameof(BaseItemEntity.SortName));
        RemoveIndex(items.Metadata, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.SeriesPresentationUniqueKey), nameof(BaseItemEntity.IsFolder), nameof(BaseItemEntity.IsVirtualItem));
        RemoveIndex(items.Metadata, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.PresentationUniqueKey));
        RemoveIndex(items.Metadata, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.IsVirtualItem), nameof(BaseItemEntity.PresentationUniqueKey), nameof(BaseItemEntity.DateCreated));
        RemoveIndex(items.Metadata, nameof(BaseItemEntity.IsFolder), nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.IsVirtualItem), nameof(BaseItemEntity.PresentationUniqueKey), nameof(BaseItemEntity.DateCreated));
        RemoveIndex(items.Metadata, nameof(BaseItemEntity.MediaType), nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.IsVirtualItem), nameof(BaseItemEntity.PresentationUniqueKey));
        RemoveIndex(items.Metadata, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.SortName));
        RemoveIndex(items.Metadata, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.SeriesPresentationUniqueKey), nameof(BaseItemEntity.ParentIndexNumber), nameof(BaseItemEntity.IndexNumber));
        RemoveIndex(items.Metadata, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.CleanName));

        ConfigureHashIndex(items, nameof(BaseItemEntity.Path));
        ConfigureHashIndex(items, nameof(BaseItemEntity.Name));
        ConfigureHashIndex(items, nameof(BaseItemEntity.CleanName));
        ConfigureHashIndex(items, nameof(BaseItemEntity.PresentationUniqueKey));
        ConfigureHashIndex(items, nameof(BaseItemEntity.SeriesPresentationUniqueKey));
        ConfigureHashIndex(items, nameof(BaseItemEntity.SeriesName));
        ConfigureHashIndex(items, nameof(BaseItemEntity.SortName));

        items.HasIndex(entity => entity.Type)
            .HasDatabaseName("IX_BaseItems_Npgsql_Type");
        items.HasIndex(entity => new { entity.Type, entity.TopParentId })
            .HasDatabaseName("IX_BaseItems_Npgsql_Type_TopParentId");
        items.HasIndex(entity => new { entity.Type, entity.IsFolder, entity.IsVirtualItem })
            .HasDatabaseName("IX_BaseItems_Npgsql_Type_IsFolder_IsVirtualItem");
        items.HasIndex(entity => new { entity.IsFolder, entity.TopParentId, entity.IsVirtualItem })
            .HasDatabaseName("IX_BaseItems_Npgsql_IsFolder_TopParentId_IsVirtualItem");
        items.HasIndex(entity => new { entity.MediaType, entity.TopParentId, entity.IsVirtualItem })
            .HasDatabaseName("IX_BaseItems_Npgsql_MediaType_TopParentId_IsVirtualItem");
        items.HasIndex(entity => new { entity.Type, entity.ParentIndexNumber, entity.IndexNumber })
            .HasDatabaseName("IX_BaseItems_Npgsql_Type_ParentIndexNumber_IndexNumber");
        items.HasIndex(entity => new { entity.Type, entity.TopParentId, entity.IsVirtualItem, entity.DateCreated })
            .HasDatabaseName("IX_BaseItems_Npgsql_Type_TopParentId_IsVirtualItem_DateCreated");
    }

    private static void ConfigurePeople(ModelBuilder modelBuilder)
    {
        var people = modelBuilder.Entity<People>();
        RemoveIndex(people.Metadata, nameof(People.Name));
        ConfigureHashIndex(people, nameof(People.Name));

        // SQLite's default LIKE/prefix behavior folds ASCII only. translate() gives the
        // query interceptor an indexable expression with exactly that behavior without
        // requiring citext or a locale-dependent lower().
        people.Property<string>("NameLower")
            .HasComputedColumnSql(
                "translate(\"Name\", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')",
                stored: true)
            .UseCollation("C");
        people.Property<string>("NameLowerPrefix")
            .HasMaxLength(512)
            .HasComputedColumnSql(
                "left(translate(\"Name\", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 512)",
                stored: true)
            .UseCollation("C");
        people.HasIndex("NameLowerPrefix")
            .HasDatabaseName("IX_Peoples_Npgsql_NameLowerPrefix")
            .HasMethod("btree")
            .HasOperators("text_pattern_ops");
    }

    private static void ConfigureBoundedIdentifiers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaStreamInfo>().Property(entity => entity.Language).HasMaxLength(64);
        modelBuilder.Entity<ApiKey>().Property(entity => entity.AccessToken).HasMaxLength(64);
        modelBuilder.Entity<Device>().Property(entity => entity.AccessToken).HasMaxLength(64);
        modelBuilder.Entity<DeviceOptions>().Property(entity => entity.DeviceId).HasMaxLength(256);
    }

    private static void ConfigureLosslessTextKeys(ModelBuilder modelBuilder)
    {
        var providers = modelBuilder.Entity<BaseItemProvider>();
        RequireKey(providers.Metadata, nameof(BaseItemProvider.ItemId), nameof(BaseItemProvider.ProviderId));
        RemoveIndex(providers.Metadata, nameof(BaseItemProvider.ProviderId), nameof(BaseItemProvider.ItemId), nameof(BaseItemProvider.ProviderValue));
        providers.Property(entity => entity.ProviderId).IsRequired();
        ConfigureHashIndex(providers, nameof(BaseItemProvider.ProviderId));
        ConfigureHashIndex(providers, nameof(BaseItemProvider.ProviderValue));
        ConfigureInternedIdentity(
            providers,
            nameof(BaseItemProvider.ProviderId),
            true,
            nameof(BaseItemProvider.ItemId));

        var peopleMap = modelBuilder.Entity<PeopleBaseItemMap>();
        RequireKey(peopleMap.Metadata, nameof(PeopleBaseItemMap.ItemId), nameof(PeopleBaseItemMap.PeopleId), nameof(PeopleBaseItemMap.Role));
        peopleMap.Property(entity => entity.Role).IsRequired();
        ConfigureHashIndex(peopleMap, nameof(PeopleBaseItemMap.Role));
        ConfigureInternedIdentity(
            peopleMap,
            nameof(PeopleBaseItemMap.Role),
            true,
            nameof(PeopleBaseItemMap.ItemId),
            nameof(PeopleBaseItemMap.PeopleId));

        var userData = modelBuilder.Entity<UserData>();
        RequireKey(userData.Metadata, nameof(UserData.ItemId), nameof(UserData.UserId), nameof(UserData.CustomDataKey));
        userData.Property(entity => entity.CustomDataKey).IsRequired();
        ConfigureHashIndex(userData, nameof(UserData.CustomDataKey));
        ConfigureInternedIdentity(
            userData,
            nameof(UserData.CustomDataKey),
            true,
            nameof(UserData.ItemId),
            nameof(UserData.UserId));

        var itemValues = modelBuilder.Entity<ItemValue>();
        RemoveIndex(itemValues.Metadata, nameof(ItemValue.Type), nameof(ItemValue.CleanValue));
        RemoveIndex(itemValues.Metadata, nameof(ItemValue.Type), nameof(ItemValue.Value));
        itemValues.HasIndex(entity => entity.Type)
            .HasDatabaseName("IX_ItemValues_Npgsql_Type");
        ConfigureHashIndex(itemValues, nameof(ItemValue.Value));
        ConfigureHashIndex(itemValues, nameof(ItemValue.CleanValue));
        ConfigureInternedIdentity(
            itemValues,
            nameof(ItemValue.Value),
            false,
            nameof(ItemValue.Type));

        var preferences = modelBuilder.Entity<CustomItemDisplayPreferences>();
        RemoveIndex(
            preferences.Metadata,
            nameof(CustomItemDisplayPreferences.UserId),
            nameof(CustomItemDisplayPreferences.ItemId),
            nameof(CustomItemDisplayPreferences.Client),
            nameof(CustomItemDisplayPreferences.Key));
        ConfigureHashIndex(preferences, nameof(CustomItemDisplayPreferences.Key));
        ConfigureInternedIdentity(
            preferences,
            nameof(CustomItemDisplayPreferences.Key),
            false,
            nameof(CustomItemDisplayPreferences.UserId),
            nameof(CustomItemDisplayPreferences.ItemId),
            nameof(CustomItemDisplayPreferences.Client));
    }

    private static void ConfigureOrdinalText(ModelBuilder modelBuilder)
    {
        var items = modelBuilder.Entity<BaseItemEntity>();
        items.Property(entity => entity.SortName).UseCollation("C");
        items.Property(entity => entity.CleanName).UseCollation("C");
        items.Property(entity => entity.OriginalTitle).UseCollation("C");
        modelBuilder.Entity<People>().Property(entity => entity.Name).UseCollation("C");

        var activity = modelBuilder.Entity<ActivityLog>();
        activity.Property(entity => entity.Name).UseCollation("C");
        activity.Property(entity => entity.Overview).UseCollation("C");
        activity.Property(entity => entity.ShortOverview).UseCollation("C");
        activity.Property(entity => entity.Type).UseCollation("C");

        var users = modelBuilder.Entity<User>();
        users.Property(entity => entity.Username).UseCollation("C");
        users.Property(entity => entity.NormalizedUsername).UseCollation("C");
    }

    private static void ConfigureQueryFunctions(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDbFunction(
                typeof(NpgsqlQueryDbFunctions).GetMethod(
                    nameof(NpgsqlQueryDbFunctions.AsciiLower),
                    BindingFlags.Public | BindingFlags.Static)!)
            .HasName("__jellyfin_ascii_lower")
            .HasSchema("public");
        modelBuilder.HasDbFunction(
                typeof(NpgsqlQueryDbFunctions).GetMethod(
                    nameof(NpgsqlQueryDbFunctions.AsciiPrefix),
                    BindingFlags.Public | BindingFlags.Static)!)
            .HasName("__jellyfin_ascii_prefix")
            .HasSchema("public");
        modelBuilder.HasDbFunction(
                typeof(NpgsqlQueryDbFunctions).GetMethod(
                    nameof(NpgsqlQueryDbFunctions.SqliteLike),
                    BindingFlags.Public | BindingFlags.Static,
                    [typeof(string), typeof(string)])!)
            .HasName("__jellyfin_sqlite_like")
            .HasSchema("public");
        modelBuilder.HasDbFunction(
                typeof(NpgsqlQueryDbFunctions).GetMethod(
                    nameof(NpgsqlQueryDbFunctions.SqliteLikeWithEscape),
                    BindingFlags.Public | BindingFlags.Static)!)
            .HasName("__jellyfin_sqlite_like_escape")
            .HasSchema("public");
    }

    private static void ConfigureInternedIdentity<TEntity>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> builder,
        string sourceProperty,
        bool replacesPhysicalPrimaryKey,
        params string[] prefixProperties)
        where TEntity : class
    {
        RequireProperty(builder.Metadata, sourceProperty, typeof(string));
        foreach (var prefixProperty in prefixProperties)
        {
            if (builder.Metadata.FindProperty(prefixProperty) is null)
            {
                throw ModelDrift($"property '{builder.Metadata.Name}.{prefixProperty}' is missing");
            }
        }

        var internProperty = sourceProperty + "InternId";
        builder.Property<long>(internProperty)
            .ValueGeneratedOnAddOrUpdate();

        var physicalMembers = prefixProperties.Concat([internProperty]).ToArray();
        var physicalIndex = builder.HasIndex(physicalMembers).IsUnique().Metadata;
        physicalIndex.SetAnnotation(NpgsqlTextInterning.ExactIdentityAnnotation, sourceProperty);
        physicalIndex.SetAnnotation(NpgsqlTextInterning.ReplacesPrimaryKeyAnnotation, replacesPhysicalPrimaryKey);

        var logicalKey = builder.Metadata.FindPrimaryKey();
        if (replacesPhysicalPrimaryKey)
        {
            logicalKey!.SetAnnotation(NpgsqlTextInterning.LogicalKeyAnnotation, true);
        }
    }

    private static void ConfigureHashIndex<TEntity>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> builder,
        string propertyName)
        where TEntity : class
    {
        RequireProperty(builder.Metadata, propertyName, typeof(string));
        builder.HasIndex(propertyName).HasMethod("hash");
    }

    private static void AssertSafeFinalModel(IMutableModel model)
    {
        var unsafeIndexes = new List<string>();
        foreach (var entityType in model.GetEntityTypes())
        {
            var indexes = entityType.GetIndexes().Where(index => index.GetMethod() != "hash");
            foreach (var index in indexes)
            {
                var unbounded = index.Properties
                    .Where(property => property.ClrType == typeof(string) && property.GetMaxLength() is null)
                    .Select(property => property.Name)
                    .ToArray();
                if (unbounded.Length > 0)
                {
                    unsafeIndexes.Add($"{entityType.Name}({string.Join(", ", index.Properties.Select(property => property.Name))})");
                }
            }

            var primaryKey = entityType.FindPrimaryKey();
            if (primaryKey is not null
                && primaryKey.FindAnnotation(NpgsqlTextInterning.LogicalKeyAnnotation) is null
                && primaryKey.Properties.Any(property => property.ClrType == typeof(string) && property.GetMaxLength() is null))
            {
                unsafeIndexes.Add($"{entityType.Name} primary key ({string.Join(", ", primaryKey.Properties.Select(property => property.Name))})");
            }
        }

        if (unsafeIndexes.Count > 0)
        {
            throw new InvalidOperationException(
                "The Jellyfin 12.2 database model no longer matches the Npgsql plugin's audited text-index contract. "
                + "Refusing to build a model which can reject valid long text. Unexpected B-tree members: "
                + string.Join("; ", unsafeIndexes.Order(StringComparer.Ordinal)));
        }
    }

    private static IMutableProperty RequireProperty(IMutableEntityType entityType, string propertyName, Type clrType)
    {
        var property = entityType.FindProperty(propertyName)
            ?? throw ModelDrift($"property '{entityType.Name}.{propertyName}' is missing");
        if (property.ClrType != clrType)
        {
            throw ModelDrift($"property '{entityType.Name}.{propertyName}' changed from {clrType.Name} to {property.ClrType.Name}");
        }

        return property;
    }

    private static void RequireKey(IMutableEntityType entityType, params string[] propertyNames)
    {
        var key = entityType.FindPrimaryKey();
        if (key is null || !key.Properties.Select(property => property.Name).SequenceEqual(propertyNames))
        {
            throw ModelDrift(
                $"primary key '{entityType.Name}({string.Join(", ", propertyNames)})' is missing or changed");
        }
    }

    private static void RemoveIndex(IMutableEntityType entityType, params string[] propertyNames)
    {
        var properties = propertyNames.Select(propertyName =>
            entityType.FindProperty(propertyName)
            ?? throw ModelDrift($"property '{entityType.Name}.{propertyName}' is missing"));
        var index = entityType.FindIndex(properties.ToArray());
        if (index is null)
        {
            throw ModelDrift($"index '{entityType.Name}({string.Join(", ", propertyNames)})' is missing or changed");
        }

        entityType.RemoveIndex(index);
    }

    private static InvalidOperationException ModelDrift(string detail)
        => new(
            "The Jellyfin database model is not the exact 12.2 shape supported by "
            + $"{PluginMetadata.Name} {PluginMetadata.Version}: {detail}. No schema operation was attempted.");
}
