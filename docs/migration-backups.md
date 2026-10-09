# PostgreSQL migration backups

Date: 2026-10-09

Bead: `jellyfin-38k.8`

Vanilla Jellyfin requests a fast provider backup before any pending database or
code migration that declares a database backup requirement. The Npgsql provider
implements that contract in managed code; it does not invoke `pg_dump`, `psql`,
Docker, or a privileged database account.

Backups are stored below Jellyfin's data directory in
`NpgsqlMigrationBackups`. The root and each completed backup directory are
owner-only (`0700`) on Unix and every file is owner-only (`0600`). Keys are
opaque `npgsql-v1-<32 lowercase hex>` identifiers; paths, links, incomplete
directories, and names outside that grammar are rejected.

## Format and publication

Creation holds a provider process gate and a PostgreSQL transaction advisory
lock, then reads the database through a repeatable-read transaction. Each table
is streamed with Npgsql's raw binary `COPY` API to a separate bounded file. The
versioned JSON manifest records the plugin version, database name and OID, a
credential-free endpoint/user binding, source plugin migration, every table and
column definition, every sequence value, foreign-key characteristics, the
complete schema fingerprint, file lengths, and SHA-256 checksums. A provider-
generated restoration script is checksum-bound as data, not accepted from the
caller.

The manifest is written only after every stream has completed. A completion
file binds its hash, and the `.partial` directory is atomically renamed to its
final key last. Cancellation, I/O failure, or PostgreSQL failure removes only
the uniquely named provider-owned partial directory. A later creation also
scavenges marker-shaped crash remnants. No incomplete key can be restored.

The only compact backup is a migration-zero sentinel. It is accepted only when
the public schema contains no table, relation, routine, collation, or type.
Vanilla first startup seeds core code-migration rows before requesting its first
provider backup, so the exact two-column history-only schema is separately
recognized and streamed; every other object, key shape, or column length is
rejected. A populated application database must match the exact supported
Jellyfin model tables and columns and must contain the plugin baseline or
successor migration. Unknown populated layouts fail before migration rather
than receiving a false backup key.

## Restore and cleanup

Restore validates the key, path components, symlinks, format version, age,
provider version, all checksums and lengths, database name/OID, and target
binding before opening a destructive transaction. Npgsql pools are retired
first. The old public schema is replaced and the provider-generated schema is
replayed inside one serializable transaction. Foreign keys are temporarily
made deferred, table streams are copied in dependency order, identity sequences
are reset to their exact prior `last_value`/`is_called` pair, original foreign-
key characteristics are restored, and the schema fingerprint must match before
commit. Any SQL, COPY, constraint, checksum, or cancellation failure rolls the
whole transaction back.

Deletion is idempotent and applies only to a completed key below the canonical
backup root. A symbolic-link key is refused rather than traversed.

The integration suite proves migration-zero rollback and a populated
successor-to-baseline failure round trip, including 4,097 streamed rows,
generated columns, migration history, schema objects, exact identity reuse,
wrong-database and corrupt-file rejection before mutation, corrupt binary COPY
rollback after destructive work begins, symlink rejection, restrictive modes,
pre-canceled cleanup, crash-remnant scavenging, concurrent serialization, and
idempotent cleanup. The fixture login is the normal restricted database
owner: it has no superuser, `CREATEDB`, `CREATEROLE`, replication, or bypass-RLS
privilege.
