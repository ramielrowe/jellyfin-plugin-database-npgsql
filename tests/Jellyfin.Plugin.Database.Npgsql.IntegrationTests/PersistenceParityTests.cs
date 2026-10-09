using System.Data.Common;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Jellyfin.Plugin.Database.Npgsql.ValueConverters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.IntegrationTests;

[Collection(PostgreSqlModelCollection.Name)]
public sealed class PersistenceParityTests
{
    private static readonly DateTime ScenarioStart = new(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc);

    [Fact]
    public async Task PublicModel_SqliteBaselineCoversPersistenceAndQueryMatrix()
    {
        await using var database = await ParityDatabase.CreateSqliteAsync(TestContext.Current.CancellationToken);
        var result = await RunMatrixAsync(database, "sqlite-baseline", TestContext.Current.CancellationToken);
        AssertExpected(result);
    }

    [Fact]
    public async Task PublicModel_PostgreSqlRepeatedIsolatedRunsMatchSQLiteBaseline()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlite = await ParityDatabase.CreateSqliteAsync(cancellationToken);
        var baseline = await RunMatrixAsync(sqlite, "sqlite-differential", cancellationToken);
        AssertExpected(baseline);

        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        for (var pass = 1; pass <= 2; pass++)
        {
            await using var testDatabase = await environment.CreateDatabaseAsync(cancellationToken);
            try
            {
                await using var postgreSql = await ParityDatabase.CreatePostgreSqlAsync(
                    testDatabase.RuntimeConnectionString,
                    cancellationToken);
                var actual = await RunMatrixAsync(postgreSql, $"postgresql-pass-{pass}", cancellationToken);
                Assert.Equal(
                    System.Text.Json.JsonSerializer.Serialize(baseline),
                    System.Text.Json.JsonSerializer.Serialize(actual));
            }
            finally
            {
                global::Npgsql.NpgsqlConnection.ClearAllPools();
            }
        }
    }

    private static async Task<ParityResult> RunMatrixAsync(
        ParityDatabase database,
        string diagnosticName,
        CancellationToken cancellationToken)
    {
        try
        {
            var identities = await RunIdentitySecurityAndActivityAsync(database, cancellationToken);
            var catalog = await RunCatalogAndQueryAsync(database, cancellationToken);
            var bulk = await RunLongKeyTransactionAndBulkAsync(database, cancellationToken);
            return new ParityResult(identities, catalog, bulk);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Persistence parity scenario '{diagnosticName}' failed.", exception);
        }
    }

    private static async Task<IdentityResult> RunIdentitySecurityAndActivityAsync(
        ParityDatabase database,
        CancellationToken cancellationToken)
    {
        var user = new User("MíXeD-用户", "test-auth", "test-reset")
        {
            AudioLanguagePreference = "jpn",
            LastActivityDate = ScenarioStart,
            LastLoginDate = ScenarioStart.AddMinutes(1)
        };
        user.Permissions.Add(new Permission(PermissionKind.EnableMediaPlayback, false));
        user.Preferences.Add(new Preference(PreferenceKind.AllowedTags, "Kids,音楽"));
        var display = new DisplayPreferences(user.Id, Guid.NewGuid(), "parity") { ShowSidebar = true };
        var custom = new CustomItemDisplayPreferences(user.Id, display.ItemId, "parity", "theme", "雪");
        var apiKey = new ApiKey("matrix-key") { DateCreated = ScenarioStart };
        var device = new Device(user.Id, "Jellyfin", "12.2", "Living Room 雪", "device_%_1")
        {
            DateCreated = ScenarioStart,
            DateModified = ScenarioStart,
            DateLastActivity = ScenarioStart,
            IsActive = true
        };

        await using (var seed = database.CreateContext())
        {
            seed.AddRange(user, display, custom, apiKey, device);
            seed.ActivityLogs.AddRange(
                new ActivityLog(@"Literal C:\100%_done alpha", "Playback", user.Id) { DateCreated = ScenarioStart },
                new ActivityLog(@"Literal C:\100%_done beta", "Playback", user.Id) { DateCreated = ScenarioStart.AddMinutes(1) },
                new ActivityLog(@"Literal C:\100%_done gamma", "Playback", user.Id) { DateCreated = ScenarioStart.AddMinutes(2) },
                new ActivityLog(@"Literal C:\100%_done omega", "Playback", user.Id) { DateCreated = ScenarioStart.AddMinutes(3) },
                new ActivityLog(@"Decoy C:\100-percent-done", "Playback", user.Id) { DateCreated = ScenarioStart.AddMinutes(4) },
                new ActivityLog(@"Decoy C:\100X_done", "Playback", user.Id) { DateCreated = ScenarioStart.AddMinutes(5) },
                new ActivityLog(@"Decoy C:/100%_done", "Playback", user.Id) { DateCreated = ScenarioStart.AddMinutes(6) },
                new ActivityLog("CAFÉ", "Unicode", user.Id) { DateCreated = ScenarioStart.AddMinutes(7) });
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using (var duplicate = database.CreateContext())
        {
            duplicate.Users.Add(new User("míxed-用户", "test-auth", "test-reset"));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync(cancellationToken));
        }

        await using (var duplicatePermission = database.CreateContext())
        {
            duplicatePermission.Permissions.Add(new Permission(PermissionKind.EnableMediaPlayback, true) { UserId = user.Id });
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicatePermission.SaveChangesAsync(cancellationToken));
        }

        await using var firstWriter = database.CreateContext();
        await using var staleWriter = database.CreateContext();
        var firstCopy = await firstWriter.Users.SingleAsync(entity => entity.Id == user.Id, cancellationToken);
        var staleCopy = await staleWriter.Users.SingleAsync(entity => entity.Id == user.Id, cancellationToken);
        firstCopy.Username = "first-writer";
        await firstWriter.SaveChangesAsync(cancellationToken);
        staleCopy.Username = "stale-writer";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleWriter.SaveChangesAsync(cancellationToken));

        await using var read = database.CreateContext();
        var stored = await read.Users
            .AsNoTracking()
            .Include(entity => entity.Permissions)
            .Include(entity => entity.Preferences)
            .SingleAsync(entity => entity.Id == user.Id, cancellationToken);
        var literalNames = await read.ActivityLogs
            .Where(entry => EF.Functions.Like(entry.Name, "%C:\\\\100\\%\\_done%", "\\"))
            .OrderBy(entry => entry.DateCreated)
            .Skip(1)
            .Take(2)
            .Select(entry => entry.Name)
            .ToArrayAsync(cancellationToken);
        var asciiCaseFold = await read.ActivityLogs.CountAsync(
            entry => EF.Functions.Like(entry.Name, "%literal%"),
            cancellationToken);
        var unicodeCaseFold = await read.ActivityLogs.CountAsync(
            entry => EF.Functions.Like(entry.Name, "%café%"),
            cancellationToken);

        var storedDisplay = await read.DisplayPreferences.SingleAsync(cancellationToken);
        var storedCustom = await read.CustomItemDisplayPreferences.SingleAsync(cancellationToken);
        return new IdentityResult(
            stored.NormalizedUsername,
            Assert.Single(stored.Permissions).Value,
            Assert.Single(stored.Preferences).Value,
            storedDisplay.ShowSidebar,
            storedCustom.Value,
            await read.ApiKeys.Select(entity => entity.Name).SingleAsync(cancellationToken),
            await read.Devices.Select(entity => entity.DeviceName).SingleAsync(cancellationToken),
            literalNames,
            asciiCaseFold,
            unicodeCaseFold,
            stored.LastActivityDate!.Value.Kind,
            stored.LastLoginDate!.Value.Kind);
    }

    private static async Task<CatalogResult> RunCatalogAndQueryAsync(
        ParityDatabase database,
        CancellationToken cancellationToken)
    {
        var parentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var itemIds = Enumerable.Range(1, 129)
            .Select(index => Guid.Parse($"{index:x8}-0000-0000-0000-000000000000"))
            .ToArray();
        var user = new User("catalog-user", "test-auth", "test-reset");
        var person = new People { Id = Guid.NewGuid(), Name = "Zoë 李", PersonType = "Actor" };
        var director = new People { Id = Guid.NewGuid(), Name = "Renée", PersonType = "Director" };
        var genre = CreateItemValue("Sci-Fi 世界");

        await using (var seed = database.CreateContext())
        {
            var parent = CreateItem(parentId, "Folder", "Library", ScenarioStart, true);
            seed.BaseItems.Add(parent);
            seed.BaseItems.AddRange(itemIds.Select((id, index) => CreateItem(
                id,
                "Movie",
                index == 64 ? "Été 用户" : $"Movie {index:D3}",
                ScenarioStart.AddDays(index),
                false,
                parentId,
                index >= 125 ? null : index is 62 or 63 ? "Sort tie" : $"Sort {index:D3}")));
            seed.AddRange(user, person, director, genre);
            seed.LinkedChildren.Add(new LinkedChildEntity
            {
                ParentId = parentId,
                ChildId = itemIds[64],
                ChildType = LinkedChildType.Manual,
                SortOrder = 1
            });
            seed.PeopleBaseItemMap.AddRange(
                new PeopleBaseItemMap
                {
                    ItemId = itemIds[64],
                    Item = null!,
                    PeopleId = person.Id,
                    People = person,
                    Role = "Lead_%_Actor",
                    SortOrder = 7,
                    ListOrder = 0
                },
                new PeopleBaseItemMap
                {
                    ItemId = itemIds[64],
                    Item = null!,
                    PeopleId = director.Id,
                    People = director,
                    Role = string.Empty,
                    SortOrder = 2,
                    ListOrder = 1
                });
            seed.ItemValuesMap.Add(new ItemValueMap
            {
                ItemId = itemIds[64],
                Item = null!,
                ItemValueId = genre.ItemValueId,
                ItemValue = genre
            });
            seed.UserData.Add(new UserData
            {
                ItemId = itemIds[64],
                Item = null!,
                UserId = user.Id,
                User = user,
                CustomDataKey = "custom/用户",
                Rating = null,
                Likes = null,
                IsFavorite = true,
                PlayCount = 3
            });
            seed.MediaStreamInfos.AddRange(
                new MediaStreamInfo
                {
                    ItemId = itemIds[64],
                    Item = null!,
                    StreamIndex = 0,
                    StreamType = MediaStreamTypeEntity.Video,
                    Codec = "h264",
                    Width = 1920
                },
                new MediaStreamInfo
                {
                    ItemId = itemIds[64],
                    Item = null!,
                    StreamIndex = 1,
                    StreamType = MediaStreamTypeEntity.Audio,
                    Language = "jpn",
                    Channels = 2
                },
                new MediaStreamInfo
                {
                    ItemId = itemIds[64],
                    Item = null!,
                    StreamIndex = 2,
                    StreamType = MediaStreamTypeEntity.Subtitle,
                    Language = null,
                    Title = "旧字幕"
                });
            seed.Chapters.AddRange(
                new Chapter { ItemId = itemIds[64], Item = null!, ChapterIndex = 0, StartPositionTicks = 0, Name = "序章" },
                new Chapter { ItemId = itemIds[64], Item = null!, ChapterIndex = 1, StartPositionTicks = 20, Name = null });
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using var context = database.CreateContext();
        var ascending = await context.BaseItems
            .Where(item => item.Type == "Movie")
            .OrderBy(item => item.SortName)
            .ThenBy(item => item.Name)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        var descending = await context.BaseItems
            .Where(item => item.Type == "Movie")
            .OrderByDescending(item => item.SortName)
            .ThenByDescending(item => item.Name)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
        var firstPage = ascending.Skip(62).Take(5).ToArray();
        var adjacentPage = ascending.Skip(67).Take(5).ToArray();
        Assert.Empty(firstPage.Intersect(adjacentPage));

        var startsWith = await context.Peoples
            .Where(entity => entity.Name.StartsWith("ZOë"))
            .OrderBy(entity => entity.Name)
            .Select(entity => entity.Name)
            .ToArrayAsync(cancellationToken);
        var rangeCount = await context.BaseItems.CountAsync(
            item => item.Type == "Movie"
                && item.StartDate >= ScenarioStart.AddDays(65)
                && item.StartDate < ScenarioStart.AddDays(100),
            cancellationToken);

        var partitioned = new List<Guid>();
        foreach (var partition in itemIds.Chunk(64))
        {
            partitioned.AddRange(await context.BaseItems
                .Where(item => partition.Contains(item.Id))
                .OrderBy(item => item.Id)
                .Select(item => item.Id)
                .ToArrayAsync(cancellationToken));
        }

        var aggregate = await context.BaseItems
            .Where(item => item.Type == "Movie")
            .GroupBy(item => item.TopParentId)
            .Select(group => new
            {
                Parent = group.Key,
                Count = group.Count(),
                Earliest = group.Min(item => item.StartDate),
                MinimumId = group.Min(item => item.Id),
                MaximumId = group.Max(item => item.Id)
            })
            .SingleAsync(cancellationToken);
        var snapshot = await context.BaseItems
            .AsNoTracking()
            .Include(item => item.Peoples!)
            .ThenInclude(map => map.People)
            .Include(item => item.ItemValues!)
            .ThenInclude(map => map.ItemValue)
            .Include(item => item.UserData!)
            .Include(item => item.MediaStreams!)
            .Include(item => item.Chapters!)
            .SingleAsync(item => item.Id == itemIds[64], cancellationToken);

        return new CatalogResult(
            ascending.Length,
            ascending.Take(3).ToArray(),
            ascending.TakeLast(4).ToArray(),
            descending.Take(3).ToArray(),
            descending.TakeLast(4).ToArray(),
            firstPage,
            adjacentPage,
            startsWith,
            rangeCount,
            partitioned.Count,
            partitioned.Distinct().Count(),
            aggregate.Parent,
            aggregate.Count,
            aggregate.Earliest,
            aggregate.MinimumId,
            aggregate.MaximumId,
            await context.LinkedChildren.Select(link => link.ChildId).SingleAsync(cancellationToken),
            snapshot.Peoples!.OrderBy(map => map.ListOrder).Select(map => $"{map.People.Name}|{map.Role}|{map.SortOrder}").ToArray(),
            Assert.Single(snapshot.ItemValues!).ItemValue.Value,
            Assert.Single(snapshot.UserData!).CustomDataKey,
            snapshot.MediaStreams!.OrderBy(stream => stream.StreamIndex).Select(stream => $"{stream.StreamIndex}|{stream.Codec}|{stream.Language}|{stream.Title}").ToArray(),
            snapshot.Chapters!.OrderBy(chapter => chapter.ChapterIndex).Select(chapter => $"{chapter.StartPositionTicks}|{chapter.Name}").ToArray());
    }

    private static async Task<BulkResult> RunLongKeyTransactionAndBulkAsync(
        ParityDatabase database,
        CancellationToken cancellationToken)
    {
        var suffix = string.Concat(Enumerable.Repeat("雪\\x41_%😀", 1500));
        var longKeyItem = CreateItem(Guid.NewGuid(), "Movie", "Long keys", ScenarioStart, false);
        var longKeyPerson = new People { Id = Guid.NewGuid(), Name = "Long Person", PersonType = "Actor" };
        var longKeyUser = new User("long-key-user", "test-auth", "test-reset");
        var longValue = CreateItemValue("genre-" + suffix);
        await using (var seed = database.CreateContext())
        {
            seed.AddRange(longKeyItem, longKeyPerson, longKeyUser, longValue);
            seed.BaseItemProviders.Add(new BaseItemProvider
            {
                ItemId = longKeyItem.Id,
                Item = longKeyItem,
                ProviderId = "provider-" + suffix,
                ProviderValue = "value-" + suffix
            });
            seed.PeopleBaseItemMap.Add(new PeopleBaseItemMap
            {
                ItemId = longKeyItem.Id,
                Item = longKeyItem,
                PeopleId = longKeyPerson.Id,
                People = longKeyPerson,
                Role = "role-" + suffix
            });
            seed.UserData.Add(new UserData
            {
                ItemId = longKeyItem.Id,
                Item = longKeyItem,
                UserId = longKeyUser.Id,
                User = longKeyUser,
                CustomDataKey = "custom-" + suffix
            });
            seed.CustomItemDisplayPreferences.Add(new CustomItemDisplayPreferences(
                longKeyUser.Id,
                longKeyItem.Id,
                "parity",
                "preference-" + suffix,
                "long-value"));
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using (var duplicate = database.CreateContext())
        {
            duplicate.ItemValues.Add(CreateItemValue("genre-" + suffix));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync(cancellationToken));
        }

        await using (var rollback = database.CreateContext())
        {
            await using var transaction = await rollback.Database.BeginTransactionAsync(cancellationToken);
            rollback.Users.Add(new User("rolled-back", "test-auth", "test-reset"));
            await rollback.SaveChangesAsync(cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
        }

        const int DeleteCount = 1025;
        var targetIds = Enumerable.Range(1, DeleteCount)
            .Select(index => Guid.Parse($"{index:x8}-1111-1111-1111-111111111111"))
            .ToArray();
        var survivorId = Guid.Parse("ffffffff-1111-1111-1111-111111111111");
        await using (var seed = database.CreateContext())
        {
            seed.BaseItems.AddRange(targetIds.Select((id, index) =>
                CreateItem(id, "Book", $"Delete {index}", ScenarioStart.AddMinutes(index), false)));
            seed.BaseItems.Add(CreateItem(survivorId, "Book", "Keep", ScenarioStart, false));
            await seed.SaveChangesAsync(cancellationToken);
        }

        var deleted = 0;
        await using (var delete = database.CreateContext())
        {
            await using var transaction = await delete.Database.BeginTransactionAsync(cancellationToken);
            foreach (var partition in targetIds.Append(targetIds[0]).Append(Guid.Empty).Distinct().Chunk(200))
            {
                deleted += await delete.BaseItems
                    .Where(item => partition.Contains(item.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await using var verify = database.CreateContext();
        var longLengths = new[]
        {
            (await verify.BaseItemProviders.SingleAsync(entity => entity.ItemId == longKeyItem.Id, cancellationToken)).ProviderId.Length,
            (await verify.PeopleBaseItemMap.SingleAsync(entity => entity.ItemId == longKeyItem.Id, cancellationToken)).Role!.Length,
            (await verify.UserData.SingleAsync(entity => entity.ItemId == longKeyItem.Id, cancellationToken)).CustomDataKey.Length,
            (await verify.ItemValues.SingleAsync(entity => entity.ItemValueId == longValue.ItemValueId, cancellationToken)).Value.Length,
            (await verify.CustomItemDisplayPreferences.SingleAsync(entity => entity.ItemId == longKeyItem.Id, cancellationToken)).Key.Length
        };
        return new BulkResult(
            longLengths,
            await verify.Users.AnyAsync(entity => entity.Username == "rolled-back", cancellationToken),
            deleted,
            await verify.BaseItems.CountAsync(entity => entity.Type == "Book", cancellationToken),
            await verify.BaseItems.AnyAsync(entity => entity.Id == survivorId, cancellationToken));
    }

    private static void AssertExpected(ParityResult result)
    {
        Assert.Equal("MÍXED-用户", result.Identity.NormalizedUsername);
        Assert.False(result.Identity.MediaPlaybackAllowed);
        Assert.Equal("Kids,音楽", result.Identity.AllowedTags);
        Assert.True(result.Identity.ShowSidebar);
        Assert.Equal("雪", result.Identity.Theme);
        Assert.Equal("matrix-key", result.Identity.ApiKeyName);
        Assert.Equal("Living Room 雪", result.Identity.DeviceName);
        Assert.Equal([@"Literal C:\100%_done beta", @"Literal C:\100%_done gamma"], result.Identity.LiteralPage);
        Assert.Equal(4, result.Identity.AsciiCaseFoldCount);
        Assert.Equal(0, result.Identity.UnicodeCaseFoldCount);
        Assert.Equal(DateTimeKind.Utc, result.Identity.ActivityKind);
        Assert.Equal(DateTimeKind.Utc, result.Identity.LoginKind);

        Assert.Equal(129, result.Catalog.TotalItems);
        Assert.Equal(5, result.Catalog.FirstPage.Length);
        Assert.Equal(5, result.Catalog.AdjacentPage.Length);
        Assert.Equal(["Zoë 李"], result.Catalog.PeoplePrefix);
        Assert.Equal(35, result.Catalog.RangeCount);
        Assert.Equal(129, result.Catalog.PartitionCount);
        Assert.Equal(129, result.Catalog.PartitionDistinctCount);
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), result.Catalog.AggregateParent);
        Assert.Equal(129, result.Catalog.AggregateCount);
        Assert.Equal(ScenarioStart, result.Catalog.AggregateEarliest);
        Assert.Equal(Guid.Parse("00000001-0000-0000-0000-000000000000"), result.Catalog.AggregateMinimumId);
        Assert.Equal(Guid.Parse("00000081-0000-0000-0000-000000000000"), result.Catalog.AggregateMaximumId);
        Assert.Equal(Guid.Parse("00000041-0000-0000-0000-000000000000"), result.Catalog.LinkedChild);
        Assert.Equal(["Zoë 李|Lead_%_Actor|7", "Renée||2"], result.Catalog.People);
        Assert.Equal("Sci-Fi 世界", result.Catalog.ItemValue);
        Assert.Equal("custom/用户", result.Catalog.UserDataKey);
        Assert.Equal(["0|h264||", "1||jpn|", "2|||旧字幕"], result.Catalog.Streams);
        Assert.Equal(["0|序章", "20|"], result.Catalog.Chapters);

        Assert.All(result.Bulk.LongKeyLengths, length => Assert.True(length > 10_000));
        Assert.False(result.Bulk.RolledBackUserExists);
        Assert.Equal(1025, result.Bulk.Deleted);
        Assert.Equal(1, result.Bulk.RemainingBooks);
        Assert.True(result.Bulk.SurvivorExists);
    }

    private static BaseItemEntity CreateItem(
        Guid id,
        string type,
        string name,
        DateTime startDate,
        bool isFolder,
        Guid? parentId = null,
        string? sortName = null)
        => new()
        {
            Id = id,
            Type = type,
            Name = name,
            CleanName = name.ToLowerInvariant(),
            SortName = sortName,
            StartDate = startDate,
            DateCreated = startDate,
            ParentId = parentId,
            TopParentId = parentId,
            IsFolder = isFolder,
            IsVirtualItem = false,
            MediaType = isFolder ? null : "Video",
            PresentationUniqueKey = id.ToString("N")
        };

    private static ItemValue CreateItemValue(string value)
        => new()
        {
            ItemValueId = Guid.NewGuid(),
            Type = ItemValueType.Genre,
            Value = value,
            CleanValue = value.ToLowerInvariant()
        };

    private sealed record ParityResult(IdentityResult Identity, CatalogResult Catalog, BulkResult Bulk);

    private sealed record IdentityResult(
        string NormalizedUsername,
        bool MediaPlaybackAllowed,
        string AllowedTags,
        bool ShowSidebar,
        string? Theme,
        string ApiKeyName,
        string DeviceName,
        string[] LiteralPage,
        int AsciiCaseFoldCount,
        int UnicodeCaseFoldCount,
        DateTimeKind ActivityKind,
        DateTimeKind LoginKind);

    private sealed record CatalogResult(
        int TotalItems,
        Guid[] AscendingHead,
        Guid[] AscendingTail,
        Guid[] DescendingHead,
        Guid[] DescendingTail,
        Guid[] FirstPage,
        Guid[] AdjacentPage,
        string[] PeoplePrefix,
        int RangeCount,
        int PartitionCount,
        int PartitionDistinctCount,
        Guid? AggregateParent,
        int AggregateCount,
        DateTime? AggregateEarliest,
        Guid AggregateMinimumId,
        Guid AggregateMaximumId,
        Guid LinkedChild,
        string[] People,
        string ItemValue,
        string UserDataKey,
        string[] Streams,
        string[] Chapters);

    private sealed record BulkResult(
        int[] LongKeyLengths,
        bool RolledBackUserExists,
        int Deleted,
        int RemainingBooks,
        bool SurvivorExists);

    private sealed class ParityDatabase : IAsyncDisposable
    {
        private readonly Func<JellyfinDbContext> _createContext;
        private readonly DbConnection? _ownedConnection;

        private ParityDatabase(Func<JellyfinDbContext> createContext, DbConnection? ownedConnection = null)
        {
            _createContext = createContext;
            _ownedConnection = ownedConnection;
        }

        public static async Task<ParityDatabase> CreatePostgreSqlAsync(
            string connectionString,
            CancellationToken cancellationToken)
        {
            var database = new ParityDatabase(() => CreatePostgreSqlContext(connectionString));
            await using var context = database.CreateContext();
            Assert.True(await context.Database.EnsureCreatedAsync(cancellationToken));
            await context.Database.ExecuteSqlRawAsync(NpgsqlTextInterning.InstallSql, cancellationToken);
            await context.Database.ExecuteSqlRawAsync(NpgsqlUuidAggregates.InstallSql, cancellationToken);
            Assert.Equal(
                NpgsqlModelCustomizer.ModelContract,
                context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>()
                    .Model[NpgsqlModelCustomizer.ModelContractAnnotation]);
            return database;
        }

        public static async Task<ParityDatabase> CreateSqliteAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var provider = new TestSqliteProvider();
            var database = new ParityDatabase(
                () => new JellyfinDbContext(
                    new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(connection).Options,
                    NullLogger<JellyfinDbContext>.Instance,
                    provider,
                    new NoLockBehavior(NullLogger<NoLockBehavior>.Instance)),
                connection);
            await using var context = database.CreateContext();
            Assert.True(await context.Database.EnsureCreatedAsync(cancellationToken));
            return database;
        }

        public JellyfinDbContext CreateContext() => _createContext();

        public async ValueTask DisposeAsync()
        {
            if (_ownedConnection is not null)
            {
                await _ownedConnection.DisposeAsync();
            }
        }

        private static JellyfinDbContext CreatePostgreSqlContext(string connectionString)
        {
            var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
            var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
            NpgsqlOptionsBootstrap.Configure(builder, connectionString, null);
            return new JellyfinDbContext(
                builder.Options,
                NullLogger<JellyfinDbContext>.Instance,
                provider,
                new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        }
    }

    private sealed class TestSqliteProvider : IJellyfinDatabaseProvider
    {
        public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

        public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
            => throw new NotSupportedException();

        public void OnModelCreating(ModelBuilder modelBuilder)
        {
        }

        public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            configurationBuilder.Properties<DateTime>()
                .HaveConversion<UtcDateTimeValueConverter>();
        }

        public Task RunScheduledOptimisation(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RunShutdownTask(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> MigrationBackupFast(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RestoreBackupFast(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteBackup(string key) => throw new NotSupportedException();

        public Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames) => throw new NotSupportedException();
    }
}
