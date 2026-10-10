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
GUID. Keep the prior ZIP and migration snapshot outside the plugin root until a
rollback is no longer required.

## Fresh install

1. Verify the ZIP against its adjacent `.zip.sha256` file.
2. Extract all seven ZIP entries into `Npgsql Database_0.1.0.0`. Do not copy
   host-owned Jellyfin, EF Core, or Microsoft.Extensions assemblies there.
3. Write `database.xml` atomically with `DatabaseType=PLUGIN_PROVIDER`, the exact
   `PluginName`, and `PluginAssembly=Jellyfin.Plugin.Database.Npgsql.dll`.
4. Leave packaged `meta.json` at `status: Active`, then start Jellyfin.

## Update and interrupted update

Extract the complete new ZIP into its new version-qualified directory before
changing `database.xml`. Validate its checksum and file allowlist, then change
`PluginName` in one atomic configuration replacement and restart. If extraction
or validation is interrupted before that switch, the old exact directory remains
selected. Never perform an in-place partial overwrite of the selected directory.

Jellyfin's database migration invokes the provider's migration snapshot and
rollback protocol. Keep the previous package and the migration snapshot until
the first successful startup and verification are complete.

## Rollback

Stop Jellyfin, restore the database with the provider-owned migration rollback
when a migration was applied, atomically restore the matching prior
`database.xml`, move the newer same-GUID package out of Jellyfin's plugin root,
restore the complete prior package, and restart. Jellyfin ordinary-plugin
housekeeping may remove a disabled older duplicate while a newer package with
the same GUID remains installed, so merely changing `PluginName` is not a safe
rollback procedure. A package-only downgrade is
not a database rollback and may fail on unknown migration history; that failure
is intentional. Host, plugin, and database lineage must remain a reviewed set.

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
  and restore its matching migration snapshot when necessary; or
- deliberately configure a different supported provider after migrating data.

The portable PDB under `artifacts/symbols` is for diagnostics and is not
installed. The ZIP contains exactly the runtime allowlist in
`build/release-policy.json`.
