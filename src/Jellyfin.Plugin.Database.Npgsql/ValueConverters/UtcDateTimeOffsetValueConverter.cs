using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jellyfin.Plugin.Database.Npgsql.ValueConverters;

/// <summary>
/// Normalizes <see cref="DateTimeOffset"/> values to UTC for Npgsql.
/// </summary>
internal sealed class UtcDateTimeOffsetValueConverter : ValueConverter<DateTimeOffset, DateTimeOffset>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UtcDateTimeOffsetValueConverter"/> class.
    /// </summary>
    public UtcDateTimeOffsetValueConverter()
        : base(
            value => value.ToUniversalTime(),
            value => value.ToUniversalTime())
    {
    }
}
