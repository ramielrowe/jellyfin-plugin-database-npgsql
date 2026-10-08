using System;
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
        var firstTask = PostgreSqlTestDatabase.CreateAsync(
            environment.AdministratorConnectionString,
            "worker-a",
            cancellationToken);
        var secondTask = PostgreSqlTestDatabase.CreateAsync(
            environment.AdministratorConnectionString,
            "worker-b",
            cancellationToken);
        var databases = await Task.WhenAll(firstTask, secondTask);
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
                "SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole FROM pg_catalog.pg_roles WHERE rolname = current_user",
                cancellationToken));

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
}
