using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.IntegrationTests;

public sealed class PostgreSqlInfrastructureOptInTests
{
    [Fact]
    public async Task DatabasePerRunFixture_IsolatesWorkersAndCleansEveryExitPath()
    {
        var options = PostgreSqlTestOptions.FromEnvironment();
        Assert.SkipUnless(options.IsConfigured, options.MissingInfrastructureMessage);
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
        var crashedIdentity = TestResourceNames.Create("crashed");
        await using (var connection = new NpgsqlConnection(environment.AdministratorConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            var marker = "jellyfin-npgsql-test-owner|" + PostgreSqlTestDatabase.TestScope + "|00000000000000000000000000000000";
            foreach (var statement in new[]
                     {
                         $"CREATE ROLE \"{crashedIdentity.RoleName}\" NOLOGIN",
                         $"COMMENT ON ROLE \"{crashedIdentity.RoleName}\" IS '{marker}'",
                         $"CREATE DATABASE \"{crashedIdentity.DatabaseName}\" OWNER \"{crashedIdentity.RoleName}\"",
                         $"COMMENT ON DATABASE \"{crashedIdentity.DatabaseName}\" IS '{marker}'"
                     })
            {
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await PostgreSqlTestDatabase.ReclaimStaleResourcesAsync(environment.AdministratorConnectionString, cancellationToken);
        Assert.False(await PostgreSqlTestDatabase.DatabaseExistsAsync(
            environment.AdministratorConnectionString,
            crashedIdentity.DatabaseName,
            cancellationToken));
        Assert.False(await PostgreSqlTestDatabase.RoleExistsAsync(
            environment.AdministratorConnectionString,
            crashedIdentity.RoleName,
            cancellationToken));
        if (options.UseContainer)
        {
            var stale = TestResourceNames.Create("stale").ContainerName;
            var decoy = TestResourceNames.Create("decoy").ContainerName;
            try
            {
                var staleCreate = await ExternalCommand.RunAsync(
                    "docker",
                    new[]
                    {
                        "create", "--name", stale,
                        "--label", "org.jellyfin.database-npgsql.test-managed=true",
                        "--label", "org.jellyfin.database-npgsql.test-scope=" + PostgreSqlTestContainer.TestScope,
                        "--label", "org.jellyfin.database-npgsql.owner-lease=00000000000000000000000000000000",
                        "--label", "org.jellyfin.database-npgsql.owner-pid=2147483647",
                        "--label", "org.jellyfin.database-npgsql.owner-start=0",
                        options.ContainerImage
                    },
                    TimeSpan.FromMinutes(2),
                    cancellationToken);
                staleCreate.EnsureSuccess("Creating a deliberately stale test container", new SensitiveDataRedactor());
                var decoyCreate = await ExternalCommand.RunAsync(
                    "docker",
                    new[] { "create", "--name", decoy, options.ContainerImage },
                    TimeSpan.FromMinutes(2),
                    cancellationToken);
                decoyCreate.EnsureSuccess("Creating an unlabeled decoy test container", new SensitiveDataRedactor());

                await using var reclamationEnvironment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
                Assert.False(await DockerContainerExistsAsync(stale, cancellationToken));
                Assert.True(await DockerContainerExistsAsync(decoy, cancellationToken));
            }
            finally
            {
                _ = await ExternalCommand.RunAsync(
                    "docker",
                    new[] { "rm", "--force", stale, decoy },
                    TimeSpan.FromSeconds(30),
                    CancellationToken.None);
            }
        }

        var databases = await PostgreSqlTestDatabase.CreateManyAsync(
            environment.AdministratorConnectionString,
            new[] { "worker-a", "worker-b" },
            cancellationToken);
        var first = databases[0];
        var second = databases[1];
        try
        {
            Assert.NotEqual(first.DatabaseName, second.DatabaseName);
            Assert.Contains("worker_a", first.DatabaseName, StringComparison.Ordinal);
            Assert.Contains("worker_b", second.DatabaseName, StringComparison.Ordinal);
            Assert.Equal(first.DatabaseName, await first.ExecuteScalarAsync<string>("SELECT current_database()", cancellationToken));
            Assert.Equal(first.RoleName, await first.ExecuteScalarAsync<string>("SELECT current_user", cancellationToken));
            Assert.True(await first.ExecuteScalarAsync<bool>(
                "SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls AND rolconnlimit = 6 FROM pg_catalog.pg_roles WHERE rolname = current_user",
                cancellationToken));
            Assert.Equal(8, await first.ExecuteScalarAsync<int>(
                "SELECT datconnlimit FROM pg_catalog.pg_database WHERE datname = current_database()",
                cancellationToken));
            var serverVersion = await first.ExecuteScalarAsync<string>("SHOW server_version", cancellationToken);
            var serverVersionNumber = await first.ExecuteScalarAsync<int>("SELECT current_setting('server_version_num')::integer", cancellationToken);
            Assert.StartsWith("16.", serverVersion, StringComparison.Ordinal);
            Assert.Equal(16, serverVersionNumber / 10000);

            await first.ExecuteAsync("CREATE TABLE worker_probe (id integer PRIMARY KEY)", cancellationToken);
            Assert.True(await first.ExecuteScalarAsync<bool>("SELECT to_regclass('public.worker_probe') IS NOT NULL", cancellationToken));
            Assert.False(await second.ExecuteScalarAsync<bool>("SELECT to_regclass('public.worker_probe') IS NOT NULL", cancellationToken));
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }

        Assert.False(await PostgreSqlTestDatabase.DatabaseExistsAsync(
            environment.AdministratorConnectionString,
            first.DatabaseName,
            cancellationToken));
        Assert.False(await PostgreSqlTestDatabase.RoleExistsAsync(
            environment.AdministratorConnectionString,
            first.RoleName,
            cancellationToken));

        var partialIdentities = new System.Collections.Concurrent.ConcurrentDictionary<string, TestResourceIdentity>();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlTestDatabase.CreateManyAsync(
                environment.AdministratorConnectionString,
                new[] { "partial-success", "partial-failure" },
                cancellationToken,
                (worker, identity) =>
                {
                    partialIdentities[worker] = identity;
                    if (worker == "partial-failure")
                    {
                        throw new InvalidOperationException("deliberate parallel creation failure");
                    }
                }));
        Assert.Equal(2, partialIdentities.Count);
        foreach (var identity in partialIdentities.Values)
        {
            Assert.False(await PostgreSqlTestDatabase.DatabaseExistsAsync(
                environment.AdministratorConnectionString,
                identity.DatabaseName,
                cancellationToken));
            Assert.False(await PostgreSqlTestDatabase.RoleExistsAsync(
                environment.AdministratorConnectionString,
                identity.RoleName,
                cancellationToken));
        }

        TestResourceIdentity? failedIdentity = null;
        const string FailureMessage = "deliberate setup callback failure";
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlTestDatabase.CreateAsync(
                environment.AdministratorConnectionString,
                "worker-failure",
                cancellationToken,
                identity =>
                {
                    failedIdentity = identity;
                    throw new InvalidOperationException(FailureMessage);
                }));
        Assert.Equal(FailureMessage, failure.Message);
        Assert.NotNull(failedIdentity);
        Assert.False(await PostgreSqlTestDatabase.DatabaseExistsAsync(
            environment.AdministratorConnectionString,
            failedIdentity.DatabaseName,
            cancellationToken));
        Assert.False(await PostgreSqlTestDatabase.RoleExistsAsync(
            environment.AdministratorConnectionString,
            failedIdentity.RoleName,
            cancellationToken));

        using var cancellationSource = new CancellationTokenSource();
        TestResourceIdentity? cancelledIdentity = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PostgreSqlTestDatabase.CreateAsync(
                environment.AdministratorConnectionString,
                "worker-cancelled",
                cancellationSource.Token,
                identity =>
                {
                    cancelledIdentity = identity;
                    cancellationSource.Cancel();
                }));
        Assert.NotNull(cancelledIdentity);
        Assert.False(await PostgreSqlTestDatabase.DatabaseExistsAsync(
            environment.AdministratorConnectionString,
            cancelledIdentity.DatabaseName,
            cancellationToken));
        Assert.False(await PostgreSqlTestDatabase.RoleExistsAsync(
            environment.AdministratorConnectionString,
            cancelledIdentity.RoleName,
            cancellationToken));
    }

    private static async Task<bool> DockerContainerExistsAsync(string name, CancellationToken cancellationToken)
    {
        var inspect = await ExternalCommand.RunAsync(
            "docker",
            new[] { "inspect", name },
            TimeSpan.FromSeconds(30),
            cancellationToken);
        return inspect.ExitCode == 0;
    }
}
