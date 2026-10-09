using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

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
