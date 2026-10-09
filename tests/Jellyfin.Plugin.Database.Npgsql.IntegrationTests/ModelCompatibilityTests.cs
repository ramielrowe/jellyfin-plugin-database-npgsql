using System.Data;
using System.Text.Json;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Jellyfin.Plugin.Database.Npgsql.TestProvider;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.IntegrationTests;

[Collection(PostgreSqlModelCollection.Name)]
public sealed class ModelCompatibilityTests
{
    [Fact]
    public async Task FinalModel_CreatesAndHandlesLongTextNormalDetachedRestoreUpdateAndDelete()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            var suffix = string.Concat(Enumerable.Repeat("雪\\x41_%😀", 1500));
            var providerId = "provider-" + suffix;
            var providerValue = "value-" + suffix;
            var role = "role-" + suffix;
            var customDataKey = "custom-" + suffix;
            var itemValueText = "genre-" + suffix;
            var preferenceKey = "preference-" + suffix;
            var itemId = Guid.NewGuid();
            var personId = Guid.NewGuid();
            Guid userId;
            int preferenceId;

            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                var item = CreateBaseItem(itemId, suffix);
                var person = new People { Id = personId, Name = "Mixed Case Person", PersonType = "Actor" };
                var user = new User("model-user", "test-auth", "test-reset");
                userId = user.Id;
                context.AddRange(item, person, user);
                context.BaseItemProviders.Add(new BaseItemProvider
                {
                    Item = item,
                    ItemId = itemId,
                    ProviderId = providerId,
                    ProviderValue = providerValue
                });
                context.PeopleBaseItemMap.Add(new PeopleBaseItemMap
                {
                    Item = item,
                    ItemId = itemId,
                    People = person,
                    PeopleId = personId,
                    Role = role
                });
                context.UserData.Add(new UserData
                {
                    Item = item,
                    ItemId = itemId,
                    User = user,
                    UserId = user.Id,
                    CustomDataKey = customDataKey
                });
                context.ItemValues.Add(CreateItemValue(itemValueText));
                var preference = new CustomItemDisplayPreferences(
                    user.Id,
                    itemId,
                    "test-client",
                    preferenceKey,
                    "first");
                context.CustomItemDisplayPreferences.Add(preference);

                await context.SaveChangesAsync(cancellationToken);
                preferenceId = preference.Id;
            }

            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                Assert.Equal(providerId, await context.BaseItemProviders.Select(value => value.ProviderId).SingleAsync(cancellationToken));
                Assert.Equal(role, await context.PeopleBaseItemMap.Select(value => value.Role).SingleAsync(cancellationToken));
                Assert.Equal(customDataKey, await context.UserData.Select(value => value.CustomDataKey).SingleAsync(cancellationToken));
                Assert.Equal(itemValueText, await context.ItemValues.Select(value => value.Value).SingleAsync(cancellationToken));
                Assert.Equal(preferenceKey, await context.CustomItemDisplayPreferences.Select(value => value.Key).SingleAsync(cancellationToken));
                var item = await context.BaseItems.SingleAsync(value => value.Id == itemId, cancellationToken);
                Assert.Equal(DateTimeKind.Utc, item.DateCreated!.Value.Kind);
            }

            var detachedProvider = new BaseItemProvider
            {
                ItemId = itemId,
                ProviderId = providerId,
                ProviderValue = "detached-update",
                Item = null!
            };
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                var entry = context.Update(detachedProvider);
                Assert.Equal(EntityState.Modified, entry.State);
                await context.SaveChangesAsync(cancellationToken);
            }

            var updatedPreferenceKey = "updated-" + suffix;
            CustomItemDisplayPreferences detachedPreference;
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                detachedPreference = await context.CustomItemDisplayPreferences
                    .AsNoTracking()
                    .SingleAsync(value => value.Id == preferenceId, cancellationToken);
            }

            detachedPreference.Key = updatedPreferenceKey;
            detachedPreference.Value = "detached";
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                Assert.Equal(EntityState.Modified, context.Update(detachedPreference).State);
                await context.SaveChangesAsync(cancellationToken);
            }

            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                const string restoreProvider = "restore-雪-\\-%_";
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO \"BaseItemProviders\" (\"ItemId\", \"ProviderId\", \"ProviderValue\") VALUES ({itemId}, {restoreProvider}, {"restore-value"})",
                    cancellationToken);
                Assert.Equal(
                    restoreProvider,
                    await context.BaseItemProviders
                        .Where(provider => provider.ProviderId == restoreProvider)
                        .Select(provider => provider.ProviderId)
                        .SingleAsync(cancellationToken));

                context.Remove(await context.BaseItemProviders.SingleAsync(
                    provider => provider.ProviderId == providerId,
                    cancellationToken));
                await context.SaveChangesAsync(cancellationToken);
                Assert.False(await context.BaseItemProviders.AnyAsync(
                    provider => provider.ProviderId == providerId,
                    cancellationToken));
            }
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ChunkInterning_ForcedSharedChunkCollisionAllowsDistinctValuesRejectsDuplicatesAndSerializesRaces()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            var common = new string('x', NpgsqlTextInterning.ChunkSize * 8);
            var firstValue = common + "-first";
            var secondValue = common + "-second";

            // Both values are forced through the same eight bounded identity nodes.
            // Unlike a digest key, a collision in this accelerator/prefix path does
            // not merge the distinct terminal identities.
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                context.ItemValues.AddRange(CreateItemValue(firstValue), CreateItemValue(secondValue));
                await context.SaveChangesAsync(cancellationToken);
                var internIds = await context.ItemValues
                    .Where(value => value.Value == firstValue || value.Value == secondValue)
                    .Select(value => EF.Property<long>(value, "ValueInternId"))
                    .ToArrayAsync(cancellationToken);
                Assert.Equal(2, internIds.Distinct().Count());
            }

            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                context.ItemValues.Add(CreateItemValue(firstValue));
                await Assert.ThrowsAsync<DbUpdateException>(
                    () => context.SaveChangesAsync(cancellationToken));
            }

            var raceValue = common + "-race";
            await using var first = CreateContext(database.RuntimeConnectionString);
            await using var second = CreateContext(database.RuntimeConnectionString);
            first.ItemValues.Add(CreateItemValue(raceValue));
            second.ItemValues.Add(CreateItemValue(raceValue));
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstSave = SaveAfterSignalAsync(first, start.Task, cancellationToken);
            var secondSave = SaveAfterSignalAsync(second, start.Task, cancellationToken);
            start.SetResult();
            var outcomes = await Task.WhenAll(firstSave, secondSave);
            Assert.Equal(1, outcomes.Count(outcome => outcome));
            Assert.Equal(1, outcomes.Count(outcome => !outcome));
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ChunkInterning_InverseOrderWholeTransactionsRetryForNewAndExistingNodes()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            var firstValue = "inverse-a-" + Guid.NewGuid().ToString("N");
            var secondValue = "inverse-b-" + Guid.NewGuid().ToString("N");

            var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var forward = InternPairWithRetryAsync(
                database.RuntimeConnectionString,
                firstValue,
                secondValue,
                firstReady,
                secondReady,
                cancellationToken);
            var reverse = InternPairWithRetryAsync(
                database.RuntimeConnectionString,
                secondValue,
                firstValue,
                secondReady,
                firstReady,
                cancellationToken);
            var newNodeResults = await Task.WhenAll(forward, reverse);
            Assert.Equal(newNodeResults[0].First, newNodeResults[1].Second);
            Assert.Equal(newNodeResults[0].Second, newNodeResults[1].First);

            await using var countContext = CreateContext(database.RuntimeConnectionString);
            var nodeCount = await ScalarAsync<long>(
                countContext,
                "SELECT count(*) FROM public.\"__JellyfinTextNodes\"",
                cancellationToken);
            Assert.Equal(4, nodeCount);

            firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            forward = InternPairWithRetryAsync(
                database.RuntimeConnectionString,
                firstValue,
                secondValue,
                firstReady,
                secondReady,
                cancellationToken);
            reverse = InternPairWithRetryAsync(
                database.RuntimeConnectionString,
                secondValue,
                firstValue,
                secondReady,
                firstReady,
                cancellationToken);
            var existingNodeResults = await Task.WhenAll(forward, reverse);
            Assert.Equal(newNodeResults[0], existingNodeResults[0]);
            Assert.Equal(newNodeResults[1], existingNodeResults[1]);
            Assert.Equal(
                nodeCount,
                await ScalarAsync<long>(
                    countContext,
                    "SELECT count(*) FROM public.\"__JellyfinTextNodes\"",
                    cancellationToken));
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LikeExpressions_MatchSQLiteForWildcardsEscapesAsciiUnicodeAndNulls()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            await using var context = CreateContext(database.RuntimeConnectionString);
            await using var sqlite = new SqliteConnection("Data Source=:memory:");
            await sqlite.OpenAsync(cancellationToken);
            var cases = new (string? Match, string? Pattern, string? Escape, bool ExplicitEscape)[]
            {
                ("100%_done", "100%_done", null, false),
                ("axb", "a_b", null, false),
                ("a\\b", "a\\b", null, false),
                ("a%b", "aX%b", "X", true),
                ("a_b", "a\\_b", "\\", true),
                ("ASCII", "ascii", null, false),
                ("CAFÉ", "café", null, false),
                (null, "%", null, false),
                ("value", null, null, false),
                ("value", "value", string.Empty, true)
            };

            foreach (var testCase in cases)
            {
                var row = new ActivityLog("like-" + Guid.NewGuid().ToString("N"), "Test", Guid.Empty)
                {
                    Overview = testCase.Match
                };
                context.ActivityLogs.Add(row);
                await context.SaveChangesAsync(cancellationToken);

                var sqliteResult = await EvaluateSqliteLikeAsync(
                    sqlite,
                    testCase.Match,
                    testCase.Pattern,
                    testCase.Escape,
                    testCase.ExplicitEscape,
                    cancellationToken);
                var postgreSqlResult = await Record.ExceptionAsync(async () =>
                {
                    var query = context.ActivityLogs.Where(entry => entry.Id == row.Id);
                    query = testCase.ExplicitEscape
                        ? query.Where(entry => EF.Functions.Like(
                            entry.Overview!,
                            testCase.Pattern!,
                            testCase.Escape!))
                        : query.Where(entry => EF.Functions.Like(entry.Overview!, testCase.Pattern!));
                    var matched = await query.AnyAsync(cancellationToken);
                    Assert.Equal(sqliteResult.Matched, matched);
                });

                if (sqliteResult.Exception is null)
                {
                    Assert.Null(postgreSqlResult);
                }
                else
                {
                    Assert.NotNull(postgreSqlResult);
                }
            }
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task InternedMappings_PreserveBoundariesNormalizationAndTriggerOwnedIdentityForEveryMapping()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            var values = new[]
            {
                string.Empty,
                new string('a', 127),
                new string('a', 128),
                new string('a', 129),
                new string('p', 128),
                new string('p', 128) + "terminal",
                "caf\u00E9",
                "cafe\u0301"
            };
            var itemId = Guid.NewGuid();
            var peopleId = Guid.NewGuid();
            Guid userId;

            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                var item = CreateBaseItem(itemId, "boundaries");
                var person = new People { Id = peopleId, Name = "Boundary Person", PersonType = "Actor" };
                var user = new User("boundary-user", "test-auth", "test-reset");
                userId = user.Id;
                context.AddRange(item, person, user);
                foreach (var value in values)
                {
                    context.BaseItemProviders.Add(new BaseItemProvider
                    {
                        Item = item,
                        ItemId = itemId,
                        ProviderId = value,
                        ProviderValue = "provider-value"
                    });
                    context.PeopleBaseItemMap.Add(new PeopleBaseItemMap
                    {
                        Item = item,
                        ItemId = itemId,
                        People = person,
                        PeopleId = peopleId,
                        Role = value
                    });
                    context.UserData.Add(new UserData
                    {
                        Item = item,
                        ItemId = itemId,
                        User = user,
                        UserId = userId,
                        CustomDataKey = value
                    });
                    context.ItemValues.Add(CreateItemValue(value));
                    context.CustomItemDisplayPreferences.Add(
                        new CustomItemDisplayPreferences(userId, itemId, "boundaries", value, "value"));
                }

                await context.SaveChangesAsync(cancellationToken);
            }

            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                Assert.Equal(values.Length, await context.BaseItemProviders.CountAsync(cancellationToken));
                Assert.Equal(values.Length, await context.PeopleBaseItemMap.CountAsync(cancellationToken));
                Assert.Equal(values.Length, await context.UserData.CountAsync(cancellationToken));
                Assert.Equal(values.Length, await context.ItemValues.CountAsync(cancellationToken));
                Assert.Equal(values.Length, await context.CustomItemDisplayPreferences.CountAsync(cancellationToken));
                Assert.Equal(values.Length, await context.BaseItemProviders.Select(value => EF.Property<long>(value, "ProviderIdInternId")).Distinct().CountAsync(cancellationToken));
                Assert.Equal(values.Length, await context.PeopleBaseItemMap.Select(value => EF.Property<long>(value, "RoleInternId")).Distinct().CountAsync(cancellationToken));
                Assert.Equal(values.Length, await context.UserData.Select(value => EF.Property<long>(value, "CustomDataKeyInternId")).Distinct().CountAsync(cancellationToken));
                Assert.Equal(values.Length, await context.ItemValues.Select(value => EF.Property<long>(value, "ValueInternId")).Distinct().CountAsync(cancellationToken));
                Assert.Equal(values.Length, await context.CustomItemDisplayPreferences.Select(value => EF.Property<long>(value, "KeyInternId")).Distinct().CountAsync(cancellationToken));

                await context.Database.ExecuteSqlRawAsync("UPDATE \"BaseItemProviders\" SET \"ProviderIdInternId\" = 0", cancellationToken);
                await context.Database.ExecuteSqlRawAsync("UPDATE \"PeopleBaseItemMap\" SET \"RoleInternId\" = 0", cancellationToken);
                await context.Database.ExecuteSqlRawAsync("UPDATE \"UserData\" SET \"CustomDataKeyInternId\" = 0", cancellationToken);
                await context.Database.ExecuteSqlRawAsync("UPDATE \"ItemValues\" SET \"ValueInternId\" = 0", cancellationToken);
                await context.Database.ExecuteSqlRawAsync("UPDATE \"CustomItemDisplayPreferences\" SET \"KeyInternId\" = 0", cancellationToken);
                Assert.Equal(0, await CountZeroInternIdsAsync(context, cancellationToken));

                var updated = "updated-" + new string('u', 129);
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"BaseItemProviders\" SET \"ProviderId\" = {updated} WHERE \"ProviderId\" = {values[0]}", cancellationToken);
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"PeopleBaseItemMap\" SET \"Role\" = {updated} WHERE \"Role\" = {values[0]}", cancellationToken);
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"UserData\" SET \"CustomDataKey\" = {updated} WHERE \"CustomDataKey\" = {values[0]}", cancellationToken);
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ItemValues\" SET \"Value\" = {updated} WHERE \"Value\" = {values[0]}", cancellationToken);
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"CustomItemDisplayPreferences\" SET \"Key\" = {updated} WHERE \"Key\" = {values[0]}", cancellationToken);
                Assert.Equal(5, await CountUpdatedInternedMappingsAsync(context, updated, cancellationToken));

                var nodesBeforeDelete = await ScalarAsync<long>(
                    context,
                    "SELECT count(*) FROM public.\"__JellyfinTextNodes\"",
                    cancellationToken);
                await context.Database.ExecuteSqlRawAsync("DELETE FROM \"BaseItemProviders\"", cancellationToken);
                await context.Database.ExecuteSqlRawAsync("DELETE FROM \"PeopleBaseItemMap\"", cancellationToken);
                await context.Database.ExecuteSqlRawAsync("DELETE FROM \"UserData\"", cancellationToken);
                await context.Database.ExecuteSqlRawAsync("DELETE FROM \"ItemValues\"", cancellationToken);
                await context.Database.ExecuteSqlRawAsync("DELETE FROM \"CustomItemDisplayPreferences\"", cancellationToken);
                Assert.Equal(
                    nodesBeforeDelete,
                    await ScalarAsync<long>(
                        context,
                        "SELECT count(*) FROM public.\"__JellyfinTextNodes\"",
                        cancellationToken));

                const string restored = "restored-caf\u00E9";
                await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"BaseItemProviders\" (\"ItemId\", \"ProviderId\", \"ProviderValue\") VALUES ({itemId}, {restored}, {"value"})", cancellationToken);
                await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"PeopleBaseItemMap\" (\"ItemId\", \"PeopleId\", \"Role\") VALUES ({itemId}, {peopleId}, {restored})", cancellationToken);
                await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"UserData\" (\"ItemId\", \"UserId\", \"CustomDataKey\", \"PlaybackPositionTicks\", \"PlayCount\", \"IsFavorite\", \"Played\") VALUES ({itemId}, {userId}, {restored}, {0L}, {0}, {false}, {false})", cancellationToken);
                await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"ItemValues\" (\"ItemValueId\", \"Type\", \"Value\", \"CleanValue\") VALUES ({Guid.NewGuid()}, {(int)ItemValueType.Genre}, {restored}, {restored})", cancellationToken);
                await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"CustomItemDisplayPreferences\" (\"UserId\", \"ItemId\", \"Client\", \"Key\", \"Value\") VALUES ({userId}, {itemId}, {"boundaries"}, {restored}, {"value"})", cancellationToken);
                Assert.Equal(5, await CountUpdatedInternedMappingsAsync(context, restored, cancellationToken));
            }

            await using (var invalidContext = CreateContext(database.RuntimeConnectionString))
            {
                invalidContext.ItemValues.Add(CreateItemValue("nul\0rejected"));
                await Assert.ThrowsAsync<DbUpdateException>(() => invalidContext.SaveChangesAsync(cancellationToken));
            }

            await using (var invalidContext = CreateContext(database.RuntimeConnectionString))
            {
                invalidContext.ItemValues.Add(CreateItemValue("surrogate-\uD800"));
                await Assert.ThrowsAsync<DbUpdateException>(() => invalidContext.SaveChangesAsync(cancellationToken));
            }
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ChunkInterning_AppendOnlyGrowthHasExactDocumentedPerValueBound()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            await using var context = CreateContext(database.RuntimeConnectionString);
            context.ItemValues.AddRange(
                CreateItemValue(string.Empty),
                CreateItemValue(new string('a', 127)),
                CreateItemValue(new string('b', 128)),
                CreateItemValue(new string('c', 129)));
            await context.SaveChangesAsync(cancellationToken);

            // One terminal node per distinct value plus ceil(UTF8-bytes / 128)
            // data nodes: 1 + 2 + 2 + 3 = 8. Distinct first bytes avoid sharing.
            Assert.Equal(
                8,
                await ScalarAsync<long>(
                    context,
                    "SELECT count(*) FROM public.\"__JellyfinTextNodes\"",
                    cancellationToken));
            await context.ItemValues.ExecuteDeleteAsync(cancellationToken);
            Assert.Equal(
                8,
                await ScalarAsync<long>(
                    context,
                    "SELECT count(*) FROM public.\"__JellyfinTextNodes\"",
                    cancellationToken));
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task QueryInterceptor_ProvidesSqliteCompatibleActualExpressionSemantics()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            await using var context = CreateContext(database.RuntimeConnectionString);
            context.ActivityLogs.AddRange(
                new ActivityLog("Library SCAN 100%_done", "Test", Guid.Empty),
                new ActivityLog("Path \\Root", "Test", Guid.Empty),
                new ActivityLog("CAFÉ", "Test", Guid.Empty));
            context.Peoples.Add(new People { Id = Guid.NewGuid(), Name = "Mixed Case Person", PersonType = "Actor" });
            context.BaseItems.AddRange(
                new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", Name = "Null", SortName = null },
                new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", Name = "Value", SortName = "Alpha" });
            await context.SaveChangesAsync(cancellationToken);

            Assert.Single(await context.ActivityLogs
                .Where(entry => EF.Functions.Like(entry.Name, "%scan 100\\%\\_done", "\\"))
                .ToArrayAsync(cancellationToken));
            Assert.Single(await context.ActivityLogs
                .Where(entry => EF.Functions.Like(entry.Name, "%\\\\Root", "\\"))
                .ToArrayAsync(cancellationToken));
            Assert.Empty(await context.ActivityLogs
                .Where(entry => EF.Functions.Like(entry.Name, "%scan 100\\%\\_done"))
                .ToArrayAsync(cancellationToken));
            Assert.Empty(await context.ActivityLogs
                .Where(entry => EF.Functions.Like(entry.Name, "%café%"))
                .ToArrayAsync(cancellationToken));
            Assert.Single(await context.Peoples
                .Where(person => person.Name.StartsWith("MIXED"))
                .ToArrayAsync(cancellationToken));

            var ascending = await context.BaseItems
                .OrderBy(item => item.SortName)
                .Select(item => item.SortName)
                .ToArrayAsync(cancellationToken);
            Assert.Null(ascending[0]);
            var descending = await context.BaseItems
                .OrderByDescending(item => item.SortName)
                .Select(item => item.SortName)
                .ToArrayAsync(cancellationToken);
            Assert.Null(descending[^1]);
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ReplacementIndexes_AreUsableAndNoPrivilegedExtensionIsInstalled()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            await using var context = CreateContext(database.RuntimeConnectionString);
            context.BaseItems.Add(CreateBaseItem(Guid.NewGuid(), "planner"));
            context.Peoples.Add(new People { Id = Guid.NewGuid(), Name = "Mixed Planner Person", PersonType = "Actor" });
            context.ItemValues.Add(CreateItemValue("planner-target"));
            await context.SaveChangesAsync(cancellationToken);

            var probes = new Dictionary<string, IQueryable<Guid>>(StringComparer.Ordinal)
            {
                ["IX_BaseItems_Path"] = context.BaseItems.Where(item => item.Path == "not-present").Select(item => item.Id),
                ["IX_BaseItems_Name"] = context.BaseItems.Where(item => item.Name == "not-present").Select(item => item.Id),
                ["IX_BaseItems_CleanName"] = context.BaseItems.Where(item => item.CleanName == "not-present").Select(item => item.Id),
                ["IX_BaseItems_PresentationUniqueKey"] = context.BaseItems.Where(item => item.PresentationUniqueKey == "not-present").Select(item => item.Id),
                ["IX_BaseItems_SeriesPresentationUniqueKey"] = context.BaseItems.Where(item => item.SeriesPresentationUniqueKey == "not-present").Select(item => item.Id),
                ["IX_BaseItems_SeriesName"] = context.BaseItems.Where(item => item.SeriesName == "not-present").Select(item => item.Id),
                ["IX_BaseItems_SortName"] = context.BaseItems.Where(item => item.SortName == "not-present").Select(item => item.Id),
                ["IX_BaseItems_Npgsql_Type"] = context.BaseItems.Where(item => item.Type == "Movie").Select(item => item.Id),
                ["IX_BaseItems_Npgsql_Type_TopParentId"] = context.BaseItems.Where(item => item.Type == "Movie" && item.TopParentId == Guid.Empty).Select(item => item.Id),
                ["IX_BaseItems_Npgsql_Type_IsFolder_IsVirtualItem"] = context.BaseItems.Where(item => item.Type == "Movie" && !item.IsFolder && !item.IsVirtualItem).Select(item => item.Id),
                ["IX_BaseItems_Npgsql_IsFolder_TopParentId_IsVirtualItem"] = context.BaseItems.Where(item => !item.IsFolder && item.TopParentId == Guid.Empty && !item.IsVirtualItem).Select(item => item.Id),
                ["IX_BaseItems_Npgsql_MediaType_TopParentId_IsVirtualItem"] = context.BaseItems.Where(item => item.MediaType == "Video" && item.TopParentId == Guid.Empty && !item.IsVirtualItem).Select(item => item.Id),
                ["IX_BaseItems_Npgsql_Type_ParentIndexNumber_IndexNumber"] = context.BaseItems.Where(item => item.Type == "Movie" && item.ParentIndexNumber == 0 && item.IndexNumber == 0).Select(item => item.Id),
                ["IX_BaseItems_Npgsql_Type_TopParentId_IsVirtualItem_DateCreated"] = context.BaseItems.Where(item => item.Type == "Movie" && item.TopParentId == Guid.Empty && !item.IsVirtualItem && item.DateCreated == new DateTime(2026, 10, 9, 1, 2, 3, DateTimeKind.Utc)).Select(item => item.Id)
            };

            foreach (var (expectedIndexFragment, query) in probes)
            {
                var plan = await ExplainWithOnlyIndexAsync(
                    context,
                    expectedIndexFragment,
                    query.ToQueryString(),
                    cancellationToken);
                Assert.Contains("Index", plan, StringComparison.Ordinal);
                Assert.Contains(expectedIndexFragment, plan, StringComparison.Ordinal);
            }

            var peoplePlan = await ExplainQueryAsync(
                context,
                context.Peoples.Where(person => person.Name.StartsWith("MIXED")).Select(person => person.Id),
                cancellationToken);
            Assert.Contains("IX_Peoples_Npgsql_NameLowerPrefix", peoplePlan, StringComparison.Ordinal);
            Assert.Contains("Index Cond", peoplePlan, StringComparison.Ordinal);

            var itemValuePlan = await ExplainQueryAsync(
                context,
                context.ItemValues
                    .Where(value => value.Type == ItemValueType.Genre && value.Value == "planner-target")
                    .Select(value => value.ItemValueId),
                cancellationToken);
            Assert.Contains("IX_ItemValues_Value", itemValuePlan, StringComparison.Ordinal);
            Assert.Contains("planner-target", itemValuePlan, StringComparison.Ordinal);

            var ascendingPlan = await ExplainQueryAsync(
                context,
                context.BaseItems.OrderBy(item => item.SortName),
                cancellationToken);
            Assert.Contains("Sort", ascendingPlan, StringComparison.Ordinal);
            Assert.Contains("IS NULL", ascendingPlan, StringComparison.Ordinal);
            var descendingPlan = await ExplainQueryAsync(
                context,
                context.BaseItems.OrderByDescending(item => item.SortName),
                cancellationToken);
            Assert.Contains("DESC", descendingPlan, StringComparison.Ordinal);
            Assert.Contains("IS NULL", descendingPlan, StringComparison.Ordinal);
            var thenByPlan = await ExplainQueryAsync(
                context,
                context.BaseItems.OrderBy(item => item.Type).ThenBy(item => item.SortName),
                cancellationToken);
            Assert.Contains("IS NULL", thenByPlan, StringComparison.Ordinal);
            Assert.Contains("Sort Key", thenByPlan, StringComparison.Ordinal);

            var extensionCount = await ScalarAsync<long>(
                context,
                "SELECT count(*) FROM pg_catalog.pg_extension WHERE extname IN ('pgcrypto', 'citext')",
                cancellationToken);
            Assert.Equal(0, extensionCount);
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UntouchedJellyfin_StartsAndCreatesApplicationDataAgainstPrecreatedCurrentModel()
    {
        var databaseOptions = PostgreSqlTestOptions.FromEnvironment();
        var publishDirectory = Environment.GetEnvironmentVariable(
            PristineJellyfinPublishOptions.PublishDirectoryEnvironmentVariable);
        var publishArchive = Environment.GetEnvironmentVariable(
            PristineJellyfinPublishOptions.ArchiveEnvironmentVariable);
        var hostConfigured = !string.IsNullOrWhiteSpace(publishDirectory)
            || !string.IsNullOrWhiteSpace(publishArchive);
        Assert.SkipUnless(
            databaseOptions.IsConfigured && hostConfigured,
            "Configure the real PostgreSQL fixture and an integrity-pinned pristine Jellyfin publish to run the full-host model proof.");

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(databaseOptions, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var artifactRoot = Path.Combine(Path.GetTempPath(), "jellyfin-npgsql-model-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        var succeeded = false;
        try
        {
            var publish = await PristineJellyfinPublish.CreateAsync(
                PristineJellyfinPublishOptions.FromEnvironment(),
                Path.Combine(artifactRoot, "host"),
                cancellationToken);
            var ffmpeg = Path.Combine(repositoryRoot, "tests", "fixtures", "fake-ffmpeg.sh");
            var vanillaHistory = await CollectVanillaMigrationHistoryAsync(
                publish,
                artifactRoot,
                ffmpeg,
                cancellationToken);

            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                var migrationId = Assert.Single(context.Database.GetMigrations());
                await context.Database.ExecuteSqlRawAsync(
                    "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (\"MigrationId\" character varying(150) NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, \"ProductVersion\" character varying(32) NOT NULL)",
                    cancellationToken);
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({migrationId}, {"10.0.3"})",
                    cancellationToken);
                foreach (var (vanillaMigrationId, productVersion) in vanillaHistory)
                {
                    await context.Database.ExecuteSqlInterpolatedAsync(
                        $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({vanillaMigrationId}, {productVersion}) ON CONFLICT (\"MigrationId\") DO NOTHING",
                        cancellationToken);
                }
            }

            var layout = JellyfinHostLayout.Create(artifactRoot, publish, "model-compatibility");
            var package = Path.Combine(
                repositoryRoot,
                "artifacts",
                "jellyfin-plugin-database-npgsql_" + PluginMetadata.Version + ".zip");
            Assert.True(File.Exists(package), "Build the audited plugin ZIP before running the full-host model proof.");
            var pluginDirectory = layout.InstallPluginZip(package, PluginMetadata.InstallationDirectory);
            File.Copy(
                typeof(TestOnlyNpgsqlDatabaseProvider).Assembly.Location,
                Path.Combine(pluginDirectory, TestOnlyNpgsqlDatabaseProvider.AssemblyFileName));
            layout.WriteDatabaseConfiguration(
                database.RuntimeConnectionString,
                PluginMetadata.InstallationDirectory,
                TestOnlyNpgsqlDatabaseProvider.AssemblyFileName);
            var portLeaseRoot = Path.Combine(artifactRoot, ".port-leases");
            using var httpPort = LoopbackPortReservation.Acquire(portLeaseRoot);
            using var httpsPort = LoopbackPortReservation.Acquire(portLeaseRoot);
            layout.WriteNetworkConfiguration(httpPort.Port, httpsPort.Port);
            var password = "model-host-" + Guid.NewGuid().ToString("N");
            var redactor = new SensitiveDataRedactor(password, database.RuntimeConnectionString);
            await using (var host = JellyfinHostProcess.Start(layout, httpPort, redactor, ffmpeg))
            {
                await host.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
                Assert.Equal("Healthy", (await host.GetStringAsync("health", cancellationToken)).Trim());
                _ = await host.GetStringAsync("Startup/User", cancellationToken);
                await host.PostJsonAsync(
                    "Startup/User",
                    JsonSerializer.Serialize(new { Name = "model-admin", Password = password }),
                    authorization: null,
                    cancellationToken);
                await host.PostJsonAsync("Startup/Complete", "{}", authorization: null, cancellationToken);
                var authentication = await host.PostJsonAsync(
                    "Users/AuthenticateByName",
                    JsonSerializer.Serialize(new { Username = "model-admin", Pw = password }),
                    $"MediaBrowser Client=\"npgsql-model\", Device=\"tests\", DeviceId=\"{Guid.NewGuid():N}\", Version=\"0.1\"",
                    cancellationToken);
                using var document = JsonDocument.Parse(authentication);
                var token = document.RootElement.GetProperty("AccessToken").GetString();
                Assert.False(string.IsNullOrWhiteSpace(token));
                redactor.Add(token);
                await host.StopGracefullyAsync(token!, TimeSpan.FromSeconds(30), cancellationToken);
            }

            await publish.VerifyUnchangedAsync(cancellationToken);
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                Assert.True(await context.Users.AnyAsync(user => user.Username == "model-admin", cancellationToken));
            }

            succeeded = true;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The pristine-host model proof failed; isolated diagnostics remain under '{artifactRoot}'.",
                exception);
        }
        finally
        {
            if (succeeded && Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }

            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    private static async Task<IReadOnlyList<(string MigrationId, string ProductVersion)>> CollectVanillaMigrationHistoryAsync(
        PristineJellyfinPublish publish,
        string artifactRoot,
        string ffmpeg,
        CancellationToken cancellationToken)
    {
        var layout = JellyfinHostLayout.Create(artifactRoot, publish, "sqlite-history-seed");
        var portLeaseRoot = Path.Combine(artifactRoot, ".port-leases");
        using var httpPort = LoopbackPortReservation.Acquire(portLeaseRoot);
        using var httpsPort = LoopbackPortReservation.Acquire(portLeaseRoot);
        layout.WriteNetworkConfiguration(httpPort.Port, httpsPort.Port);
        var password = "history-seed-" + Guid.NewGuid().ToString("N");
        var redactor = new SensitiveDataRedactor(password);
        var rows = new List<(string MigrationId, string ProductVersion)>();
        await using (var host = JellyfinHostProcess.Start(layout, httpPort, redactor, ffmpeg))
        {
            await host.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken);
            _ = await host.GetStringAsync("Startup/User", cancellationToken);
            await host.PostJsonAsync(
                "Startup/User",
                JsonSerializer.Serialize(new { Name = "history-seed", Password = password }),
                authorization: null,
                cancellationToken);
            await host.PostJsonAsync("Startup/Complete", "{}", authorization: null, cancellationToken);
            var authentication = await host.PostJsonAsync(
                "Users/AuthenticateByName",
                JsonSerializer.Serialize(new { Username = "history-seed", Pw = password }),
                $"MediaBrowser Client=\"npgsql-history-seed\", Device=\"tests\", DeviceId=\"{Guid.NewGuid():N}\", Version=\"0.1\"",
                cancellationToken);
            using var document = JsonDocument.Parse(authentication);
            var token = document.RootElement.GetProperty("AccessToken").GetString();
            Assert.False(string.IsNullOrWhiteSpace(token));
            redactor.Add(token);

            var databasePath = Path.Combine(layout.DataDirectory, "data", "jellyfin.db");
            Assert.True(File.Exists(databasePath));
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly
            };
            await using var connection = new SqliteConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }

            await host.StopGracefullyAsync(token!, TimeSpan.FromSeconds(30), cancellationToken);
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    private static BaseItemEntity CreateBaseItem(Guid id, string suffix)
        => new()
        {
            Id = id,
            Type = "Movie",
            MediaType = "Video",
            Name = "Unicode model test",
            Path = "/media/" + suffix,
            SortName = suffix,
            CleanName = suffix,
            PresentationUniqueKey = suffix,
            DateCreated = new DateTime(2026, 10, 9, 1, 2, 3, DateTimeKind.Unspecified)
        };

    private static ItemValue CreateItemValue(string value)
        => new()
        {
            ItemValueId = Guid.NewGuid(),
            Type = ItemValueType.Genre,
            Value = value,
            CleanValue = value
        };

    private static async Task<bool> SaveAfterSignalAsync(
        JellyfinDbContext context,
        Task signal,
        CancellationToken cancellationToken)
    {
        await signal;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    private static async Task<(long First, long Second)> InternPairWithRetryAsync(
        string connectionString,
        string firstValue,
        string secondValue,
        TaskCompletionSource ownFirstReady,
        TaskCompletionSource otherFirstReady,
        CancellationToken cancellationToken)
    {
        await using var context = CreateContext(connectionString);
        var strategy = context.Database.CreateExecutionStrategy();
        var attempt = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            var currentAttempt = Interlocked.Increment(ref attempt);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var first = await InternTextAsync(context, firstValue, cancellationToken);
            if (currentAttempt == 1)
            {
                ownFirstReady.TrySetResult();
                await otherFirstReady.Task.WaitAsync(cancellationToken);
            }

            var second = await InternTextAsync(context, secondValue, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (first, second);
        });
    }

    private static Task<long> CountZeroInternIdsAsync(
        JellyfinDbContext context,
        CancellationToken cancellationToken)
        => ScalarAsync<long>(
            context,
            "SELECT "
            + "(SELECT count(*) FROM \"BaseItemProviders\" WHERE \"ProviderIdInternId\" = 0) + "
            + "(SELECT count(*) FROM \"PeopleBaseItemMap\" WHERE \"RoleInternId\" = 0) + "
            + "(SELECT count(*) FROM \"UserData\" WHERE \"CustomDataKeyInternId\" = 0) + "
            + "(SELECT count(*) FROM \"ItemValues\" WHERE \"ValueInternId\" = 0) + "
            + "(SELECT count(*) FROM \"CustomItemDisplayPreferences\" WHERE \"KeyInternId\" = 0)",
            cancellationToken);

    private static async Task<int> CountUpdatedInternedMappingsAsync(
        JellyfinDbContext context,
        string value,
        CancellationToken cancellationToken)
    {
        var count = 0;
        count += await context.BaseItemProviders.AnyAsync(entity => entity.ProviderId == value, cancellationToken) ? 1 : 0;
        count += await context.PeopleBaseItemMap.AnyAsync(entity => entity.Role == value, cancellationToken) ? 1 : 0;
        count += await context.UserData.AnyAsync(entity => entity.CustomDataKey == value, cancellationToken) ? 1 : 0;
        count += await context.ItemValues.AnyAsync(entity => entity.Value == value, cancellationToken) ? 1 : 0;
        count += await context.CustomItemDisplayPreferences.AnyAsync(entity => entity.Key == value, cancellationToken) ? 1 : 0;
        return count;
    }

    private static async Task<long> InternTextAsync(
        JellyfinDbContext context,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT public.\"__jellyfin_intern_text\"(@value)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "value";
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<(bool Matched, Exception? Exception)> EvaluateSqliteLikeAsync(
        SqliteConnection connection,
        string? match,
        string? pattern,
        string? escape,
        bool explicitEscape,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = explicitEscape
                ? "SELECT CASE WHEN @match LIKE @pattern ESCAPE @escape THEN 1 ELSE 0 END"
                : "SELECT CASE WHEN @match LIKE @pattern THEN 1 ELSE 0 END";
            command.Parameters.AddWithValue("match", (object?)match ?? DBNull.Value);
            command.Parameters.AddWithValue("pattern", (object?)pattern ?? DBNull.Value);
            if (explicitEscape)
            {
                command.Parameters.AddWithValue("escape", (object?)escape ?? DBNull.Value);
            }

            var result = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            return (result == 1, null);
        }
        catch (SqliteException exception)
        {
            return (false, exception);
        }
    }

    private static async Task CreateSchemaAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var context = CreateContext(connectionString);
        Assert.True(await context.Database.EnsureCreatedAsync(cancellationToken));
        await context.Database.ExecuteSqlRawAsync(NpgsqlTextInterning.InstallSql, cancellationToken);
        Assert.Equal(
            NpgsqlModelCustomizer.ModelContract,
            context.GetService<IDesignTimeModel>().Model[NpgsqlModelCustomizer.ModelContractAnnotation]);
    }

    private static JellyfinDbContext CreateContext(string connectionString)
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

    private static async Task<string> ExplainWithOnlyIndexAsync(
        JellyfinDbContext context,
        string retainedIndex,
        string sql,
        CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await context.Database.GetDbConnection()
            .BeginTransactionAsync(cancellationToken);
        var indexes = new List<string>();
        await using (var catalog = context.Database.GetDbConnection().CreateCommand())
        {
            catalog.Transaction = transaction;
            catalog.CommandText = "SELECT indexname FROM pg_catalog.pg_indexes WHERE schemaname = 'public' AND tablename = 'BaseItems' AND indexname <> @retained AND indexname <> 'PK_BaseItems'";
            var parameter = catalog.CreateParameter();
            parameter.ParameterName = "retained";
            parameter.Value = retainedIndex;
            catalog.Parameters.Add(parameter);
            await using var reader = await catalog.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                indexes.Add(reader.GetString(0));
            }
        }

        foreach (var index in indexes)
        {
            await using var drop = context.Database.GetDbConnection().CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = "DROP INDEX public.\"" + index.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        var lines = new List<string>();
        await using (var disableScan = context.Database.GetDbConnection().CreateCommand())
        {
            disableScan.Transaction = transaction;
            disableScan.CommandText = "SET LOCAL enable_seqscan = off";
            await disableScan.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "EXPLAIN (COSTS OFF) " + sql;
            await using var planReader = await command.ExecuteReaderAsync(cancellationToken);
            while (await planReader.ReadAsync(cancellationToken))
            {
                lines.Add(planReader.GetString(0));
            }
        }

        await transaction.RollbackAsync(cancellationToken);
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<string> ExplainQueryAsync<TEntity>(
        JellyfinDbContext context,
        IQueryable<TEntity> query,
        CancellationToken cancellationToken)
    {
        var sql = query.ToQueryString();
        Assert.DoesNotContain("-- @", sql, StringComparison.Ordinal);
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await context.Database.GetDbConnection()
            .BeginTransactionAsync(cancellationToken);
        await using (var disableScan = context.Database.GetDbConnection().CreateCommand())
        {
            disableScan.Transaction = transaction;
            disableScan.CommandText = "SET LOCAL enable_seqscan = off";
            await disableScan.ExecuteNonQueryAsync(cancellationToken);
        }

        var lines = new List<string>();
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "EXPLAIN (COSTS OFF) " + sql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(reader.GetString(0));
            }
        }

        await transaction.RollbackAsync(cancellationToken);
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<T> ScalarAsync<T>(
        JellyfinDbContext context,
        string sql,
        CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlModelCollection
{
    public const string Name = "PostgreSQL model compatibility";
}
