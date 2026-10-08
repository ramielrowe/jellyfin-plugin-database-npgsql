# Test infrastructure

The reusable test infrastructure lives entirely in this repository under
`tests/Jellyfin.Plugin.Database.Npgsql.TestInfrastructure`. Default builds and
tests never invoke Docker, open a PostgreSQL connection, or download a Jellyfin
host. Real infrastructure is enabled only through the variables below.

## Real PostgreSQL fixture

Every test run creates a separate database and login. Names begin with
`jellyfin_test_db_` and `jellyfin_test_role_`, include an optional worker tag,
and end in a random 32-digit identifier. Cleanup refuses to drop any database,
role, or container which does not exactly match the generated pattern.

The generated runtime login is `NOSUPERUSER`, `NOCREATEDB`, `NOCREATEROLE`,
`NOREPLICATION`, and `NOBYPASSRLS`, with a connection limit of six. Its client
pool is limited to four connections. The administrator connection is never
given to Jellyfin.

Use either an explicit, test-only administrator:

```sh
export JELLYFIN_NPGSQL_TEST_ADMIN_CONNECTION_STRING='Host=127.0.0.1;Port=5432;Database=jellyfin_npgsql_test_admin;Username=test_admin;Password=...'
export JELLYFIN_NPGSQL_TEST_REQUIRED=true
dotnet test --project tests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~PostgreSqlInfrastructureOptInTests'
```

The administrator database name must contain `jellyfin_npgsql_test_admin`, and
the administrator must have `CREATEDB` and `CREATEROLE`. This explicit safety
marker is intentional; do not point the fixture at a production cluster.

Alternatively, start the immutable PostgreSQL 16.6 test image automatically:

```sh
export JELLYFIN_NPGSQL_TEST_USE_CONTAINER=true
export JELLYFIN_NPGSQL_TEST_REQUIRED=true
dotnet test --project tests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~PostgreSqlInfrastructureOptInTests'
```

The default image is pinned by SHA-256. An override through
`JELLYFIN_NPGSQL_TEST_POSTGRES_IMAGE` is accepted only when it is also a
digest-pinned reference. Set `JELLYFIN_NPGSQL_TEST_WORKER` when separate test
processes need a readable worker tag; randomness already prevents collisions.
The real test verifies concurrent worker isolation, restricted privileges,
cleanup after success, a setup callback failure, and cancellation.

If neither opt-in is set, the real test is skipped with the exact variables to
configure. `JELLYFIN_NPGSQL_TEST_REQUIRED=true` changes a missing opt-in into a
failure so CI cannot silently omit it. Connection strings, passwords, passfile
paths, and tokens are redacted from infrastructure diagnostics.

## Exact pristine Jellyfin host

Point tests at a previously published, untouched Jellyfin directory or ZIP.
The harness copies or safely extracts it into the test run and executes only the
copy. A required SHA-256 proves the selected input is exact, and another
content/path manifest proves the copied host was unchanged after every run.
Plugin files, configuration, cache, data, media, logs, and the PostgreSQL
database all live outside the publish directory.

Calculate a publish-directory hash:

```sh
dotnet run --project tests/Jellyfin.Plugin.Database.Npgsql.TestHarness/Jellyfin.Plugin.Database.Npgsql.TestHarness.csproj \
  -- hash-publish /path/to/vanilla-jellyfin-publish
```

Then exercise two independent stock-host launches, including readiness, setup
API calls, authentication, graceful API shutdown, sanitized log collection,
and host-integrity verification:

```sh
export JELLYFIN_NPGSQL_TEST_HOST_PUBLISH=/path/to/vanilla-jellyfin-publish
export JELLYFIN_NPGSQL_TEST_HOST_SHA256=<hash printed above>
export JELLYFIN_NPGSQL_TEST_ARTIFACT_ROOT=/tmp/jellyfin-npgsql-tests
dotnet test --project tests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~PristineHostOptInTests'
```

For a ZIP, calculate the ordinary file SHA-256 with `hash-archive`, then set
`JELLYFIN_NPGSQL_TEST_HOST_ARCHIVE` instead. ZIP traversal paths are rejected.
Successful runs delete their isolated case directory. A failed host run keeps
only its isolated case under the explicitly selected artifact root, with
collected diagnostics redacted.

No helper searches for or references a sibling/parent Jellyfin checkout. The
standalone plugin root is found by its own solution marker, so the same commands
work after this directory is moved to its dedicated repository.

## Packaged plugin black-box proof

`tests/black-box-smoke.sh` remains the comprehensive package-level consumer of
these conventions. It installs the built ZIP into isolated data, writes
`database.xml`, launches the exact publish, waits for health, uses Jellyfin's
HTTP APIs, gracefully stops the server, and checks that the host publish did not
change. During the current backup-development stage, run its provider-core
boundary:

```sh
export JELLYFIN_HOST_PUBLISH=/path/to/vanilla-jellyfin-publish
export JELLYFIN_HOST_SHA256=<publish hash>
PROVIDER_CORE_ONLY=true ./tests/black-box-smoke.sh
```

This path uses the same digest-pinned PostgreSQL image. The reusable C# fixture
above is the preferred path for new database tests and supports an external
administrator connection; the package smoke retains its container because it
also controls exact PostgreSQL process/version evidence.
