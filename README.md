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

The provider is loaded before Jellyfin's ordinary plugin initialization. Its
constructor consequently depends only on the early `ILogger<T>` service and it
does not use `IPluginServiceRegistrator`.

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

The packaged `meta.json` is disabled deliberately. `database.xml`, not normal
plugin enablement, activates an early database provider. Keeping the ordinary
plugin entry disabled avoids loading a redundant collectible copy later in the
same process. No ordinary `IPlugin` descriptor or service registrator is needed.

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
```

The package target writes a staged, allowlisted layout and ZIP beneath
`artifacts/`; the audit rejects host-owned runtime assemblies, any ZIP entry
outside the seven-file allowlist, and external project
references in every solution project after MSBuild resolves them. The audit also
runs a negative control that injects a known external project reference and proves
the guard rejects it. Builds are isolated from an enclosing Git repository so
moving the workspace does not change the release binary. `build.yaml` lists the
same six package artifacts for the official Jellyfin plugin packager.

The opt-in black-box feasibility proof remains available as
`./tests/black-box-smoke.sh`. It requires Docker and a local Jellyfin Git checkout;
it is historical compatibility evidence, not part of the standalone default test
suite. When this repository is not directly nested inside that Jellyfin checkout,
pass its location explicitly:

```sh
JELLYFIN_HOST_REPOSITORY=/path/to/jellyfin ./tests/black-box-smoke.sh
```

Without that setting, the script safely uses the plugin directory's parent only
when it is a Jellyfin Git checkout; otherwise it stops with an actionable error.
