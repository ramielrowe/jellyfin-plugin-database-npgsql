# Operator guide

This guide is the supported path from a release artifact to a fresh
PostgreSQL-backed Jellyfin server. Read the limitations before changing an
existing installation.

## Supported matrix and hard limits

| Component | Supported by plugin 0.1.3.0 |
| --- | --- |
| Jellyfin host | Exact database ABI `12.2.0.0`, tested with vanilla Jellyfin `12.2.0.0` |
| PostgreSQL server | Exact qualification target and supported matrix: PostgreSQL `16.6` |
| Runtime | .NET 10 on Linux x64, as exercised by release qualification |
| Plugin directory | Exact `Npgsql Database_0.1.3.0` |
| Provider assembly | Exact `Jellyfin.Plugin.Database.Npgsql.dll` |

Do not force an unsupported Jellyfin version by editing metadata, renaming
directories, replacing dependencies, or bypassing the startup guards. Every
Jellyfin ABI or persisted-model release needs a new compatibility audit and will
normally require a new plugin release. PostgreSQL 16.6 is the exact server used
for qualification and the only server version in this release's support matrix;
the runtime startup guards enforce the Jellyfin ABI, model, and package
artifacts, not the PostgreSQL server version. Another PostgreSQL version may
start, but that does not make it supported.

This plugin creates the Jellyfin schema inside a fresh, operator-provisioned
PostgreSQL database; it never creates the database itself. Changing
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

For a Kubernetes Secret-volume passfile, the explicit
`passfile-permission-policy=owner-or-group-read` custom option also permits mode
`0440`/`0640` on Linux when the file's group is one of Jellyfin's actual process
groups. Group write, group execute, all other permissions, and group-readable
TLS private keys remain rejected. Use a dedicated Pod `fsGroup`; do not use a
shared application group. See the [Kubernetes image guide](../docker/kubernetes/README.md#postgresql-passfile-secret)
for the complete configuration and PVC ownership caveats.

Use `SSL Mode=VerifyFull` and a trusted root certificate for a network database.
The provider validates credential and certificate files before Npgsql startup,
then removes their paths from the normalized connection string. It rejects
connection options that expose parameter values or retain pooled session state.
It also rejects operator-supplied `Options` and injects the fixed
`Options=-c jit=off` startup parameter on every plugin connection. This prevents
PostgreSQL from spending seconds compiling Jellyfin's large generated Continue
Watching query during a synchronous HTTP request. Restart Jellyfin after upgrading
so all physical connections are recreated with the new session policy.
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

Stop Jellyfin. Replace the two `/absolute/path/...` placeholders, then run the
whole block as the account that manages Jellyfin plugins. This exact block is
qualified on Ubuntu 24.04 with Bash, Python 3, and GNU coreutils. In particular,
it relies on GNU `sha256sum --check --strict`, `mktemp --tmpdir`, and
`mv --no-clobber --no-target-directory` behavior. On another platform, use
equivalent tools that preserve the checksum validation, unique same-filesystem
staging directory, no-overwrite atomic rename, and final postconditions; do not
assume these options are portable. Every attempt uses a new staging directory
inside the plugin root, so a staging directory left by an interrupted attempt
cannot contribute files to a retry.

```bash
set -eu
export RELEASE_DIR='/absolute/path/to/downloaded-release'
export JELLYFIN_PLUGIN_ROOT='/absolute/path/to/jellyfin/plugins'
export PACKAGE_NAME=jellyfin-plugin-database-npgsql_0.1.3.0.zip
export CHECKSUM_NAME=jellyfin-plugin-database-npgsql_0.1.3.0.zip.sha256
export PLUGIN_NAME='Npgsql Database_0.1.3.0'

cd "$RELEASE_DIR"
sha256sum --check --strict "$CHECKSUM_NAME"
install -d -m 0750 "$JELLYFIN_PLUGIN_ROOT"
export PACKAGE_PATH="$RELEASE_DIR/$PACKAGE_NAME"
export PLUGIN_DIR="$JELLYFIN_PLUGIN_ROOT/$PLUGIN_NAME"
test ! -e "$PLUGIN_DIR"
export STAGE_DIR
STAGE_DIR=$(mktemp -d --tmpdir="$JELLYFIN_PLUGIN_ROOT" \
  '.npgsql-install.XXXXXXXX')

python3 - <<'PY'
import os
import shutil
import stat
import zipfile

expected = {
    "Jellyfin.Plugin.Database.Npgsql.deps.json",
    "Jellyfin.Plugin.Database.Npgsql.dll",
    "LICENSE",
    "Npgsql.EntityFrameworkCore.PostgreSQL.dll",
    "Npgsql.dll",
    "THIRD-PARTY-NOTICES.md",
    "meta.json",
}
package = os.environ["PACKAGE_PATH"]
stage = os.environ["STAGE_DIR"]

with zipfile.ZipFile(package, "r") as archive:
    entries = archive.infolist()
    names = [entry.filename for entry in entries]
    if len(entries) != len(expected) or set(names) != expected:
        raise SystemExit("package does not contain the exact seven-file allowlist")

    for entry in entries:
        file_type = stat.S_IFMT(entry.external_attr >> 16)
        if entry.is_dir() or file_type not in (0, stat.S_IFREG):
            raise SystemExit(f"package entry is not a regular file: {entry.filename}")
        target = os.path.join(stage, entry.filename)
        flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
        flags |= getattr(os, "O_NOFOLLOW", 0)
        descriptor = os.open(target, flags, 0o600)
        with os.fdopen(descriptor, "wb") as destination:
            with archive.open(entry, "r") as source:
                shutil.copyfileobj(source, destination)
        os.chmod(target, 0o640)

os.chmod(stage, 0o750)
PY

mv --no-clobber --no-target-directory "$STAGE_DIR" "$PLUGIN_DIR"
test ! -e "$STAGE_DIR"
test -d "$PLUGIN_DIR"
```

The checksum is verified before the archive is opened. The extractor accepts
only the seven expected root-level regular files, creates each file exclusively
instead of overwriting anything, and assigns directory mode `0750` and file mode
`0640`. Because staging is a unique directory on the same filesystem as the
canonical destination, the final move is atomic; `--no-clobber` plus the two
final tests makes an existing destination a failure instead of an overwrite.
An interrupted attempt can leave an `.npgsql-install.*` sibling for an operator
to inspect and remove while Jellyfin is stopped, but a later attempt always uses
a different directory. The ZIP allowlist is:

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
    <PluginName>Npgsql Database_0.1.3.0</PluginName>
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
`PluginAssembly`, `ConnectionString`, `Options`, and `NoLock`. The
`command-timeout` option accepts 1–3600 seconds. The optional
`passfile-permission-policy` accepts only `owner-or-group-read`; omitting it
keeps the owner-only default. Connection timeout is 1–120 seconds, cancellation
timeout is 1–30000 milliseconds, and pool sizes are bounded to 0–200 minimum
and 1–200 maximum. The provider forces UTC and the exact `public` search path.
It also forces PostgreSQL JIT off for plugin sessions; do not add an `Options`
parameter to the configured connection string.

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

For an update, stop Jellyfin and repeat the checksum, allowlist, unique-staging,
and atomic-rename procedure for the new exact versioned directory, then
atomically change `PluginName` once. Never partially overwrite the selected
directory. Keep the old ZIP and its configuration outside the plugin root until
startup is verified; ordinary plugin housekeeping can remove an older same-GUID
folder.

Provider migration rollback is automatic when Jellyfin reports a migration
failure during that migration. Jellyfin deletes the internal snapshot after a
successful migration. Operators cannot retain, select, or replay these
snapshots later. If an update fails, stop the server and preserve the complete
logs, host, plugin, configuration, and database state for review. After a
successful schema migration, a package-only downgrade is unsupported; restore
an older host/plugin/database combination only through a separately qualified
database-recovery procedure. Merely selecting an older DLL does not roll back
schema history.

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
