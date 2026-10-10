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
owner-only, marker-named recovery directory below the Jellyfin data path. All
validation and `HomeSection` scratch also lives below the configured data path's
`.jellyfin-npgsql-scratch` root. Test-only use before application paths exist
defaults to the repository's ignored `.artifacts/npgsql-scratch` tree;
deployments may set `JELLYFIN_NPGSQL_SCRATCH_ROOT` to another private writable
location.

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
references are stored by a plugin-owned external-sort index. SHA-256 is only a
sort prefix: canonical composite-key bytes are retained and compared in full,
so a digest collision cannot create equality. The index has a 1 MiB canonical
key limit and an enforced 8 GiB physical-write quota (including unsorted data,
runs, and merged output) while the archive remains capped at ten million rows.
It uses bounded 16 MiB sort chunks and at most 32 merge readers, threads the
operation cancellation token through ingestion, sorting, run creation, and
multi-pass merge, uses cancellation-aware comparison checkpoints inside both
CPU sorting and priority-queue merge, needs no SQLite library from the host, and fails
deterministically if its real write accounting exceeds quota. A separately
stored, append-only 256-bit root key authenticates owner markers, deletion
claims, per-entry claims, and initialization receipts with HMAC-SHA-256. Proofs
bind purpose, canonical basename, key generation, directory identity, and
marker identity and, on Linux, device, inode, mount ID, and birth time when the
filesystem reports it. Linux `statx` does not expose an inode-generation field
on every supported kernel/filesystem, so mount ID and birth time supplement but
do not overstate the kernel identity available. Key rotation appends a durable
new key and retains old keys; a torn new record does not invalidate old proofs.
If every key record is torn or lost, startup appends a fresh key for new work
while retaining all older generations as unprovable data; the fresh key cannot
authorize them because every marker binds its original random key ID.
The key protects against corruption and cross-root copying, not a hostile
process running as the Jellyfin OS account, which can read owner-private files
and is outside this trust boundary. Dead validation directories are scavenged
on the next validation while live/concurrent, forged, or unmarked published
directories are never mutated. Empty pre-marker staging generations use a
reserved name and are reclaimed only after a one-hour inactivity bound and a
separately authenticated creation receipt. Unknown empty, unknown nonempty,
and malformed staging generations are preserved fail closed and isolated per
candidate. Retention is capped at 1,024 namespace entries or 64 MiB; exceeding either
bound disables new scratch work without deleting unknown data. ZIP central-directory size and count are checked before `ZipArchive`
materializes entries.

A process-wide provider gate serializes backup and restore from preflight
through cleanup. After a scheduled restore it remains held until the host's
actual `ApplicationStopped` acknowledgement. A delayed or failed shutdown does
not reopen the gate in the same process; later operations fail closed instead
of racing the scheduled restore.
The shutdown acknowledgement watchdog defaults to 30 seconds and may be set to
1–600 seconds with `JELLYFIN_NPGSQL_SHUTDOWN_DEADLINE_SECONDS`. Expiry emits an
actionable critical log and makes later calls reject immediately while keeping
the gate held; it never treats a timeout as permission to race the restore.
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
Successful application restores each ZIP file's supported Unix mode, safe file
attributes, and ZIP timestamp. Special mode bits, non-regular types, and
world-writable files are rejected; absent Unix metadata is normalized to
owner-read/write. Scratch files and directories remain owner-only regardless of
archive metadata.

Before the first transactional or filesystem mutation, the plugin durably
writes a versioned recovery journal, complete old-file snapshots, staged new
files, root identities, and the restore UUID, then fsyncs the files and owning
directories. Every apply/commit/rollback transition is atomically rewritten and
fsynced. The PostgreSQL transaction inserts the same UUID as a temporary
migration-history commit marker. If commit acknowledgement is lost, a fresh
connection queries that marker rather than guessing. On the next early-provider
startup—before Jellyfin creates its normal database services—the plugin reads
the journal and marker and idempotently completes either the exact new file
state (committed marker) or the exact old file state (no marker). Once the
committed journal is durable, marker and snapshot cleanup are best effort and
retryable; cleanup failure cannot misreport a committed restore as failed.
Terminal cleanup first validates the HMAC owner marker through an open
no-follow directory handle, atomically renames that exact generation with
`renameat2(RENAME_NOREPLACE)` to a unique same-parent tombstone, and fsyncs the
already-open parent. Each child is recorded in a durable authenticated claim,
renamed to a private retained name, and regular-file payload bytes are reduced
to zero only through the pinned file handle. POSIX has no general operation to
unlink the exact object represented by an open descriptor; checking identity
and then unlinking a mutable name would retain a final TOCTOU. The plugin
therefore never unlinks scratch names on Linux. It deliberately retains small
authenticated tombstones, entry claims, directory metadata, and symlinks under
the bounded retention policy. Windows similarly retains a same-parent claimed
generation because the managed recursive-delete API cannot provide the same
exact-object guarantee. A replacement at an old, retained, root, or sidecar
name is never truncated or deleted. Interrupted tombstones are idempotently
replayed from their durable claims with parent-fsync ordering. Recovery itself is serialized across processes by
an OS lock held on the stable application-data directory inode; the separately
held recovery-root handle and its generation are revalidated throughout replay,
so swapping the configured recovery root cannot establish a second lease
namespace. Kernel lease release after a crash permits the next startup to resume.
Missing, corrupt, escaping, or root-identity-mismatched journals fail startup
closed. A separate process is killed with `Environment.FailFast` immediately
after each durable journal transition (including both visible and absent commit
marker outcomes). Separate processes are also killed at all 19 scratch
publication, sidecar, rename, stable-handle reclamation, and fsync boundaries;
restart scavenging must converge without mutating file, directory, symlink,
root, or sidecar replacements. Recovery is then exercised against real PostgreSQL and
rerun to prove idempotence.

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
