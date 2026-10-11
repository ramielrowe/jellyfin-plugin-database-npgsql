#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C

image=${1:-jellyfin-npgsql-linuxserver:test}
postgres_image='postgres:16.6@sha256:557fea37a744d5f4c8faab304b0a90858b53ab119735a88c131fd19dab802f36'
installation_directory='Npgsql Database_0.1.3.0'
password='jellyfin-smoke-password'
suffix="${GITHUB_RUN_ID:-local}-$$"
network="jellyfin-npgsql-smoke-$suffix"
volume="jellyfin-npgsql-smoke-$suffix"
postgres="jellyfin-npgsql-postgres-$suffix"
app="jellyfin-npgsql-app-$suffix"
workspace=$(mktemp -d)

cleanup() {
    docker rm -f "$app" "$postgres" >/dev/null 2>&1 || true
    docker volume rm "$volume" >/dev/null 2>&1 || true
    docker network rm "$network" >/dev/null 2>&1 || true
    rm -rf -- "$workspace"
}
trap cleanup EXIT

for command in docker curl cmp sort sed; do
    command -v "$command" >/dev/null || {
        printf 'smoke-test: required command is unavailable: %s\n' "$command" >&2
        exit 1
    }
done

docker image inspect "$image" >/dev/null
[[ $(docker image inspect --format '{{.Architecture}}' "$image") == amd64 ]] || {
    printf 'smoke-test: image must be linux/amd64\n' >&2
    exit 1
}

docker network create "$network" >/dev/null
docker volume create "$volume" >/dev/null
mkdir -p "$workspace/media"
chmod 0755 "$workspace" "$workspace/media"

cat > "$workspace/database.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<DatabaseConfigurationOptions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <DatabaseType>PLUGIN_PROVIDER</DatabaseType>
  <CustomProviderOptions>
    <PluginName>${installation_directory}</PluginName>
    <PluginAssembly>Jellyfin.Plugin.Database.Npgsql.dll</PluginAssembly>
    <ConnectionString>Host=db;Port=5432;Database=jellyfin;Username=jellyfin;Password=${password}</ConnectionString>
    <Options />
  </CustomProviderOptions>
  <LockingBehavior>NoLock</LockingBehavior>
</DatabaseConfigurationOptions>
EOF
chmod 0644 "$workspace/database.xml"

docker run -d --name "$postgres" --network "$network" --network-alias db \
    -e POSTGRES_DB=jellyfin -e POSTGRES_USER=jellyfin -e "POSTGRES_PASSWORD=$password" \
    "$postgres_image" >/dev/null

for _ in $(seq 1 60); do
    if docker exec "$postgres" pg_isready -U jellyfin -d jellyfin >/dev/null 2>&1; then
        break
    fi
    sleep 1
done
docker exec "$postgres" pg_isready -U jellyfin -d jellyfin >/dev/null

docker run --rm --entrypoint /bin/bash \
    -v "$volume:/config" -v "$workspace/database.xml:/input/database.xml:ro" \
    "$image" -euo pipefail -c \
    'install -m 0644 -- /input/database.xml /config/database.xml'

start_app() {
    docker run -d --name "$app" --network "$network" \
        -e "PUID=$(id -u)" -e "PGID=$(id -g)" -e TZ=Etc/UTC \
        -v "$volume:/config" -v "$workspace/media:/media:ro" \
        -p 127.0.0.1::8096 "$image" >/dev/null
}

wait_for_app() {
    local port response
    port=$(docker port "$app" 8096/tcp | sed -n 's/^.*://p')
    [[ -n "$port" ]] || return 1
    for _ in $(seq 1 240); do
        if [[ $(docker inspect --format '{{.State.Running}}' "$app") != true ]]; then
            docker logs "$app" >&2 || true
            return 1
        fi
        response=$(curl --silent --show-error --fail --max-time 2 "http://127.0.0.1:$port/health" 2>/dev/null || true)
        if [[ "$response" == Healthy ]] \
            && docker exec "$app" test -d "/config/data/plugins/$installation_directory"; then
            return 0
        fi
        sleep 1
    done
    docker logs "$app" >&2 || true
    return 1
}

verify_plugin() {
    docker exec "$app" /bin/bash -euo pipefail -c '
        export LC_ALL=C
        seed=/usr/local/share/jellyfin-npgsql
        destination="/config/data/plugins/Npgsql Database_0.1.3.0"
        expected=$(mktemp)
        actual=$(mktemp)
        trap '\''rm -f -- "$expected" "$actual"'\'' EXIT
        sort "$seed/package-files.txt" > "$expected"
        find "$destination" -mindepth 1 -maxdepth 1 -type f -printf "%f\\n" | sort > "$actual"
        cmp -s "$expected" "$actual"
        test -z "$(find "$destination" -mindepth 1 -maxdepth 1 ! -type f -print -quit)"
        test "$(stat -c "%a %u:%g" "$destination")" = "750 $(id -u abc):$(id -g abc)"
        while IFS= read -r name; do
            cmp -s -- "$seed/package/$name" "$destination/$name"
            test "$(stat -c "%a %u:%g" "$destination/$name")" = "640 $(id -u abc):$(id -g abc)"
        done < "$expected"
        grep -Fq "<DatabaseType>PLUGIN_PROVIDER</DatabaseType>" /config/database.xml
        grep -Fq "<PluginName>Npgsql Database_0.1.3.0</PluginName>" /config/database.xml
        test -z "$(find /config -type f \( -name "jellyfin.db" -o -name "jellyfin.db-*" -o -name "*.sqlite" -o -name "*.sqlite3" \) -print -quit)"
    '
    # Do not use grep -q here: with pipefail it can close the pipe early and
    # turn a successful Docker log read into SIGPIPE status 141.
    docker logs "$app" 2>&1 | grep -F 'Npgsql database provider' >/dev/null
    [[ $(docker exec "$postgres" psql -U jellyfin -d jellyfin -Atqc \
        "SELECT CASE WHEN to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL AND EXISTS (SELECT 1 FROM pg_proc WHERE proname = '__jellyfin_sqlite_like') AND EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory') THEN 'ok' ELSE 'missing' END") == ok ]]
}

start_app
wait_for_app
verify_plugin

docker rm -f "$app" >/dev/null
start_app
wait_for_app
verify_plugin

printf 'smoke-test: image startup, PostgreSQL persistence, and idempotent restart passed\n'
