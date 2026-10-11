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

    /// <summary>Normalizes a value with the same semantics used by the provider conversion.</summary>
    /// <param name="value">The date-time value to normalize.</param>
    /// <returns>The value expressed as UTC.</returns>
    internal static DateTime Normalize(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}
