# Jellyfin Npgsql database provider

This repository is the standalone development workspace for an Npgsql-backed
`IJellyfinDatabaseProvider` that loads into an unmodified Jellyfin server. It is
currently under development and does not yet contain a production provider or a
supported release artifact.

The permanent plugin GUID is `8a6e4c5e-2185-45df-a30f-cc828529330b`. Do not
change it between releases.

## Compatibility baseline

The release project targets .NET 10 and compiles against the published
`Jellyfin.Database.Implementations` 12.2.0 contract. It pins EF Core 10.0.11 and
Npgsql 10.0.3, matching the successful feasibility run documented in
[`docs/feasibility-report.md`](docs/feasibility-report.md). Jellyfin and EF Core
assemblies are host-owned compile contracts; only the provider and Npgsql runtime
assemblies belong in the plugin package.

The prototype provider and its generated schema are retained only under
`tests/feasibility`. They are excluded from the solution and release package so
later implementation beads can replace them deliberately.

## Build and test

The following commands work from this directory without a sibling Jellyfin source
checkout:

```sh
dotnet restore Jellyfin.Plugin.Database.Npgsql.slnx --locked-mode
dotnet build Jellyfin.Plugin.Database.Npgsql.slnx --configuration Release --no-restore
dotnet test Jellyfin.Plugin.Database.Npgsql.slnx --configuration Release --no-build
dotnet msbuild build/Package.proj -target:Package -property:Configuration=Release
./scripts/audit-build.sh
```

The package target writes a staged, allowlisted layout and ZIP beneath
`artifacts/`; the audit rejects host-owned runtime assemblies and external project
references. Builds are isolated from an enclosing Git repository so moving the
workspace does not change the release binary. `build.yaml` lists the same six
package artifacts for the official Jellyfin plugin packager.

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
