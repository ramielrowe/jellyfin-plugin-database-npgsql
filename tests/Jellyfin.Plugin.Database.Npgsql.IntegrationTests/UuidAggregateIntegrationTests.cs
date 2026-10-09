using System.Text.Json;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.IntegrationTests;

[Collection(PostgreSqlModelCollection.Name)]
public sealed class UuidAggregateIntegrationTests
{
    [Fact]
    public async Task UuidMinMax_HaveExactCatalogAclBehaviorAndParameterizedIndexPlan()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        await using var database = await environment.CreateDatabaseAsync(cancellationToken);
        try
        {
            await using var context = CreateContext(database.RuntimeConnectionString);
            Assert.True(await context.Database.EnsureCreatedAsync(cancellationToken));
            await context.Database.ExecuteSqlRawAsync(NpgsqlUuidAggregates.InstallSql, cancellationToken);

            const string CatalogSql = """
                SELECT p.proname,
                       p.prokind::text,
                       p.proparallel::text,
                       p.provolatile::text,
                       p.proisstrict,
                       coalesce(p.proconfig::text, ''),
                       pg_catalog.pg_get_userbyid(p.proowner),
                       NOT EXISTS (
                           SELECT 1
                           FROM pg_catalog.aclexplode(coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) acl
                           WHERE acl.grantee = 0 AND acl.privilege_type = 'EXECUTE'),
                       pg_catalog.has_function_privilege(current_user, p.oid, 'EXECUTE'),
                       CASE WHEN p.proname = 'min' THEN a.aggtransfn = 'public.__jellyfin_uuid_min(uuid,uuid)'::regprocedure
                                                        AND a.aggcombinefn = 'public.__jellyfin_uuid_min(uuid,uuid)'::regprocedure
                            WHEN p.proname = 'max' THEN a.aggtransfn = 'public.__jellyfin_uuid_max(uuid,uuid)'::regprocedure
                                                        AND a.aggcombinefn = 'public.__jellyfin_uuid_max(uuid,uuid)'::regprocedure
                            ELSE true END,
                       CASE WHEN p.prokind = 'a' THEN a.aggfinalfn = 0 ELSE true END,
                       CASE WHEN p.proname = 'min' THEN a.aggsortop = 'pg_catalog.<(uuid,uuid)'::regoperator
                            WHEN p.proname = 'max' THEN a.aggsortop = 'pg_catalog.>(uuid,uuid)'::regoperator
                            ELSE true END
                FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                LEFT JOIN pg_catalog.pg_aggregate a ON a.aggfnoid = p.oid
                WHERE n.nspname = 'public'
                  AND ((p.proname IN ('min', 'max') AND pg_catalog.pg_get_function_identity_arguments(p.oid) = 'uuid')
                       OR p.proname IN ('__jellyfin_uuid_min', '__jellyfin_uuid_max'))
                ORDER BY p.proname
                """;
            await context.Database.OpenConnectionAsync(cancellationToken);
            await using (var command = new NpgsqlCommand(CatalogSql, (NpgsqlConnection)context.Database.GetDbConnection()))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                var rows = new List<string>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(reader.GetString(0));
                    var isAggregate = reader.GetString(1) == "a";
                    Assert.Equal("s", reader.GetString(2));
                    Assert.Equal("i", reader.GetString(3));
                    Assert.False(reader.GetBoolean(4));
                    Assert.Equal(isAggregate ? string.Empty : "{search_path=pg_catalog}", reader.GetString(5));
                    Assert.Equal(database.RoleName, reader.GetString(6));
                    Assert.True(reader.GetBoolean(7));
                    Assert.True(reader.GetBoolean(8));
                    Assert.True(reader.GetBoolean(9));
                    Assert.True(reader.GetBoolean(10));
                    Assert.True(reader.GetBoolean(11));
                }

                Assert.Equal(["__jellyfin_uuid_max", "__jellyfin_uuid_min", "max", "min"], rows);
            }

            Assert.Equal(
                "00000001-0000-0000-0000-000000000000|false|00000001-0000-0000-0000-000000000000|ffffffff-ffff-ffff-ffff-ffffffffffff",
                await ScalarAsync<string>(
                    context,
                    """
                    SELECT coalesce(public.min(value)::text, '') || '|'
                        || (public.max(value) IS NULL)::text || '|'
                        || public.min(value)::text || '|'
                        || public.max(value)::text
                    FROM (VALUES
                        (NULL::uuid),
                        ('ffffffff-ffff-ffff-ffff-ffffffffffff'::uuid),
                        ('00000001-0000-0000-0000-000000000000'::uuid),
                        (NULL::uuid)) values(value)
                    """,
                    cancellationToken));
            Assert.True(await ScalarAsync<bool>(context, "SELECT public.min(value) IS NULL AND public.max(value) IS NULL FROM (SELECT NULL::uuid AS value WHERE false) values", cancellationToken));
            Assert.True(await ScalarAsync<bool>(context, "SELECT public.min(value) IS NULL AND public.max(value) IS NULL FROM (VALUES (NULL::uuid), (NULL::uuid)) values(value)", cancellationToken));

            await context.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE public."__UuidAggregatePlanProbe" ("Id" uuid PRIMARY KEY, "Payload" text NOT NULL);
                INSERT INTO public."__UuidAggregatePlanProbe" ("Id", "Payload")
                SELECT (pg_catalog.lpad(pg_catalog.to_hex(value), 8, '0') || '-0000-0000-0000-000000000000')::uuid,
                       pg_catalog.repeat('x', 64)
                FROM pg_catalog.generate_series(1, 10000) value;
                ANALYZE public."__UuidAggregatePlanProbe";
                """,
                cancellationToken);
            var connection = (NpgsqlConnection)context.Database.GetDbConnection();
            await using var explain = new NpgsqlCommand(
                "EXPLAIN (FORMAT JSON, COSTS TRUE) SELECT public.min(\"Id\") FROM public.\"__UuidAggregatePlanProbe\" WHERE \"Id\" >= @floor",
                connection);
            explain.Parameters.AddWithValue("floor", Guid.Parse("00001000-0000-0000-0000-000000000000"));
            var plan = (string)(await explain.ExecuteScalarAsync(cancellationToken))!;
            using var planDocument = JsonDocument.Parse(plan);
            var planText = planDocument.RootElement.ToString();
            Assert.Contains("Limit", planText, StringComparison.Ordinal);
            Assert.Contains("Index Only Scan", planText, StringComparison.Ordinal);
            Assert.Contains("__UuidAggregatePlanProbe", planText, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Node Type\": \"Aggregate\"", planText, StringComparison.Ordinal);

            await using var execute = new NpgsqlCommand(
                "SELECT public.min(\"Id\") FROM public.\"__UuidAggregatePlanProbe\" WHERE \"Id\" >= @floor",
                connection);
            execute.Parameters.AddWithValue("floor", Guid.Parse("00001000-0000-0000-0000-000000000000"));
            Assert.Equal(Guid.Parse("00001000-0000-0000-0000-000000000000"), await execute.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    private static JellyfinDbContext CreateContext(string connectionString)
    {
        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
        var options = new DbContextOptionsBuilder<JellyfinDbContext>();
        NpgsqlOptionsBootstrap.Configure(options, connectionString, null);
        return new JellyfinDbContext(
            options.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private static async Task<T> ScalarAsync<T>(JellyfinDbContext context, string sql, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
