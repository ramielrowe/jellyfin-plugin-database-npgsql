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
# Provision this marker once, while no other client is connected:
# COMMENT ON DATABASE jellyfin_npgsql_test_admin IS 'jellyfin-npgsql-exclusive-test-cluster-v1';
export JELLYFIN_NPGSQL_TEST_ADMIN_CONNECTION_STRING='Host=127.0.0.1;Port=5432;Database=jellyfin_npgsql_test_admin;Username=test_admin;Password=...'
export JELLYFIN_NPGSQL_TEST_EXTERNAL_EXCLUSIVE=true
export JELLYFIN_NPGSQL_TEST_REQUIRED=true
dotnet test --project tests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~PostgreSqlInfrastructureOptInTests'
```

External automatic creation and cleanup is deliberately unavailable unless the
operator opts in, the administrator database is named exactly
`jellyfin_npgsql_test_admin`, and that database has the exact exclusivity comment
shown above. The administrator must have `CREATEDB` and `CREATEROLE`. The harness
also requires its PostgreSQL advisory lock and refuses to start while another
privileged client session is present. The operator contract is that this is a
dedicated disposable cluster and no other privileged actor will connect for the
entire test. Do not point the fixture at a shared, development, or production
cluster.

PostgreSQL `DROP DATABASE` is name-based, nontransactional, and cannot be
atomically tied to a previously observed OID. The harness serializes all of its
actors, tombstones names, and revalidates names, OIDs, owners, and paired lease
markers immediately before destructive statements. A malicious or merely
non-cooperating superuser can ignore the advisory lock and defeat these checks;
that is why external use requires the exclusive-cluster contract. The
harness-created container satisfies that contract automatically.

Both the external fixture and controlled container require the exact numeric
`server_version_num` value `160006`.

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
An override which does not actually run PostgreSQL 16.6 fails after connection.
The real test verifies concurrent worker isolation, restricted privileges,
cleanup after success, a setup callback failure, and cancellation. It also runs
two simultaneous `dotnet` worker processes against distinct containers and two
simultaneous `dotnet` processes against one cluster. Local owner-lease files are
namespaced by PostgreSQL's stable system identifier, so distinct clusters never
contend on a reused stale-control lease while actors on the same cluster still
exclude one another.

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

First build the hash helper while dependencies are available, then run the
prebuilt helper offline. Its stdout is exactly one lowercase 64-hex digest;
diagnostics use stderr:

```sh
dotnet build tests/Jellyfin.Plugin.Database.Npgsql.TestHarness/Jellyfin.Plugin.Database.Npgsql.TestHarness.csproj \
  --configuration Release --no-restore
dotnet tests/Jellyfin.Plugin.Database.Npgsql.TestHarness/bin/Release/net10.0/Jellyfin.Plugin.Database.Npgsql.TestHarness.dll \
  hash-publish /path/to/vanilla-jellyfin-publish
```

Then exercise two independent stock-host launches, including readiness, setup
API calls, authentication, graceful API shutdown, sanitized log collection,
and host-integrity verification:

```sh
export JELLYFIN_NPGSQL_TEST_HOST_PUBLISH=/path/to/vanilla-jellyfin-publish
export JELLYFIN_NPGSQL_TEST_HOST_SHA256=<hash printed above>
dotnet test --project tests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests/Jellyfin.Plugin.Database.Npgsql.IntegrationTests.csproj \
  --filter 'FullyQualifiedName~PristineHostOptInTests'
```

Host-test scratch data defaults to the standalone plugin's ignored
`.artifacts/host-tests` directory. Set `JELLYFIN_NPGSQL_TEST_ARTIFACT_ROOT` to
an absolute path only when a different filesystem is needed; choosing a path
inside a writable workspace lets the C# harness clean it without a sandbox
permission prompt.

For a ZIP, calculate the ordinary file SHA-256 with `hash-archive`, then set
`JELLYFIN_NPGSQL_TEST_HOST_ARCHIVE` instead. ZIP traversal paths are rejected.
Every run creates an ownership-marked case directory and validates that marker
before recursively deleting it. A failed host run keeps at most 8 MiB of
redacted diagnostics under `.artifacts/host-tests/retained-evidence`, prints
the exact retained path and byte count, and then deletes the entire case,
including configuration, data, cache, media, raw logs, keys, passfiles,
certificates, and the copied host. Successful runs retain no case artifacts.

No helper searches for or references a sibling/parent Jellyfin checkout. The
standalone plugin root is found by its own solution marker, so the same commands
work after this directory is moved to its dedicated repository.

On Linux, host launches go through the checked-in `tests/process-supervisor.py`.
An outer containment watchdog and inner launch supervisor are both subreapers;
every signal is sent through a pidfd opened before identity inspection, never by
numeric PID or process group. The watchdog adopts and reclaims the complete tree
if the launch supervisor is killed after handoff. The launch barrier is released
only after the identity record is durably published, and all shutdown phases are
bounded. Tests inject publication failure, a launch-supervisor `SIGKILL`, detached
double-forks, wrong identity data, and inherited secret output. Initialization
fails closed when pidfds or subreaper containment are unavailable; no change to
the vanilla Jellyfin publish is required.

## Packaged plugin black-box proof

`tests/black-box-smoke.sh` remains the comprehensive package-level consumer of
these conventions. It installs the built ZIP into isolated data, writes
`database.xml`, launches the exact publish, waits for health, uses Jellyfin's
HTTP APIs, gracefully stops the server, and checks that the host publish did not
change. Existing provider-core CI jobs may retain their compatibility flag:

```sh
export JELLYFIN_HOST_PUBLISH=/path/to/vanilla-jellyfin-publish
export JELLYFIN_HOST_SHA256=<publish hash>
PROVIDER_CORE_ONLY=true ./tests/black-box-smoke.sh
```

The compatibility flag now runs the complete successful migration, snapshot
deletion, forced-failure restore, restart, and public-API flow; there is no
longer a pre-backup implementation guard. This path uses the same digest-pinned
PostgreSQL 16.6 image and requires exact `server_version_num = 160006` after
startup. Every exit path removes the exact revalidated labeled container with
`docker rm --force --volumes` and verifies its captured anonymous volume set is
absent. The reusable C# fixture
above is the preferred path for new database tests and supports an external
administrator connection. The package smoke removes its labeled container and
retains only redacted logs and evidence with owner-only modes.
