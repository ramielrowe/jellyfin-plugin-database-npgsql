# Standalone Npgsql database-provider feasibility

Date: 2026-10-08

Bead: `jellyfin-38k.1`

Result: **PASS for an independently packaged provider, with the version and
installation constraints below.**

> Historical note: this report records the original feasibility bead. The
> release-shaped package now supports only the automated Jellyfin 12.2 ABI;
> its black-box gate also publishes unmodified Jellyfin 13.0 and proves the
> provider rejects that unsupported ABI before database access. Current
> installation and recovery instructions live in the repository README.

This is a contract proof, not a production release. It establishes that the
vanilla loader, model pipeline, migration callbacks, basic Jellyfin CRUD, and
restart lifecycle are sufficient to build the real provider. Full query-surface,
long-text, purge, maintenance, packaging, and upgrade coverage remains in the
dependent implementation beads.

## Tested support matrix

| Component | Tested version | Initial support decision |
| --- | --- | --- |
| Jellyfin | 12.2.0, commit `ca0f16eb7c195f720a1493ed469a27c4657db0c6` | Automated black-box gate |
| Jellyfin development | 13.0.0, master commit `284997ebe2` | Prior manual ABI-binding observation only; not part of the automated gate |
| `Jellyfin.Database.Implementations` reference | NuGet 12.2.0 | Build-time contract |
| .NET | SDK 10.0.401, runtime 10.0 | Supported |
| Entity Framework Core | 10.0.11 | Must match the host |
| Npgsql EF provider / Npgsql | 10.0.3 / 10.0.3 | Shipped beside the plugin |
| PostgreSQL | 16.6, image digest `sha256:557fea37a744d5f4c8faab304b0a90858b53ab119735a88c131fd19dab802f36` | Automated black-box gate with runtime version assertion |
| Host PostgreSQL client programs | `pg_dump` absent; `psql` absent | Not required |

The provider rejects any Jellyfin database assembly outside ABI 12.2.x or 13.0.x
before it parses the connection string, opens PostgreSQL, or runs a migration.
ABI 13 had the same provider interface in the manually inspected master commit,
and the 12.2 reference bound forward in that manual run. That is compatibility
evidence, not a support guarantee; a pinned automated run is required before
claiming support for any 13.x release.

## Loader and package findings

Vanilla Jellyfin's `PLUGIN_PROVIDER` path can load a genuinely external provider.
The early loader puts `Jellyfin.Plugin.Database.Npgsql.dll` in the default assembly
load context. Its adjacent `.deps.json`, `Npgsql.EntityFrameworkCore.PostgreSQL.dll`,
and `Npgsql.dll` resolve there without copying any Jellyfin or EF assemblies into
the package. Runtime logging confirmed:

- the provider and both Npgsql assemblies came from exact paths in the plugin
  directory in the default load context;
- `Jellyfin.Database.Implementations` and EF Core came from exact paths in the
  untouched host publish directory, also in the default load context;
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
changes. This probe changed only `BaseItems.Type.MaxLength`; it appeared as
`character varying(511)` in both the migration snapshot and the real PostgreSQL
catalog. That proves this one explicit property facet can be changed after the
vanilla configuration runs. It does not yet prove equivalent handling for keys,
indexes, collations, converters, shadow properties, or every future model change.

## Migration, backup, and rollback result

The package contains a PostgreSQL baseline generated from the published Jellyfin
12.2 model. On a new database, the untouched host:

1. created its migration history table;
2. invoked `MigrationBackupFast`;
3. applied the plugin baseline;
4. invoked `DeleteBackup`;
5. completed its application migrations and became healthy.

The native backup implementation uses Npgsql and PostgreSQL schemas, not external
client programs. For the measured schema, it copies Jellyfin tables with data,
indexes, defaults, identity definitions, and checks. Foreign-key definitions are
retained as metadata and recreated after restoration so dropping the damaged
`public` schema cannot cascade them away. For identity-owned sequences it copies
the source sequence's exact `last_value` and `is_called` state into the snapshot;
it does not infer sequence state from table rows.

The black-box test then installed a test-only migration which committed a table
outside an EF transaction and deliberately failed with division by zero. Vanilla
Jellyfin invoked `RestoreBackupFast`. Verification after the failure showed:

- the partially committed marker table was absent;
- the administrator row and password remained usable;
- all 28 foreign keys remained;
- the failed migration was not in `__EFMigrationsHistory`;
- the temporary backup schema was gone;
- identity sequences retained exact state when advanced beyond the table maximum,
  when their only row had been deleted, when never called, and when explicitly
  advanced with `is_called=false`;
- replacing the deliberately bad package with the normal package allowed the
  untouched host to become healthy again.

The snapshot algorithm is sufficient for the measured Jellyfin-managed baseline,
which contains tables, identity-owned sequences, constraints, and indexes. This
probe does not claim preservation of standalone sequences, views, materialized
views, functions, triggers, row-level-security policies, custom grants, ownership
outside the runtime role, or other schema objects. Production work must reject or
explicitly preserve such objects. Future core migrations are therefore a mandatory
compatibility-test trigger.

## Host lifecycle and CRUD result

The smoke test binds both first-run and normal Jellyfin listeners exclusively to
IPv4 loopback, verifies the live listening socket before invoking setup, and uses
a random per-run administrator password. It then uses Jellyfin's startup API to
create the initial administrator and complete setup. It authenticates that user,
shuts the host down through Jellyfin's API, restarts the same untouched host, and
authenticates the persisted user again. It repeats the restart and authentication
after forced migration rollback.

PostgreSQL is bootstrapped separately as its container administrator. The test
then creates a random-password `jellyfin` login as owner of only the disposable
application database. Jellyfin uses that role exclusively. Catalog assertions and
negative `CREATE DATABASE`/`CREATE ROLE` attempts prove it is `NOSUPERUSER`,
`NOCREATEDB`, `NOCREATEROLE`, `NOREPLICATION`, and `NOBYPASSRLS`.

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
JELLYFIN_HOST_REPOSITORY=/path/to/jellyfin ./tests/black-box-smoke.sh
```

The script archives the exact default Jellyfin commit beneath ignored `.artifacts`,
builds and publishes that copy, starts the digest-pinned PostgreSQL image, installs
the minimum package, executes migrate/start/CRUD/shutdown/restart, forces and
verifies rollback, and records an `evidence.txt`. It uses collision-resistant
OS-assigned ports, random per-run credentials, and a paired fake `ffmpeg`/`ffprobe`.
It never edits the archived host and cleans up its Docker container.

`JELLYFIN_HOST_REPOSITORY` identifies the Jellyfin Git checkout. It defaults to
the plugin directory's parent only when that directory is a valid Jellyfin
checkout, so the proof remains convenient in this temporary layout and safely
fails with instructions after the plugin is relocated. `JELLYFIN_HOST_COMMIT` and
`POSTGRES_IMAGE` parameterize the two pinned inputs for an explicit compatibility
run; `POSTGRES_PORT`, `HTTP_PORT`, and `HTTPS_PORT` may also be supplied when fixed
ports are required. The passing default run used
Jellyfin 12.2.0 and PostgreSQL 16.6 and reported 28 restored foreign keys, the
single 511-character model override, exact identity sequence state, no host
`pg_dump` or `psql`, and only the three expected package DLLs.

## Parent-host isolation

All checked-in product, migration, test, and report files are under
`jellyfin-plugin-database-npgsql`. The parent Jellyfin solution and source are
unchanged from upstream master; the test builds immutable Git archives beneath the
plugin's ignored artifact directory.
