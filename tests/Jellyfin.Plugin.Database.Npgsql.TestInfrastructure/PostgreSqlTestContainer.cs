using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

public sealed class PostgreSqlTestContainer : IAsyncDisposable
{
    private const int StartupAttempts = 60;
    private readonly SensitiveDataRedactor _redactor;
    private bool _disposed;

    private PostgreSqlTestContainer(
        string containerName,
        string administratorConnectionString,
        SensitiveDataRedactor redactor)
    {
        ContainerName = containerName;
        AdministratorConnectionString = administratorConnectionString;
        _redactor = redactor;
    }

    public string ContainerName { get; }

    public string AdministratorConnectionString { get; }

    public static async Task<PostgreSqlTestContainer> StartAsync(
        PostgreSqlTestOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.UseContainer)
        {
            throw new InvalidOperationException("The PostgreSQL container may be started only after explicit opt-in.");
        }

        if (!PostgreSqlTestOptions.IsDigestPinnedImage(options.ContainerImage))
        {
            throw new InvalidOperationException("Refusing to run a PostgreSQL image which is not pinned by SHA-256 digest.");
        }

        var identity = TestResourceNames.Create(options.WorkerId);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var redactor = new SensitiveDataRedactor(password);
        var environment = new Dictionary<string, string?>
        {
            ["POSTGRES_PASSWORD"] = password
        };
        var arguments = new[]
        {
            "run", "--detach", "--name", identity.ContainerName,
            "--env", "POSTGRES_PASSWORD", "--env", "POSTGRES_USER=jellyfin_npgsql_test_admin",
            "--env", "POSTGRES_DB=" + TestResourceNames.AdministratorDatabaseMarker,
            "--publish", "127.0.0.1::5432", options.ContainerImage,
            "postgres", "-c", "max_connections=20"
        };

        var started = false;
        try
        {
            var run = await ExternalCommand.RunAsync(
                "docker",
                arguments,
                TimeSpan.FromMinutes(2),
                cancellationToken,
                environment).ConfigureAwait(false);
            run.EnsureSuccess("Starting the opt-in PostgreSQL test container", redactor);
            started = true;

            var portResult = await ExternalCommand.RunAsync(
                "docker",
                new[] { "port", identity.ContainerName, "5432/tcp" },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            portResult.EnsureSuccess("Resolving the PostgreSQL test container port", redactor);
            var endpoint = portResult.StandardOutput.Trim();
            var separator = endpoint.LastIndexOf(':');
            if (separator < 0
                || !int.TryParse(endpoint.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                throw new InvalidOperationException("Docker did not report a usable loopback port for the PostgreSQL test container.");
            }

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = port,
                Database = TestResourceNames.AdministratorDatabaseMarker,
                Username = "jellyfin_npgsql_test_admin",
                Password = password,
                Pooling = false,
                Timeout = 3,
                CommandTimeout = 10,
                IncludeErrorDetail = false
            };

            Exception? lastError = null;
            for (var attempt = 0; attempt < StartupAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await using var connection = new NpgsqlConnection(builder.ConnectionString);
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                    return new PostgreSqlTestContainer(identity.ContainerName, builder.ConnectionString, redactor);
                }
                catch (NpgsqlException ex)
                {
                    lastError = ex;
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("The opt-in PostgreSQL test container did not become ready within 30 seconds.", lastError);
        }
        catch
        {
            if (started)
            {
                await RemoveAsync(identity.ContainerName, redactor).ConfigureAwait(false);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await RemoveAsync(ContainerName, _redactor).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static async Task RemoveAsync(string containerName, SensitiveDataRedactor redactor)
    {
        TestResourceNames.EnsureOwnedContainer(containerName);
        var result = await ExternalCommand.RunAsync(
            "docker",
            new[] { "rm", "--force", containerName },
            TimeSpan.FromSeconds(30),
            CancellationToken.None).ConfigureAwait(false);
        result.EnsureSuccess("Removing the test-only PostgreSQL container", redactor);
    }
}
