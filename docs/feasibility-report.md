# Standalone Npgsql database-provider feasibility

Date: 2026-10-08

Bead: `jellyfin-38k.1`

Result: **PASS for an independently packaged provider, with the version and
installation constraints below.**

This is a contract proof, not a production release. It establishes that the
vanilla loader, model pipeline, migration callbacks, basic Jellyfin CRUD, and
restart lifecycle are sufficient to build the real provider. Full query-surface,
long-text, purge, maintenance, packaging, and upgrade coverage remains in the
dependent implementation beads.

## Tested support matrix

| Component | Tested version | Initial support decision |
| --- | --- | --- |
| Jellyfin | 12.2.0, tag commit `ca0f16eb7c195f720a1493ed469a27c4657db0c6` | Supported |
| Jellyfin development | 13.0.0, master commit `284997ebe2` | Supported provisionally for ABI 13.0.x; repeat the smoke test for every release |
| `Jellyfin.Database.Implementations` reference | NuGet 12.2.0 | Build-time contract |
| .NET | SDK 10.0.401, runtime 10.0 | Supported |
| Entity Framework Core | 10.0.11 | Must match the host |
| Npgsql EF provider / Npgsql | 10.0.3 / 10.0.3 | Shipped beside the plugin |
| PostgreSQL | 16.6 and 18.6 | Supported test range: 16–18 |
| Host PostgreSQL client programs | `pg_dump` absent; `psql` absent | Not required |

The provider rejects any Jellyfin database assembly outside ABI 12.2.x or 13.0.x
before it parses the connection string, opens PostgreSQL, or runs a migration.
ABI 13 has the same provider interface in the tested master commit, and the
12.2 reference binds forward successfully, but it remains provisional until a
13.0 release is available.

## Loader and package findings

Vanilla Jellyfin's `PLUGIN_PROVIDER` path can load a genuinely external provider.
The early loader puts `Jellyfin.Plugin.Database.Npgsql.dll` in the default assembly
load context. Its adjacent `.deps.json`, `Npgsql.EntityFrameworkCore.PostgreSQL.dll`,
and `Npgsql.dll` resolve there without copying any Jellyfin or EF assemblies into
the package. Runtime logging confirmed:

- the provider and both Npgsql assemblies came from the plugin directory in the
  default load context;
- `Jellyfin.Database.Implementations` and EF Core came from the untouched host;
- the installed package contained exactly three DLLs: the provider and the two
  Npgsql DLLs.

The ordinary plugin manager runs later and would create a second collectible load
context. The safe package contract is therefore a `meta.json` whose status is
`Disabled` and whose assembly allowlist contains only the provider DLL. The normal
plugin manager then skips it, while the early database loader still loads it. This
also proves that normal plugin enabled/disabled state does **not** control a custom
database provider; `database.xml` is the effective enablement switch.

The loader selects the first sorted plugin folder whose name starts with
`PluginName`. Installation must ensure there is only one matching folder. Also,
`PluginAssembly` must include the `.dll` suffix. Without it,
`Path.ChangeExtension("Jellyfin.Plugin.Database.Npgsql", "dll")` incorrectly seeks
`Jellyfin.Plugin.Database.dll`.

The early constructor may depend on services registered in the temporary startup
container. The probe uses only `ILogger<T>` and succeeds in both migration and full
host startup. It cannot depend on services registered later by ordinary plugins.

## Model-finalization result

`IJellyfinDatabaseProvider.OnModelCreating` runs before Jellyfin calls
`ApplyConfigurationsFromAssembly`, so it cannot reliably override explicit shared
configuration. The provider instead uses EF's public
`DbContextOptionsBuilder.ReplaceService<IModelCustomizer, NpgsqlModelCustomizer>()`.
The replacement calls the standard customizer first and then applies provider
changes. This deterministic late hook produced `BaseItems.Type` as
`character varying(511)` in both the migration snapshot and the real PostgreSQL
catalog, proving that it can override a shared explicit property mapping after the
vanilla model is complete.

## Migration, backup, and rollback result

The package contains a PostgreSQL baseline generated from the published Jellyfin
12.2 model. On a new database, the untouched host:

1. created its migration history table;
2. invoked `MigrationBackupFast`;
3. applied the plugin baseline;
4. invoked `DeleteBackup`;
5. completed its application migrations and became healthy.

The native backup implementation uses Npgsql and PostgreSQL schemas, not external
client programs. It copies every Jellyfin table with data, indexes, defaults,
identity definitions, checks, and identity sequence positions. Foreign-key
definitions are retained as metadata and recreated after restoration so dropping
the damaged `public` schema cannot cascade them away.

The black-box test then installed a test-only migration which committed a table
outside an EF transaction and deliberately failed with division by zero. Vanilla
Jellyfin invoked `RestoreBackupFast`. Verification after the failure showed:

- the partially committed marker table was absent;
- the administrator row and password remained usable;
- all 28 foreign keys remained;
- the failed migration was not in `__EFMigrationsHistory`;
- the temporary backup schema was gone;
- replacing the deliberately bad package with the normal package allowed the
  untouched host to become healthy again.

The snapshot algorithm is sufficient for the measured Jellyfin-managed baseline,
which contains tables, sequences, constraints, and indexes. Before production
release it must either reject or explicitly preserve any newly introduced views,
materialized views, functions, triggers, row-level-security policies, custom
grants, or other schema objects. Future core migrations are therefore a mandatory
compatibility-test trigger.

## Host lifecycle and CRUD result

The smoke test used Jellyfin's own startup API to create and rename the initial
administrator, set its password, and complete setup. It authenticated that user,
shut the host down through Jellyfin's API, restarted the same untouched host, and
authenticated the persisted user again. It repeated the restart and authentication
after the forced migration rollback. PostgreSQL 18.6 was used by the automated
run; the same sequence was also measured manually on PostgreSQL 16.6.

## Installation and recovery contract

1. Install one versioned plugin folder containing only `meta.json`, the provider
   DLL and deps file, `Npgsql.EntityFrameworkCore.PostgreSQL.dll`, and `Npgsql.dll`.
2. Keep the manifest disabled. Do not place Jellyfin, EF Core, or Microsoft
   Extensions assemblies in the plugin folder.
3. Configure `DatabaseType` as `PLUGIN_PROVIDER`, use the exact unique folder
   prefix for `PluginName`, and set `PluginAssembly` to
   `Jellyfin.Plugin.Database.Npgsql.dll`.
4. Use an empty PostgreSQL database owned by the configured runtime role. Selecting
   this provider does not convert or import an existing SQLite database.
5. Install the plugin before changing `database.xml`. A missing or incompatible
   provider is startup-fatal by design.
6. If migration startup fails, leave the database and configuration in place,
   replace the incompatible plugin package with the previous compatible package,
   and restart. The provider restores the pre-migration schema before Jellyfin
   exits. Preserve an independent operator backup for defense in depth.

## Reproduction

From this directory:

```sh
./tests/black-box-smoke.sh
```

The script archives the untouched `v12.2` tag beneath ignored `.artifacts`, builds
and publishes that copy, starts disposable PostgreSQL 18, installs the minimum
package, executes migrate/start/CRUD/shutdown/restart, forces and verifies rollback,
and records an `evidence.txt`. It never edits the archived host and cleans up its
Docker container. The passing run used PostgreSQL 18.6 and reported 28 restored
foreign keys, a 511-character late model override, no host `pg_dump` or `psql`, and
only the three expected package DLLs.

## Parent-host isolation

All checked-in product, migration, test, and report files are under
`jellyfin-plugin-database-npgsql`. The parent Jellyfin solution and source are
unchanged from upstream master; the test builds immutable Git archives beneath the
plugin's ignored artifact directory.
