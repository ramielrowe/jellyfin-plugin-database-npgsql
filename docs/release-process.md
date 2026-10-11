# Contributor release and qualification guide

Run release qualification from the standalone plugin root. The repository must
not depend on a sibling Jellyfin checkout, and the release verifier proves that
by copying the tracked source into an isolated directory outside the checkout.

## Compatibility policy

The `0.1.3.0` release is pinned to the exact Jellyfin database ABI `12.2.0.0`,
the audited vanilla Jellyfin `12.2.0.0` host and its 49 code migrations, EF Core
10.0.11, Npgsql/EF provider 10.0.3, .NET 10, and PostgreSQL 16.6. PostgreSQL 16.6
is the exact qualified server and the only version in the support matrix; the
runtime startup guards verify the Jellyfin ABI, model, and package artifacts,
not the PostgreSQL server version. A new Jellyfin release requires both ABI and
persisted-model audits even when the public ABI number does not change. Review
the final relational model, host code-migration set, query semantics, generated
SQL, and real lifecycle before changing the supported matrix. Never widen
`targetAbi` or bypass runtime guards merely because a newer host starts.

The provider is loaded by Jellyfin before ordinary plugins. Its constructor and
first configuration path therefore use only early host contracts and load the
two adjacent Npgsql runtime dependencies deliberately. The plugin applies
provider-specific conventions and services during late EF model finalization,
after Jellyfin's shared configurations. The same assembly is discovered later
as an active ordinary plugin solely to register the full-system restore guard;
the early provider proves that exact guard registration before database access.

## Fast local gates

Run these commands in the listed order. The first command supplies the locked
restore required by the following `--no-restore` commands, and the Release build
supplies the outputs required by the `--no-build` test command.

```sh
dotnet restore Jellyfin.Plugin.Database.Npgsql.slnx --locked-mode
dotnet format Jellyfin.Plugin.Database.Npgsql.slnx --verify-no-changes --no-restore
dotnet build Jellyfin.Plugin.Database.Npgsql.slnx --configuration Release --no-restore
dotnet test --solution Jellyfin.Plugin.Database.Npgsql.slnx \
  --configuration Release --no-build --max-parallel-test-modules 4
./scripts/verify-migrations.sh
dotnet msbuild build/Package.proj -target:Package -property:Configuration=Release
./scripts/audit-build.sh
```

`verify-migrations.sh` checks for pending EF model changes and regenerates the
idempotent migration script under ignored `.artifacts/migrations`. Review every
new migration, snapshot diff, hand-authored SQL boundary, generated script, and
real PostgreSQL catalog. The upstream model and code-migration audits are also
runtime guards, not documentation-only checks. The checked-in predecessor
fixture supplies the prior plugin lineage for upgrade, restart, failure, and
package rollback tests.

## Real PostgreSQL and pristine-host qualification

Build the helper, hash an independently produced untouched Jellyfin 12.2
publish, and pin that exact input:

```sh
dotnet build tests/Jellyfin.Plugin.Database.Npgsql.TestHarness/Jellyfin.Plugin.Database.Npgsql.TestHarness.csproj \
  --configuration Release --no-restore
dotnet tests/Jellyfin.Plugin.Database.Npgsql.TestHarness/bin/Release/net10.0/Jellyfin.Plugin.Database.Npgsql.TestHarness.dll \
  hash-publish /path/to/vanilla-jellyfin-12.2-publish
```

Then run the complete integration project against the immutable PostgreSQL 16.6
container and the exact host. The test runner uses four module workers; the
suite and fixture add their own concurrent process/database cases.

```sh
export JELLYFIN_NPGSQL_TEST_USE_CONTAINER=true
export JELLYFIN_NPGSQL_TEST_REQUIRED=true
export JELLYFIN_NPGSQL_TEST_HOST_PUBLISH=/path/to/vanilla-jellyfin-12.2-publish
export JELLYFIN_NPGSQL_TEST_HOST_SHA256=REPLACE_WITH_HASH_FROM_HELPER
dotnet test --project tests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests.csproj \
  --configuration Release --no-build --max-parallel-test-modules 4
```

This covers migrations and drift, SQLite differential/query parity, late model
behavior, fresh/restart/predecessor upgrade/failure rollback, provider migration
backup, lifecycle recovery, stock-host API/media paths, restore rejection, and
test-infrastructure containment. PostgreSQL test containers are digest-pinned
and must report exact `server_version_num = 160006`.

## Relocated package qualification

The release gate performs source and relocated restore/build/test/package runs,
dependency and license inspection, NuGet vulnerability audit, negative controls,
path-leak checks, and byte-for-byte ZIP/PDB reproducibility. Its black-box phase
installs only the final ZIP into a staged copy of the exact pristine host:

```sh
export RUN_BLACK_BOX=true
export JELLYFIN_HOST_PUBLISH=/path/to/vanilla-jellyfin-12.2-publish
export JELLYFIN_HOST_SHA256=REPLACE_WITH_HASH_FROM_HELPER
./scripts/verify-release.sh
```

Do not supply a host repository to the release verifier. It accepts only a
prebuilt publish plus its digest, stages it through the marker-owned isolated
root, and proves the input is unchanged. Cleanup is performed by the ownership-
checking C# harness. Do not replace it with ad-hoc recursive shell deletion.

The package must be the canonical file
`artifacts/jellyfin-plugin-database-npgsql_0.1.3.0.zip` and contain exactly the
seven entries in `build/release-policy.json`. Its adjacent `.zip.sha256` is in
canonical SHA256SUMS form. The portable PDB and its own checksum live under
`artifacts/symbols/`; the package builder verifies that the PDB debug identifier
matches the release DLL, but the PDB is not installed. Source and relocated ZIP
bytes and PDB bytes must match exactly.

## Final evidence and repository checks

Record the following in the release bead or equivalent immutable release
record: commit, branch, upstream ancestry, SDK/runtime/OS, exact host digest,
PostgreSQL image digest and runtime version, test pass/skip counts, every gate
result, retained sanitized evidence path, ZIP/PDB paths and SHA-256 values, and
accepted limitations. Validate every local Markdown link and every documented
configuration snippet. Review `git status`, including ignored-artifact policy,
and prove the branch consists only of upstream master plus standalone-plugin
work. Do not publish, push, create a catalog entry, issue, or pull request as
part of local qualification.

Accepted limitations for `0.1.3.0` are exact Jellyfin 12.2/PostgreSQL 16.6 only;
no SQLite migration; no HA claim; no cross-server migration-backup portability;
no qualified operator database restore; and fail-closed dashboard, API,
scheduled, and startup full-system restore.
