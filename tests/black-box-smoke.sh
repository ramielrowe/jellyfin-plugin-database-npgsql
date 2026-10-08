#!/usr/bin/env bash
set -euo pipefail

PLUGIN_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
if [ -n "${JELLYFIN_HOST_REPOSITORY:-}" ]; then
    [ -d "$JELLYFIN_HOST_REPOSITORY" ] \
        || { echo "FAIL: JELLYFIN_HOST_REPOSITORY is not a directory: $JELLYFIN_HOST_REPOSITORY" >&2; exit 1; }
    HOST_REPOSITORY=$(cd "$JELLYFIN_HOST_REPOSITORY" && pwd)
else
    HOST_REPOSITORY=$(cd "$PLUGIN_ROOT/.." && pwd)
fi
SMOKE_ID="${SMOKE_ID:-$(date -u +%Y%m%d%H%M%S)-$$}"
RUN_ROOT="$PLUGIN_ROOT/.artifacts/smoke-$SMOKE_ID"
POSTGRES_CONTAINER="jellyfin-npgsql-smoke-$SMOKE_ID"
read -r AUTOMATIC_POSTGRES_PORT AUTOMATIC_HTTP_PORT AUTOMATIC_HTTPS_PORT < <(
    python3 -c 'import socket
sockets = [socket.socket() for _ in range(3)]
for item in sockets: item.bind(("127.0.0.1", 0))
print(*(item.getsockname()[1] for item in sockets))'
)
POSTGRES_PORT="${POSTGRES_PORT:-$AUTOMATIC_POSTGRES_PORT}"
HTTP_PORT="${HTTP_PORT:-$AUTOMATIC_HTTP_PORT}"
HTTPS_PORT="${HTTPS_PORT:-$AUTOMATIC_HTTPS_PORT}"
JELLYFIN_HOST_COMMIT="${JELLYFIN_HOST_COMMIT:-ca0f16eb7c195f720a1493ed469a27c4657db0c6}"
UNSUPPORTED_HOST_COMMIT="${UNSUPPORTED_HOST_COMMIT:-284997ebe28678ebca971f9f64fcfe02976ed937}"
POSTGRES_IMAGE="${POSTGRES_IMAGE:-postgres@sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336}"
NUGET_PACKAGES="$PLUGIN_ROOT/.packages"
NUGET_HTTP_CACHE_PATH="$PLUGIN_ROOT/.nuget-http"
PLUGIN_PROJECT="$PLUGIN_ROOT/src/Jellyfin.Plugin.Database.Npgsql/Jellyfin.Plugin.Database.Npgsql.csproj"
PACKAGE_ZIP="$PLUGIN_ROOT/artifacts/jellyfin-plugin-database-npgsql_0.1.0.0.zip"
PLUGIN_FOLDER_NAME="Npgsql Database_0.1.0.0"
PLUGIN_FOLDER="$RUN_ROOT/runtime/data/plugins/$PLUGIN_FOLDER_NAME"
AMBIGUOUS_PLUGIN_FOLDER="$RUN_ROOT/runtime/data/plugins/$PLUGIN_FOLDER_NAME-ambiguous"
OLD_PLUGIN_FOLDER="$RUN_ROOT/runtime/data/plugins/Npgsql Database_0.0.9.0"
BOOTSTRAP_PASSWORD=$(tr -d '-' </proc/sys/kernel/random/uuid)
APPLICATION_PASSWORD=$(tr -d '-' </proc/sys/kernel/random/uuid)
ADMIN_PASSWORD=$(tr -d '-' </proc/sys/kernel/random/uuid)
HOST_PID=""

export NUGET_PACKAGES NUGET_HTTP_CACHE_PATH

cleanup() {
    if [ -n "$HOST_PID" ] && kill -0 "$HOST_PID" 2>/dev/null; then
        kill -TERM "$HOST_PID" 2>/dev/null || true
        for _ in $(seq 1 10); do
            if ! kill -0 "$HOST_PID" 2>/dev/null; then
                break
            fi

            sleep 1
        done
        kill -KILL "$HOST_PID" 2>/dev/null || true
        wait "$HOST_PID" 2>/dev/null || true
    fi

    docker rm --force "$POSTGRES_CONTAINER" >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

if [ ! -f "$HOST_REPOSITORY/Jellyfin.Server/Jellyfin.Server.csproj" ] \
    || ! git -C "$HOST_REPOSITORY" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    fail "Jellyfin host checkout not found at $HOST_REPOSITORY; set JELLYFIN_HOST_REPOSITORY to a Jellyfin Git checkout"
fi

JELLYFIN_HOST_COMMIT=$(
    git -C "$HOST_REPOSITORY" rev-parse --verify --end-of-options "$JELLYFIN_HOST_COMMIT^{commit}"
) || fail "could not resolve Jellyfin host revision to a commit"
readonly JELLYFIN_HOST_COMMIT
UNSUPPORTED_HOST_COMMIT=$(
    git -C "$HOST_REPOSITORY" rev-parse --verify --end-of-options "$UNSUPPORTED_HOST_COMMIT^{commit}"
) || fail "could not resolve the unsupported-host Jellyfin revision to a commit"
readonly UNSUPPORTED_HOST_COMMIT

wait_for_health() {
    local port=$1
    for _ in $(seq 1 120); do
        if curl --silent --fail "http://127.0.0.1:$port/health" 2>/dev/null | grep -qx 'Healthy'; then
            return 0
        fi

        if ! kill -0 "$HOST_PID" 2>/dev/null; then
            return 1
        fi

        sleep 1
    done

    return 1
}

start_host() {
    local log_file=$1
    dotnet "$RUN_ROOT/host-publish/jellyfin.dll" \
        --datadir "$RUN_ROOT/runtime/data" \
        --configdir "$RUN_ROOT/runtime/config" \
        --cachedir "$RUN_ROOT/runtime/cache" \
        --logdir "$RUN_ROOT/runtime/log" \
        --nowebclient \
        --nonetchange \
        --ffmpeg "$PLUGIN_ROOT/tests/fixtures/fake-ffmpeg.sh" \
        >"$log_file" 2>&1 &
    HOST_PID=$!
}

start_unsupported_host() {
    local log_file=$1
    dotnet "$RUN_ROOT/unsupported-host-publish/jellyfin.dll" \
        --datadir "$RUN_ROOT/runtime/data" \
        --configdir "$RUN_ROOT/runtime/config" \
        --cachedir "$RUN_ROOT/runtime/cache" \
        --logdir "$RUN_ROOT/runtime/log" \
        --nowebclient \
        --nonetchange \
        --ffmpeg "$PLUGIN_ROOT/tests/fixtures/fake-ffmpeg.sh" \
        >"$log_file" 2>&1 &
    HOST_PID=$!
}

assert_loopback_listener() {
    local port=$1
    local listener
    listener=$(ss -H -ltn "sport = :$port" | awk '{print $4}')
    [ "$listener" = "127.0.0.1:$port" ] \
        || fail "Jellyfin listener was not restricted to IPv4 loopback: ${listener:-absent}"
}

assert_log_origin() {
    local log_file=$1
    local role=$2
    local expected_path=$3
    grep -F "Npgsql provider $role:" "$log_file" \
        | grep -F " from $expected_path in load context Default" >/dev/null \
        || fail "$role assembly did not load from $expected_path in the default load context"
}

wait_for_host_exit() {
    for _ in $(seq 1 60); do
        if ! kill -0 "$HOST_PID" 2>/dev/null; then
            wait "$HOST_PID" 2>/dev/null || true
            HOST_PID=""
            return 0
        fi

        sleep 1
    done

    return 1
}

authenticate() {
    local port=$1
    local device_id=$2
    curl --silent --show-error --fail \
        --request POST \
        --header 'Content-Type: application/json' \
        --header "Authorization: MediaBrowser Client=\"npgsql-feasibility\", Device=\"smoke\", DeviceId=\"$device_id\", Version=\"0.0.1\"" \
        --data "{\"Username\":\"feasibility-admin\",\"Pw\":\"$ADMIN_PASSWORD\"}" \
        "http://127.0.0.1:$port/Users/AuthenticateByName" \
        | sed -n 's/.*"AccessToken":"\([^"]*\)".*/\1/p'
}

stop_host() {
    local port=$1
    local token=$2
    curl --silent --show-error --fail \
        --request POST \
        --header "Authorization: MediaBrowser Token=\"$token\"" \
        "http://127.0.0.1:$port/System/Shutdown" >/dev/null

    for _ in $(seq 1 30); do
        if ! kill -0 "$HOST_PID" 2>/dev/null; then
            wait "$HOST_PID" 2>/dev/null || true
            HOST_PID=""
            return 0
        fi

        sleep 1
    done

    return 1
}

psql_value() {
    docker exec \
        --env PGPASSWORD="$APPLICATION_PASSWORD" \
        "$POSTGRES_CONTAINER" \
        psql --username jellyfin --dbname jellyfin --tuples-only --no-align --command "$1"
}

psql_exec() {
    docker exec \
        --env PGPASSWORD="$APPLICATION_PASSWORD" \
        "$POSTGRES_CONTAINER" \
        psql --username jellyfin --dbname jellyfin --set ON_ERROR_STOP=1 --command "$1" >/dev/null
}

identity_sequence_state() {
    local table_name=$1
    local sequence_name
    sequence_name=$(psql_value "SELECT pg_get_serial_sequence('\"$table_name\"', 'Id')")
    [ -n "$sequence_name" ] || fail "could not resolve identity sequence for $table_name"
    psql_value "SELECT last_value::text || '|' || is_called::text FROM $sequence_name"
}

mkdir -p \
    "$RUN_ROOT/host-source" \
    "$RUN_ROOT/host-publish" \
    "$RUN_ROOT/unsupported-host-source" \
    "$RUN_ROOT/unsupported-host-publish" \
    "$RUN_ROOT/failure-plugin" \
    "$PLUGIN_FOLDER" \
    "$RUN_ROOT/runtime/config" \
    "$RUN_ROOT/runtime/cache" \
    "$RUN_ROOT/runtime/log"

git -C "$HOST_REPOSITORY" archive "$JELLYFIN_HOST_COMMIT" | tar -x -C "$RUN_ROOT/host-source"
git -C "$HOST_REPOSITORY" archive "$UNSUPPORTED_HOST_COMMIT" | tar -x -C "$RUN_ROOT/unsupported-host-source"
dotnet publish \
    "$RUN_ROOT/host-source/Jellyfin.Server/Jellyfin.Server.csproj" \
    --configuration Release \
    --output "$RUN_ROOT/host-publish" \
    --no-self-contained
dotnet publish \
    "$RUN_ROOT/unsupported-host-source/Jellyfin.Server/Jellyfin.Server.csproj" \
    --configuration Release \
    --output "$RUN_ROOT/unsupported-host-publish" \
    --no-self-contained

dotnet msbuild "$PLUGIN_ROOT/build/Package.proj" -target:Package -property:Configuration=Release
[ -f "$PACKAGE_ZIP" ] || fail "release package was not created at $PACKAGE_ZIP"
python3 -c 'import pathlib, sys, zipfile
destination = pathlib.Path(sys.argv[2]).resolve()
with zipfile.ZipFile(sys.argv[1]) as archive:
    for entry in archive.infolist():
        target = (destination / entry.filename).resolve()
        if destination not in target.parents and target != destination:
            raise SystemExit(f"unsafe ZIP path: {entry.filename}")
    archive.extractall(destination)' "$PACKAGE_ZIP" "$PLUGIN_FOLDER"
dotnet build \
    "$PLUGIN_PROJECT" \
    --configuration Release \
    --output "$RUN_ROOT/failure-plugin" \
    -p:PackagingFailureMigration=true

mkdir -p "$OLD_PLUGIN_FOLDER" "$AMBIGUOUS_PLUGIN_FOLDER"
cp "$PLUGIN_ROOT/meta.json" "$OLD_PLUGIN_FOLDER/meta.json"
cp "$PLUGIN_ROOT/meta.json" "$AMBIGUOUS_PLUGIN_FOLDER/meta.json"
cp "$PLUGIN_FOLDER/Jellyfin.Plugin.Database.Npgsql.dll" "$AMBIGUOUS_PLUGIN_FOLDER/Jellyfin.Plugin.Database.Npgsql.dll"
cp "$PLUGIN_FOLDER/Jellyfin.Plugin.Database.Npgsql.deps.json" "$AMBIGUOUS_PLUGIN_FOLDER/Jellyfin.Plugin.Database.Npgsql.deps.json"
cp "$PLUGIN_FOLDER/Npgsql.EntityFrameworkCore.PostgreSQL.dll" "$AMBIGUOUS_PLUGIN_FOLDER/Npgsql.EntityFrameworkCore.PostgreSQL.dll"
cp "$PLUGIN_FOLDER/Npgsql.dll" "$AMBIGUOUS_PLUGIN_FOLDER/Npgsql.dll"

mapfile -t package_dlls < <(find "$PLUGIN_FOLDER" -maxdepth 1 -type f -name '*.dll' -printf '%f\n' | sort)
expected_dlls=(Jellyfin.Plugin.Database.Npgsql.dll Npgsql.EntityFrameworkCore.PostgreSQL.dll Npgsql.dll)
if [ "${package_dlls[*]}" != "${expected_dlls[*]}" ]; then
    fail "package DLL set contains a host assembly: ${package_dlls[*]}"
fi

sed \
    -e "s/__POSTGRES_PORT__/$POSTGRES_PORT/g" \
    -e "s/__APPLICATION_PASSWORD__/$APPLICATION_PASSWORD/g" \
    "$PLUGIN_ROOT/tests/fixtures/database.xml" \
    >"$RUN_ROOT/runtime/config/database.xml"
sed \
    -e "s/18096/$HTTP_PORT/g" \
    -e "s/18920/$HTTPS_PORT/g" \
    "$PLUGIN_ROOT/tests/fixtures/network.xml" \
    >"$RUN_ROOT/runtime/config/network.xml"

# These two failures occur before PostgreSQL is started. They prove that an
# incomplete ZIP and Jellyfin's prefix-selected ambiguous directory are both
# rejected before any database connection can be attempted, and that restoring
# the canonical package is sufficient recovery.
mv "$PLUGIN_FOLDER/Npgsql.dll" "$RUN_ROOT/Npgsql.dll.missing-test"
start_host "$RUN_ROOT/missing-dependency.log"
wait_for_host_exit || fail "host did not stop after the missing dependency was detected"
grep -F "Required Npgsql dependency 'Npgsql.dll' is missing or incompatible" "$RUN_ROOT/missing-dependency.log" >/dev/null \
    || fail "missing dependency failure did not provide the recovery diagnostic"
grep -F "No database connection was attempted" "$RUN_ROOT/missing-dependency.log" >/dev/null \
    || fail "missing dependency failure did not state its pre-connection boundary"
mv "$RUN_ROOT/Npgsql.dll.missing-test" "$PLUGIN_FOLDER/Npgsql.dll"

mv "$PLUGIN_FOLDER" "$RUN_ROOT/canonical-package-hold"
start_host "$RUN_ROOT/ambiguous-folder.log"
wait_for_host_exit || fail "host did not stop after the ambiguous folder was selected"
grep -F "requires the exact folder and PluginName '$PLUGIN_FOLDER_NAME'" "$RUN_ROOT/ambiguous-folder.log" >/dev/null \
    || fail "ambiguous prefix selection did not provide the canonical-folder recovery diagnostic"
grep -F "No database connection was attempted" "$RUN_ROOT/ambiguous-folder.log" >/dev/null \
    || fail "ambiguous prefix selection did not state its pre-connection boundary"
mv "$RUN_ROOT/canonical-package-hold" "$PLUGIN_FOLDER"

start_unsupported_host "$RUN_ROOT/unsupported-abi.log"
wait_for_host_exit || fail "unsupported vanilla Jellyfin host did not stop after the ABI guard"
grep -F "supports Jellyfin database ABI 12.2.x; the host loaded 13.0.0.0" "$RUN_ROOT/unsupported-abi.log" >/dev/null \
    || fail "unsupported host ABI failure did not identify the supported and loaded versions"
grep -F "No database connection was attempted" "$RUN_ROOT/unsupported-abi.log" >/dev/null \
    || fail "unsupported host ABI failure did not state its pre-connection boundary"

docker run \
    --detach \
    --name "$POSTGRES_CONTAINER" \
    --env POSTGRES_USER=postgres \
    --env POSTGRES_PASSWORD="$BOOTSTRAP_PASSWORD" \
    --env POSTGRES_DB=postgres \
    --publish "127.0.0.1:$POSTGRES_PORT:5432" \
    "$POSTGRES_IMAGE" >/dev/null

for _ in $(seq 1 60); do
    if docker exec "$POSTGRES_CONTAINER" pg_isready --username postgres --dbname postgres >/dev/null 2>&1; then
        break
    fi

    sleep 1
done
docker exec "$POSTGRES_CONTAINER" pg_isready --username postgres --dbname postgres >/dev/null
docker exec \
    --env PGPASSWORD="$BOOTSTRAP_PASSWORD" \
    "$POSTGRES_CONTAINER" \
    psql --username postgres --dbname postgres --set ON_ERROR_STOP=1 \
    --command "CREATE ROLE jellyfin LOGIN PASSWORD '$APPLICATION_PASSWORD' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS" \
    --command 'CREATE DATABASE jellyfin OWNER jellyfin' >/dev/null

[ "$(psql_value "SELECT rolcanlogin::text || '|' || rolsuper::text || '|' || rolcreatedb::text || '|' || rolcreaterole::text || '|' || rolreplication::text || '|' || rolbypassrls::text FROM pg_catalog.pg_roles WHERE rolname = current_user")" = "true|false|false|false|false|false" ] \
    || fail "Jellyfin runtime role has unexpected cluster privileges"
[ "$(psql_value "SELECT pg_catalog.pg_get_userbyid(datdba) FROM pg_catalog.pg_database WHERE datname = current_database()")" = "jellyfin" ] \
    || fail "Jellyfin runtime role does not own its database"
if docker exec --env PGPASSWORD="$APPLICATION_PASSWORD" "$POSTGRES_CONTAINER" \
    psql --username jellyfin --dbname jellyfin --command 'CREATE DATABASE jellyfin_privilege_probe' >/dev/null 2>&1; then
    fail "Jellyfin runtime role unexpectedly created a database"
fi
if docker exec --env PGPASSWORD="$APPLICATION_PASSWORD" "$POSTGRES_CONTAINER" \
    psql --username jellyfin --dbname jellyfin --command 'CREATE ROLE jellyfin_privilege_probe' >/dev/null 2>&1; then
    fail "Jellyfin runtime role unexpectedly created a role"
fi

start_host "$RUN_ROOT/first-start.log"
wait_for_health "$HTTP_PORT" || fail "vanilla Jellyfin 12.2 did not become healthy"
assert_loopback_listener "$HTTP_PORT"
grep -q 'Npgsql database provider 0.1.0.0 loaded.*in Default' "$RUN_ROOT/first-start.log" \
    || fail "provider was not loaded in the default load context"
assert_log_origin "$RUN_ROOT/first-start.log" "provider" "$PLUGIN_FOLDER/Jellyfin.Plugin.Database.Npgsql.dll"
assert_log_origin "$RUN_ROOT/first-start.log" "Npgsql EF" "$PLUGIN_FOLDER/Npgsql.EntityFrameworkCore.PostgreSQL.dll"
assert_log_origin "$RUN_ROOT/first-start.log" "Npgsql" "$PLUGIN_FOLDER/Npgsql.dll"
assert_log_origin "$RUN_ROOT/first-start.log" "host database" "$RUN_ROOT/host-publish/Jellyfin.Database.Implementations.dll"
assert_log_origin "$RUN_ROOT/first-start.log" "EF Core" "$RUN_ROOT/host-publish/Microsoft.EntityFrameworkCore.dll"
grep -q 'Skipping disabled plugin.*Npgsql Database' "$RUN_ROOT/first-start.log" \
    || fail "normal plugin loading did not honor the disabled manifest"
grep -q 'Deleted PostgreSQL migration snapshot' "$RUN_ROOT/first-start.log" \
    || fail "successful migration did not exercise DeleteBackup"

curl --silent --show-error --fail "http://127.0.0.1:$HTTP_PORT/Startup/User" >/dev/null
curl --silent --show-error --fail \
    --request POST \
    --header 'Content-Type: application/json' \
    --data "{\"Name\":\"feasibility-admin\",\"Password\":\"$ADMIN_PASSWORD\"}" \
    "http://127.0.0.1:$HTTP_PORT/Startup/User" >/dev/null
curl --silent --show-error --fail --request POST "http://127.0.0.1:$HTTP_PORT/Startup/Complete" >/dev/null

FIRST_TOKEN=$(authenticate "$HTTP_PORT" "npgsql-first")
[ -n "$FIRST_TOKEN" ] || fail "could not authenticate the user created through Jellyfin"
stop_host "$HTTP_PORT" "$FIRST_TOKEN" || fail "first host did not stop cleanly"

[ "$(psql_value 'SELECT "Username" FROM "Users"')" = "feasibility-admin" ] \
    || fail "Jellyfin CRUD did not persist the administrator"
[ "$(psql_value "SELECT character_maximum_length FROM information_schema.columns WHERE table_schema='public' AND table_name='BaseItems' AND column_name='Type'")" = "511" ] \
    || fail "late model override was not present in PostgreSQL"
FOREIGN_KEY_COUNT=$(psql_value "SELECT COUNT(*) FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class r ON r.oid=c.conrelid JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace WHERE n.nspname='public' AND c.contype='f'")
[ "$FOREIGN_KEY_COUNT" -gt 0 ] || fail "baseline contains no foreign keys"

sed 's/"status": "Disabled"/"status": "Active"/' "$PLUGIN_ROOT/meta.json" >"$PLUGIN_FOLDER/meta.json"
start_host "$RUN_ROOT/active-scan.log"
wait_for_health "$HTTP_PORT" || fail "vanilla Jellyfin did not survive the later active plugin scan"
grep -F 'Registering whitelisted assemblies for plugin "Npgsql Database"' "$RUN_ROOT/active-scan.log" >/dev/null \
    || fail "normal plugin loading did not exercise the provider assembly allowlist"
grep -F "Loaded assembly Jellyfin.Plugin.Database.Npgsql" "$RUN_ROOT/active-scan.log" \
    | grep -F " from $PLUGIN_FOLDER/Jellyfin.Plugin.Database.Npgsql.dll" >/dev/null \
    || fail "normal PluginLoadContext scan did not report the provider's canonical assembly origin"
if grep -E 'Failed to load assembly .*Jellyfin.Plugin.Database.Npgsql|Error creating .*Npgsql' "$RUN_ROOT/active-scan.log" >/dev/null; then
    fail "normal PluginLoadContext scanning reported a type or context failure"
fi
RESTART_TOKEN=$(authenticate "$HTTP_PORT" "npgsql-active-scan")
[ -n "$RESTART_TOKEN" ] || fail "persisted user could not authenticate after restart"
stop_host "$HTTP_PORT" "$RESTART_TOKEN" || fail "active-scan host did not stop cleanly"

printf '{ malformed plugin metadata\n' >"$PLUGIN_FOLDER/meta.json"
start_host "$RUN_ROOT/malformed-metadata.log"
wait_for_health "$HTTP_PORT" || fail "early provider did not survive malformed ordinary-plugin metadata"
grep -F 'Error deserializing' "$RUN_ROOT/malformed-metadata.log" >/dev/null \
    || fail "normal plugin scan did not report malformed metadata"
MALFORMED_TOKEN=$(authenticate "$HTTP_PORT" "npgsql-malformed-metadata")
[ -n "$MALFORMED_TOKEN" ] || fail "persisted user could not authenticate after malformed metadata recovery"
stop_host "$HTTP_PORT" "$MALFORMED_TOKEN" || fail "malformed-metadata host did not stop cleanly"
cp "$PLUGIN_ROOT/meta.json" "$PLUGIN_FOLDER/meta.json"

psql_exec '
    CREATE TABLE "SequenceAdvancedProbe" ("Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY);
    INSERT INTO "SequenceAdvancedProbe" DEFAULT VALUES;
    SELECT setval(pg_get_serial_sequence('"'"'"SequenceAdvancedProbe"'"'"', '"'"'Id'"'"'), 900, true);
    CREATE TABLE "SequenceDeletedProbe" ("Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY);
    INSERT INTO "SequenceDeletedProbe" DEFAULT VALUES;
    DELETE FROM "SequenceDeletedProbe";
    CREATE TABLE "SequenceEmptyProbe" ("Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY);
    CREATE TABLE "SequenceFalseProbe" ("Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY);
    INSERT INTO "SequenceFalseProbe" ("Id") OVERRIDING SYSTEM VALUE VALUES (200);
    SELECT setval(pg_get_serial_sequence('"'"'"SequenceFalseProbe"'"'"', '"'"'Id'"'"'), 37, false);
'
ADVANCED_SEQUENCE_STATE=$(identity_sequence_state "SequenceAdvancedProbe")
DELETED_SEQUENCE_STATE=$(identity_sequence_state "SequenceDeletedProbe")
EMPTY_SEQUENCE_STATE=$(identity_sequence_state "SequenceEmptyProbe")
FALSE_SEQUENCE_STATE=$(identity_sequence_state "SequenceFalseProbe")
[ "$ADVANCED_SEQUENCE_STATE" = "900|true" ] || fail "advanced sequence fixture was not established"
[ "$DELETED_SEQUENCE_STATE" = "1|true" ] || fail "deleted-row sequence fixture was not established"
[ "$EMPTY_SEQUENCE_STATE" = "1|false" ] || fail "empty sequence fixture was not established"
[ "$FALSE_SEQUENCE_STATE" = "37|false" ] || fail "false-state sequence fixture was not established"

cp "$RUN_ROOT/failure-plugin/Jellyfin.Plugin.Database.Npgsql.dll" "$PLUGIN_FOLDER/Jellyfin.Plugin.Database.Npgsql.dll"
start_host "$RUN_ROOT/forced-failure.log"
for _ in $(seq 1 60); do
    if grep -q 'Restored PostgreSQL migration snapshot' "$RUN_ROOT/forced-failure.log"; then
        break
    fi

    sleep 1
done
grep -q 'ForcedNonTransactionalFailure' "$RUN_ROOT/forced-failure.log" \
    || fail "forced non-transactional migration was not attempted"
grep -q 'Restored PostgreSQL migration snapshot' "$RUN_ROOT/forced-failure.log" \
    || fail "RestoreBackupFast was not completed"
kill -TERM "$HOST_PID" 2>/dev/null || true
for _ in $(seq 1 10); do
    if ! kill -0 "$HOST_PID" 2>/dev/null; then
        break
    fi

    sleep 1
done
kill -KILL "$HOST_PID" 2>/dev/null || true
wait "$HOST_PID" 2>/dev/null || true
HOST_PID=""

[ "$(psql_value "SELECT to_regclass('\"FeasibilityFailureMarker\"') IS NULL")" = "t" ] \
    || fail "partial migration table survived restore"
[ "$(psql_value 'SELECT "Username" FROM "Users"')" = "feasibility-admin" ] \
    || fail "user data was not restored"
[ "$(psql_value "SELECT COUNT(*) FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class r ON r.oid=c.conrelid JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace WHERE n.nspname='public' AND c.contype='f'")" = "$FOREIGN_KEY_COUNT" ] \
    || fail "foreign keys were not restored"
[ "$(psql_value "SELECT COUNT(*) FROM pg_catalog.pg_namespace WHERE nspname LIKE 'jellyfin_migration_backup_%'")" = "0" ] \
    || fail "restore left a backup schema behind"
[ "$(psql_value "SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20261008160000_ForcedNonTransactionalFailure'")" = "0" ] \
    || fail "failed migration was recorded as applied"
[ "$(identity_sequence_state "SequenceAdvancedProbe")" = "$ADVANCED_SEQUENCE_STATE" ] \
    || fail "restore changed an identity sequence advanced beyond its table maximum"
[ "$(identity_sequence_state "SequenceDeletedProbe")" = "$DELETED_SEQUENCE_STATE" ] \
    || fail "restore changed the sequence state for an empty table whose generated row was deleted"
[ "$(identity_sequence_state "SequenceEmptyProbe")" = "$EMPTY_SEQUENCE_STATE" ] \
    || fail "restore changed a never-called empty identity sequence"
[ "$(identity_sequence_state "SequenceFalseProbe")" = "$FALSE_SEQUENCE_STATE" ] \
    || fail "restore changed an explicitly advanced identity sequence with is_called=false"

cp "$PLUGIN_ROOT/artifacts/package/Jellyfin.Plugin.Database.Npgsql.dll" "$PLUGIN_FOLDER/Jellyfin.Plugin.Database.Npgsql.dll"
start_host "$RUN_ROOT/recovery.log"
wait_for_health "$HTTP_PORT" || fail "normal package did not recover after the forced failure"
RECOVERY_TOKEN=$(authenticate "$HTTP_PORT" "npgsql-recovery")
[ -n "$RECOVERY_TOKEN" ] || fail "restored user could not authenticate during recovery"
stop_host "$HTTP_PORT" "$RECOVERY_TOKEN" || fail "recovery host did not stop cleanly"

{
    echo "result=PASS"
    echo "host_commit=$JELLYFIN_HOST_COMMIT"
    echo "unsupported_host_commit=$UNSUPPORTED_HOST_COMMIT"
    echo "host_version=$(dotnet "$RUN_ROOT/host-publish/jellyfin.dll" --version 2>&1 | head -n 1)"
    echo "plugin_database_reference=12.2.0"
    echo "dotnet_sdk=$(dotnet --version)"
    echo "ef_core=10.0.11"
    echo "npgsql_ef=10.0.3"
    echo "postgres_image=$POSTGRES_IMAGE"
    echo "postgres_image_id=$(docker image inspect "$POSTGRES_IMAGE" --format '{{.Id}}')"
    echo "postgres=$(psql_value 'SHOW server_version')"
    echo "host_pg_dump=$(command -v pg_dump || echo absent)"
    echo "host_psql=$(command -v psql || echo absent)"
    echo "late_model_BaseItems_Type_length=511"
    echo "foreign_keys=$FOREIGN_KEY_COUNT"
    echo "runtime_role_flags=LOGIN,NOSUPERUSER,NOCREATEDB,NOCREATEROLE,NOREPLICATION,NOBYPASSRLS"
    echo "jellyfin_listener=127.0.0.1:$HTTP_PORT"
    echo "advanced_sequence_state=$ADVANCED_SEQUENCE_STATE"
    echo "deleted_sequence_state=$DELETED_SEQUENCE_STATE"
    echo "empty_sequence_state=$EMPTY_SEQUENCE_STATE"
    echo "false_sequence_state=$FALSE_SEQUENCE_STATE"
    echo "package_dlls=${package_dlls[*]}"
    echo "canonical_plugin_folder=$PLUGIN_FOLDER_NAME"
    echo "early_failure_guards=missing-dependency,ambiguous-prefix,unsupported-abi"
    echo "normal_plugin_scans=disabled,active,malformed-metadata"
} | tee "$RUN_ROOT/evidence.txt"

echo "Black-box smoke test passed. Evidence: $RUN_ROOT/evidence.txt"
