namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Defines the lossless physical identity used for Jellyfin logical keys that contain
/// unbounded text.
/// </summary>
/// <remarks>
/// EF continues to track entities by Jellyfin's original full-text keys. PostgreSQL
/// stores a canonical surrogate produced by a bounded-chunk trie and places physical
/// unique indexes on that surrogate. Every trie comparison is over at most 128 bytes,
/// so no supported PostgreSQL text value can overflow a B-tree entry. The database,
/// rather than a client hash, canonicalizes values under ordinary unique constraints;
/// concurrent inserts and restore-style SQL therefore have the same exact semantics.
/// </remarks>
internal static class NpgsqlTextInterning
{
    /// <summary>Marks the original EF key as logical rather than a physical B-tree constraint.</summary>
    internal const string LogicalKeyAnnotation = "JellyfinNpgsql:LogicalTextKey";

    /// <summary>Identifies the source text for an intern-backed physical unique index.</summary>
    internal const string ExactIdentityAnnotation = "JellyfinNpgsql:ExactTextIdentity";

    /// <summary>Marks an intern index which replaces the database primary-key constraint.</summary>
    internal const string ReplacesPrimaryKeyAnnotation = "JellyfinNpgsql:ReplacesPhysicalPrimaryKey";

    /// <summary>The fixed maximum number of UTF-8 bytes in one indexed trie edge.</summary>
    internal const int ChunkSize = 128;

    /// <summary>
    /// Installs the deadlock-safe text interning function used by new databases and
    /// by the successor migration for existing databases.
    /// </summary>
    internal const string InstallDeadlockSafeInternFunctionSql = """
        CREATE OR REPLACE FUNCTION public."__jellyfin_intern_text"(source_value text)
        RETURNS bigint
        LANGUAGE plpgsql
        STRICT
        SET search_path = pg_catalog, public
        AS $jellyfin$
        DECLARE
            source_bytes bytea := convert_to(source_value, 'UTF8');
            source_length integer := octet_length(source_bytes);
            source_offset integer := 0;
            parent_node bigint := 0;
            inserted_node bigint;
            edge_bytes bytea;
            path_exists boolean := true;
        BEGIN
            -- Existing values are the steady-state path and need no write lock.
            -- Resolve the complete trie path optimistically before serializing a
            -- miss. A partially visible path is treated as a miss because MVCC
            -- cannot expose another transaction's uncommitted terminal node.
            WHILE source_offset < source_length LOOP
                edge_bytes := substring(source_bytes FROM source_offset + 1 FOR 128);
                SELECT "Id" INTO inserted_node
                FROM public."__JellyfinTextNodes"
                WHERE "ParentId" = parent_node
                    AND "Chunk" = edge_bytes
                    AND "IsTerminal" = false;
                IF inserted_node IS NULL THEN
                    path_exists := false;
                    EXIT;
                END IF;
                parent_node := inserted_node;
                source_offset := source_offset + 128;
            END LOOP;

            IF path_exists THEN
                SELECT "Id" INTO inserted_node
                FROM public."__JellyfinTextNodes"
                WHERE "ParentId" = parent_node
                    AND "Chunk" = ''::bytea
                    AND "IsTerminal" = true;
                IF inserted_node IS NOT NULL THEN
                    RETURN inserted_node;
                END IF;
            END IF;

            -- Row-trigger order follows each caller's entity order. Two explicit
            -- transactions can therefore otherwise insert new values A/B and B/A,
            -- each waiting on the other's uncommitted unique-index entry. Serialize
            -- only trie misses for the remainder of their transactions; ordinary
            -- lookups above remain concurrent and no aborted transaction is retried.
            LOCK TABLE public."__JellyfinTextNodes" IN SHARE ROW EXCLUSIVE MODE;

            source_offset := 0;
            parent_node := 0;
            WHILE source_offset < source_length LOOP
                edge_bytes := substring(source_bytes FROM source_offset + 1 FOR 128);
                inserted_node := NULL;
                INSERT INTO public."__JellyfinTextNodes" ("ParentId", "Chunk", "IsTerminal")
                VALUES (parent_node, edge_bytes, false)
                ON CONFLICT ("ParentId", "Chunk", "IsTerminal") DO NOTHING
                RETURNING "Id" INTO inserted_node;
                IF inserted_node IS NULL THEN
                    SELECT "Id" INTO inserted_node
                    FROM public."__JellyfinTextNodes"
                    WHERE "ParentId" = parent_node
                        AND "Chunk" = edge_bytes
                        AND "IsTerminal" = false;
                    IF inserted_node IS NULL THEN
                        RAISE EXCEPTION 'Unable to resolve canonical text node after serialized insertion'
                            USING ERRCODE = 'XX000';
                    END IF;
                END IF;
                parent_node := inserted_node;
                source_offset := source_offset + 128;
            END LOOP;

            inserted_node := NULL;
            INSERT INTO public."__JellyfinTextNodes" ("ParentId", "Chunk", "IsTerminal")
            VALUES (parent_node, ''::bytea, true)
            ON CONFLICT ("ParentId", "Chunk", "IsTerminal") DO NOTHING
            RETURNING "Id" INTO inserted_node;
            IF inserted_node IS NULL THEN
                SELECT "Id" INTO inserted_node
                FROM public."__JellyfinTextNodes"
                WHERE "ParentId" = parent_node
                    AND "Chunk" = ''::bytea
                    AND "IsTerminal" = true;
                IF inserted_node IS NULL THEN
                    RAISE EXCEPTION 'Unable to resolve canonical terminal text node after serialized insertion'
                        USING ERRCODE = 'XX000';
                END IF;
            END IF;
            RETURN inserted_node;
        END;
        $jellyfin$;
        """;

    /// <summary>Restores the predecessor function when the successor migration is rolled back.</summary>
    internal const string RestorePredecessorInternFunctionSql = """
        CREATE OR REPLACE FUNCTION public."__jellyfin_intern_text"(source_value text)
        RETURNS bigint
        LANGUAGE plpgsql
        STRICT
        SET search_path = pg_catalog, public
        AS $jellyfin$
        DECLARE
            source_bytes bytea := convert_to(source_value, 'UTF8');
            source_length integer := octet_length(source_bytes);
            source_offset integer := 0;
            parent_node bigint := 0;
            inserted_node bigint;
            edge_bytes bytea;
        BEGIN
            WHILE source_offset < source_length LOOP
                edge_bytes := substring(source_bytes FROM source_offset + 1 FOR 128);
                inserted_node := NULL;
                INSERT INTO public."__JellyfinTextNodes" ("ParentId", "Chunk", "IsTerminal")
                VALUES (parent_node, edge_bytes, false)
                ON CONFLICT ("ParentId", "Chunk", "IsTerminal") DO NOTHING
                RETURNING "Id" INTO inserted_node;
                IF inserted_node IS NULL THEN
                    SELECT "Id" INTO inserted_node
                    FROM public."__JellyfinTextNodes"
                    WHERE "ParentId" = parent_node
                        AND "Chunk" = edge_bytes
                        AND "IsTerminal" = false;
                    IF inserted_node IS NULL THEN
                        RAISE EXCEPTION 'Concurrent text-node insertion requires whole-operation retry'
                            USING ERRCODE = '40001';
                    END IF;
                END IF;
                parent_node := inserted_node;
                source_offset := source_offset + 128;
            END LOOP;

            inserted_node := NULL;
            INSERT INTO public."__JellyfinTextNodes" ("ParentId", "Chunk", "IsTerminal")
            VALUES (parent_node, ''::bytea, true)
            ON CONFLICT ("ParentId", "Chunk", "IsTerminal") DO NOTHING
            RETURNING "Id" INTO inserted_node;
            IF inserted_node IS NULL THEN
                SELECT "Id" INTO inserted_node
                FROM public."__JellyfinTextNodes"
                WHERE "ParentId" = parent_node
                    AND "Chunk" = ''::bytea
                    AND "IsTerminal" = true;
                IF inserted_node IS NULL THEN
                    RAISE EXCEPTION 'Concurrent terminal-node insertion requires whole-operation retry'
                        USING ERRCODE = '40001';
                END IF;
            END IF;
            RETURN inserted_node;
        END;
        $jellyfin$;
        """;

    /// <summary>
    /// SQL installed by the initial plugin migration after the ordinary model tables are
    /// created. Tests also use this exact script after EnsureCreated.
    /// </summary>
    internal const string InstallSql = """
        CREATE OR REPLACE FUNCTION public."__jellyfin_ascii_lower"(source_value text)
        RETURNS text
        LANGUAGE sql
        IMMUTABLE STRICT PARALLEL SAFE
        SET search_path = pg_catalog, public
        RETURN translate(source_value, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz');

        CREATE OR REPLACE FUNCTION public."__jellyfin_ascii_prefix"(source_value text)
        RETURNS text
        LANGUAGE sql
        IMMUTABLE STRICT PARALLEL SAFE
        SET search_path = pg_catalog, public
        RETURN left(public."__jellyfin_ascii_lower"(source_value), 512);

        CREATE OR REPLACE FUNCTION public."__jellyfin_sqlite_like"(
            match_value text,
            pattern_value text)
        RETURNS boolean
        LANGUAGE sql
        IMMUTABLE STRICT PARALLEL SAFE
        SET search_path = pg_catalog, public
        RETURN public."__jellyfin_ascii_lower"(match_value) LIKE
            public."__jellyfin_ascii_lower"(pattern_value)
            ESCAPE '';

        CREATE OR REPLACE FUNCTION public."__jellyfin_sqlite_like_escape"(
            match_value text,
            pattern_value text,
            escape_value text)
        RETURNS boolean
        LANGUAGE plpgsql
        IMMUTABLE STRICT PARALLEL SAFE
        SET search_path = pg_catalog, public
        AS $jellyfin$
        DECLARE
            pattern_length integer := char_length(pattern_value);
            pattern_offset integer := 1;
            pattern_character text;
            folded_literal text;
            canonical_pattern text := '';
        BEGIN
            IF char_length(escape_value) <> 1 THEN
                RAISE EXCEPTION 'ESCAPE expression must be a single character'
                    USING ERRCODE = '22023';
            END IF;

            -- SQLite recognizes the escape token before applying its ASCII-only
            -- case folding. In particular, ESCAPE 'X' does not make a lower-case
            -- x in the pattern an escape. Build a canonical PostgreSQL pattern
            -- token by token so folding can never change which characters are
            -- literals and which are wildcards.
            WHILE pattern_offset <= pattern_length LOOP
                pattern_character := substr(pattern_value, pattern_offset, 1);
                IF convert_to(pattern_character, 'UTF8') = convert_to(escape_value, 'UTF8') THEN
                    pattern_offset := pattern_offset + 1;
                    IF pattern_offset > pattern_length THEN
                        -- SQLite treats a trailing escape token as a pattern that
                        -- cannot match rather than as a literal escape character.
                        RETURN false;
                    END IF;

                    pattern_character := substr(pattern_value, pattern_offset, 1);
                    folded_literal := public."__jellyfin_ascii_lower"(pattern_character);
                    IF folded_literal IN ('%', '_', E'\\') THEN
                        canonical_pattern := canonical_pattern || E'\\';
                    END IF;

                    canonical_pattern := canonical_pattern || folded_literal;
                ELSIF pattern_character IN ('%', '_') THEN
                    canonical_pattern := canonical_pattern || pattern_character;
                ELSE
                    folded_literal := public."__jellyfin_ascii_lower"(pattern_character);
                    IF folded_literal = E'\\' THEN
                        canonical_pattern := canonical_pattern || E'\\';
                    END IF;

                    canonical_pattern := canonical_pattern || folded_literal;
                END IF;

                pattern_offset := pattern_offset + 1;
            END LOOP;

            RETURN public."__jellyfin_ascii_lower"(match_value) LIKE
                canonical_pattern
                ESCAPE E'\\';
        END;
        $jellyfin$;

        CREATE TABLE IF NOT EXISTS public."__JellyfinTextNodes" (
            "Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            "ParentId" bigint NOT NULL,
            "Chunk" bytea NOT NULL,
            "IsTerminal" boolean NOT NULL,
            CONSTRAINT "CK___JellyfinTextNodes_Chunk"
                CHECK (octet_length("Chunk") <= 128),
            CONSTRAINT "AK___JellyfinTextNodes_Path"
                UNIQUE ("ParentId", "Chunk", "IsTerminal")
        );

        """ + RestorePredecessorInternFunctionSql + """
        CREATE OR REPLACE FUNCTION public."__jellyfin_intern_BaseItemProviders"()
        RETURNS trigger LANGUAGE plpgsql
        SET search_path = pg_catalog, public
        AS $jellyfin$
        BEGIN
            NEW."ProviderIdInternId" := public."__jellyfin_intern_text"(NEW."ProviderId");
            RETURN NEW;
        END;
        $jellyfin$;

        CREATE OR REPLACE FUNCTION public."__jellyfin_intern_PeopleBaseItemMap"()
        RETURNS trigger LANGUAGE plpgsql
        SET search_path = pg_catalog, public
        AS $jellyfin$
        BEGIN
            NEW."RoleInternId" := public."__jellyfin_intern_text"(NEW."Role");
            RETURN NEW;
        END;
        $jellyfin$;

        CREATE OR REPLACE FUNCTION public."__jellyfin_intern_UserData"()
        RETURNS trigger LANGUAGE plpgsql
        SET search_path = pg_catalog, public
        AS $jellyfin$
        BEGIN
            NEW."CustomDataKeyInternId" := public."__jellyfin_intern_text"(NEW."CustomDataKey");
            RETURN NEW;
        END;
        $jellyfin$;

        CREATE OR REPLACE FUNCTION public."__jellyfin_intern_ItemValues"()
        RETURNS trigger LANGUAGE plpgsql
        SET search_path = pg_catalog, public
        AS $jellyfin$
        BEGIN
            NEW."ValueInternId" := public."__jellyfin_intern_text"(NEW."Value");
            RETURN NEW;
        END;
        $jellyfin$;

        CREATE OR REPLACE FUNCTION public."__jellyfin_intern_CustomItemDisplayPreferences"()
        RETURNS trigger LANGUAGE plpgsql
        SET search_path = pg_catalog, public
        AS $jellyfin$
        BEGIN
            NEW."KeyInternId" := public."__jellyfin_intern_text"(NEW."Key");
            RETURN NEW;
        END;
        $jellyfin$;

        DROP TRIGGER IF EXISTS "TR_BaseItemProviders_Intern" ON public."BaseItemProviders";
        CREATE TRIGGER "TR_BaseItemProviders_Intern"
            BEFORE INSERT OR UPDATE ON public."BaseItemProviders"
            FOR EACH ROW EXECUTE FUNCTION public."__jellyfin_intern_BaseItemProviders"();

        DROP TRIGGER IF EXISTS "TR_PeopleBaseItemMap_Intern" ON public."PeopleBaseItemMap";
        CREATE TRIGGER "TR_PeopleBaseItemMap_Intern"
            BEFORE INSERT OR UPDATE ON public."PeopleBaseItemMap"
            FOR EACH ROW EXECUTE FUNCTION public."__jellyfin_intern_PeopleBaseItemMap"();

        DROP TRIGGER IF EXISTS "TR_UserData_Intern" ON public."UserData";
        CREATE TRIGGER "TR_UserData_Intern"
            BEFORE INSERT OR UPDATE ON public."UserData"
            FOR EACH ROW EXECUTE FUNCTION public."__jellyfin_intern_UserData"();

        DROP TRIGGER IF EXISTS "TR_ItemValues_Intern" ON public."ItemValues";
        CREATE TRIGGER "TR_ItemValues_Intern"
            BEFORE INSERT OR UPDATE ON public."ItemValues"
            FOR EACH ROW EXECUTE FUNCTION public."__jellyfin_intern_ItemValues"();

        DROP TRIGGER IF EXISTS "TR_CustomItemDisplayPreferences_Intern" ON public."CustomItemDisplayPreferences";
        CREATE TRIGGER "TR_CustomItemDisplayPreferences_Intern"
            BEFORE INSERT OR UPDATE ON public."CustomItemDisplayPreferences"
            FOR EACH ROW EXECUTE FUNCTION public."__jellyfin_intern_CustomItemDisplayPreferences"();

        ALTER TABLE public."BaseItemProviders" DROP CONSTRAINT IF EXISTS "PK_BaseItemProviders";
        ALTER TABLE public."PeopleBaseItemMap" DROP CONSTRAINT IF EXISTS "PK_PeopleBaseItemMap";
        ALTER TABLE public."UserData" DROP CONSTRAINT IF EXISTS "PK_UserData";
        """;

    /// <summary>
    /// Installs the complete current text support outside EF migrations, where the
    /// historical baseline and every successor are not applied automatically.
    /// </summary>
    internal const string InstallCurrentSql = InstallSql + InstallDeadlockSafeInternFunctionSql;

    /// <summary>
    /// Removes the PostgreSQL-only exact-text support after model tables have
    /// been removed by the baseline migration rollback.
    /// </summary>
    internal const string UninstallSql = """
        DROP FUNCTION IF EXISTS public."__jellyfin_intern_BaseItemProviders"();
        DROP FUNCTION IF EXISTS public."__jellyfin_intern_PeopleBaseItemMap"();
        DROP FUNCTION IF EXISTS public."__jellyfin_intern_UserData"();
        DROP FUNCTION IF EXISTS public."__jellyfin_intern_ItemValues"();
        DROP FUNCTION IF EXISTS public."__jellyfin_intern_CustomItemDisplayPreferences"();
        DROP FUNCTION IF EXISTS public."__jellyfin_intern_text"(text);
        DROP FUNCTION IF EXISTS public."__jellyfin_sqlite_like_escape"(text, text, text);
        DROP FUNCTION IF EXISTS public."__jellyfin_sqlite_like"(text, text);
        DROP FUNCTION IF EXISTS public."__jellyfin_ascii_prefix"(text);
        DROP FUNCTION IF EXISTS public."__jellyfin_ascii_lower"(text);
        DROP TABLE IF EXISTS public."__JellyfinTextNodes";
        """;
}
