# Operator guide

This guide is the supported path from a release artifact to a fresh
PostgreSQL-backed Jellyfin server. Read the limitations before changing an
existing installation.

## Supported matrix and hard limits

| Component | Supported by plugin 0.1.0.0 |
| --- | --- |
| Jellyfin host | Exact database ABI `12.2.0.0`, tested with vanilla Jellyfin `12.2.0.0` |
| PostgreSQL server | Exact PostgreSQL `16.6` |
| Runtime | .NET 10 on Linux x64, as exercised by release qualification |
| Plugin directory | Exact `Npgsql Database_0.1.0.0` |
| Provider assembly | Exact `Jellyfin.Plugin.Database.Npgsql.dll` |

Do not force an unsupported Jellyfin or PostgreSQL version by editing metadata,
renaming directories, replacing dependencies, or bypassing the startup guards.
Every Jellyfin ABI or persisted-model release needs a new compatibility audit
and will normally require a new plugin release. PostgreSQL versions other than
16.6 may work, but this release makes no such support claim.

This plugin creates a new PostgreSQL Jellyfin database. Changing
`DatabaseType` does **not** migrate an existing SQLite database, and an SQLite
file is **not** a PostgreSQL backup. There is no supported SQLite-to-PostgreSQL
conversion workflow in this release.

> **Full-system restore is unsupported.** Dashboard, API, scheduled, and
> `--restore-archive` restores fail closed before changing Config, Data, Root,
> migration history, PostgreSQL, or SQLite. The automatic migration snapshot
> described below is a separate internal rollback mechanism.

## 1. Provision a fresh least-privilege database

Run the following SQL as a PostgreSQL administrator, substituting your own role
and database names. It creates a UTF8 database owned by an unprivileged login.
Set the role password separately with `\password jellyfin_runtime`, or use a
site-managed certificate authentication policy, so a password is not placed in
shell history or SQL logs.

```sql
CREATE ROLE jellyfin_runtime
  LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE DATABASE jellyfin
  WITH OWNER = jellyfin_runtime TEMPLATE = template0 ENCODING = 'UTF8';
\connect jellyfin
REVOKE ALL ON DATABASE jellyfin FROM PUBLIC;
GRANT CONNECT ON DATABASE jellyfin TO jellyfin_runtime;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO jellyfin_runtime;
GRANT USAGE, CREATE ON SCHEMA public TO jellyfin_runtime;
```

The runtime role needs `CONNECT` on this database and `USAGE`/`CREATE` on its
owned `public` schema. It does not need superuser, `CREATEDB`, `CREATEROLE`,
replication, or bypass-RLS privileges. Give Jellyfin a dedicated database and
do not place unrelated objects in its `public` schema. The provider refuses to
create the database itself.

## 2. Prepare credentials and TLS

Prefer a PostgreSQL passfile owned by the Jellyfin service account. This sample
uses the default PostgreSQL port; replace every placeholder before use.

```text
db.example:5432:jellyfin:jellyfin_runtime:REPLACE_WITH_A_SECRET
```

On Unix, make the passfile and every client private key or client PKCS#12 file
owner-only. Certificate-only PEM files may be world-readable.

```sh
chmod 0600 /run/secrets/jellyfin-pgpass
chmod 0600 /run/secrets/jellyfin-client.key
```

Mode `0400` is also accepted. Group or other permission bits are rejected.
On Windows, restrict the equivalent ACLs to the Jellyfin service identity and
trusted administrators; the plugin does not interpret Windows ACL inheritance.

Use `SSL Mode=VerifyFull` and a trusted root certificate for a network database.
The provider validates credential and certificate files before Npgsql startup,
then removes their paths from the normalized connection string. It rejects
connection options that expose parameter values or retain pooled session state.
Do not put a password directly in `database.xml` unless your deployment cannot
provide a protected passfile or certificate; configuration backups and support
bundles must be treated as secrets if you do.

Unset PostgreSQL environment variables consumed by Npgsql, including
`PGUSER`, `PGPASSWORD`, `PGPASSFILE`, `PGSSLCERT`, `PGSSLKEY`,
`PGSSLROOTCERT`, `PGCLIENTENCODING`, `PGTZ`, `PGOPTIONS`,
`PGTARGETSESSIONATTRS`, `PGSSLNEGOTIATION`, `PGGSSENCMODE`,
`PGREQUIREAUTH`, and `PGAPPNAME`. Their presence, including an empty value,
causes startup to fail so `database.xml` remains authoritative.

## 3. Install the ZIP before first start

Stop Jellyfin. Set these placeholders for your installation and run the
checksum and extraction steps as the account that manages Jellyfin plugins:

```sh
export RELEASE_DIR=/path/to/downloaded-release
export JELLYFIN_PLUGIN_ROOT=/path/to/jellyfin/plugins
cd "$RELEASE_DIR"
sha256sum --check jellyfin-plugin-database-npgsql_0.1.0.0.zip.sha256
install -d -m 0750 "$JELLYFIN_PLUGIN_ROOT"
install -d -m 0750 "$JELLYFIN_PLUGIN_ROOT/Npgsql Database_0.1.0.0.staging"
python3 -m zipfile -e jellyfin-plugin-database-npgsql_0.1.0.0.zip \
  "$JELLYFIN_PLUGIN_ROOT/Npgsql Database_0.1.0.0.staging"
mv --no-target-directory \
  "$JELLYFIN_PLUGIN_ROOT/Npgsql Database_0.1.0.0.staging" \
  "$JELLYFIN_PLUGIN_ROOT/Npgsql Database_0.1.0.0"
```

The example uses Python's standard ZIP extractor; a trusted equivalent is fine.
The target directory must not already exist. The ZIP contains exactly these
seven files:

```text
Jellyfin.Plugin.Database.Npgsql.deps.json
Jellyfin.Plugin.Database.Npgsql.dll
LICENSE
Npgsql.EntityFrameworkCore.PostgreSQL.dll
Npgsql.dll
THIRD-PARTY-NOTICES.md
meta.json
```

Do not add host-owned Jellyfin, Entity Framework Core, or Microsoft Extensions
assemblies. The separately checksummed portable PDB is optional diagnostic
material and does not belong in the plugin directory.

## 4. Configure the stopped server

With Jellyfin still stopped, atomically replace
`$JELLYFIN_CONFIG_DIR/database.xml` with the following document after replacing
the endpoint and secret-file paths:

```xml
<?xml version="1.0" encoding="utf-8"?>
<DatabaseConfigurationOptions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <DatabaseType>PLUGIN_PROVIDER</DatabaseType>
  <CustomProviderOptions>
    <PluginName>Npgsql Database_0.1.0.0</PluginName>
    <PluginAssembly>Jellyfin.Plugin.Database.Npgsql.dll</PluginAssembly>
    <ConnectionString>Host=db.example;Port=5432;Database=jellyfin;Username=jellyfin_runtime;Passfile=/run/secrets/jellyfin-pgpass;SSL Mode=VerifyFull;Root Certificate=/run/secrets/postgresql-root.crt;Maximum Pool Size=20;Timeout=15;Cancellation Timeout=2000</ConnectionString>
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

All six selection fields are significant: `PLUGIN_PROVIDER`, `PluginName`,
`PluginAssembly`, `ConnectionString`, `Options`, and `NoLock`. Only
`command-timeout` is accepted in `Options`; it is 1–3600 seconds. Connection
timeout is 1–120 seconds, cancellation timeout is 1–30000 milliseconds, and
pool sizes are bounded to 0–200 minimum and 1–200 maximum. The provider forces
UTC and the exact `public` search path.

Start Jellyfin only after the package, credentials, PostgreSQL service, and
configuration are all in place. On first start the plugin validates its early
load, exact ABI, late restore guard, host migration set, configuration, and
final EF model before creating the schema. Confirm normal health and inspect
logs for the selected non-secret host, port, database, TLS mode, pooling state,
and component versions. Logs must not contain the connection string, password,
passfile path, certificate path, or token.

## Migrations, maintenance, and backups

Jellyfin owns migration scheduling. Before a pending database migration, the
provider writes a bounded, durable snapshot beneath
`$JELLYFIN_DATA_DIR/NpgsqlMigrationBackups`; on migration failure Jellyfin asks
the provider to restore it transactionally. Do not edit, copy between servers,
or submit these directories to the dashboard restore endpoint. Their exact
database/cluster/plugin binding and short validity window make them migration
rollback artifacts, not general backups. Details are in
[migration-backups.md](migration-backups.md).

Scheduled Jellyfin database optimization runs bounded `VACUUM (ANALYZE)` on
model-owned tables; statistics refresh runs `ANALYZE`. Shutdown retires only
this provider's connection pool and data source. Do not delete the provider's
interning rows or managed schema objects by hand.

Stock backup creation, listing, and manifest inspection remain available, but
a database-inclusive stock backup is an export only and cannot be restored by
this plugin. This release does not qualify `pg_dump`/`pg_restore` portability or
offer any other operator-triggered database restore. Establish and test an
independent PostgreSQL disaster-recovery procedure before production use.

## Update, rollback, disable, uninstall, and recovery

For an update, stop Jellyfin, verify and extract the complete new ZIP into its
new exact versioned directory, then atomically change `PluginName` once. Never
partially overwrite the selected directory. Keep the old ZIP, its configuration,
and any migration snapshot outside the plugin root until startup is verified;
ordinary plugin housekeeping can remove an older same-GUID folder.

Provider migration rollback is automatic when Jellyfin reports a migration
failure; there is no supported operator command for replaying one of these
snapshots later. If an update fails, leave the snapshot untouched, stop the
server, and preserve the complete logs, host, plugin, configuration, and
database state for review. Restore an older host/plugin/database combination
only through a separately qualified recovery procedure. Merely selecting an
older DLL does not roll back schema history.

Do not disable or uninstall the ordinary plugin while PostgreSQL is selected.
The late plugin supplies the mandatory full-system restore guard; disabling,
deleting, or corrupting it intentionally stops startup before database access.
If a dependency, directory, or metadata file is missing, stop Jellyfin and
either restore the complete checksummed package at the exact configured path,
restore a complete compatible prior host/plugin/database set, or deliberately
select another provider only after migrating its data. There is no automatic
SQLite fallback and no supported data conversion during recovery.

See [installation-lifecycle.md](installation-lifecycle.md) for the canonical
directory protocol and interruption cases, and
[full-system-restore.md](full-system-restore.md) for the restore guard boundary.
