# Full-system backup and restore

The packaged plugin participates in both Jellyfin's early database-provider
loader and its ordinary plugin lifecycle. Keep **Npgsql Database** enabled in
the dashboard. Its ordinary registration decorates Jellyfin's stock
`IBackupService`; the stock implementation still creates, schedules, and
executes every backup and restore.

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

Stock Jellyfin rewrites migration history, requests a purge, deserializes all
rows, and calls one `SaveChangesAsync`. The provider validates the exact
Jellyfin table set and migration lineage, then extends purge and save into one
transaction. It truncates only exact model-owned `public` tables plus the
provider-owned text trie, without `CASCADE`. Before save, it reapplies explicit
identity values which stock `System.Text.Json` cannot assign to private setters.
After save, it restores `HomeSection`, reseeds every model identity and the
provider trie sequence beyond their restored maxima, and commits. Failure or
cancellation rolls the purge and inserts back together.

Preflight streams every database row through the finalized model and requires
the exact ordered migration ID and `ProductVersion` contract, including the
stock `varchar(150)` and `varchar(32)` limits. A process-wide provider gate
serializes backup and restore from preflight through cleanup. Before stock
extraction, the decorator captures every exact Config/Data/Root target in an
owner-only bounded snapshot, rejecting symlink and hard-link targets. Any later
filesystem, history, purge, insert, reseed, connection, or cancellation failure
restores overwritten files and their modes, removes newly created targets,
repairs the original migration history transactionally, and relies on the
provider restore transaction to roll back application rows and sequence state.

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
