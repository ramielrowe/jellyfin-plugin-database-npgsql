# Stock Jellyfin lifecycle validation

The lifecycle proof runs the packaged provider against an integrity-pinned,
untouched Jellyfin 12.2 publish and a restricted PostgreSQL 16.6 login. The
host publish is copied into an ownership-marked repository-local case, hashed
before use, and hashed again after every process. Plugin, configuration, data,
cache, media, logs, migration backups, and PostgreSQL all remain outside the
publish.

`StockServerLifecycleTests` builds a genuine `0.0.9.0` predecessor from the
checked-in, immutable source snapshot rooted at plugin commit
`f7575d769132236431388eddbcd13525d4507150`. That assembly contains only the
baseline and index migrations. A checked-in manifest asserts the complete
source-tree, DLL, and deterministic ZIP SHA-256 values and rejects post-0.0.9
symbols. The fixture project can compile only `source-tree/**/*.cs`, so a change
to the current production tree cannot silently change the predecessor. The test
also records the SHA-256 of both predecessor and current ZIPs,
installs the predecessor before first start, and writes vanilla
`PLUGIN_PROVIDER` configuration. It completes setup over HTTP, creates two
users, adds and scans a metadata-rich movie library, shuts down through the
public API, and proves the exact 51-row history.
It then installs `0.1.3.0` in its versioned folder, atomically rewrites
`database.xml`, launches a fresh process, and proves the UUID aggregate
migration is the only new database migration. A third fresh process proves the
52-row code/database history is idempotent and the users and media remain
available through the public API. No SQLite database file may appear.

The same real-host case also proves an interrupted side-by-side staging leaves
the exact predecessor selected, a duplicate older directory cannot intercept
the version-qualified current selection, and an explicit package rollback can
select a restored predecessor after moving the newer same-GUID package outside
Jellyfin's discovery root. It then verifies
that dashboard-style metadata disable, uninstall while configured, and a
missing provider DLL each stop startup without changing PostgreSQL or creating
SQLite. Restoring the exact metadata, directory, or DLL recovers on a fresh
process. If a migration fails, Jellyfin uses the separate provider migration
snapshot automatically during that failure. It deletes the snapshot after a
successful migration, and operators cannot retain or replay it. The package
rollback exercise therefore does not claim a schema downgrade; after a
successful schema migration, selecting an older DLL without separately
qualified database recovery is unsupported.

The predecessor fixture is source, metadata, and migrations rather than a
checked-in binary. Its package reuses only the release ZIP's version-locked
Npgsql runtime binaries; the fixture supplies its own provider assembly,
dependency manifest, metadata, and shorter migration lineage. Any dependency
binary change changes the asserted predecessor ZIP hash and fails the audit.

## Supported-host upgrade limitation

This release deliberately supports the exact `Jellyfin.Database.Implementations`
ABI `12.2.0.0`, not an untested `12.2.x` range. Jellyfin tag `v12.2` and the
`release-12.z` ref both resolve to the single commit
`ca0f16eb7c195f720a1493ed469a27c4657db0c6`; there is no second immutable,
supported 12.2 host release to use as a genuine host-version upgrade fixture.
The next available host line reports ABI `13.0.0.0` and is intentionally rejected
before connection parsing. Consequently this release proves plugin `0.0.9.0` to
`0.1.3.0` upgrade plus restart on the one supported stock host, and explicitly
does not claim a host-version upgrade. A second supported host must be added to
the integrity manifest and exercised against the same plugin/data/history/API
state before the compatibility contract can be widened.

## Failure and recovery matrix

| Condition | Automated evidence | Safety boundary |
| --- | --- | --- |
| Unsupported host ABI or changed host code migrations | `EarlyLoadContractTests`, `MigrationContractTests`, and `black-box-smoke.sh` | Rejected before a database connection. |
| Missing provider dependencies, ambiguous version folders, or malformed ordinary-plugin metadata | `black-box-smoke.sh` | Actionable reinstall/rollback diagnostic; no connection for early package failures. |
| Missing or unsafe `database.xml`, credentials, TLS/passfile permissions, ambient PostgreSQL settings, and unbounded timeouts | `ProviderCoreTests` and `black-box-smoke.sh` | Configuration is validated and secrets are redacted before the data source is built. |
| Empty database and restricted runtime role | `MigrationIntegrationTests.UntouchedJellyfin_FreshSetupSeedsAuditedLegacyRowsAndRunsSetupRoutines` and the lifecycle proof | Actual pre/core/app migrations run under the generated non-superuser role. |
| Unknown, gapped, or future migration/code history | `Snapshot_RejectsUnknownGappedAndFutureStockHistoryBeforeMutation` | Exact history is checked under the migration lock before a backup is published or schema is changed. |
| Incompatible schema, ownership/ACL drift, extra objects, dependencies, and sequence drift | `MigrationBackupIntegrationTests` sentinel and drift cases | Backup creation refuses the target before mutation. |
| Migration lock contention and timeout | `Snapshot_MigrationLockContentionTimesOutWithoutMutationOrArtifact` | The bounded command timeout leaves migration zero and publishes no backup. |
| Deliberate transactional/non-transactional migration failure | `black-box-smoke.sh` and `MigrationBackupIntegrationTests` | The vanilla migration service calls provider restore; exact rows, schema, history, FKs, and sequence states return. |
| Corrupt, stale, wrong-target, symlinked, partially written, or actively leased backup | `MigrationBackupIntegrationTests` restore cases | Validation/staging finishes before the target transaction; the target and published backup remain unchanged on failure. |
| Interrupted update, duplicate old folder, package rollback, dashboard disable, uninstall, missing folder/DLL/metadata, and recovery | `StockServerLifecycleTests` and early-loader package smoke controls | Exact version selection remains deterministic; incomplete or inactive selected packages fail before database mutation and never create a SQLite fallback. |

`StockServerLifecycleTests` launches the untouched host from clean state for
connection refusal, authentication failure, a missing database, and PostgreSQL
permission denial. Each case asserts a 35-second outer bound, its stable
Npgsql/PostgreSQL diagnostic, secret redaction, an unchanged public schema, and
the absence of SQLite files. The isolated database fixture and package smoke use
generated credentials only, and all retained diagnostics are passed through the
same bounded streaming redactor.

The exercised native publish is the official .NET publish layout used by the
Jellyfin packaging pipeline. The test does not claim an official Linux
container-image run: no immutable Jellyfin image digest is supplied by the
repository. The image path would add only the host filesystem/container entry
point around the same seven-file plugin ZIP; the integrity-pinned untouched
publish path is the reproducible release gate until an image digest is pinned.
