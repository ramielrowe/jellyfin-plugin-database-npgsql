#!/usr/bin/env bash
set -euo pipefail

PLUGIN_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
HOST_REPOSITORY=$(cd "$PLUGIN_ROOT/.." && pwd)
SMOKE_ID="${SMOKE_ID:-$(date -u +%Y%m%d%H%M%S)-$$}"
RUN_ROOT="$PLUGIN_ROOT/.artifacts/smoke-$SMOKE_ID"
POSTGRES_CONTAINER="jellyfin-npgsql-smoke-$SMOKE_ID"
POSTGRES_PORT="${POSTGRES_PORT:-55439}"
HTTP_PORT="${HTTP_PORT:-18099}"
HTTPS_PORT=$((HTTP_PORT + 1))
NUGET_PACKAGES="$PLUGIN_ROOT/.packages"
NUGET_HTTP_CACHE_PATH="$PLUGIN_ROOT/.nuget-http"
PLUGIN_PROJECT="$PLUGIN_ROOT/src/Jellyfin.Plugin.Database.Npgsql/Jellyfin.Plugin.Database.Npgsql.csproj"
PLUGIN_FOLDER="$RUN_ROOT/runtime/data/plugins/Npgsql Database_0.0.1"
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

authenticate() {
    local port=$1
    local device_id=$2
    curl --silent --show-error --fail \
        --request POST \
        --header 'Content-Type: application/json' \
        --header "Authorization: MediaBrowser Client=\"npgsql-feasibility\", Device=\"smoke\", DeviceId=\"$device_id\", Version=\"0.0.1\"" \
        --data '{"Username":"feasibility-admin","Pw":"feasibility-password"}' \
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
        --env PGPASSWORD=feasibility \
        "$POSTGRES_CONTAINER" \
        psql --username jellyfin --dbname jellyfin --tuples-only --no-align --command "$1"
}

mkdir -p \
    "$RUN_ROOT/host-source" \
    "$RUN_ROOT/host-publish" \
    "$RUN_ROOT/normal-plugin" \
    "$RUN_ROOT/failure-plugin" \
    "$PLUGIN_FOLDER" \
    "$RUN_ROOT/runtime/config" \
    "$RUN_ROOT/runtime/cache" \
    "$RUN_ROOT/runtime/log"

git -C "$HOST_REPOSITORY" archive v12.2 | tar -x -C "$RUN_ROOT/host-source"
dotnet publish \
    "$RUN_ROOT/host-source/Jellyfin.Server/Jellyfin.Server.csproj" \
    --configuration Release \
    --output "$RUN_ROOT/host-publish" \
    --no-self-contained

dotnet build "$PLUGIN_PROJECT" --configuration Release --output "$RUN_ROOT/normal-plugin"
dotnet build \
    "$PLUGIN_PROJECT" \
    --configuration Release \
    --output "$RUN_ROOT/failure-plugin" \
    -p:FeasibilityFailureMigration=true

cp "$PLUGIN_ROOT/tests/fixtures/meta.json" "$PLUGIN_FOLDER/meta.json"
for file in \
    Jellyfin.Plugin.Database.Npgsql.dll \
    Jellyfin.Plugin.Database.Npgsql.deps.json \
    Npgsql.EntityFrameworkCore.PostgreSQL.dll \
    Npgsql.dll; do
    cp "$RUN_ROOT/normal-plugin/$file" "$PLUGIN_FOLDER/$file"
done

mapfile -t package_dlls < <(find "$PLUGIN_FOLDER" -maxdepth 1 -type f -name '*.dll' -printf '%f\n' | sort)
expected_dlls=(Jellyfin.Plugin.Database.Npgsql.dll Npgsql.EntityFrameworkCore.PostgreSQL.dll Npgsql.dll)
if [ "${package_dlls[*]}" != "${expected_dlls[*]}" ]; then
    fail "package DLL set contains a host assembly: ${package_dlls[*]}"
fi

sed \
    -e "s/55432/$POSTGRES_PORT/g" \
    -e 's/Database=jellyfin_plugin_feasibility/Database=jellyfin/g' \
    -e 's/Username=jellyfin_plugin/Username=jellyfin/g' \
    "$PLUGIN_ROOT/tests/fixtures/database.xml" \
    >"$RUN_ROOT/runtime/config/database.xml"
sed \
    -e "s/18096/$HTTP_PORT/g" \
    -e "s/18920/$HTTPS_PORT/g" \
    "$PLUGIN_ROOT/tests/fixtures/network.xml" \
    >"$RUN_ROOT/runtime/config/network.xml"

docker run \
    --detach \
    --name "$POSTGRES_CONTAINER" \
    --env POSTGRES_USER=jellyfin \
    --env POSTGRES_PASSWORD=feasibility \
    --env POSTGRES_DB=jellyfin \
    --publish "127.0.0.1:$POSTGRES_PORT:5432" \
    postgres:18 >/dev/null

for _ in $(seq 1 60); do
    if docker exec "$POSTGRES_CONTAINER" pg_isready --username jellyfin --dbname jellyfin >/dev/null 2>&1; then
        break
    fi

    sleep 1
done
docker exec "$POSTGRES_CONTAINER" pg_isready --username jellyfin --dbname jellyfin >/dev/null

start_host "$RUN_ROOT/first-start.log"
wait_for_health "$HTTP_PORT" || fail "vanilla Jellyfin 12.2 did not become healthy"
grep -q 'Npgsql feasibility provider loaded.*in Default' "$RUN_ROOT/first-start.log" \
    || fail "provider was not loaded in the default load context"
grep -q 'Skipping disabled plugin.*Npgsql Database' "$RUN_ROOT/first-start.log" \
    || fail "normal plugin loading did not honor the disabled manifest"
grep -q 'Deleted PostgreSQL migration snapshot' "$RUN_ROOT/first-start.log" \
    || fail "successful migration did not exercise DeleteBackup"

curl --silent --show-error --fail "http://127.0.0.1:$HTTP_PORT/Startup/User" >/dev/null
curl --silent --show-error --fail \
    --request POST \
    --header 'Content-Type: application/json' \
    --data '{"Name":"feasibility-admin","Password":"feasibility-password"}' \
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

start_host "$RUN_ROOT/restart.log"
wait_for_health "$HTTP_PORT" || fail "vanilla Jellyfin did not restart"
RESTART_TOKEN=$(authenticate "$HTTP_PORT" "npgsql-restart")
[ -n "$RESTART_TOKEN" ] || fail "persisted user could not authenticate after restart"
stop_host "$HTTP_PORT" "$RESTART_TOKEN" || fail "restarted host did not stop cleanly"

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

cp "$RUN_ROOT/normal-plugin/Jellyfin.Plugin.Database.Npgsql.dll" "$PLUGIN_FOLDER/Jellyfin.Plugin.Database.Npgsql.dll"
start_host "$RUN_ROOT/recovery.log"
wait_for_health "$HTTP_PORT" || fail "normal package did not recover after the forced failure"
RECOVERY_TOKEN=$(authenticate "$HTTP_PORT" "npgsql-recovery")
[ -n "$RECOVERY_TOKEN" ] || fail "restored user could not authenticate during recovery"
stop_host "$HTTP_PORT" "$RECOVERY_TOKEN" || fail "recovery host did not stop cleanly"

{
    echo "result=PASS"
    echo "host_tag=v12.2"
    echo "host_commit=$(git -C "$HOST_REPOSITORY" rev-list -n 1 v12.2)"
    echo "host_version=$(dotnet "$RUN_ROOT/host-publish/jellyfin.dll" --version 2>&1 | head -n 1)"
    echo "plugin_database_reference=12.2.0"
    echo "dotnet_sdk=$(dotnet --version)"
    echo "ef_core=10.0.11"
    echo "npgsql_ef=10.0.3"
    echo "postgres=$(psql_value 'SHOW server_version')"
    echo "host_pg_dump=$(command -v pg_dump || echo absent)"
    echo "host_psql=$(command -v psql || echo absent)"
    echo "late_model_BaseItems_Type_length=511"
    echo "foreign_keys=$FOREIGN_KEY_COUNT"
    echo "package_dlls=${package_dlls[*]}"
} | tee "$RUN_ROOT/evidence.txt"

echo "Black-box smoke test passed. Evidence: $RUN_ROOT/evidence.txt"
