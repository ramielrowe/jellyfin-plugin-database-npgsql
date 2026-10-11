# PostgreSQL migration lineage

Date: 2026-10-09

Bead: `jellyfin-38k.7`

The release assembly owns a PostgreSQL-only EF migration lineage for the exact
vanilla Jellyfin 12.2 model finalized by the provider. It does not copy the
discarded built-in PostgreSQL migrations: the checked-in baseline and snapshot
were freshly generated through the standalone design-time project from published
packages. They contain the five shadow `InternId` properties and no host-added
digest CLR members.

The design project is `tools/Jellyfin.Plugin.Database.Npgsql.Migrations`. It is
not packaged. Its `IDesignTimeDbContextFactory<JellyfinDbContext>` uses the
published Jellyfin database contract plus the provider's production model
services, so the workflow remains valid after this directory is moved to its own
repository. A local .NET tool manifest pins `dotnet-ef` 10.0.11. Run:

```sh
./scripts/verify-migrations.sh
```

That command restores only declared packages, checks for pending model changes,
and writes the complete idempotent script beneath the ignored, repository-local
`.artifacts/migrations` root. The tool and NuGet caches are likewise kept in the
ignored repository-local `.dotnet` and `.packages` roots. No `/tmp` cleanup or
parent Jellyfin checkout is required.

To add a reviewed successor after changing the model, run the same build and then:

```sh
MIGRATION_NAME=ReplaceWithMigrationName
dotnet ef migrations add "$MIGRATION_NAME" \
  --context JellyfinDbContext \
  --project src/Jellyfin.Plugin.Database.Npgsql/Jellyfin.Plugin.Database.Npgsql.csproj \
  --startup-project tools/Jellyfin.Plugin.Database.Npgsql.Migrations/Jellyfin.Plugin.Database.Npgsql.Migrations.csproj \
  --output-dir Migrations
```

Review the generated operations, snapshot, runtime fingerprint, idempotent SQL,
and real PostgreSQL catalog before committing them.

## Generated and hand-authored boundaries

`20261009061101_InitialPostgreSqlBaseline` and its designer were generated from
an empty migration lineage. EF generated every ordinary table, column, foreign
key, index, identity, computed column, collation, and type. The only hand-authored
operation invokes `NpgsqlTextInterning.InstallSql`, because EF cannot express the
bounded exact-text trie, its trigger functions, or replacing the three unsafe
unbounded-text physical primary keys while retaining Jellyfin's logical EF keys.
Its matching rollback invokes `UninstallSql`. Neither script installs an
extension or requires superuser privileges.

`20261009061446_AddActivityLogTypeDateIndex` is a small, fully generated successor.
It adds and reversibly removes `IX_ActivityLogs_Npgsql_Type_DateCreated`, proving
same-provider upgrade and downgrade independently from the baseline.

`20261009113458_AddUuidMinMaxAggregates` installs reversible `min(uuid)` and
`max(uuid)` aggregates plus two provider-prefixed transition functions. Vanilla
Jellyfin uses UUID minima while collapsing presentation-key groups, but
PostgreSQL 16 has no built-in UUID minimum or maximum aggregate. The objects are
created in the application-owned `public` schema by the restricted runtime role;
they require no extension or elevated privilege.

`20261011031154_SerializeTextInterningMisses` replaces only
`__jellyfin_intern_text(text)`. Already interned values retain the lock-free lookup
path; a missing value takes a transaction-scoped table write lock before creating
trie nodes, preventing inverse-order callers from deadlocking inside an explicit
Jellyfin transaction. The rollback restores the predecessor function. This is a
provider migration for the unchanged Jellyfin 12.2 database ABI and requires no
host patch. The guarantee applies to Jellyfin's default `READ COMMITTED`
transactions; custom stronger isolation can retain a pre-wait snapshot and may
still require retrying the complete owning operation.

## Shared migration history audit

Vanilla Jellyfin deliberately stores code-migration IDs and EF migration IDs in
the same `__EFMigrationsHistory` table. The plugin's IDs use their own timestamp
and names and preserve unrelated history rows on upgrade or downgrade.

This release pins all 49 Jellyfin 12.2 code-migration IDs in
`NpgsqlHostMigrationAudit`. The classification was audited against the migration
attributes in the exact supported Jellyfin 12.2 source commit
`ca0f16eb7c195f720a1493ed469a27c4657db0c6`: five routines have
`RunMigrationOnSetup = true`, and the other 44 are seeded into history on a fresh
setup. Provider initialization reflects the loaded `jellyfin` assembly and
rejects additions, removals, and duplicates before reading configuration or
opening PostgreSQL. A future host migration therefore cannot run merely because
its database ABI still has the same major/minor number.

The current CoreInitialisation routines are
`20260522092304_UpdateNormalizedUsername` and
`20260531160000_DisableLegacyAuthorization`. Neither is marked to run on a fresh
setup, so vanilla Jellyfin records both as already applied before the plugin
baseline runs. The five setup-safe routines are AddDefaultPluginRepository,
ReaddDefaultPluginRepository, AddDefaultCastReceivers,
UpdateDefaultPluginRepository, and MoveTrickplayFiles; they still execute
normally. Unit tests require the setup and seeded partitions to be disjoint and
to cover all 49 pinned IDs exactly. The pristine-host migration test starts an
untouched publish against an empty, pre-created PostgreSQL database and requires
it to become healthy with the exact 49 code rows plus the four plugin rows. This
proves the legacy transforms were seeded and every setup routine completed
without changing Jellyfin's migration service.

The real PostgreSQL migration test uses only the generated restricted login. It
applies the baseline twice, migrates up/down/up through the successor without
losing a sentinel row, executes the idempotent SQL twice, preserves representative
code-history rows, checks the exact history, and inspects every critical function,
trigger, index, removed physical key, role flag, and extension absence.
