using Jellyfin.Plugin.Database.Npgsql;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using MediaBrowser.Controller;
using Npgsql;

if (args.Length == 2 && args[0] == "restore-recovery-crash")
{
    var connectionString = Environment.GetEnvironmentVariable("JELLYFIN_NPGSQL_RESTORE_CRASH_CONNECTION")
        ?? throw new InvalidOperationException("The private restore crash connection is missing.");
    using var crash = NpgsqlBackupFileSystem.InjectFaults(point =>
    {
        if (string.Equals(point, "restore-apply-before-rename", StringComparison.Ordinal))
        {
            Environment.FailFast("Intentional committed recovery replay crash at " + point);
        }
    });
    await NpgsqlRestoreFileSnapshot.RecoverPendingAsync(
        args[1],
        connectionString,
        CancellationToken.None).ConfigureAwait(false);
    throw new InvalidOperationException("The requested committed recovery replay crash checkpoint was not reached.");
}

if (args.Length == 5 && args[0] == "restore-crash")
{
    var connectionString = Environment.GetEnvironmentVariable("JELLYFIN_NPGSQL_RESTORE_CRASH_CONNECTION")
        ?? throw new InvalidOperationException("The private restore crash connection is missing.");
    var paths = new CrashApplicationPaths(args[2]);
    using var crash = NpgsqlBackupFileSystem.InjectFaults(point =>
    {
        if (string.Equals(point, "restore-journal-" + args[3], StringComparison.Ordinal))
        {
            Environment.FailFast("Intentional restore recovery crash at " + point);
        }
    });
    await using var snapshot = await NpgsqlRestoreFileSnapshot.CaptureAsync(
        args[1],
        paths,
        CancellationToken.None).ConfigureAwait(false);
    if (args[3] is not "Captured")
    {
        await snapshot.ApplyAsync(CancellationToken.None).ConfigureAwait(false);
    }

    if (args[4] == "marker")
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "INSERT INTO public.\"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ($1, $2)",
            connection);
        _ = command.Parameters.AddWithValue(NpgsqlRestoreCoordinator.GetMarkerId(snapshot.RestoreId));
        _ = command.Parameters.AddWithValue(Microsoft.EntityFrameworkCore.Infrastructure.ProductInfo.GetVersion());
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    if (args[3] is "CommitIntent" or "Committed")
    {
        snapshot.MarkCommitIntent();
    }

    if (args[3] == "Committed")
    {
        snapshot.Commit();
    }

    throw new InvalidOperationException("The requested restore crash checkpoint was not reached.");
}

if (args.Length == 3 && args[0] == "cluster-lease-hold")
{
    try
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgreSqlTestOptions.AdministratorConnectionEnvironmentVariable)
            ?? throw new InvalidOperationException("The administrator connection environment is required.");
        await using var lease = await PostgreSqlClusterLease.AcquireAsync(
            connectionString,
            harnessOwnsContainer: true,
            externalExclusiveOptIn: false,
            CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(args[1], "ready", CancellationToken.None).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture))).ConfigureAwait(false);
        return 0;
    }
    catch
    {
        Console.Error.WriteLine("The same-cluster lease holder failed closed.");
        return 1;
    }
}

if (args.Length == 1 && args[0] == "cluster-lease-expect-blocked")
{
    try
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgreSqlTestOptions.AdministratorConnectionEnvironmentVariable)
            ?? throw new InvalidOperationException("The administrator connection environment is required.");
        await using var lease = await PostgreSqlClusterLease.AcquireAsync(
            connectionString,
            harnessOwnsContainer: true,
            externalExclusiveOptIn: false,
            CancellationToken.None).ConfigureAwait(false);
        Console.Error.WriteLine("A second process unexpectedly acquired the same-cluster mutation lease.");
        return 1;
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("mutation lease", StringComparison.Ordinal))
    {
        return 0;
    }
    catch
    {
        Console.Error.WriteLine("The same-cluster contention probe failed unexpectedly.");
        return 1;
    }
}

if (args.Length == 2 && args[0] == "container-cycle")
{
    try
    {
        var options = PostgreSqlTestOptions.FromEnvironment() with { WorkerId = args[1] };
        if (!options.UseContainer)
        {
            throw new InvalidOperationException("container-cycle requires the explicit container opt-in.");
        }

        await using var environment = await PostgreSqlTestEnvironment.CreateAsync(options, CancellationToken.None).ConfigureAwait(false);
        await using var database = await environment.CreateDatabaseAsync(CancellationToken.None).ConfigureAwait(false);
        if (await database.ExecuteScalarAsync<int>("SELECT 1", CancellationToken.None).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The isolated PostgreSQL worker probe failed.");
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

if (args.Length != 2 || args[0] is not ("hash-publish" or "hash-archive"))
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/Jellyfin.Plugin.Database.Npgsql.TestHarness -- hash-publish <directory>");
    Console.Error.WriteLine("   or: dotnet run --project tests/Jellyfin.Plugin.Database.Npgsql.TestHarness -- hash-archive <zip>");
    Console.Error.WriteLine("   or: dotnet run --project tests/Jellyfin.Plugin.Database.Npgsql.TestHarness -- container-cycle <worker>");
    Console.Error.WriteLine("   or: dotnet run --project tests/Jellyfin.Plugin.Database.Npgsql.TestHarness -- cluster-lease-hold <ready-file> <seconds>");
    Console.Error.WriteLine("   or: dotnet run --project tests/Jellyfin.Plugin.Database.Npgsql.TestHarness -- cluster-lease-expect-blocked");
    Console.Error.WriteLine("   or: dotnet run --project tests/Jellyfin.Plugin.Database.Npgsql.TestHarness -- restore-recovery-crash <data-path>");
    return 2;
}

try
{
    var hash = args[0] == "hash-publish"
        ? await PristineJellyfinPublish.HashDirectoryAsync(args[1], CancellationToken.None).ConfigureAwait(false)
        : await PristineJellyfinPublish.HashFileAsync(args[1], CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine(hash);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

internal sealed class CrashApplicationPaths(string root) : IServerApplicationPaths
{
    public string ProgramDataPath => root;
    public string WebPath => Path.Combine(root, "web");
    public string ProgramSystemPath => Path.Combine(root, "system");
    public string DataPath => Path.Combine(root, "data");
    public string ImageCachePath => Path.Combine(root, "cache", "images");
    public string PluginsPath => Path.Combine(root, "plugins");
    public string PluginConfigurationsPath => Path.Combine(root, "plugins", "configurations");
    public string LogDirectoryPath => Path.Combine(root, "log");
    public string ConfigurationDirectoryPath => Path.Combine(root, "config");
    public string SystemConfigurationFilePath => Path.Combine(this.ConfigurationDirectoryPath, "system.xml");
    public string CachePath => Path.Combine(root, "cache");
    public string TempDirectory => Path.Combine(root, "cache", "temp");
    public string VirtualDataPath => "%data%";
    public string TrickplayPath => Path.Combine(root, "data", "trickplay");
    public string BackupPath => Path.Combine(root, "data", "backups");
    public string RootFolderPath => Path.Combine(root, "root");
    public string DefaultUserViewsPath => Path.Combine(this.RootFolderPath, "views");
    public string PeoplePath => Path.Combine(this.RootFolderPath, "people");
    public string GenrePath => Path.Combine(this.RootFolderPath, "genres");
    public string MusicGenrePath => Path.Combine(this.RootFolderPath, "musicgenres");
    public string StudioPath => Path.Combine(this.RootFolderPath, "studios");
    public string YearPath => Path.Combine(this.RootFolderPath, "years");
    public string UserConfigurationDirectoryPath => Path.Combine(this.ConfigurationDirectoryPath, "users");
    public string DefaultInternalMetadataPath => Path.Combine(root, "default-metadata");
    public string InternalMetadataPath => Path.Combine(root, "metadata");
    public string VirtualInternalMetadataPath => "%metadata%";
    public string ArtistsPath => Path.Combine(this.RootFolderPath, "artists");
    public void CreateDirectories() => throw new NotSupportedException();
    public void MakeSanityCheckOrThrow() => throw new NotSupportedException();
    public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        => throw new NotSupportedException();
}
