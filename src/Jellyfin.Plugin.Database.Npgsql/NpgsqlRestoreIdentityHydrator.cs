using System.IO.Compression;
using System.Text.Json;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Restores generated key values which stock System.Text.Json cannot assign to private setters.
/// </summary>
internal static class NpgsqlRestoreIdentityHydrator
{
    /// <summary>Applies the audited archive identities to stock-tracked rows before their single save.</summary>
    /// <param name="context">The stock restore context containing added archive rows.</param>
    /// <param name="cancellationToken">The restore cancellation token.</param>
    /// <returns>A task representing identity hydration.</returns>
    internal static async Task HydrateAsync(
        JellyfinDbContext context,
        CancellationToken cancellationToken)
    {
        var archivePath = NpgsqlRestoreScope.ArchivePath;
        if (archivePath is null)
        {
            // Direct provider tests can exercise the transaction mechanics
            // without impersonating the stock archive flow.
            return;
        }

        var sourceNames = typeof(JellyfinDbContext)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(property => property.PropertyType.IsAssignableTo(typeof(IQueryable)))
            .ToDictionary(
                property => property.PropertyType.GetGenericArguments()[0],
                property => property.Name);
        var trackedByType = context.ChangeTracker.Entries()
            .Where(entry => entry.State == EntityState.Added)
            .GroupBy(entry => entry.Metadata.ClrType)
            .ToDictionary(group => group.Key, group => group.ToArray());

        using var archive = await ZipFile.OpenReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
        foreach (var (clrType, tracked) in trackedByType)
        {
            var identityProperties = tracked[0].Metadata.GetProperties()
                .Where(property => property.FindAnnotation("Npgsql:ValueGenerationStrategy")?.Value?.ToString()
                    ?.StartsWith("Identity", StringComparison.Ordinal) is true)
                .ToArray();
            if (identityProperties.Length == 0)
            {
                continue;
            }

            if (!sourceNames.TryGetValue(clrType, out var sourceName))
            {
                throw new InvalidDataException($"Restored identity entity '{clrType.FullName}' has no audited stock DbSet source.");
            }

            var zipEntry = archive.GetEntry($"Database/{sourceName}.json")
                ?? throw new InvalidDataException($"The bound PostgreSQL archive is missing identity source '{sourceName}'.");
            var stream = await zipEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var seenValues = identityProperties.ToDictionary(
                    property => property.Name,
                    _ => new HashSet<object>(),
                    StringComparer.Ordinal);
                var rowIndex = 0;
                await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    if (row.ValueKind is not JsonValueKind.Object)
                    {
                        throw new InvalidDataException($"Identity source '{sourceName}' row {rowIndex} is not an object.");
                    }

                    if (rowIndex >= tracked.Length)
                    {
                        throw new InvalidDataException(
                            $"Identity source '{sourceName}' contains more rows than stock tracked ({tracked.Length}).");
                    }

                    foreach (var identityProperty in identityProperties)
                    {
                        if (!row.TryGetProperty(identityProperty.Name, out var jsonValue))
                        {
                            throw new InvalidDataException(
                                $"Identity source '{sourceName}' row {rowIndex} has no '{identityProperty.Name}' value.");
                        }

                        var value = jsonValue.Deserialize(identityProperty.ClrType)
                            ?? throw new InvalidDataException(
                                $"Identity source '{sourceName}' row {rowIndex} has a null '{identityProperty.Name}' value.");
                        if (!seenValues[identityProperty.Name].Add(value))
                        {
                            throw new InvalidDataException(
                                $"Identity source '{sourceName}' contains duplicate '{identityProperty.Name}' value '{value}'.");
                        }

                        var propertyEntry = tracked[rowIndex].Property(identityProperty.Name);
                        propertyEntry.CurrentValue = value;
                        propertyEntry.IsTemporary = false;
                    }

                    rowIndex++;
                }

                if (rowIndex != tracked.Length)
                {
                    throw new InvalidDataException(
                        $"Identity source '{sourceName}' contains {rowIndex} rows but stock tracked {tracked.Length} rows.");
                }
            }
        }
    }
}
