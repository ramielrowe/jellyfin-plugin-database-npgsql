using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jellyfin.Plugin.Database.Npgsql.ValueConverters;

/// <summary>
/// Normalizes Jellyfin's UTC timestamps before Npgsql persists them.
/// </summary>
internal sealed class UtcDateTimeValueConverter : ValueConverter<DateTime, DateTime>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UtcDateTimeValueConverter"/> class.
    /// </summary>
    public UtcDateTimeValueConverter()
        : base(
            value => Normalize(value),
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
    {
    }

    private static DateTime Normalize(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}
