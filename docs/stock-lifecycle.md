# Stock Jellyfin lifecycle validation

The lifecycle proof runs the packaged provider against an integrity-pinned,
untouched Jellyfin 12.2 publish and a restricted PostgreSQL 16.6 login. The
host publish is copied into an ownership-marked repository-local case, hashed
before use, and hashed again after every process. Plugin, configuration, data,
cache, media, logs, migration backups, and PostgreSQL all remain outside the
publish.

`StockServerLifecycleTests` builds a genuine `0.0.9.0` predecessor from the
checked-in fixture project. That assembly contains only the baseline and index
migrations. The test records the SHA-256 of both predecessor and current ZIPs,
installs the predecessor before first start, and writes vanilla
`PLUGIN_PROVIDER` configuration. It completes setup over HTTP, creates two
users, adds and scans a metadata-rich movie library, shuts down through the
public API, and proves the exact 51-row history.
It then installs `0.1.0.0` in its versioned folder, atomically rewrites
`database.xml`, launches a fresh process, and proves the UUID aggregate
migration is the only new database migration. A third fresh process proves the
52-row code/database history is idempotent and the users and media remain
available through the public API. No SQLite database file may appear.

The predecessor fixture is source, metadata, and migrations rather than a
checked-in binary. Its package reuses the release ZIP's integrity-checked
Npgsql runtime dependencies, so the only intentional differences are the
provider assembly, dependency manifest, version metadata, and shorter
migration lineage.

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
| Missing/deleted selected provider | `StockServerLifecycleTests` and early-loader package smoke controls | Startup fails with the configured provider identity; tests assert that no SQLite fallback database is created. |

Connection refusal, authentication failure, a missing database, and PostgreSQL
permission errors use Npgsql's bounded connection/command timeouts after the
provider's secret-redacting configuration gate. The isolated database fixture
and package smoke use generated credentials only, and all retained diagnostics
are passed through the same bounded streaming redactor.

The exercised native publish is the official .NET publish layout used by the
Jellyfin packaging pipeline. The test does not claim an official Linux
container-image run: no immutable Jellyfin image digest is supplied by the
repository. The image path would add only the host filesystem/container entry
point around the same seven-file plugin ZIP; the integrity-pinned untouched
publish path is the reproducible release gate until an image digest is pinned.
