# Standalone Npgsql database-provider feasibility

Date: 2026-10-08

Bead: `jellyfin-38k.1`

Result: **PASS for an independently packaged provider, with the version and
installation constraints below.**

> This report supersedes the original exploratory result where it described
> behavior not present in the current branch. The package supports only the
> Jellyfin 12.2 ABI. Provider migration backup and rollback have since landed;
> stock full-system restore remains deliberately unsupported.

This document records the initial feasibility proof and its scope at the time it
was run. The later release work completed migration backup, host health, CRUD,
restart persistence, query parity, purge, maintenance, and upgrade coverage;
the current operational contract is in [operator-guide.md](operator-guide.md)
and final qualification is in [release-process.md](release-process.md).

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

The ordinary plugin manager runs later and creates a second collectible load
context. The provider does not use that copy for database configuration, model
construction, migrations, or persistence. The release metadata is nevertheless
`Active` so the late copy can install the small full-system restore guard. The
early provider proves the exact final guard registration before the full host
opens PostgreSQL. Disabled or failed normal loading therefore stops startup
rather than weakening the early provider path; `database.xml` remains the switch
that selects PostgreSQL.

The loader selects the first sorted plugin folder whose name starts with
`PluginName`. Installation must ensure there is only one matching folder. Also,
`PluginAssembly` must include the `.dll` suffix. Without it,
`Path.ChangeExtension("Jellyfin.Plugin.Database.Npgsql", "dll")` incorrectly seeks
`Jellyfin.Plugin.Database.dll`.

The early constructor may depend on services registered in the temporary startup
container. The core provider uses only early host services and succeeds in both
migration and full-host startup. Restore protection is separately verified from
the completed full-host service collection and skipped only in Jellyfin's
migration-only container, which has no backup service or restore entry point.

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

The package contains the PostgreSQL baseline generated from the audited
Jellyfin 12.2 model and implements Jellyfin's migration backup callback. Before
a pending migration, the provider creates its bounded snapshot; Jellyfin uses it
automatically only to roll back that failing migration and deletes it after
success. Operators cannot retain, select, or replay it. Full-system restore is a
different interface and remains fail-closed. See
[migration-backups.md](migration-backups.md) and
[full-system-restore.md](full-system-restore.md).

## Host lifecycle status

The harness binds a copied pristine host to IPv4 loopback and ties every HTTP
request to the launched process identity, process group, and listening socket.
The completed release gates cover setup, CRUD, graceful shutdown, restart
persistence, forced migration rollback, upgrade, package rollback boundaries,
and full-system restore rejection.

PostgreSQL is bootstrapped separately as its container administrator. The test
then creates a random-password `jellyfin` login as owner of only the disposable
application database. Jellyfin uses that role exclusively. Catalog assertions and
negative `CREATE DATABASE`/`CREATE ROLE` attempts prove it is `NOSUPERUSER`,
`NOCREATEDB`, `NOCREATEROLE`, `NOREPLICATION`, and `NOBYPASSRLS`.

## Installation and recovery contract

1. Verify and install the exact seven-file ZIP through a fresh unique staging
   directory, then atomically rename it to the exact versioned plugin folder.
2. Keep `meta.json` active so the mandatory full-system restore guard registers.
   Do not place Jellyfin, EF Core, or Microsoft Extensions assemblies in the
   plugin folder.
3. Configure `DatabaseType` as `PLUGIN_PROVIDER`, use the exact unique folder
   prefix for `PluginName`, and set `PluginAssembly` to
   `Jellyfin.Plugin.Database.Npgsql.dll`.
4. Use an empty PostgreSQL database owned by the configured runtime role. Selecting
   this provider does not convert or import an existing SQLite database.
5. Install the plugin before changing `database.xml`. A missing or incompatible
   provider is startup-fatal by design.
6. Treat provider migration snapshots as automatic, short-lived rollback state,
   not operator backup media. Maintain a separately qualified PostgreSQL
   disaster-recovery procedure before production use.

## Reproduction

From this directory:

```sh
JELLYFIN_HOST_PUBLISH=/path/to/exact-jellyfin-12.2-publish \
JELLYFIN_HOST_SHA256=REPLACE_WITH_PRECOMPUTED_PUBLISH_DIGEST \
PROVIDER_CORE_ONLY=true ./tests/black-box-smoke.sh
```

The targeted provider-core gate requires an explicitly supplied, separately
published vanilla Jellyfin 12.2 host and its precomputed digest. It starts the
digest-pinned PostgreSQL image, installs the package, and verifies the provider
core. It does not infer, edit, or depend on the parent repository. Use the full
release command in [release-process.md](release-process.md) for qualification.

The standalone smoke script's legacy source-build path runs only when
`JELLYFIN_HOST_REPOSITORY` is set explicitly to a suitable checkout. The release
verifier rejects that variable (and unsupported-host repository variants) and
requires a prebuilt publish with its pinned SHA-256, so its black-box gate cannot
fall back to a source checkout. `POSTGRES_IMAGE`, `POSTGRES_PORT`,
`HTTP_PORT`, and `HTTPS_PORT` remain explicit overrides. The completed release
gate now verifies migration, automatic failure rollback, CRUD, and restart.

## Parent-host isolation

All checked-in product, migration, test, and report files are under
`jellyfin-plugin-database-npgsql`. The parent Jellyfin solution and source are
unchanged from upstream master; the test builds immutable Git archives beneath the
plugin's ignored artifact directory.
