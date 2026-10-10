# Standalone Npgsql database-provider feasibility

Date: 2026-10-08

Bead: `jellyfin-38k.1`

Result: **PASS for an independently packaged provider, with the version and
installation constraints below.**

> This report supersedes the original exploratory result where it described
> behavior not present in the current branch. The package supports only the
> Jellyfin 12.2 ABI. Migration backup remains deliberately unsupported pending
> bead `jellyfin-38k.8`, so the current package is not a usable production
> database backend.

This is a partial contract proof, not a production release. It establishes the
vanilla early-loader, packaging, provider configuration, and model-finalization
boundaries. Migration backup, host health, CRUD, restart persistence, query
parity, purge, maintenance, and upgrade coverage remain in dependent beads.

## Tested support matrix

| Component | Tested version | Initial support decision |
| --- | --- | --- |
| Jellyfin | 12.2.0, commit `ca0f16eb7c195f720a1493ed469a27c4657db0c6` | Automated black-box gate |
| Jellyfin development | 13.x | Unsupported and rejected before database access |
| `Jellyfin.Database.Implementations` reference | NuGet 12.2.0 | Build-time contract |
| .NET | SDK 10.0.401, runtime 10.0 | Supported |
| Entity Framework Core | 10.0.11 | Must match the host |
| Npgsql EF provider / Npgsql | 10.0.3 / 10.0.3 | Shipped beside the plugin |
| PostgreSQL | 16.6, image digest `sha256:557fea37a744d5f4c8faab304b0a90858b53ab119735a88c131fd19dab802f36` | Automated black-box gate with runtime version assertion |
| Host PostgreSQL client programs | `pg_dump` absent; `psql` absent | Not required |

The provider accepts only the exact Jellyfin database ABI 12.2.0.0. It rejects 13.x and every
other ABI before parsing the connection string, opening PostgreSQL, or running a
migration. No manual binding observation is a support claim.

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

## Migration, backup, and rollback status

The package contains a PostgreSQL baseline generated from the Jellyfin 12.2
model, but vanilla Jellyfin requires a rollback-capable backup callback before it
will apply that baseline. The current provider intentionally throws
`NotSupportedException` from that callback before making database changes. The
provider-core smoke test verifies this fail-closed boundary and verifies that no
application table or backup schema was created.

Backup, restore, deletion, forced-failure rollback, and the full healthy-host CRUD
path are work for bead `jellyfin-38k.8` and later dependent beads. Earlier text
claiming that those operations completed referred to a superseded host-integrated
prototype and is not behavior shipped by this standalone plugin branch.

## Host lifecycle status

The harness can bind a copied pristine host to IPv4 loopback and ties every HTTP
request to the launched process identity, process group, and listening socket.
The current provider-core gate stops at the deliberate migration-backup guard.
Setup, CRUD, graceful shutdown, restart persistence, and forced rollback are not
claimed complete until the backup and parity beads land.

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
6. Do not use the current development package for a migration. Its deliberate
   backup guard prevents startup before mutation. A future release must document
   tested recovery behavior and still require an independent operator backup.

## Reproduction

From this directory:

```sh
JELLYFIN_HOST_PUBLISH=/path/to/exact-jellyfin-12.2-publish \
JELLYFIN_HOST_SHA256=<precomputed-publish-digest> \
PROVIDER_CORE_ONLY=true ./tests/black-box-smoke.sh
```

The supported gate requires an explicitly supplied, separately published vanilla
Jellyfin 12.2 host and its precomputed digest. It starts the digest-pinned
PostgreSQL image, installs the package, and verifies the pre-mutation provider
guard. It does not infer, edit, or depend on the parent repository.

The legacy source-build path runs only when `JELLYFIN_HOST_REPOSITORY` is set
explicitly to a suitable checkout; it is not the supported reproducibility path
and no parent checkout is inferred. `POSTGRES_IMAGE`, `POSTGRES_PORT`,
`HTTP_PORT`, and `HTTPS_PORT` remain explicit overrides. A full migration,
rollback, CRUD, and restart result must not be reported until bead
`jellyfin-38k.8` and its dependent parity work are complete.

## Parent-host isolation

All checked-in product, migration, test, and report files are under
`jellyfin-plugin-database-npgsql`. The parent Jellyfin solution and source are
unchanged from upstream master; the test builds immutable Git archives beneath the
plugin's ignored artifact directory.
