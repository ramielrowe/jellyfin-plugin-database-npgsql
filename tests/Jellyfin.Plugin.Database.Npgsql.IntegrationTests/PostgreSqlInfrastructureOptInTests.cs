using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
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
        var markerDecoyIdentity = TestResourceNames.Create("decoy");
        var orphanIdentity = TestResourceNames.Create("orphan");
        var staleMarker = "jellyfin-npgsql-test-owner|" + PostgreSqlTestDatabase.TestScope + "|00000000000000000000000000000000";
        var decoyMarker = "jellyfin-npgsql-test-owner|" + PostgreSqlTestDatabase.TestScope + "|11111111111111111111111111111111";
        await environment.ExecuteAdministrativeControlsAsync(
            new[]
            {
                         $"CREATE ROLE \"{crashedIdentity.RoleName}\" NOLOGIN",
                         $"COMMENT ON ROLE \"{crashedIdentity.RoleName}\" IS '{staleMarker}'",
                         $"CREATE DATABASE \"{crashedIdentity.DatabaseName}\" OWNER \"{crashedIdentity.RoleName}\"",
                         $"COMMENT ON DATABASE \"{crashedIdentity.DatabaseName}\" IS '{staleMarker}'",
                         $"CREATE ROLE \"{markerDecoyIdentity.RoleName}\" NOLOGIN",
                         $"COMMENT ON ROLE \"{markerDecoyIdentity.RoleName}\" IS '{staleMarker}'",
                         $"CREATE DATABASE \"{markerDecoyIdentity.DatabaseName}\" OWNER \"{markerDecoyIdentity.RoleName}\"",
                         $"COMMENT ON DATABASE \"{markerDecoyIdentity.DatabaseName}\" IS '{decoyMarker}'",
                         $"CREATE ROLE \"{orphanIdentity.RoleName}\" NOLOGIN",
                         $"COMMENT ON ROLE \"{orphanIdentity.RoleName}\" IS '{staleMarker}'"
            },
            cancellationToken);

        await environment.ReclaimStaleResourcesAsync(cancellationToken);
        Assert.False(await PostgreSqlTestDatabase.DatabaseExistsAsync(
            environment.AdministratorConnectionString,
            crashedIdentity.DatabaseName,
            cancellationToken));
        Assert.False(await PostgreSqlTestDatabase.RoleExistsAsync(
            environment.AdministratorConnectionString,
            crashedIdentity.RoleName,
            cancellationToken));
        Assert.False(await PostgreSqlTestDatabase.RoleExistsAsync(
            environment.AdministratorConnectionString,
            orphanIdentity.RoleName,
            cancellationToken));
        Assert.True(await PostgreSqlTestDatabase.DatabaseExistsAsync(
            environment.AdministratorConnectionString,
            markerDecoyIdentity.DatabaseName,
            cancellationToken));
        await environment.ExecuteAdministrativeControlAsync(
            $"COMMENT ON DATABASE \"{markerDecoyIdentity.DatabaseName}\" IS '{staleMarker}'",
            cancellationToken);

        await environment.ReclaimStaleResourcesAsync(cancellationToken);
        Assert.False(await PostgreSqlTestDatabase.DatabaseExistsAsync(
            environment.AdministratorConnectionString,
            markerDecoyIdentity.DatabaseName,
            cancellationToken));
        Assert.False(await PostgreSqlTestDatabase.RoleExistsAsync(
            environment.AdministratorConnectionString,
            markerDecoyIdentity.RoleName,
            cancellationToken));

        var competingLeaseError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgreSqlClusterLease.AcquireAsync(
                environment.AdministratorConnectionString,
                harnessOwnsContainer: options.UseContainer,
                externalExclusiveOptIn: options.ExternalExclusive,
                cancellationToken));
        Assert.Contains("mutation lease", competingLeaseError.Message, StringComparison.Ordinal);

        TestResourceIdentity? replacedIdentity = null;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => environment.CreateDatabasesAsync(
                new[] { "replacement-race" },
                cancellationToken,
                (_, identity) =>
                {
                    replacedIdentity = identity;
                    environment.ExecuteAdministrativeControlsAsync(
                            new[]
                            {
                                $"DROP DATABASE \"{identity.DatabaseName}\" WITH (FORCE)",
                                $"DROP ROLE \"{identity.RoleName}\"",
                                $"CREATE ROLE \"{identity.RoleName}\" NOLOGIN",
                                $"CREATE DATABASE \"{identity.DatabaseName}\" OWNER \"{identity.RoleName}\""
                            },
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }));
        Assert.NotNull(replacedIdentity);
        Assert.True(await PostgreSqlTestDatabase.DatabaseExistsAsync(
            environment.AdministratorConnectionString,
            replacedIdentity.DatabaseName,
            cancellationToken));
        await environment.ExecuteAdministrativeControlsAsync(
            new[]
            {
                $"DROP DATABASE \"{replacedIdentity.DatabaseName}\" WITH (FORCE)",
                $"DROP ROLE \"{replacedIdentity.RoleName}\""
            },
            cancellationToken);

        if (options.UseContainer)
        {
            var stale = TestResourceNames.Create("stale").ContainerName;
            var decoy = TestResourceNames.Create("decoy").ContainerName;
            string? staleId = null;
            string? decoyId = null;
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
                staleId = staleCreate.StandardOutput.Trim();
                var decoyCreate = await ExternalCommand.RunAsync(
                    "docker",
                    new[] { "create", "--name", decoy, options.ContainerImage },
                    TimeSpan.FromMinutes(2),
                    cancellationToken);
                decoyCreate.EnsureSuccess("Creating an unlabeled decoy test container", new SensitiveDataRedactor());
                decoyId = decoyCreate.StandardOutput.Trim();

                await using var reclamationEnvironment = await PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken);
                Assert.False(await DockerContainerExistsAsync(staleId, cancellationToken));
                Assert.True(await DockerContainerExistsAsync(decoyId, cancellationToken));
            }
            finally
            {
                var immutableIds = new[] { staleId, decoyId }
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .ToArray();
                if (immutableIds.Length != 0)
                {
                    var removal = await ExternalCommand.RunAsync(
                        "docker",
                        new[] { "rm", "--force" }.Concat(immutableIds),
                        TimeSpan.FromSeconds(30),
                        CancellationToken.None);
                    removal.EnsureSuccess("Removing stale-container test controls", new SensitiveDataRedactor());
                }
            }

            var duplicateLease = "22222222222222222222222222222222";
            var duplicateNames = new[]
            {
                TestResourceNames.Create("duplicate-a").ContainerName,
                TestResourceNames.Create("duplicate-b").ContainerName
            };
            var duplicateIds = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var duplicateName in duplicateNames)
                {
                    var create = await ExternalCommand.RunAsync(
                        "docker",
                        new[]
                        {
                            "create", "--name", duplicateName,
                            "--label", "org.jellyfin.database-npgsql.test-managed=true",
                            "--label", "org.jellyfin.database-npgsql.test-scope=" + PostgreSqlTestContainer.TestScope,
                            "--label", "org.jellyfin.database-npgsql.owner-lease=" + duplicateLease,
                            "--label", "org.jellyfin.database-npgsql.owner-pid=2147483647",
                            "--label", "org.jellyfin.database-npgsql.owner-start=0",
                            options.ContainerImage
                        },
                        TimeSpan.FromMinutes(2),
                        cancellationToken);
                    create.EnsureSuccess("Creating duplicate-lease container controls", new SensitiveDataRedactor());
                    duplicateIds.Add(create.StandardOutput.Trim());
                }

                var duplicateError = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => PostgreSqlTestEnvironment.CreateAsync(options, cancellationToken));
                Assert.Contains(duplicateLease, duplicateError.Message, StringComparison.Ordinal);
                foreach (var duplicateId in duplicateIds)
                {
                    Assert.True(await DockerContainerExistsAsync(duplicateId, cancellationToken));
                }
            }
            finally
            {
                if (duplicateIds.Count != 0)
                {
                    var removal = await ExternalCommand.RunAsync(
                        "docker",
                        new[] { "rm", "--force" }.Concat(duplicateIds),
                        TimeSpan.FromSeconds(30),
                        CancellationToken.None);
                    removal.EnsureSuccess("Removing duplicate-lease container controls", new SensitiveDataRedactor());
                }
            }

            var root = PluginRepository.FindRoot(AppContext.BaseDirectory);
            var targetFrameworkDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = targetFrameworkDirectory.Parent?.Name
                ?? throw new InvalidOperationException("Could not resolve the current test build configuration.");
            var helper = Path.Combine(
                root,
                "tests",
                "Jellyfin.Plugin.Database.Npgsql.TestHarness",
                "bin",
                configuration,
                "net10.0",
                "Jellyfin.Plugin.Database.Npgsql.TestHarness.dll");
            var childEnvironment = new System.Collections.Generic.Dictionary<string, string?>
            {
                [PostgreSqlTestOptions.UseContainerEnvironmentVariable] = "true",
                [PostgreSqlTestOptions.AdministratorConnectionEnvironmentVariable] = null,
                [PostgreSqlTestOptions.RequiredEnvironmentVariable] = "true",
                [PostgreSqlTestOptions.ContainerImageEnvironmentVariable] = options.ContainerImage
            };
            var childA = ExternalCommand.RunAsync(
                "dotnet",
                new[] { helper, "container-cycle", "process-a" },
                TimeSpan.FromMinutes(3),
                cancellationToken,
                childEnvironment);
            var childB = ExternalCommand.RunAsync(
                "dotnet",
                new[] { helper, "container-cycle", "process-b" },
                TimeSpan.FromMinutes(3),
                cancellationToken,
                childEnvironment);
            var childResults = await Task.WhenAll(childA, childB);
            foreach (var child in childResults)
            {
                child.EnsureSuccess("Running a true parallel container-backed worker", new SensitiveDataRedactor());
            }

            await using var sharedCluster = await PostgreSqlTestContainer.StartAsync(
                options with { WorkerId = "same-cluster" },
                cancellationToken);
            var readyFile = Path.Combine(
                root,
                ".artifacts",
                "same-cluster-ready-" + Guid.NewGuid().ToString("N"));
            try
            {
                var sharedEnvironment = new System.Collections.Generic.Dictionary<string, string?>
                {
                    [PostgreSqlTestOptions.AdministratorConnectionEnvironmentVariable] = sharedCluster.AdministratorConnectionString,
                    [PostgreSqlTestOptions.UseContainerEnvironmentVariable] = null,
                    [PostgreSqlTestOptions.ExternalExclusiveEnvironmentVariable] = null
                };
                var holder = ExternalCommand.RunAsync(
                    "dotnet",
                    new[] { helper, "cluster-lease-hold", readyFile, "3" },
                    TimeSpan.FromSeconds(30),
                    cancellationToken,
                    sharedEnvironment);
                var readyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (!File.Exists(readyFile) && DateTime.UtcNow < readyDeadline && !holder.IsCompleted)
                {
                    await Task.Delay(25, cancellationToken);
                }

                Assert.True(File.Exists(readyFile), "The first dotnet process did not publish its same-cluster lease handoff.");
                var contender = await ExternalCommand.RunAsync(
                    "dotnet",
                    new[] { helper, "cluster-lease-expect-blocked" },
                    TimeSpan.FromSeconds(30),
                    cancellationToken,
                    sharedEnvironment);
                contender.EnsureSuccess("Proving cross-process same-cluster lease exclusion", new SensitiveDataRedactor());
                (await holder).EnsureSuccess("Holding a cluster lease in a separate dotnet process", new SensitiveDataRedactor());
            }
            finally
            {
                File.Delete(readyFile);
            }
        }

        var databases = await environment.CreateDatabasesAsync(
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
            var serverVersionNumber = await first.ExecuteScalarAsync<int>("SELECT current_setting('server_version_num')::integer", cancellationToken);
            Assert.Equal(160006, serverVersionNumber);

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
            () => environment.CreateDatabasesAsync(
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
            () => environment.CreateDatabaseAsync(
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
            () => environment.CreateDatabaseAsync(
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

    private static async Task<bool> DockerContainerExistsAsync(string containerId, CancellationToken cancellationToken)
    {
        var inspect = await ExternalCommand.RunAsync(
            "docker",
            new[] { "inspect", containerId },
            TimeSpan.FromSeconds(30),
            cancellationToken);
        return inspect.ExitCode == 0;
    }
}
