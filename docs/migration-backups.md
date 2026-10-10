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

These snapshots exist only for Jellyfin's migration transaction flow. Jellyfin
automatically requests restore when that migration fails and deletes the
snapshot after a successful migration. The provider exposes no operator command
to retain, select, or replay a snapshot later. Consequently, a package-only
downgrade after a successful schema migration is unsupported without a
separately qualified database-recovery procedure.

## Format and publication

Creation holds a provider process gate and a PostgreSQL transaction advisory
lock, then reads the database through a repeatable-read transaction. Each table
is streamed with Npgsql's raw binary `COPY` API to a separate bounded file. The
versioned JSON manifest records the plugin version, database name and OID, the
restricted-role-readable PostgreSQL system identifier, the server actually
selected from a multi-host connection, source plugin migration, every table and
column definition, complete sequence definition/ownership/value state,
foreign-key characteristics, the complete schema fingerprint, file lengths, and
SHA-256 checksums. A provider-
generated restoration script is checksum-bound as data, not accepted from the
caller.

The manifest is written only after every stream has completed. A completion
file binds its hash. Every table, schema, manifest, and completion file is forced
to stable storage. On first use, the new backup root is synchronized and its
already-existing application-data parent is synchronized to durably record the
new directory entry. Recursively creating a missing parent is refused. For each
key, the partial directory and root are synchronized, the directory is
atomically renamed, and the root is synchronized again before the key is
returned. Failure to synchronize a file or directory is fail-closed on every
supported platform. Any flush, rename, partial-write disk exhaustion,
cancellation, I/O, or PostgreSQL failure removes only the uniquely named
provider-owned partial directory. A later creation also scavenges marker-shaped
crash remnants. No incomplete key can be restored.

The only compact backup is a migration-zero sentinel. It is accepted only when
the public schema contains no table, relation, routine, collation, or type.
Vanilla first startup seeds core code-migration rows before requesting its first
provider backup, so the exact two-column history-only schema is separately
recognized and streamed; every other object, key shape, or column length is
rejected. A populated application database must match the exact supported
Jellyfin model tables, complete column types/facets, functions, triggers,
constraints, indexes, owned identity sequences, policies/RLS, owners, grants,
extensions, and schema properties and must contain the plugin baseline or
successor migration. The canonical contract also includes schema, relation,
index, sequence, function, type, constraint, trigger, rule, and extended-
statistics ownership and ACLs; exact sequence-to-column dependency edges;
replica identity; persistence, tablespace, access method, storage, reloptions;
column defaults, identity/generated state, collation and precision facets; and
comments. Public-schema object classes that the representation does not support
are explicitly rejected. Extra views/materialized views/sequences and altered
sequence definitions are rejected. The same exhaustive fingerprint is checked
after every restore, including migration-zero and history-only restores.
Unknown or drifted layouts fail before migration rather than receiving a false
backup key.

Before any key is published, a catalog-address dependency audit builds the
provider-owned object set from the public namespace and its PostgreSQL-created
internal and automatic children. It rejects inbound or outbound edges across
that boundary, including rewrite rules for external views, cross-schema foreign
keys, inheritance and partition links, triggers, generated/default expressions,
and extension membership. Only dependencies on an explicit set of immutable
system namespaces and built-in languages are allowed. Objects in another schema
that do not depend on `public` are left alone and are proven to survive restore.

## Restore and cleanup

Restore validates the key, path components, symlinks, format version, age,
provider version, all checksums and lengths, database name/OID, stable cluster
binding, and the server endpoint actually selected from a multi-host data source
before opening a destructive transaction. Validation streams
every artifact once into a private provider-owned staging directory and restore
consumes those same open staged handles, so path, hard-link, symlink, data-file,
and manifest swaps cannot create a validation/use race. The old public schema is
replaced and the provider-generated schema is replayed inside one serializable
transaction. Foreign keys are temporarily
made deferred, table streams are copied in dependency order, identity sequences
are reset to their exact prior `last_value`/`is_called` pair, original foreign-
key characteristics are restored, and the schema fingerprint must match before
commit. Restore has an internal deadline even when the vanilla host passes
`CancellationToken.None`, bounded PostgreSQL lock/statement/command timeouts,
and a pre-mutation active-session check; a leased transaction or table lock is
reported rather than hanging. Any SQL, COPY, constraint, checksum, timeout, or
cancellation failure rolls the whole transaction back.

Deletion is idempotent and applies only to a completed key below the canonical
backup root. A symbolic-link key is refused rather than traversed.

The integration suite proves migration-zero rollback and a populated
successor-to-baseline failure round trip, including 4,097 streamed rows,
generated columns, migration history, schema objects, exact identity reuse,
wrong-database and corrupt-file rejection before mutation, corrupt binary COPY
rollback after destructive work begins, symlink rejection, restrictive modes,
pre-canceled cleanup, crash-remnant scavenging, concurrent serialization, and
idempotent cleanup. Deterministic bounded-write tests fail after only a few
bytes of table COPY, schema, manifest, and immutable restore-staging output;
they prove that no key or staging remnant survives, the pre-restore database is
untouched, and all file handles are released for an immediate retry. The
fixture login is the normal restricted database owner: it has no superuser,
`CREATEDB`, `CREATEROLE`, replication, or bypass-RLS privilege.
