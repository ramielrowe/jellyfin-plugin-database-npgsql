using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

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
