using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

const string OwnershipMarker = ".jellyfin-npgsql-owned-artifacts";

if (args.Length == 3 && args[0] is "create-owned-artifacts" or "relocate-standalone")
{
    try
    {
        var source = Path.GetFullPath(args[1]);
        var ownedRoot = Path.GetFullPath(args[2]);
        await CreateOwnedRootAsync(source, ownedRoot).ConfigureAwait(false);
        if (args[0] == "relocate-standalone")
        {
            CopyStandalone(source, Path.Combine(ownedRoot, "repository"));
            Console.WriteLine(Path.Combine(ownedRoot, "repository"));
        }
        else
        {
            Console.WriteLine(ownedRoot);
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

if (args.Length == 3 && args[0] == "delete-owned-artifacts")
{
    try
    {
        var source = Path.GetFullPath(args[1]);
        var ownedRoot = Path.GetFullPath(args[2]);
        RequireChild(Path.Combine(source, ".artifacts"), ownedRoot);
        var marker = Path.Combine(ownedRoot, OwnershipMarker);
        if (!File.Exists(marker)
            || !string.Equals(
                await File.ReadAllTextAsync(marker, CancellationToken.None).ConfigureAwait(false),
                "Owned by Jellyfin.Plugin.Database.Npgsql.TestHarness\n",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete a directory without the exact ownership marker.");
        }

        Directory.Delete(ownedRoot, recursive: true);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
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
    Console.Error.WriteLine("   or: dotnet run --project tests/Jellyfin.Plugin.Database.Npgsql.TestHarness -- create-owned-artifacts <source> <owned-artifact-root>");
    Console.Error.WriteLine("   or: dotnet run --project tests/Jellyfin.Plugin.Database.Npgsql.TestHarness -- relocate-standalone <source> <owned-artifact-root>");
    Console.Error.WriteLine("   or: dotnet run --project tests/Jellyfin.Plugin.Database.Npgsql.TestHarness -- delete-owned-artifacts <source> <owned-artifact-root>");
    return 2;
}

static async Task CreateOwnedRootAsync(string source, string ownedRoot)
{
    RequireChild(Path.Combine(source, ".artifacts"), ownedRoot);
    if (Directory.Exists(ownedRoot) || File.Exists(ownedRoot))
    {
        throw new InvalidOperationException("The owned artifact destination must not already exist.");
    }

    Directory.CreateDirectory(ownedRoot);
    await File.WriteAllTextAsync(
        Path.Combine(ownedRoot, ".jellyfin-npgsql-owned-artifacts"),
        "Owned by Jellyfin.Plugin.Database.Npgsql.TestHarness\n",
        CancellationToken.None).ConfigureAwait(false);
}

static void RequireChild(string parent, string child)
{
    var parentPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
    if (!child.StartsWith(parentPrefix, StringComparison.Ordinal)
        || string.Equals(child, parentPrefix, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The owned artifact directory must be below the repository .artifacts directory.");
    }
}

static void CopyStandalone(string source, string destination)
{
    var excludedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".artifacts",
        ".dotnet",
        ".dotnet-cli",
        ".git",
        ".idea",
        ".nuget-http",
        ".nuget-packages",
        ".packages",
        ".vs",
        "artifacts",
        "bin",
        "obj",
        "TestResults",
    };

    Directory.CreateDirectory(destination);
    foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
    {
        var info = new DirectoryInfo(directory);
        if (excludedDirectoryNames.Contains(info.Name))
        {
            continue;
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"The standalone source contains a directory link: {info.Name}");
        }

        CopyStandalone(directory, Path.Combine(destination, info.Name));
    }

    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
    {
        var info = new FileInfo(file);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"The standalone source contains a file link: {info.Name}");
        }

        File.Copy(file, Path.Combine(destination, info.Name), overwrite: false);
    }
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
