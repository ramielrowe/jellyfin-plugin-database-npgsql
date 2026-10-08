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
        FileStream ownerLease)
    {
        ContainerName = containerName;
        AdministratorConnectionString = administratorConnectionString;
        _redactor = redactor;
        _ownerPid = ownerPid;
        _ownerStartTicks = ownerStartTicks;
        _scope = scope;
        _leaseId = leaseId;
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
        var environment = new Dictionary<string, string?>
        {
            ["POSTGRES_PASSWORD"] = password
        };
        var arguments = new[]
        {
            "run", "--detach", "--name", identity.ContainerName,
            "--label", ManagedLabel + "=true",
            "--label", ScopeLabel + "=" + scope,
            "--label", LeaseLabel + "=" + leaseId,
            "--label", OwnerPidLabel + "=" + ownerPid.ToString(CultureInfo.InvariantCulture),
            "--label", OwnerStartLabel + "=" + ownerStartTicks.ToString(CultureInfo.InvariantCulture),
            "--env", "POSTGRES_PASSWORD", "--env", "POSTGRES_USER=jellyfin_npgsql_test_admin",
            "--env", "POSTGRES_DB=" + TestResourceNames.AdministratorDatabaseMarker,
            "--publish", "127.0.0.1::5432", options.ContainerImage,
            "postgres", "-c", "max_connections=20"
        };

        var startAttempted = false;
        try
        {
            startAttempted = true;
            var run = await ExternalCommand.RunAsync(
                "docker",
                arguments,
                TimeSpan.FromMinutes(2),
                cancellationToken,
                environment).ConfigureAwait(false);
            run.EnsureSuccess("Starting the opt-in PostgreSQL test container", redactor);
            var portResult = await ExternalCommand.RunAsync(
                "docker",
                new[] { "port", identity.ContainerName, "5432/tcp" },
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
                    if (!serverVersion.StartsWith("16.6", StringComparison.Ordinal)
                        || serverVersionNumber / 10000 != 16)
                    {
                        throw new InvalidOperationException(
                            $"The pinned PostgreSQL test container must run PostgreSQL 16.6 (major 16), but reported '{serverVersion}'.");
                    }

                    return new PostgreSqlTestContainer(
                        identity.ContainerName,
                        builder.ConnectionString,
                        redactor,
                        ownerPid,
                        ownerStartTicks,
                        scope,
                        leaseId,
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
            if (startAttempted)
            {
                await RemoveAsync(identity.ContainerName, redactor, ownerPid, ownerStartTicks, scope, leaseId).ConfigureAwait(false);
            }

            ownerLease.Dispose();
            DeleteLeaseFile(leaseId);

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
            await RemoveAsync(ContainerName, _redactor, _ownerPid, _ownerStartTicks, _scope, _leaseId).ConfigureAwait(false);
        }
        finally
        {
            _ownerLease.Dispose();
            DeleteLeaseFile(_leaseId);
        }
        GC.SuppressFinalize(this);
    }

    private static async Task RemoveAsync(
        string containerName,
        SensitiveDataRedactor redactor,
        int expectedOwnerPid,
        long expectedOwnerStartTicks,
        string expectedScope,
        string? expectedLeaseId = null)
    {
        TestResourceNames.EnsureOwnedContainer(containerName);
        var inspect = await ExternalCommand.RunAsync(
            "docker",
            new[]
            {
                "inspect", "--format",
                "{{index .Config.Labels \"" + ManagedLabel + "\"}}|{{index .Config.Labels \"" + ScopeLabel + "\"}}|{{index .Config.Labels \"" + LeaseLabel + "\"}}|{{index .Config.Labels \"" + OwnerPidLabel + "\"}}|{{index .Config.Labels \"" + OwnerStartLabel + "\"}}",
                containerName
            },
            TimeSpan.FromSeconds(30),
            CancellationToken.None).ConfigureAwait(false);
        if (inspect.ExitCode != 0
            && inspect.StandardError.Contains("No such", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        inspect.EnsureSuccess("Inspecting the test-only PostgreSQL container before cleanup", redactor);
        var expectedLabels = "true|" + expectedScope + "|" + (expectedLeaseId ?? string.Empty)
            + "|" + expectedOwnerPid.ToString(CultureInfo.InvariantCulture)
            + "|" + expectedOwnerStartTicks.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(inspect.StandardOutput.Trim(), expectedLabels, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to remove test container '{containerName}' because its ownership labels do not match this run.");
        }

        var result = await ExternalCommand.RunAsync(
            "docker",
            new[] { "rm", "--force", containerName },
            TimeSpan.FromSeconds(30),
            CancellationToken.None).ConfigureAwait(false);
        // A failed/partial docker run can leave no container. That state is
        // already clean and must not hide the original startup error.
        if (result.ExitCode != 0
            && !result.StandardError.Contains("No such container", StringComparison.OrdinalIgnoreCase)
            && !result.StandardError.Contains("removal of container", StringComparison.OrdinalIgnoreCase))
        {
            result.EnsureSuccess("Removing the test-only PostgreSQL container", redactor);
        }
    }

    private static async Task ReclaimStaleContainersAsync(string scope, CancellationToken cancellationToken)
    {
        var result = await ExternalCommand.RunAsync(
            "docker",
            new[]
            {
                "ps", "--all", "--filter", "label=" + ManagedLabel + "=true", "--filter", "label=" + ScopeLabel + "=" + scope, "--format",
                "{{.Names}}|{{.Label \"" + LeaseLabel + "\"}}|{{.Label \"" + OwnerPidLabel + "\"}}|{{.Label \"" + OwnerStartLabel + "\"}}"
            },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("Discovering stale Npgsql test containers", new SensitiveDataRedactor());
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('|');
            if (fields.Length != 4
                || !TestResourceNames.IsOwnedContainer(fields[0])
                || fields[1].Length != 32
                || !fields[1].All(Uri.IsHexDigit)
                || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ownerPid)
                || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ownerStart))
            {
                continue;
            }

            FileStream? staleLease = null;
            try
            {
                staleLease = AcquireOwnerLease(fields[1]);
            }
            catch (IOException)
            {
                continue;
            }

            using (staleLease)
            {
                await RemoveAsync(fields[0], new SensitiveDataRedactor(), ownerPid, ownerStart, scope, fields[1]).ConfigureAwait(false);
            }

            DeleteLeaseFile(fields[1]);
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static string GetLeasePath(string leaseId)
        => Path.Combine(PluginRepository.FindRoot(AppContext.BaseDirectory), ".artifacts", "container-leases", leaseId + ".lock");

    private static void DeleteLeaseFile(string leaseId)
    {
        var path = GetLeasePath(leaseId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
