using System.Data;
using System.Data.Common;
using System.Text.Json;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task PluginConnections_DisablePostgreSqlJitForRequestQueries()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await using var context = CreateContext(database.RuntimeConnectionString);
            Assert.Equal("off", await ScalarAsync<string>(context, "SHOW jit", cancellationToken));
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

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
    public async Task CallerOwnedTransactions_ResolveConcurrentItemValueAndRebindBothMapsToWinner()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            var firstItemId = Guid.NewGuid();
            var secondItemId = Guid.NewGuid();
            var sharedTag = "concurrent-tag-" + Guid.NewGuid().ToString("N");
            await using (var seed = CreateContext(database.RuntimeConnectionString))
            {
                seed.BaseItems.AddRange(
                    CreateBaseItem(firstItemId, "concurrent-first"),
                    CreateBaseItem(secondItemId, "concurrent-second"));
                await seed.SaveChangesAsync(cancellationToken);
            }

            var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstOperation = PersistItemTagAfterSharedReadAsync(
                database.RuntimeConnectionString,
                firstItemId,
                sharedTag,
                firstRead,
                secondRead,
                cancellationToken);
            var secondOperation = PersistItemTagAfterSharedReadAsync(
                database.RuntimeConnectionString,
                secondItemId,
                sharedTag,
                secondRead,
                firstRead,
                cancellationToken);

            await Task.WhenAll(firstOperation, secondOperation);

            await using var verification = CreateContext(database.RuntimeConnectionString);
            var canonicalValue = await verification.ItemValues
                .SingleAsync(
                    value => value.Type == ItemValueType.Tags && value.Value == sharedTag,
                    cancellationToken);
            var maps = await verification.ItemValuesMap
                .Where(map => map.ItemId == firstItemId || map.ItemId == secondItemId)
                .OrderBy(map => map.ItemId)
                .ToArrayAsync(cancellationToken);
            Assert.Equal(2, maps.Length);
            Assert.All(maps, map => Assert.Equal(canonicalValue.ItemValueId, map.ItemValueId));
            Assert.Equal(2, maps.Select(map => map.ItemId).Distinct().Count());

            await using var duplicate = CreateContext(database.RuntimeConnectionString);
            await using var duplicateTransaction = await duplicate.Database.BeginTransactionAsync(cancellationToken);
            duplicate.ItemValues.Add(CreateItemValue(sharedTag, ItemValueType.Tags));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync(cancellationToken));
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CallerOwnedTransactions_UpsertConcurrentUserDataAsOneCompletePlaybackSnapshot()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            var itemId = Guid.NewGuid();
            Guid userId;
            await using (var seed = CreateContext(database.RuntimeConnectionString))
            {
                var user = new User("concurrent-playback", "auth", "reset");
                userId = user.Id;
                seed.AddRange(user, CreateBaseItem(itemId, "concurrent-playback"));
                await seed.SaveChangesAsync(cancellationToken);
            }

            var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstOperation = PersistUserDataAfterSharedReadAsync(
                database.RuntimeConnectionString,
                CreateUserDataSnapshot(itemId, userId, "playback-key", 1, 100),
                firstRead,
                secondRead,
                cancellationToken);
            var secondOperation = PersistUserDataAfterSharedReadAsync(
                database.RuntimeConnectionString,
                CreateUserDataSnapshot(itemId, userId, "playback-key", 2, 200),
                secondRead,
                firstRead,
                cancellationToken);

            await Task.WhenAll(firstOperation, secondOperation);

            await using var verification = CreateContext(database.RuntimeConnectionString);
            var stored = await verification.UserData.SingleAsync(
                data => data.ItemId == itemId
                    && data.UserId == userId
                    && data.CustomDataKey == "playback-key",
                cancellationToken);
            Assert.Contains(stored.PlayCount, new[] { 1, 2 });
            Assert.Equal(stored.PlayCount * 100, stored.PlaybackPositionTicks);
            Assert.Equal(
                stored.PlayCount == 1 ? DateTime.UnixEpoch.AddMinutes(1) : DateTime.UnixEpoch.AddMinutes(2),
                stored.LastPlayedDate);
            Assert.True(verification.Entry(stored).Property<long>("CustomDataKeyInternId").CurrentValue > 0);

            await using var duplicate = CreateContext(database.RuntimeConnectionString);
            duplicate.UserData.Add(CreateUserDataSnapshot(itemId, userId, "playback-key", 3, 300));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync(cancellationToken));
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CallerOwnedTransactions_PredecessorDeadlocksButSuccessorSerializesInverseRoleInterning()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            var predecessorFirstItemId = Guid.NewGuid();
            var predecessorSecondItemId = Guid.NewGuid();
            var successorFirstItemId = Guid.NewGuid();
            var successorSecondItemId = Guid.NewGuid();
            var firstPersonId = Guid.Parse("10000000-0000-0000-0000-000000000001");
            var secondPersonId = Guid.Parse("10000000-0000-0000-0000-000000000002");
            await using (var setup = CreateContext(database.RuntimeConnectionString))
            {
                setup.AddRange(
                    CreateBaseItem(predecessorFirstItemId, "inverse-intern-predecessor-first"),
                    CreateBaseItem(predecessorSecondItemId, "inverse-intern-predecessor-second"),
                    CreateBaseItem(successorFirstItemId, "inverse-intern-successor-first"),
                    CreateBaseItem(successorSecondItemId, "inverse-intern-successor-second"),
                    new People { Id = firstPersonId, Name = "Inverse Intern First", PersonType = "Actor" },
                    new People { Id = secondPersonId, Name = "Inverse Intern Second", PersonType = "Actor" });
                await setup.SaveChangesAsync(cancellationToken);
                await setup.Database.ExecuteSqlRawAsync(
                    """
                    CREATE SEQUENCE public."__jellyfin_test_intern_barrier_one_sequence";
                    CREATE SEQUENCE public."__jellyfin_test_intern_barrier_two_sequence";
                    CREATE OR REPLACE FUNCTION public."__jellyfin_test_intern_barrier"()
                    RETURNS trigger LANGUAGE plpgsql
                    SET search_path = pg_catalog, public
                    AS $jellyfin$
                    DECLARE
                        test_writer text := current_setting('jellyfin.test_intern_writer', true);
                        writer_invocation bigint;
                        lock_namespace bigint := (
                            SELECT oid::bigint << 2
                            FROM pg_catalog.pg_database
                            WHERE datname = current_database());
                        own_lock bigint;
                        opposing_lock bigint;
                        barrier_deadline timestamp with time zone := clock_timestamp() + interval '10 seconds';
                    BEGIN
                        IF test_writer = 'one' THEN
                            writer_invocation := nextval('public."__jellyfin_test_intern_barrier_one_sequence"');
                            own_lock := lock_namespace + 101;
                            opposing_lock := lock_namespace + 102;
                        ELSIF test_writer = 'two' THEN
                            writer_invocation := nextval('public."__jellyfin_test_intern_barrier_two_sequence"');
                            own_lock := lock_namespace + 102;
                            opposing_lock := lock_namespace + 101;
                        ELSE
                            RAISE EXCEPTION 'missing deterministic interning-barrier writer'
                                USING ERRCODE = '22023';
                        END IF;

                        IF writer_invocation <> 1 THEN
                            RETURN NEW;
                        END IF;

                        -- The production interning trigger sorts before this test
                        -- trigger. Each writer therefore retains its first new trie
                        -- path before joining this bounded rendezvous.
                        PERFORM pg_advisory_xact_lock(own_lock);
                        LOOP
                            EXIT WHEN EXISTS (
                                SELECT 1
                                FROM pg_catalog.pg_locks
                                WHERE locktype = 'advisory'
                                    AND granted
                                    AND pid <> pg_backend_pid()
                                    AND classid = (opposing_lock >> 32)::oid
                                    AND objid = (opposing_lock & 4294967295)::oid
                                    AND objsubid = 1);
                            IF clock_timestamp() >= barrier_deadline THEN
                                RAISE EXCEPTION 'timed out waiting for opposing interning-barrier lock'
                                    USING ERRCODE = '57014';
                            END IF;
                            PERFORM pg_sleep(0.01);
                        END LOOP;
                        RETURN NEW;
                    END;
                    $jellyfin$;
                    CREATE TRIGGER "ZZ_JellyfinTestInternBarrier"
                        BEFORE INSERT ON public."PeopleBaseItemMap"
                        FOR EACH ROW EXECUTE FUNCTION public."__jellyfin_test_intern_barrier"();
                    """,
                    cancellationToken);

                await setup.Database.ExecuteSqlRawAsync(
                    NpgsqlTextInterning.RestorePredecessorInternFunctionSql,
                    cancellationToken);
            }

            // Both operations use the same ordered People keys, but assign opposite
            // new roles to them. EF therefore reaches the real row triggers as A/B
            // and B/A regardless of Add order. The test trigger runs after the
            // interning trigger and holds both writers at a bounded advisory-lock
            // rendezvous after their first trie insertions.
            var predecessorFirstRole = "predecessor-role-a-" + Guid.NewGuid().ToString("N");
            var predecessorSecondRole = "predecessor-role-b-" + Guid.NewGuid().ToString("N");
            var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var predecessorForward = CaptureExceptionAsync(() => PersistRolesAfterBarrierAsync(
                database.RuntimeConnectionString,
                predecessorFirstItemId,
                [firstPersonId, secondPersonId],
                [predecessorFirstRole, predecessorSecondRole],
                firstReady,
                secondReady,
                cancellationToken,
                "one"));
            var predecessorReverse = CaptureExceptionAsync(() => PersistRolesAfterBarrierAsync(
                database.RuntimeConnectionString,
                predecessorSecondItemId,
                [firstPersonId, secondPersonId],
                [predecessorSecondRole, predecessorFirstRole],
                secondReady,
                firstReady,
                cancellationToken,
                "two"));
            var predecessorOutcomes = await Task.WhenAll(predecessorForward, predecessorReverse);
            Assert.Single(predecessorOutcomes, exception => exception is null);
            var predecessorFailure = Assert.Single(predecessorOutcomes, exception => exception is not null)!;
            Assert.True(
                HasPostgresSqlState(predecessorFailure, "40P01"),
                $"Expected the predecessor's inverse trigger order to deadlock, but received: {predecessorFailure}");

            await using (var upgrade = CreateContext(database.RuntimeConnectionString))
            {
                await upgrade.Database.ExecuteSqlRawAsync(
                    """
                    DROP TRIGGER "ZZ_JellyfinTestInternBarrier" ON public."PeopleBaseItemMap";
                    DROP FUNCTION public."__jellyfin_test_intern_barrier"();
                    DROP SEQUENCE public."__jellyfin_test_intern_barrier_one_sequence";
                    DROP SEQUENCE public."__jellyfin_test_intern_barrier_two_sequence";
                    """,
                    cancellationToken);
                await upgrade.Database.ExecuteSqlRawAsync(
                    NpgsqlTextInterning.InstallDeadlockSafeInternFunctionSql,
                    cancellationToken);
            }

            // ProviderId is itself part of EF's logical key, so EF may sort these
            // rows into one order. This half verifies canonical provider identity;
            // the role half above and below is the actual inverse-order regression.
            var firstProvider = "inverse-provider-a-" + Guid.NewGuid().ToString("N");
            var secondProvider = "inverse-provider-b-" + Guid.NewGuid().ToString("N");
            firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var forwardProviders = PersistProviderIdsAfterBarrierAsync(
                database.RuntimeConnectionString,
                successorFirstItemId,
                [firstProvider, secondProvider],
                firstReady,
                secondReady,
                cancellationToken);
            var reverseProviders = PersistProviderIdsAfterBarrierAsync(
                database.RuntimeConnectionString,
                successorSecondItemId,
                [secondProvider, firstProvider],
                secondReady,
                firstReady,
                cancellationToken);
            await Task.WhenAll(forwardProviders, reverseProviders);

            var firstRole = "inverse-role-a-" + Guid.NewGuid().ToString("N");
            var secondRole = "inverse-role-b-" + Guid.NewGuid().ToString("N");
            firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var forwardRoles = PersistRolesAfterBarrierAsync(
                database.RuntimeConnectionString,
                successorFirstItemId,
                [firstPersonId, secondPersonId],
                [firstRole, secondRole],
                firstReady,
                secondReady,
                cancellationToken);
            var reverseRoles = PersistRolesAfterBarrierAsync(
                database.RuntimeConnectionString,
                successorSecondItemId,
                [firstPersonId, secondPersonId],
                [secondRole, firstRole],
                secondReady,
                firstReady,
                cancellationToken);
            await Task.WhenAll(forwardRoles, reverseRoles);

            await using var verification = CreateContext(database.RuntimeConnectionString);
            var providerRows = await verification.BaseItemProviders
                .Where(value => value.ItemId == successorFirstItemId || value.ItemId == successorSecondItemId)
                .Select(value => new
                {
                    value.ProviderId,
                    InternId = EF.Property<long>(value, "ProviderIdInternId")
                })
                .ToArrayAsync(cancellationToken);
            Assert.Equal(4, providerRows.Length);
            Assert.Equal(2, providerRows.Select(value => value.InternId).Distinct().Count());
            Assert.All(
                providerRows.GroupBy(value => value.ProviderId),
                group => Assert.Single(group.Select(value => value.InternId).Distinct()));

            var roleRows = await verification.PeopleBaseItemMap
                .Where(value => value.ItemId == successorFirstItemId || value.ItemId == successorSecondItemId)
                .Select(value => new
                {
                    value.Role,
                    InternId = EF.Property<long>(value, "RoleInternId")
                })
                .ToArrayAsync(cancellationToken);
            Assert.Equal(4, roleRows.Length);
            Assert.Equal(2, roleRows.Select(value => value.InternId).Distinct().Count());
            Assert.All(
                roleRows.GroupBy(value => value.Role),
                group => Assert.Single(group.Select(value => value.InternId).Distinct()));
            Assert.Equal(
                6,
                await ScalarAsync<long>(
                    verification,
                    "SELECT count(DISTINCT \"Chunk\") FROM public.\"__JellyfinTextNodes\" WHERE NOT \"IsTerminal\"",
                    cancellationToken));
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ChunkInterning_CanonicalizesInverseOrderNewAndExistingNodes()
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

            var forward = InternPairAsync(
                database.RuntimeConnectionString,
                firstValue,
                secondValue,
                cancellationToken);
            var reverse = InternPairAsync(
                database.RuntimeConnectionString,
                secondValue,
                firstValue,
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

            var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
    public async Task SaveChanges_AutomaticallyRetriesInverseOrderDeadlockAndSerializationFailureWithoutDuplicates()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            await using (var setup = CreateContext(database.RuntimeConnectionString))
            {
                await setup.Database.ExecuteSqlRawAsync(
                    """
                    CREATE SEQUENCE public."__jellyfin_test_deadlock_sequence";
                    CREATE SEQUENCE public."__jellyfin_test_deadlock_role_one_sequence";
                    CREATE SEQUENCE public."__jellyfin_test_deadlock_role_two_sequence";
                    CREATE OR REPLACE FUNCTION public."__jellyfin_test_force_deadlock"()
                    RETURNS trigger LANGUAGE plpgsql
                    SET search_path = pg_catalog, public
                    AS $jellyfin$
                    DECLARE
                        invocation bigint := nextval('public."__jellyfin_test_deadlock_sequence"');
                        test_role text := current_setting('jellyfin.test_deadlock_role', true);
                        role_invocation bigint;
                        lock_namespace bigint := (
                            SELECT oid::bigint << 2
                            FROM pg_catalog.pg_database
                            WHERE datname = current_database());
                        own_lock bigint;
                        opposing_lock bigint;
                        barrier_deadline timestamp with time zone := clock_timestamp() + interval '10 seconds';
                    BEGIN
                        IF test_role = 'one' THEN
                            role_invocation := nextval('public."__jellyfin_test_deadlock_role_one_sequence"');
                            own_lock := lock_namespace + 1;
                            opposing_lock := lock_namespace + 2;
                        ELSIF test_role = 'two' THEN
                            role_invocation := nextval('public."__jellyfin_test_deadlock_role_two_sequence"');
                            own_lock := lock_namespace + 2;
                            opposing_lock := lock_namespace + 1;
                        ELSE
                            RAISE EXCEPTION 'missing deterministic deadlock test role'
                                USING ERRCODE = '22023';
                        END IF;

                        IF role_invocation <> 1 THEN
                            RETURN NEW;
                        END IF;

                        PERFORM pg_advisory_xact_lock(own_lock);
                        LOOP
                            EXIT WHEN EXISTS (
                                SELECT 1
                                FROM pg_catalog.pg_locks
                                WHERE locktype = 'advisory'
                                    AND granted
                                    AND pid <> pg_backend_pid()
                                    AND classid = (opposing_lock >> 32)::oid
                                    AND objid = (opposing_lock & 4294967295)::oid
                                    AND objsubid = 1);
                            IF clock_timestamp() >= barrier_deadline THEN
                                RAISE EXCEPTION 'timed out waiting for opposing test advisory lock'
                                    USING ERRCODE = '57014';
                            END IF;
                            PERFORM pg_sleep(0.01);
                        END LOOP;
                        PERFORM pg_advisory_xact_lock(opposing_lock);
                        RETURN NEW;
                    END;
                    $jellyfin$;
                    CREATE TRIGGER "ZZ_JellyfinTestForceDeadlock"
                        BEFORE INSERT ON public."ItemValues"
                        FOR EACH ROW EXECUTE FUNCTION public."__jellyfin_test_force_deadlock"();
                    """,
                    cancellationToken);
            }

            var forwardFirstValue = "save-race-forward-a-" + Guid.NewGuid().ToString("N");
            var forwardSecondValue = "save-race-forward-b-" + Guid.NewGuid().ToString("N");
            var reverseFirstValue = "save-race-reverse-a-" + Guid.NewGuid().ToString("N");
            var reverseSecondValue = "save-race-reverse-b-" + Guid.NewGuid().ToString("N");
            await using (var internSetup = CreateContext(database.RuntimeConnectionString))
            await using (var internTransaction = await internSetup.Database.BeginTransactionAsync(cancellationToken))
            {
                foreach (var value in new[] { forwardFirstValue, forwardSecondValue, reverseFirstValue, reverseSecondValue })
                {
                    await InternTextAsync(internSetup, value, cancellationToken);
                }

                await internTransaction.CommitAsync(cancellationToken);
            }

            await using var forwardContext = CreateContext(database.RuntimeConnectionString);
            await using var reverseContext = CreateContext(database.RuntimeConnectionString);
            await forwardContext.Database.OpenConnectionAsync(cancellationToken);
            await reverseContext.Database.OpenConnectionAsync(cancellationToken);
            await forwardContext.Database.ExecuteSqlRawAsync(
                "SET jellyfin.test_deadlock_role = 'one'",
                cancellationToken);
            await reverseContext.Database.ExecuteSqlRawAsync(
                "SET jellyfin.test_deadlock_role = 'two'",
                cancellationToken);
            var forwardEntries = new[]
            {
                forwardContext.ItemValues.Add(CreateItemValue(forwardFirstValue, ItemValueType.Genre)),
                forwardContext.ItemValues.Add(CreateItemValue(forwardSecondValue, ItemValueType.Genre))
            };
            var reverseEntries = new[]
            {
                reverseContext.ItemValues.Add(CreateItemValue(reverseFirstValue, ItemValueType.Studios)),
                reverseContext.ItemValues.Add(CreateItemValue(reverseSecondValue, ItemValueType.Studios))
            };
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var forwardSave = SaveAfterSignalWithoutHandlingAsync(forwardContext, start.Task, cancellationToken);
            var reverseSave = SaveAfterSignalWithoutHandlingAsync(reverseContext, start.Task, cancellationToken);
            start.SetResult();
            await Task.WhenAll(forwardSave, reverseSave);

            Assert.All(forwardEntries.Concat(reverseEntries), entry =>
            {
                Assert.Equal(EntityState.Unchanged, entry.State);
                Assert.True(entry.Property<long>("ValueInternId").CurrentValue > 0);
            });

            await using (var verification = CreateContext(database.RuntimeConnectionString))
            {
                var expectedValues = new[]
                {
                    forwardFirstValue,
                    forwardSecondValue,
                    reverseFirstValue,
                    reverseSecondValue
                };
                var rows = await verification.ItemValues
                    .Where(value => expectedValues.Contains(value.Value))
                    .Select(value => new
                    {
                        value.Type,
                        value.Value,
                        InternId = EF.Property<long>(value, "ValueInternId")
                    })
                    .ToArrayAsync(cancellationToken);
                Assert.Equal(4, rows.Length);
                Assert.Equal(expectedValues.Order(), rows.Select(row => row.Value).Order());
                Assert.Equal(4, rows.Select(row => row.InternId).Distinct().Count());
                Assert.Equal(
                    8,
                    await ScalarAsync<long>(
                        verification,
                        "SELECT count(*) FROM public.\"__JellyfinTextNodes\"",
                        cancellationToken));
                Assert.Equal(
                    5,
                    await ScalarAsync<long>(
                        verification,
                        "SELECT last_value FROM public.\"__jellyfin_test_deadlock_sequence\"",
                        cancellationToken));
                var roleInvocations = new[]
                {
                    await ScalarAsync<long>(
                        verification,
                        "SELECT last_value FROM public.\"__jellyfin_test_deadlock_role_one_sequence\"",
                        cancellationToken),
                    await ScalarAsync<long>(
                        verification,
                        "SELECT last_value FROM public.\"__jellyfin_test_deadlock_role_two_sequence\"",
                        cancellationToken)
                };
                Assert.Equal([2L, 3L], roleInvocations.Order());

                await verification.Database.ExecuteSqlRawAsync(
                    """
                    DROP TRIGGER "ZZ_JellyfinTestForceDeadlock" ON public."ItemValues";
                    CREATE SEQUENCE public."__jellyfin_test_serialization_sequence";
                    CREATE OR REPLACE FUNCTION public."__jellyfin_test_force_serialization"()
                    RETURNS trigger LANGUAGE plpgsql
                    SET search_path = pg_catalog, public
                    AS $jellyfin$
                    BEGIN
                        IF nextval('public."__jellyfin_test_serialization_sequence"') = 1 THEN
                            RAISE EXCEPTION 'forced retryable serialization failure'
                                USING ERRCODE = '40001';
                        END IF;
                        RETURN NEW;
                    END;
                    $jellyfin$;
                    CREATE TRIGGER "ZZ_JellyfinTestForceSerialization"
                        BEFORE INSERT ON public."ItemValues"
                        FOR EACH ROW EXECUTE FUNCTION public."__jellyfin_test_force_serialization"();
                    """,
                    cancellationToken);
            }

            await using var serializationContext = CreateContext(database.RuntimeConnectionString);
            var serializationValue = "save-serialization-" + Guid.NewGuid().ToString("N");
            var serializationEntry = serializationContext.ItemValues.Add(
                CreateItemValue(serializationValue, ItemValueType.Tags));
            await serializationContext.SaveChangesAsync(cancellationToken);
            Assert.Equal(EntityState.Unchanged, serializationEntry.State);
            var serializationInternId = serializationEntry.Property<long>("ValueInternId").CurrentValue;
            Assert.True(serializationInternId > 0);

            await using var finalVerification = CreateContext(database.RuntimeConnectionString);
            var persistedSerializationIds = await finalVerification.ItemValues
                .Where(value => value.Type == ItemValueType.Tags && value.Value == serializationValue)
                .Select(value => EF.Property<long>(value, "ValueInternId"))
                .ToArrayAsync(cancellationToken);
            Assert.Equal([serializationInternId], persistedSerializationIds);
            Assert.Equal(
                2,
                await ScalarAsync<long>(
                    finalVerification,
                    "SELECT last_value FROM public.\"__jellyfin_test_serialization_sequence\"",
                    cancellationToken));
        }
        finally
        {
            global::Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task HostManagedTransaction_AllowsOrdinaryQueriesAndSaveChangesWithoutRetryStrategyFailure()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await CreateSchemaAsync(database.RuntimeConnectionString, cancellationToken);
            var value = "host-transaction-" + Guid.NewGuid().ToString("N");
            await using (var context = CreateContext(database.RuntimeConnectionString))
            await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
            {
                context.ItemValues.Add(CreateItemValue(value));
                await context.SaveChangesAsync(cancellationToken);
                Assert.Equal(1, await context.ItemValues.CountAsync(item => item.Value == value, cancellationToken));
                await transaction.CommitAsync(cancellationToken);
            }

            await using var verification = CreateContext(database.RuntimeConnectionString);
            Assert.Equal(1, await verification.ItemValues.CountAsync(item => item.Value == value, cancellationToken));
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
                ("axfoo", "ax%", "X", true),
                ("aXfoo", "aX%", "x", true),
                ("a%", "aX%", "X", true),
                ("a%", "ax%", "x", true),
                ("CAFEÉx雪", "cafeÉX_", "x", true),
                ("CAFEÉ%", "cafeÉ雪%", "雪", true),
                ("path\\_%", "pathZ\\Z_Z%", "Z", true),
                ("value", "valueX", "X", true),
                ("ASCII", "ascii", null, false),
                ("CAFÉ", "café", null, false),
                (null, "%", null, false),
                ("value", null, null, false),
                ("value", "value", null, true),
                ("value", "value", string.Empty, true),
                ("value", "value", "XX", true)
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
            var capture = new QueryCommandCaptureInterceptor();
            await using var context = CreateContext(database.RuntimeConnectionString, capture);
            var targetTopParentId = Guid.NewGuid();
            var targetDate = new DateTime(2026, 10, 9, 1, 2, 3, DateTimeKind.Utc);
            var target = CreateBaseItem(Guid.NewGuid(), "planner-target");
            target.Type = "PlannerCommon";
            target.MediaType = "PlannerMedia";
            target.Name = "planner-target";
            target.CleanName = "planner-target";
            target.PresentationUniqueKey = "planner-target";
            target.SeriesPresentationUniqueKey = "planner-target";
            target.SeriesName = "planner-target";
            target.TopParentId = targetTopParentId;
            target.ParentIndexNumber = 7;
            target.IndexNumber = 11;
            target.DateCreated = targetDate;
            context.BaseItems.Add(target);
            var typeOnlyTarget = CreateBaseItem(Guid.NewGuid(), "planner-type-only");
            typeOnlyTarget.Type = "PlannerTypeOnly";
            context.BaseItems.Add(typeOnlyTarget);
            for (var index = 0; index < 2000; index++)
            {
                var noise = CreateBaseItem(Guid.NewGuid(), "planner-noise-" + index);
                noise.Type = "PlannerCommon";
                noise.MediaType = "PlannerMedia";
                noise.Name = "planner-noise-name-" + index;
                noise.CleanName = "planner-noise-clean-" + index;
                noise.PresentationUniqueKey = "planner-noise-presentation-" + index;
                noise.SeriesPresentationUniqueKey = "planner-noise-series-presentation-" + index;
                noise.SeriesName = "planner-noise-series-" + index;
                noise.TopParentId = Guid.NewGuid();
                noise.IsFolder = true;
                noise.IsVirtualItem = true;
                noise.ParentIndexNumber = index + 100;
                noise.IndexNumber = index + 200;
                noise.DateCreated = targetDate.AddMinutes(index + 1);
                context.BaseItems.Add(noise);
            }

            context.Peoples.Add(new People { Id = Guid.NewGuid(), Name = "Unique Planner Person", PersonType = "Actor" });
            for (var index = 0; index < 10000; index++)
            {
                context.Peoples.Add(new People
                {
                    Id = Guid.NewGuid(),
                    Name = "Noise Planner Person " + index,
                    PersonType = "Actor"
                });
            }

            const int itemValueDimension = 30;
            for (var type = 0; type < itemValueDimension; type++)
            {
                for (var value = 0; value < itemValueDimension; value++)
                {
                    context.ItemValues.Add(CreateItemValue(
                        "planner-value-" + value,
                        (ItemValueType)type));
                }
            }

            const string plannerTargetValue = "planner-target";
            for (var type = 0; type < itemValueDimension; type++)
            {
                context.ItemValues.Add(CreateItemValue(plannerTargetValue, (ItemValueType)type));
            }

            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlRawAsync("ANALYZE", cancellationToken);

            var targetPath = target.Path;
            var targetName = target.Name;
            var targetCleanName = target.CleanName;
            var targetPresentationKey = target.PresentationUniqueKey;
            var targetSeriesPresentationKey = target.SeriesPresentationUniqueKey;
            var targetSeriesName = target.SeriesName;
            var targetSortName = target.SortName;
            var targetTypeOnly = typeOnlyTarget.Type;
            var targetType = target.Type;
            var targetMediaType = target.MediaType;
            var targetIsFolder = target.IsFolder;
            var targetIsVirtual = target.IsVirtualItem;
            var targetParentIndex = target.ParentIndexNumber;
            var targetIndex = target.IndexNumber;

            var probes = new Dictionary<string, IQueryable<Guid>>(StringComparer.Ordinal)
            {
                ["IX_BaseItems_Path"] = context.BaseItems.Where(item => item.Path == targetPath).Select(item => item.Id),
                ["IX_BaseItems_Name"] = context.BaseItems.Where(item => item.Name == targetName).Select(item => item.Id),
                ["IX_BaseItems_CleanName"] = context.BaseItems.Where(item => item.CleanName == targetCleanName).Select(item => item.Id),
                ["IX_BaseItems_PresentationUniqueKey"] = context.BaseItems.Where(item => item.PresentationUniqueKey == targetPresentationKey).Select(item => item.Id),
                ["IX_BaseItems_SeriesPresentationUniqueKey"] = context.BaseItems.Where(item => item.SeriesPresentationUniqueKey == targetSeriesPresentationKey).Select(item => item.Id),
                ["IX_BaseItems_SeriesName"] = context.BaseItems.Where(item => item.SeriesName == targetSeriesName).Select(item => item.Id),
                ["IX_BaseItems_SortName"] = context.BaseItems.Where(item => item.SortName == targetSortName).Select(item => item.Id),
                ["IX_BaseItems_Npgsql_Type"] = context.BaseItems.Where(item => item.Type == targetTypeOnly).Select(item => item.Id),
                ["IX_BaseItems_Npgsql_Type_TopParentId"] = context.BaseItems.Where(item => item.Type == targetType && item.TopParentId == targetTopParentId).Select(item => item.Id),
                ["IX_BaseItems_Npgsql_Type_IsFolder_IsVirtualItem"] = context.BaseItems.Where(item => item.Type == targetType && item.IsFolder == targetIsFolder && item.IsVirtualItem == targetIsVirtual).Select(item => item.Id),
                ["IX_BaseItems_Npgsql_IsFolder_TopParentId_IsVirtualItem"] = context.BaseItems.Where(item => item.IsFolder == targetIsFolder && item.TopParentId == targetTopParentId && item.IsVirtualItem == targetIsVirtual).Select(item => item.Id),
                ["IX_BaseItems_Npgsql_MediaType_TopParentId_IsVirtualItem"] = context.BaseItems.Where(item => item.MediaType == targetMediaType && item.TopParentId == targetTopParentId && item.IsVirtualItem == targetIsVirtual).Select(item => item.Id),
                ["IX_BaseItems_Npgsql_Type_ParentIndexNumber_IndexNumber"] = context.BaseItems.Where(item => item.Type == targetType && item.ParentIndexNumber == targetParentIndex && item.IndexNumber == targetIndex).Select(item => item.Id),
                ["IX_BaseItems_Npgsql_Type_TopParentId_IsVirtualItem_DateCreated"] = context.BaseItems.Where(item => item.Type == targetType && item.TopParentId == targetTopParentId && item.IsVirtualItem == targetIsVirtual && item.DateCreated == targetDate).Select(item => item.Id)
            };

            foreach (var (expectedIndexFragment, query) in probes)
            {
                var plan = await ExplainWithOnlyIndexAsync(
                    context,
                    expectedIndexFragment,
                    query,
                    capture,
                    cancellationToken);
                Assert.Contains("Index", plan, StringComparison.Ordinal);
                Assert.Contains(expectedIndexFragment, plan, StringComparison.Ordinal);
                Assert.Contains("Index Cond", plan, StringComparison.Ordinal);
            }

            // PeopleRepository lowercases NameStartsWith before constructing this LINQ.
            var peoplePrefix = "unique";
            var peoplePlan = await ExplainQueryAsync(
                context,
                context.Peoples.Where(person => person.Name.StartsWith(peoplePrefix)).Select(person => person.Id),
                capture,
                cancellationToken);
            Assert.Contains("IX_Peoples_Npgsql_NameLowerPrefix", peoplePlan, StringComparison.Ordinal);
            Assert.Contains("Index Cond", peoplePlan, StringComparison.Ordinal);

            var targetItemValueType = (ItemValueType)7;
            var itemValuePlan = await ExplainQueryAsync(
                context,
                context.ItemValues
                    .Where(value => value.Type == targetItemValueType && value.Value == plannerTargetValue)
                    .Select(value => value.ItemValueId),
                capture,
                cancellationToken);
            Assert.Contains("IX_ItemValues_Value", itemValuePlan, StringComparison.Ordinal);
            Assert.Contains("IX_ItemValues_Npgsql_Type", itemValuePlan, StringComparison.Ordinal);
            Assert.Contains("Type", itemValuePlan, StringComparison.Ordinal);

            var orderedType = target.Type;
            var ascendingPlan = await ExplainQueryAsync(
                context,
                context.BaseItems.Where(item => item.Type == orderedType).OrderBy(item => item.SortName),
                capture,
                cancellationToken);
            Assert.Contains("Sort", ascendingPlan, StringComparison.Ordinal);
            Assert.Contains("IS NULL", ascendingPlan, StringComparison.Ordinal);
            var descendingPlan = await ExplainQueryAsync(
                context,
                context.BaseItems.Where(item => item.Type == orderedType).OrderByDescending(item => item.SortName),
                capture,
                cancellationToken);
            Assert.Contains("DESC", descendingPlan, StringComparison.Ordinal);
            Assert.Contains("IS NULL", descendingPlan, StringComparison.Ordinal);
            var thenByPlan = await ExplainQueryAsync(
                context,
                context.BaseItems.Where(item => item.MediaType == targetMediaType).OrderBy(item => item.Type).ThenBy(item => item.SortName),
                capture,
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
        using var artifacts = HostTestArtifactScope.Create("model-compatibility");
        var artifactRoot = artifacts.CaseDirectory;
        var databasePassword = new global::Npgsql.NpgsqlConnectionStringBuilder(database.RuntimeConnectionString).Password;
        var evidenceRedactor = new SensitiveDataRedactor(database.RuntimeConnectionString, databasePassword);
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
                var migrationIds = context.Database.GetMigrations().ToArray();
                Assert.Equal(
                    [
                        "20261009061101_InitialPostgreSqlBaseline",
                        "20261009061446_AddActivityLogTypeDateIndex",
                        "20261009113458_AddUuidMinMaxAggregates",
                        "20261011031154_SerializeTextInterningMisses",
                    ],
                    migrationIds);
                await context.Database.ExecuteSqlRawAsync(
                    "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (\"MigrationId\" character varying(150) NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, \"ProductVersion\" character varying(32) NOT NULL)",
                    cancellationToken);
                foreach (var migrationId in migrationIds)
                {
                    await context.Database.ExecuteSqlInterpolatedAsync(
                        $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({migrationId}, {"10.0.3"})",
                        cancellationToken);
                }

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
            layout.InstallPluginZip(package, PluginMetadata.InstallationDirectory);
            layout.WriteDatabaseConfiguration(
                database.RuntimeConnectionString,
                PluginMetadata.InstallationDirectory,
                PluginMetadata.AssemblyFileName);
            var portLeaseRoot = Path.Combine(artifactRoot, ".port-leases");
            using var httpPort = LoopbackPortReservation.Acquire(portLeaseRoot);
            using var httpsPort = LoopbackPortReservation.Acquire(portLeaseRoot);
            layout.WriteNetworkConfiguration(httpPort.Port, httpsPort.Port);
            var password = "model-host-" + Guid.NewGuid().ToString("N");
            evidenceRedactor.Add(password);
            var redactor = new SensitiveDataRedactor(password, database.RuntimeConnectionString, databasePassword);
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
                evidenceRedactor.Add(token);
                await host.StopGracefullyAsync(token!, TimeSpan.FromSeconds(30), cancellationToken);
            }

            await publish.VerifyUnchangedAsync(cancellationToken);
            await using (var context = CreateContext(database.RuntimeConnectionString))
            {
                Assert.True(await context.Users.AnyAsync(user => user.Username == "model-admin", cancellationToken));
            }

        }
        catch (Exception exception)
        {
            artifacts.RetainFailureEvidence(exception, evidenceRedactor);
            throw new InvalidOperationException(
                $"The pristine-host model proof failed. Bounded sanitized diagnostics are in '{artifacts.RetainedEvidenceDirectory}' "
                + $"({artifacts.RetainedEvidenceBytes} bytes); its copied host and case scratch directory were removed.",
                exception);
        }
        finally
        {
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

    private static ItemValue CreateItemValue(string value, ItemValueType type = ItemValueType.Genre)
        => new()
        {
            ItemValueId = Guid.NewGuid(),
            Type = type,
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

    private static async Task PersistItemTagAfterSharedReadAsync(
        string connectionString,
        Guid itemId,
        string tag,
        TaskCompletionSource ownRead,
        TaskCompletionSource otherRead,
        CancellationToken cancellationToken)
    {
        await using var context = CreateContext(connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existingValue = await context.ItemValues.SingleOrDefaultAsync(
            value => value.Type == ItemValueType.Tags && value.Value == tag,
            cancellationToken);

        ownRead.TrySetResult();
        await otherRead.Task.WaitAsync(cancellationToken);

        var itemValue = existingValue ?? CreateItemValue(tag, ItemValueType.Tags);
        if (existingValue is null)
        {
            context.ItemValues.Add(itemValue);
        }

        context.ItemValuesMap.Add(new ItemValueMap
        {
            ItemId = itemId,
            Item = null!,
            ItemValueId = itemValue.ItemValueId,
            ItemValue = null!
        });
        // ItemPersistenceService.SaveItems uses these synchronous calls inside its
        // caller-owned transaction; keep the regression on that production path.
        context.SaveChanges();
        transaction.Commit();
    }

    private static UserData CreateUserDataSnapshot(
        Guid itemId,
        Guid userId,
        string customDataKey,
        int playCount,
        long playbackPositionTicks)
        => new()
        {
            ItemId = itemId,
            Item = null,
            UserId = userId,
            User = null,
            CustomDataKey = customDataKey,
            PlayCount = playCount,
            PlaybackPositionTicks = playbackPositionTicks,
            LastPlayedDate = DateTime.UnixEpoch.AddMinutes(playCount)
        };

    private static async Task PersistUserDataAfterSharedReadAsync(
        string connectionString,
        UserData snapshot,
        TaskCompletionSource ownRead,
        TaskCompletionSource otherRead,
        CancellationToken cancellationToken)
    {
        await using var context = CreateContext(connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        Assert.False(await context.UserData.AnyAsync(
            data => data.ItemId == snapshot.ItemId
                && data.UserId == snapshot.UserId
                && data.CustomDataKey == snapshot.CustomDataKey,
            cancellationToken));

        ownRead.TrySetResult();
        await otherRead.Task.WaitAsync(cancellationToken);

        context.UserData.Add(snapshot);
        context.SaveChanges();
        transaction.Commit();
    }

    private static async Task SaveAfterSignalWithoutHandlingAsync(
        JellyfinDbContext context,
        Task signal,
        CancellationToken cancellationToken)
    {
        await signal;
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static bool HasPostgresSqlState(Exception? exception, string sqlState)
        => exception switch
        {
            global::Npgsql.PostgresException postgresException => postgresException.SqlState == sqlState,
            AggregateException aggregateException => aggregateException.InnerExceptions.Any(
                innerException => HasPostgresSqlState(innerException, sqlState)),
            null => false,
            _ => HasPostgresSqlState(exception.InnerException, sqlState),
        };

    private static async Task PersistProviderIdsAfterBarrierAsync(
        string connectionString,
        Guid itemId,
        IReadOnlyList<string> providerIds,
        TaskCompletionSource ownReady,
        TaskCompletionSource otherReady,
        CancellationToken cancellationToken)
    {
        await using var context = CreateContext(connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        Assert.Equal(IsolationLevel.ReadCommitted, transaction.GetDbTransaction().IsolationLevel);
        foreach (var providerId in providerIds)
        {
            context.BaseItemProviders.Add(new BaseItemProvider
            {
                ItemId = itemId,
                Item = null!,
                ProviderId = providerId,
                ProviderValue = "inverse-order"
            });
        }

        ownReady.TrySetResult();
        await otherReady.Task.WaitAsync(cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task PersistRolesAfterBarrierAsync(
        string connectionString,
        Guid itemId,
        IReadOnlyList<Guid> personIds,
        IReadOnlyList<string> roles,
        TaskCompletionSource ownReady,
        TaskCompletionSource otherReady,
        CancellationToken cancellationToken,
        string? barrierWriter = null)
    {
        await using var context = CreateContext(connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        Assert.Equal(IsolationLevel.ReadCommitted, transaction.GetDbTransaction().IsolationLevel);
        if (barrierWriter is not null)
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('jellyfin.test_intern_writer', {barrierWriter}, true)",
                cancellationToken);
        }

        for (var index = 0; index < roles.Count; index++)
        {
            context.PeopleBaseItemMap.Add(new PeopleBaseItemMap
            {
                ItemId = itemId,
                Item = null!,
                PeopleId = personIds[index],
                People = null!,
                Role = roles[index],
                ListOrder = index
            });
        }

        ownReady.TrySetResult();
        await otherReady.Task.WaitAsync(cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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

    private static async Task<(long First, long Second)> InternPairAsync(
        string connectionString,
        string firstValue,
        string secondValue,
        CancellationToken cancellationToken)
    {
        await using var context = CreateContext(connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var first = await InternTextAsync(context, firstValue, cancellationToken);
        var second = await InternTextAsync(context, secondValue, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (first, second);
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
        await context.Database.ExecuteSqlRawAsync(NpgsqlTextInterning.InstallCurrentSql, cancellationToken);
        Assert.Equal(
            NpgsqlModelCustomizer.ModelContract,
            context.GetService<IDesignTimeModel>().Model[NpgsqlModelCustomizer.ModelContractAnnotation]);
    }

    private static JellyfinDbContext CreateContext(
        string connectionString,
        params IInterceptor[] interceptors)
    {
        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        NpgsqlOptionsBootstrap.Configure(builder, connectionString, null);
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new JellyfinDbContext(
            builder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private static async Task<string> ExplainWithOnlyIndexAsync<TEntity>(
        JellyfinDbContext context,
        string retainedIndex,
        IQueryable<TEntity> query,
        QueryCommandCaptureInterceptor capture,
        CancellationToken cancellationToken)
    {
        var captured = await ExecuteAndCaptureAsync(query, capture, cancellationToken);
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
        await ReadPlanAsync(context, transaction, captured, lines, cancellationToken);

        await transaction.RollbackAsync(cancellationToken);
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<string> ExplainQueryAsync<TEntity>(
        JellyfinDbContext context,
        IQueryable<TEntity> query,
        QueryCommandCaptureInterceptor capture,
        CancellationToken cancellationToken)
    {
        var captured = await ExecuteAndCaptureAsync(query, capture, cancellationToken);
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await context.Database.GetDbConnection()
            .BeginTransactionAsync(cancellationToken);
        var lines = new List<string>();
        await ReadPlanAsync(context, transaction, captured, lines, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<CapturedCommand> ExecuteAndCaptureAsync<TEntity>(
        IQueryable<TEntity> query,
        QueryCommandCaptureInterceptor capture,
        CancellationToken cancellationToken)
    {
        var tag = "jellyfin-npgsql-plan-" + Guid.NewGuid().ToString("N");
        capture.Watch(tag);
        _ = await query.TagWith(tag).ToArrayAsync(cancellationToken);
        var captured = capture.Take(tag);
        Assert.NotEmpty(captured.Parameters);
        Assert.All(
            captured.Parameters,
            parameter => Assert.Contains(parameter.ParameterName, captured.CommandText, StringComparison.Ordinal));
        return captured;
    }

    private static async Task ReadPlanAsync(
        JellyfinDbContext context,
        DbTransaction transaction,
        CapturedCommand captured,
        List<string> lines,
        CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "EXPLAIN (COSTS OFF) " + captured.CommandText;
        foreach (var capturedParameter in captured.Parameters)
        {
            command.Parameters.Add(capturedParameter.Clone());
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(reader.GetString(0));
        }
    }

    private sealed record CapturedCommand(
        string CommandText,
        IReadOnlyList<global::Npgsql.NpgsqlParameter> Parameters);

    private sealed class QueryCommandCaptureInterceptor : DbCommandInterceptor
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, CapturedCommand?> _commands = new(StringComparer.Ordinal);

        public void Watch(string tag)
        {
            lock (_sync)
            {
                _commands.Add(tag, null);
            }
        }

        public CapturedCommand Take(string tag)
        {
            lock (_sync)
            {
                var command = Assert.IsType<CapturedCommand>(_commands[tag]);
                _commands.Remove(tag);
                return command;
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                foreach (var tag in _commands.Keys)
                {
                    if (_commands[tag] is null
                        && command.CommandText.Contains(tag, StringComparison.Ordinal))
                    {
                        _commands[tag] = new CapturedCommand(
                            command.CommandText,
                            command.Parameters
                                .Cast<global::Npgsql.NpgsqlParameter>()
                                .Select(parameter => (global::Npgsql.NpgsqlParameter)parameter.Clone())
                                .ToArray());
                        break;
                    }
                }
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
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
