# Jellyfin Npgsql database provider

This repository is the standalone development workspace for an Npgsql-backed
`IJellyfinDatabaseProvider` that loads into an unmodified Jellyfin server. The
release ZIP is intentionally self-contained except for Jellyfin's host-owned
database, Entity Framework Core, and Microsoft Extensions assemblies.

The permanent plugin GUID is `8a6e4c5e-2185-45df-a30f-cc828529330b`. Do not
change it between releases.

## Compatibility baseline

The release project targets .NET 10 and compiles against the published
`Jellyfin.Database.Implementations` 12.2.0 contract. It pins EF Core 10.0.11 and
Npgsql 10.0.3, matching the successful feasibility run documented in
[`docs/feasibility-report.md`](docs/feasibility-report.md). Jellyfin and EF Core
assemblies are host-owned compile contracts; only the provider and Npgsql runtime
assemblies belong in the plugin package.

> **Full-system restore is not supported.** While this provider is selected,
> every dashboard, API, scheduled, and startup full-system restore request is
> rejected before Jellyfin can change Config, Data, Root, migration history,
> PostgreSQL, or create/fall back to SQLite. This includes PostgreSQL-origin,
> SQLite-origin, unknown, and corrupt archives. Provider migration snapshots
> used for automatic migration rollback remain supported; they are a separate
> internal format and workflow.

The provider assembly is loaded before Jellyfin's ordinary plugin
initialization. Its provider constructor consequently depends only on early
host services. The same assembly also contains a very small ordinary
`IPluginServiceRegistrator` which replaces only `IBackupService` with the
fail-closed restore guard. Before opening PostgreSQL, the early provider proves
that this exact guard is the final registration. If the ordinary plugin is
disabled, deleted, malformed, absent, throws during registration, or its guard
is replaced, startup stops before database or restore access.
The `--restore-archive` entry point is rejected even earlier, before Jellyfin's
preliminary migration container can change PostgreSQL.

## Installation and version selection

Extract one complete release ZIP into the exact versioned directory
`Npgsql Database_0.1.0.0`. In `database.xml`, select `PLUGIN_PROVIDER`, set
`PluginName` to that exact directory name, and set `PluginAssembly` to
`Jellyfin.Plugin.Database.Npgsql.dll`. The plugin validates the exact directory
because vanilla Jellyfin uses a prefix search; this prevents a leftover older or
similarly named folder from silently selecting an unintended binary.

For upgrades or rollback, extract the complete package to its new versioned
directory first, then change `PluginName` in one configuration edit and restart.
Leave the prior directory installed until the new version starts successfully.
If startup reports a missing dependency or unsupported database ABI, restore the
prior `PluginName` (or the prior Jellyfin version) and restart. Do not partially
overwrite a live version directory.

The packaged `meta.json` must remain `Active`. `database.xml` selects the early
database provider, while active ordinary discovery installs the independent
restore guard. Do not disable the plugin entry: the early provider treats a
missing or disabled late registration as an unsafe partial installation and
refuses startup. Jellyfin may load a collectible copy of the same assembly for
service registration; the database provider itself still runs only from the
early default load context.

## Database configuration

`database.xml` is the only configuration source used by the provider. A minimal
configuration is:

```xml
<DatabaseConfigurationOptions>
  <DatabaseType>PLUGIN_PROVIDER</DatabaseType>
  <CustomProviderOptions>
    <PluginName>Npgsql Database_0.1.0.0</PluginName>
    <PluginAssembly>Jellyfin.Plugin.Database.Npgsql.dll</PluginAssembly>
    <ConnectionString>Host=db.example;Database=jellyfin;Username=jellyfin_runtime;Passfile=/run/secrets/jellyfin-pgpass;SSL Mode=VerifyFull;Root Certificate=/run/secrets/postgresql-root.crt</ConnectionString>
    <Options>
      <CustomDatabaseOption>
        <Key>command-timeout</Key>
        <Value>30</Value>
      </CustomDatabaseOption>
    </Options>
  </CustomProviderOptions>
  <LockingBehavior>NoLock</LockingBehavior>
</DatabaseConfigurationOptions>
```

The command timeout is case-insensitive and bounded from 1 to 3600 seconds.
Connection, cancellation, and pool sizes are also bounded. The provider requires
an explicit host, database, and username, forces UTC and the `public` search path,
and rejects connection settings that log parameters, include potentially secret
server details, retain security information, or preserve pooled session state.
TLS certificate settings are validated and loaded before Npgsql startup, then
their paths are removed from the normalized connection string. A configured
passfile is preflighted, its path is removed, and a password callback safely
reopens it for each physical connection pool. This preserves Npgsql's multi-host
behavior: each child pool selects the first entry matching its own host, port,
database, and user instead of resolving against the parent's comma-separated
host list. Configuration failures identify only the setting—not its path or
contents. A passfile used for authentication must be readable, no larger than 1
MiB, and contain well-formed PostgreSQL password entries. As in Npgsql, lines
are trimmed, escaped colons and backslashes are decoded, first-match precedence
applies, and `*` is a wildcard in every field (including a null password).
An explicitly configured password takes precedence over an otherwise unused
passfile. Client certificates must be a PEM certificate/key pair or a
PFX/PKCS#12 file; root certificates must be PEM or an unencrypted PFX/PKCS#12
file.

Passfile processing uses positional reads on the opened file handle rather than
a buffered stream, stops at the first applicable entry, and clears every owned
byte and character buffer. The selected password must be returned through
Npgsql's managed `string` callback contract; .NET strings are immutable and
cannot be deterministically wiped after authentication. The passfile path
remains private callback state so it can be reopened, but neither the path nor
any credential is placed in the normalized connection string or provider
diagnostics.

On Unix, a configured passfile, PEM client private key, or client PFX/PKCS#12
file must have no group or other permission bits; mode `0600` is recommended
(`0400` is also accepted). The check uses the opened file handle, so a symlink
is judged by the permissions of the target actually read and a pathname swap
cannot substitute an unchecked file. Certificate-only PEM files may remain
world-readable. On Windows, Unix mode checks do not apply: the plugin preloads
the configured files and removes their paths, but it does not attempt to
interpret Windows ACL inheritance. Administrators must restrict each passfile,
client private key, and client PFX/PKCS#12 ACL to the Jellyfin service identity
and trusted administrators.

Npgsql 10.0.3 recognizes several PostgreSQL environment fallbacks. To keep
`database.xml` authoritative, startup rejects `PGUSER`, `PGPASSWORD`,
`PGPASSFILE`, `PGSSLCERT`, `PGSSLKEY`, `PGSSLROOTCERT`, `PGCLIENTENCODING`,
`PGTZ`, `PGOPTIONS`, `PGTARGETSESSIONATTRS`, `PGSSLNEGOTIATION`,
`PGGSSENCMODE`, `PGREQUIREAUTH`, and `PGAPPNAME` when they are present (even
when empty). This Npgsql release does not consume `PGHOST`, `PGPORT`,
`PGDATABASE`, or `PGSSLMODE`; explicit connection-string endpoint and TLS
settings remain authoritative. Startup also rejects conventional password or
TLS files in the home directory whenever Npgsql could otherwise load them.

Create the UTF8 database before starting Jellyfin. The configured login should
own the Jellyfin objects and needs `CONNECT` on that database plus `USAGE` and
`CREATE` on its `public` schema. It does not need superuser, `CREATEDB`, or
`CREATEROLE`. Do not share that schema with unrelated application objects.

Before vanilla Jellyfin runs a pending database migration, the provider creates
a managed, streamed, rollback-capable snapshot below the Jellyfin data path. It
does not invoke external PostgreSQL clients or require elevated database
privileges. Unsupported populated layouts fail before mutation.

## Backup and restore policy

The provider supports its own migration backup, rollback, and cleanup protocol
documented in [`docs/migration-backups.md`](docs/migration-backups.md). That
protocol is invoked by Jellyfin migrations and is not a user-selectable
full-system restore.

Stock full-system backup creation, manifest inspection, and backup listing are
left unchanged. A database-inclusive full-system backup is therefore an export,
not a restorable recovery artifact for this provider. Every stock full-system
restore is disabled—even one claiming not to contain a database—because a
restart-time handoff cannot safely preserve and re-prove that distinction
without implementing a cross-resource restore engine. See
[`docs/full-system-restore.md`](docs/full-system-restore.md).

## Build and test

The following commands work from this directory without a sibling Jellyfin source
checkout:

```sh
dotnet restore Jellyfin.Plugin.Database.Npgsql.slnx --locked-mode
dotnet build Jellyfin.Plugin.Database.Npgsql.slnx --configuration Release --no-restore
dotnet test --project tests/Jellyfin.Plugin.Database.Npgsql.UnitTests/Jellyfin.Plugin.Database.Npgsql.UnitTests.csproj --configuration Release --no-build
dotnet test --project tests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests.csproj --configuration Release --no-build
dotnet msbuild build/Package.proj -target:Package -property:Configuration=Release
./scripts/audit-build.sh
./scripts/verify-migrations.sh
```

The package target writes a staged, allowlisted layout and ZIP beneath
`artifacts/`; the audit rejects host-owned runtime assemblies, any ZIP entry
outside the seven-file allowlist, and external project
references in every solution project after MSBuild resolves them. The audit also
runs a negative control that injects a known external project reference and proves
the guard rejects it. Builds are isolated from an enclosing Git repository so
moving the workspace does not change the release binary. `build.yaml` lists the
same seven package artifacts for the official Jellyfin plugin packager.

Reusable opt-in PostgreSQL and pristine-host infrastructure is documented in
[`docs/test-infrastructure.md`](docs/test-infrastructure.md). Default test runs
do not contact Docker, PostgreSQL, or the network. The real fixture supports an
explicit test-administrator connection or an immutable pinned container; the
host fixture accepts an integrity-checked vanilla Jellyfin publish or ZIP and
has no sibling/parent checkout dependency.

The audited late-model transformations, exact chunk-interned text-key design,
and the public query-compatibility contract are
recorded in [`docs/model-compatibility.md`](docs/model-compatibility.md).
The independent migration lineage, design-time workflow, hand-authored SQL
boundary, and pinned host migration audit are documented in
[`docs/migrations.md`](docs/migrations.md).
The SQLite differential matrix, repeated PostgreSQL runs, stock-server
API/media-scan proof, and intentional provider differences are documented in
[`docs/persistence-parity.md`](docs/persistence-parity.md).
The atomic backup format, validation boundaries, and transactional restore are
documented in [`docs/migration-backups.md`](docs/migration-backups.md).
The checked-in predecessor package and exact fresh-start, upgrade, restart, and
failure matrix are documented in
[`docs/stock-lifecycle.md`](docs/stock-lifecycle.md).

The black-box smoke test exercises the packaged ZIP, early dependency/ABI
guards, restricted PostgreSQL role, and untouched vanilla startup path:

```sh
JELLYFIN_HOST_PUBLISH=/path/to/vanilla-jellyfin-publish \
JELLYFIN_HOST_SHA256=<documented-publish-hash> \
./tests/black-box-smoke.sh
```

The script never infers a parent checkout. `JELLYFIN_HOST_REPOSITORY` remains an
explicit legacy compatibility input while the plugin is developed inside the
old monorepo, but it is not needed after relocation.
