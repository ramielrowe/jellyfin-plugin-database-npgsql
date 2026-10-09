using System;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>Public EF database-function stubs used by the query compatibility interceptor.</summary>
public static class NpgsqlQueryDbFunctions
{
    /// <summary>Applies SQLite-compatible ASCII-only lowercase conversion.</summary>
    /// <param name="value">The source text.</param>
    /// <returns>The ASCII-folded text.</returns>
    public static string AsciiLower(string value)
        => throw new InvalidOperationException("This method may only be evaluated by Entity Framework Core.");

    /// <summary>Evaluates a SQLite-compatible LIKE pattern with an explicit escape character.</summary>
    /// <param name="match">The value being matched.</param>
    /// <param name="pattern">The already-constructed SQL LIKE pattern.</param>
    /// <param name="escapeCharacter">The single pattern escape character.</param>
    /// <returns><see langword="true"/> when the pattern matches.</returns>
    public static bool SqliteLike(string match, string pattern, string escapeCharacter)
        => throw new InvalidOperationException("This method may only be evaluated by Entity Framework Core.");
}
