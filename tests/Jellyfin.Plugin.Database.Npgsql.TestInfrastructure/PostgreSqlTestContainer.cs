using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

public sealed class PostgreSqlTestContainer : IAsyncDisposable
{
    private const int StartupAttempts = 60;
    private const string ManagedLabel = "org.jellyfin.database-npgsql.test-managed";
    private const string OwnerPidLabel = "org.jellyfin.database-npgsql.owner-pid";
    private const string OwnerStartLabel = "org.jellyfin.database-npgsql.owner-start";
    private const string ScopeLabel = "org.jellyfin.database-npgsql.test-scope";
    private const string LeaseLabel = "org.jellyfin.database-npgsql.owner-lease";
    private readonly SensitiveDataRedactor _redactor;
    private readonly int _ownerPid;
    private readonly long _ownerStartTicks;
    private readonly string _scope;
    private readonly string _leaseId;
    private readonly string _containerId;
    private readonly string _secretDirectory;
    private readonly FileStream _ownerLease;
    private bool _disposed;

    private PostgreSqlTestContainer(
        string containerName,
        string administratorConnectionString,
        SensitiveDataRedactor redactor,
        int ownerPid,
        long ownerStartTicks,
        string scope,
        string leaseId,
        string containerId,
        string secretDirectory,
        FileStream ownerLease)
    {
        ContainerName = containerName;
        AdministratorConnectionString = administratorConnectionString;
        _redactor = redactor;
        _ownerPid = ownerPid;
        _ownerStartTicks = ownerStartTicks;
        _scope = scope;
        _leaseId = leaseId;
        _containerId = containerId;
        _secretDirectory = secretDirectory;
        _ownerLease = ownerLease;
    }

    public string ContainerName { get; }

    public string AdministratorConnectionString { get; }

    public string ServerVersion { get; private init; } = string.Empty;

    public static string TestScope => GetScope();

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
        var scope = GetScope();
        await ReclaimStaleContainersAsync(scope, cancellationToken).ConfigureAwait(false);
        var leaseId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var ownerLease = AcquireOwnerLease(leaseId);
        var ownerPid = Environment.ProcessId;
        var ownerStartTicks = GetProcessStartTicks(ownerPid);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var redactor = new SensitiveDataRedactor(password);
        var secretDirectory = Path.Combine(PluginRepository.FindRoot(AppContext.BaseDirectory), ".artifacts", "container-secrets", leaseId);
        SecureFileSystem.CreateDirectory(secretDirectory);
        var passwordFile = Path.Combine(secretDirectory, "postgres-password");
        SecureFileSystem.WriteAllTextAtomic(passwordFile, password + "\n");
        var arguments = new[]
        {
            "run", "--detach", "--name", identity.ContainerName,
            "--label", ManagedLabel + "=true",
            "--label", ScopeLabel + "=" + scope,
            "--label", LeaseLabel + "=" + leaseId,
            "--label", OwnerPidLabel + "=" + ownerPid.ToString(CultureInfo.InvariantCulture),
            "--label", OwnerStartLabel + "=" + ownerStartTicks.ToString(CultureInfo.InvariantCulture),
            "--mount", "type=bind,source=" + passwordFile + ",target=/run/secrets/postgres-password,readonly",
            "--env", "POSTGRES_PASSWORD_FILE=/run/secrets/postgres-password", "--env", "POSTGRES_USER=jellyfin_npgsql_test_admin",
            "--env", "POSTGRES_DB=" + TestResourceNames.AdministratorDatabaseMarker,
            "--publish", "127.0.0.1::5432", options.ContainerImage,
            "postgres", "-c", "max_connections=20"
        };

        string? containerId = null;
        try
        {
            var run = await ExternalCommand.RunAsync(
                "docker",
                arguments,
                TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);
            var reportedId = run.StandardOutput.Trim();
            if (reportedId.Length == 64 && reportedId.All(character => char.IsAsciiHexDigit(character) && !char.IsUpper(character)))
            {
                containerId = reportedId;
            }

            run.EnsureSuccess("Starting the opt-in PostgreSQL test container", redactor);
            if (containerId is null)
            {
                throw new InvalidOperationException("Docker did not return one immutable lowercase container ID.");
            }

            var portResult = await ExternalCommand.RunAsync(
                "docker",
                new[] { "port", containerId, "5432/tcp" },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            portResult.EnsureSuccess("Resolving the PostgreSQL test container port", redactor);
            var endpoint = portResult.StandardOutput.Trim();
            var separator = endpoint.LastIndexOf(':');
            if (separator < 0
                || !int.TryParse(endpoint.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port is < 1 or > 65535)
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
                    await using var versionCommand = connection.CreateCommand();
                    versionCommand.CommandText = "SELECT current_setting('server_version'), current_setting('server_version_num')::integer";
                    await using var reader = await versionCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    var serverVersion = reader.GetString(0);
                    var serverVersionNumber = reader.GetInt32(1);
                    if (serverVersionNumber != 160006)
                    {
                        throw new InvalidOperationException(
                            $"The pinned PostgreSQL test container must report server_version_num 160006, but reported {serverVersionNumber}.");
                    }

                    return new PostgreSqlTestContainer(
                        identity.ContainerName,
                        builder.ConnectionString,
                        redactor,
                        ownerPid,
                        ownerStartTicks,
                        scope,
                        leaseId,
                        containerId,
                        secretDirectory,
                        ownerLease)
                    {
                        ServerVersion = serverVersion
                    };
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
            try
            {
                containerId ??= await FindContainerIdByLeaseAsync(scope, leaseId).ConfigureAwait(false);
                if (containerId is not null)
                {
                    await RemoveAsync(containerId, identity.ContainerName, redactor, ownerPid, ownerStartTicks, scope, leaseId).ConfigureAwait(false);
                }
            }
            finally
            {
                ownerLease.Dispose();
                DeleteSecretDirectory(secretDirectory);
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
        try
        {
            await RemoveAsync(_containerId, ContainerName, _redactor, _ownerPid, _ownerStartTicks, _scope, _leaseId).ConfigureAwait(false);
        }
        finally
        {
            _ownerLease.Dispose();
            DeleteSecretDirectory(_secretDirectory);
        }
        GC.SuppressFinalize(this);
    }

    private static async Task RemoveAsync(
        string containerId,
        string expectedContainerName,
        SensitiveDataRedactor redactor,
        int expectedOwnerPid,
        long expectedOwnerStartTicks,
        string expectedScope,
        string? expectedLeaseId = null)
    {
        TestResourceNames.EnsureOwnedContainer(expectedContainerName);
        if (containerId.Length != 64 || !containerId.All(character => char.IsAsciiHexDigit(character) && !char.IsUpper(character)))
        {
            throw new InvalidOperationException("Refusing to inspect a non-canonical Docker container ID.");
        }

        var inspect = await ExternalCommand.RunAsync(
            "docker",
            new[]
            {
                "inspect", "--format",
                "{{.Id}}|{{.Name}}|{{index .Config.Labels \"" + ManagedLabel + "\"}}|{{index .Config.Labels \"" + ScopeLabel + "\"}}|{{index .Config.Labels \"" + LeaseLabel + "\"}}|{{index .Config.Labels \"" + OwnerPidLabel + "\"}}|{{index .Config.Labels \"" + OwnerStartLabel + "\"}}",
                containerId
            },
            TimeSpan.FromSeconds(30),
            CancellationToken.None).ConfigureAwait(false);
        if (inspect.ExitCode != 0
            && inspect.StandardError.Contains("No such", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        inspect.EnsureSuccess("Inspecting the test-only PostgreSQL container before cleanup", redactor);
        var expectedLabels = containerId + "|/" + expectedContainerName + "|true|" + expectedScope + "|" + (expectedLeaseId ?? string.Empty)
            + "|" + expectedOwnerPid.ToString(CultureInfo.InvariantCulture)
            + "|" + expectedOwnerStartTicks.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(inspect.StandardOutput.Trim(), expectedLabels, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to remove test container '{expectedContainerName}' because its immutable ID, name, or ownership labels do not match this run.");
        }

        var result = await ExternalCommand.RunAsync(
            "docker",
            new[] { "rm", "--force", containerId },
            TimeSpan.FromSeconds(30),
            CancellationToken.None).ConfigureAwait(false);
        if (result.ExitCode != 0
            && !result.StandardError.Contains("No such container", StringComparison.OrdinalIgnoreCase))
        {
            result.EnsureSuccess("Removing the test-only PostgreSQL container", redactor);
        }
        else if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Docker could not confirm removal of the exact test container ID.");
        }
    }

    private static async Task ReclaimStaleContainersAsync(string scope, CancellationToken cancellationToken)
    {
        var result = await ExternalCommand.RunAsync(
            "docker",
            new[]
            {
                "ps", "--all", "--filter", "label=" + ManagedLabel + "=true", "--filter", "label=" + ScopeLabel + "=" + scope, "--format",
                "{{.ID}}|{{.Names}}|{{.Label \"" + LeaseLabel + "\"}}|{{.Label \"" + OwnerPidLabel + "\"}}|{{.Label \"" + OwnerStartLabel + "\"}}"
            },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("Discovering stale Npgsql test containers", new SensitiveDataRedactor());
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('|');
            if (fields.Length != 5
                || fields[0].Length is < 12 or > 64
                || !fields[0].All(character => char.IsAsciiHexDigit(character) && !char.IsUpper(character))
                || !TestResourceNames.IsOwnedContainer(fields[1])
                || fields[2].Length != 32
                || !fields[2].All(Uri.IsHexDigit)
                || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ownerPid)
                || !long.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var ownerStart))
            {
                continue;
            }

            FileStream? staleLease = null;
            try
            {
                staleLease = AcquireOwnerLease(fields[2]);
            }
            catch (IOException)
            {
                continue;
            }

            using (staleLease)
            {
                var fullId = await ResolveFullContainerIdAsync(fields[0], cancellationToken).ConfigureAwait(false);
                await RemoveAsync(fullId, fields[1], new SensitiveDataRedactor(), ownerPid, ownerStart, scope, fields[2]).ConfigureAwait(false);
            }

            DeleteSecretDirectory(Path.Combine(PluginRepository.FindRoot(AppContext.BaseDirectory), ".artifacts", "container-secrets", fields[2]));
        }
    }

    private static long GetProcessStartTicks(int processId)
    {
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static string GetScope()
    {
        var root = PluginRepository.FindRoot(AppContext.BaseDirectory);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root))).ToLowerInvariant();
    }

    private static FileStream AcquireOwnerLease(string leaseId)
    {
        var path = GetLeasePath(leaseId);
        return SecureFileSystem.OpenLease(path);
    }

    private static string GetLeasePath(string leaseId)
        => Path.Combine(PluginRepository.FindRoot(AppContext.BaseDirectory), ".artifacts", "container-leases", leaseId + ".lock");

    private static async Task<string> ResolveFullContainerIdAsync(string containerId, CancellationToken cancellationToken)
    {
        var result = await ExternalCommand.RunAsync(
            "docker",
            new[] { "inspect", "--format", "{{.Id}}", containerId },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("Resolving an immutable stale test container ID", new SensitiveDataRedactor());
        var fullId = result.StandardOutput.Trim();
        if (fullId.Length != 64 || !fullId.All(character => char.IsAsciiHexDigit(character) && !char.IsUpper(character)))
        {
            throw new InvalidOperationException("Docker returned a malformed immutable container ID.");
        }

        return fullId;
    }

    private static async Task<string?> FindContainerIdByLeaseAsync(string scope, string leaseId)
    {
        var result = await ExternalCommand.RunAsync(
            "docker",
            new[]
            {
                "ps", "--all",
                "--filter", "label=" + ManagedLabel + "=true",
                "--filter", "label=" + ScopeLabel + "=" + scope,
                "--filter", "label=" + LeaseLabel + "=" + leaseId,
                "--format", "{{.ID}}"
            },
            TimeSpan.FromSeconds(30),
            CancellationToken.None).ConfigureAwait(false);
        result.EnsureSuccess("Finding a partially-created test container by its exact lease labels", new SensitiveDataRedactor());
        var ids = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0)
        {
            return null;
        }

        if (ids.Length != 1)
        {
            throw new InvalidOperationException("More than one Docker container reported the unique test lease; refusing cleanup.");
        }

        return await ResolveFullContainerIdAsync(ids[0], CancellationToken.None).ConfigureAwait(false);
    }

    private static void DeleteSecretDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
