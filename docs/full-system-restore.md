# Full-system restore policy

## Unsupported operation

Jellyfin 12.2 full-system restore is unsupported whenever this Npgsql provider
is selected. The restriction is intentional and applies to every archive and
every stock entry point:

- dashboard and API restore scheduling;
- restart-time restore execution;
- PostgreSQL-origin and SQLite-origin archives;
- archives with unknown provenance; and
- missing, malformed, or corrupt archives.

The guard does not open or parse the requested archive. It returns one stable,
secret-free `NotSupportedException` before delegating to Jellyfin's stock
service, so Config, Data, Root, migration history, PostgreSQL, and SQLite remain
untouched. Rejecting all archives also removes a replacement race between API
validation and the later restart-time restore.

The early provider also recognizes Jellyfin's `--restore-archive` option by its
option name alone and rejects it before the host's preliminary migration
container can run pending migrations. The archive argument itself is never read
or included in the provider diagnostic. A restore scheduled through the API
cannot reach the restart handoff because the service guard rejects the request
synchronously before Jellyfin publishes restart state.

Stock backup creation, listing, and manifest inspection are unchanged. A stock
database-inclusive backup may be useful as an operator-reviewed export, but it
is not a supported recovery artifact for this plugin.

## Fail-closed loading

Vanilla Jellyfin loads a custom database provider before its ordinary plugin
service registration. The release assembly participates in both stages:

1. The early provider rejects the command-line restore option before preliminary
   migrations.
2. Ordinary active-plugin discovery replaces the single stock
   `IBackupService` registration with the small rejecting decorator.
3. The early provider checks the completed service collection before any
   PostgreSQL connection is opened. It accepts only the exact decorator from
   the same release binary and only when no later registration replaced or
   appended another backup service.

If metadata is disabled, deleted, or malformed, the assembly cannot load, a
registrator throws, or another plugin overrides the backup service, the early
provider refuses startup. Jellyfin therefore cannot serve a restore API or run
a queued startup restore in any unguarded state. The corrective action is to
restore the complete matching release directory with `meta.json` status
`Active`, then restart; selecting a different database provider is the other
recovery option.

## Supported migration rollback

The provider migration protocol is separate. Before a pending Jellyfin
migration, `MigrationBackupFast` creates a bounded, provider-owned PostgreSQL
snapshot. Jellyfin calls `RestoreBackupFast` only as part of automatically
rolling back that failing migration, and deletes the snapshot after a successful
migration. Operators cannot retain, select, or replay it. Its format,
validation, transaction boundary, and cleanup contract are described in
[migration-backups.md](migration-backups.md). It does not restore Jellyfin's
Config, Data, or Root trees and must not be presented to the dashboard restore
endpoint.

Implementing full-system restore later requires a separate design and review of
cross-resource atomicity, crash recovery, archive provenance, and SQLite to
PostgreSQL conversion. None of that experimental machinery is shipped or
compiled by this release.
