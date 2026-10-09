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
Jellyfin database ABI.

Only archives carrying that exact binding can be restored while PostgreSQL is
selected. SQLite, unknown-provider, older/newer plugin, wrong-ABI, partial,
duplicate-path, malformed, truncated, or modified archives are rejected before
the stock service extracts configuration or touches PostgreSQL. Restore uses a
validated marker-named snapshot beside the selected archive, and deletes only
that owned snapshot afterward.

Stock Jellyfin rewrites migration history, requests a purge, deserializes all
rows, and calls one `SaveChangesAsync`. The provider validates the exact
Jellyfin table set and migration lineage, then extends purge and save into one
transaction. It truncates only exact model-owned `public` tables plus the
provider-owned text trie, without `CASCADE`. Before save, it reapplies explicit
identity values which stock `System.Text.Json` cannot assign to private setters.
After save, it restores `HomeSection`, reseeds every model identity and the
provider trie sequence beyond their restored maxima, and commits. Failure or
cancellation rolls the purge and inserts back together.

If the ordinary plugin registration is disabled while the early provider is
still selected, a stock full-table purge is refused. The provider repairs the
audited migration history and leaves application tables intact, so the
dashboard restore cannot silently continue through an unsafe PostgreSQL purge.

The stock lifecycle test creates a database-inclusive archive through the
public HTTP API, preserves explicit IDs in every identity-backed table,
relationships, long exact-text keys, history, and the omitted navigation table,
then performs real generated inserts into every identity-backed table. It also
restores the same archive twice and restarts an untouched Jellyfin publish after
each restore.
