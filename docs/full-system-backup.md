# Full-system backup and restore

The packaged plugin participates in both Jellyfin's early database-provider
loader and its ordinary plugin lifecycle. Keep **Npgsql Database** enabled in
the dashboard. Its ordinary registration decorates Jellyfin's stock
`IBackupService`; the stock implementation still creates and schedules backups
and restores, while the decorator owns the PostgreSQL/file apply phase needed
to keep those two state stores coordinated.

For a database-inclusive backup, the provider changes the stock read
transaction to PostgreSQL `REPEATABLE READ`. This establishes one snapshot
before Jellyfin enumerates its tables. Jellyfin 12.2 does not enumerate the
mapped `HomeSection` table, so the provider captures that table from the same
transaction and adds it as a provider-owned archive entry. After the stock ZIP
is complete, the plugin atomically adds an integrity index containing every
entry's path, length, and SHA-256, plus the exact provider version and supported
Jellyfin database ABI. The provider marker's database flag must exactly match
the bounded stock `manifest.json` `Options.Database` value, and the resulting
presence or absence of `Database/` entries must agree with both. The manifest
is itself covered by the integrity index; neither metadata source is trusted in
isolation.

Only archives carrying that exact binding can be restored while PostgreSQL is
selected. SQLite, unknown-provider, older/newer plugin, wrong-ABI, partial,
duplicate-path, malformed, truncated, or modified archives are rejected before
the stock service extracts configuration or touches PostgreSQL. ZIP names are
canonicalized under the same extraction semantics as the stock service, with
case, Unicode, separator, dot-segment, ADS, trailing-dot/space, absolute-path,
and file/directory aliases rejected. Restore uses a deadline-bounded,
owner-readable marker-named snapshot beside the selected archive, and deletes
only that owned snapshot afterward.

The decorator first lets stock process a sanitized archive whose manifest has
no database or production-file payloads. It then creates an unpooled restore
context on a provider-owned connection and transaction, rewrites the validated
migration history, and streams every database row through the same public
`DbSet` contract stock Jellyfin uses. It truncates only exact model-owned
`public` tables plus the
provider-owned text trie, without `CASCADE`. Before save, it reapplies explicit
identity values which stock `System.Text.Json` cannot assign to private setters.
After save, it restores `HomeSection` and reseeds every model identity and the
provider trie sequence beyond their restored maxima, but does not commit from
the `SaveChanges` interceptor. The external transaction survives restore-context
disposal and is committed only by the outer decorator after the stock lifecycle
call and staged filesystem application return successfully. Failure or
cancellation before that boundary rolls history, purge, rows, text nodes, and
transactional sequence restarts back together.

Preflight streams every database row through the finalized model and requires
the exact ordered migration ID and `ProductVersion` contract, including the
stock `varchar(150)` and `varchar(32)` limits. Keys, unique values, and foreign
references are stored as binary digests in an owner-only, marker-named SQLite
validation index under the process temporary directory. Its cache and logical
disk budget are fixed, it is cancellation-aware, and its exact directory is
deleted internally on every exit; row count no longer determines managed heap
growth. ZIP central-directory size and count are checked before `ZipArchive`
materializes entries.

A process-wide provider gate serializes backup and restore from preflight
through cleanup and remains held through the stock delayed-shutdown handoff.
Before restore, the decorator captures file contents plus file and directory
metadata (including configured roots) and pre-extracts archive files into an
owner-only directory. Stock receives a private payload-free archive, so it
never reopens or mutates production filesystem paths after validation. On
Linux, the decorator holds each configured root as an open directory handle,
opens or creates descendants relative to that handle without following links,
and replaces each final name atomically. Root replacement is detected, while a
final-name symlink or hard-link swap cannot redirect the operation outside the
captured tree. Existing linked targets are rejected. Platforms without the
required handle-relative proof fail closed before filesystem mutation. Any
later failure restores overwritten files, modes, attributes, and timestamps,
removes newly created targets/directories, and rolls back the still-open
provider transaction.

If the ordinary plugin metadata is disabled, deleted, malformed, or mismatched
while the early provider is selected, the provider refuses startup before a
database connection or restore entry point exists. It also verifies the exact
final `IBackupService` decoration in the full host. The later full-table purge
refusal remains defense in depth, but it is not the primary restore boundary.

The stock lifecycle test creates a database-inclusive archive through the
public HTTP API, preserves explicit IDs in every identity-backed table,
relationships, long exact-text keys, history, and the omitted navigation table,
then performs real generated inserts into every identity-backed table. It also
restores the same archive twice and restarts an untouched Jellyfin publish after
each restore.
