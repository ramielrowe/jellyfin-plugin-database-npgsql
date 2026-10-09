namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Installs the UUID aggregates used by unchanged Jellyfin repository queries.
/// </summary>
internal static class NpgsqlUuidAggregates
{
    /// <summary>The reversible restricted-role installation SQL.</summary>
    internal const string InstallSql = """
        CREATE FUNCTION public.__jellyfin_uuid_min(state uuid, value uuid)
        RETURNS uuid
        LANGUAGE sql
        IMMUTABLE
        PARALLEL SAFE
        SET search_path = pg_catalog
        AS $jellyfin$
            SELECT CASE
                WHEN state IS NULL THEN value
                WHEN value IS NULL THEN state
                WHEN value < state THEN value
                ELSE state
            END
        $jellyfin$;

        CREATE FUNCTION public.__jellyfin_uuid_max(state uuid, value uuid)
        RETURNS uuid
        LANGUAGE sql
        IMMUTABLE
        PARALLEL SAFE
        SET search_path = pg_catalog
        AS $jellyfin$
            SELECT CASE
                WHEN state IS NULL THEN value
                WHEN value IS NULL THEN state
                WHEN value > state THEN value
                ELSE state
            END
        $jellyfin$;

        CREATE AGGREGATE public.min(uuid) (
            SFUNC = public.__jellyfin_uuid_min,
            STYPE = uuid,
            COMBINEFUNC = public.__jellyfin_uuid_min,
            SORTOP = OPERATOR(pg_catalog.<),
            PARALLEL = SAFE
        );

        CREATE AGGREGATE public.max(uuid) (
            SFUNC = public.__jellyfin_uuid_max,
            STYPE = uuid,
            COMBINEFUNC = public.__jellyfin_uuid_max,
            SORTOP = OPERATOR(pg_catalog.>),
            PARALLEL = SAFE
        );

        REVOKE EXECUTE ON FUNCTION public.__jellyfin_uuid_min(uuid, uuid) FROM PUBLIC;
        REVOKE EXECUTE ON FUNCTION public.__jellyfin_uuid_max(uuid, uuid) FROM PUBLIC;
        REVOKE EXECUTE ON FUNCTION public.min(uuid) FROM PUBLIC;
        REVOKE EXECUTE ON FUNCTION public.max(uuid) FROM PUBLIC;
        """;

    /// <summary>The matching rollback SQL.</summary>
    internal const string UninstallSql = """
        DROP AGGREGATE public.max(uuid);
        DROP AGGREGATE public.min(uuid);
        DROP FUNCTION public.__jellyfin_uuid_max(uuid, uuid);
        DROP FUNCTION public.__jellyfin_uuid_min(uuid, uuid);
        """;
}
