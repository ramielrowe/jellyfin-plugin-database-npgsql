# Installation, update, rollback, and recovery

This provider is loaded before normal Jellyfin plugin startup. Treat its folder
and `database.xml` as one startup-critical release selection.

## Canonical folder protocol

Release 0.1.0.0 uses the exact directory `Npgsql Database_0.1.0.0` and the exact
`PluginName` value `Npgsql Database_0.1.0.0`. Jellyfin 12.2 searches plugin
directories by prefix, but the provider independently compares both the
configured name and its actual parent directory with this exact value before it
opens PostgreSQL. An older release directory cannot match a newer fully
qualified name. A shortened name, suffix, partial directory, or copied assembly
under another directory fails closed.

Do not rename a release directory and do not overwrite it while Jellyfin is
running. Old version directories may remain during a staged update; they are not
selected by the new exact name. Do not rely on an old directory remaining after
startup: ordinary plugin housekeeping may remove an older package with the same
GUID. Keep the prior ZIP and configuration outside the plugin root until the new
startup is verified. Migration snapshots are internal, short-lived artifacts;
they are not operator-controlled rollback media.

## Fresh install

1. Verify the ZIP against its adjacent `.zip.sha256` file. If retaining
   diagnostics, independently verify the portable PDB against its adjacent
   `.pdb.sha256` file; the release audit also proves that its portable debug ID
   matches the provider DLL.
2. Follow the exact unique-staging installation block in
   [operator-guide.md](operator-guide.md). It validates the exact seven-file
   root allowlist, refuses non-regular or duplicate entries, extracts without
   overwriting, sets modes, and atomically renames the completed directory. Do
   not extract directly into the canonical directory or copy host-owned
   Jellyfin, EF Core, or Microsoft.Extensions assemblies there.
3. Write `database.xml` atomically with `DatabaseType=PLUGIN_PROVIDER`, the exact
   `PluginName`, and `PluginAssembly=Jellyfin.Plugin.Database.Npgsql.dll`.
4. Leave packaged `meta.json` at `status: Active`, then start Jellyfin.

## Update and interrupted update

Use the operator guide's complete unique-staging installation block for the new
version before changing `database.xml`. Validate its checksum and exact
seven-file allowlist, then change `PluginName` in one atomic configuration
replacement and restart. If extraction or validation is interrupted before that
switch, the old exact directory remains selected. A stale `.npgsql-install.*`
sibling cannot contaminate a retry because every attempt creates a fresh staging
directory. Never perform an in-place partial overwrite of the selected
directory.

Jellyfin's database migration invokes the provider's migration snapshot and
rollback protocol automatically if that migration fails. Jellyfin deletes the
snapshot when the migration succeeds. There is no supported way for an operator
to retain it or invoke its restore path later. Keep the previous package and
configuration until the first successful startup and verification are complete.

## Rollback

If startup fails before a schema migration succeeds, stop Jellyfin, atomically
restore the matching prior `database.xml`, move the newer same-GUID package out
of Jellyfin's plugin root, restore the complete prior package, and restart.
Jellyfin ordinary-plugin housekeeping may remove a disabled older duplicate
while a newer package with the same GUID remains installed, so merely changing
`PluginName` is not a safe rollback procedure.

If a migration itself fails, Jellyfin invokes the provider-owned rollback only
as part of that failing migration. Operators cannot invoke or replay it. After a
schema migration succeeds, Jellyfin deletes its snapshot and a package-only
downgrade is unsupported; recover the matching older database through a
separately qualified database-recovery procedure before selecting the older
host/plugin pair. Unknown migration history then fails intentionally. Host,
plugin, and database lineage must remain a reviewed set.

## Disable, uninstall, missing files, and recovery

Do not disable or uninstall this plugin while PostgreSQL remains selected.
Dashboard disable, a missing directory, inactive/malformed metadata, a missing
provider DLL, a missing Npgsql DLL, or an incomplete update all stop startup
before a PostgreSQL connection or SQLite fallback. This is safer than silently
selecting another provider, but it means removal is a planned database-provider
migration rather than an ordinary plugin uninstall.

To recover, stop Jellyfin and either:

- restore the complete checksummed package at the exact configured directory;
- atomically point `database.xml` back to a complete compatible prior package
  only when no schema migration succeeded, or after a separately qualified
  database recovery has restored matching history; or
- deliberately configure a different supported provider after migrating data.

The portable PDB and its checksum under `artifacts/symbols` are for diagnostics
and are not installed. The ZIP contains exactly the runtime allowlist in
`build/release-policy.json`; its checksum does not cover the separately shipped
PDB, so both sidecars must be retained and verified.
