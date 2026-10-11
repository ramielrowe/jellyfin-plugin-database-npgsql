#!/usr/bin/env bash
set -euo pipefail

export LC_ALL=C

image=${1:-jellyfin-npgsql-seed:test}
linuxserver_image='lscr.io/linuxserver/jellyfin:12.2ubu2604-ls53@sha256:1bb4f88d822a0510bb3604b68aacb80d77c1d2a30181e500090c65111b98723b'
postgres_image='postgres:16.6@sha256:557fea37a744d5f4c8faab304b0a90858b53ab119735a88c131fd19dab802f36'
installation_directory='Npgsql Database_0.1.3.0'
password='jellyfin-seed-smoke-password'
suffix="${GITHUB_RUN_ID:-local}-$$"
prefix="jellyfin-npgsql-seed-$suffix"
network="$prefix-network"
postgres="$prefix-postgres"
app="$prefix-app"
workspace=$(mktemp -d)
volumes=()

cleanup() {
    docker rm -f "$app" "$postgres" >/dev/null 2>&1 || true
    local volume
    for volume in "${volumes[@]}"; do
        docker volume rm "$volume" >/dev/null 2>&1 || true
    done
    docker network rm "$network" >/dev/null 2>&1 || true
    rm -rf -- "$workspace"
}
trap cleanup EXIT

for command in cmp curl docker grep sed sort; do
    command -v "$command" >/dev/null || {
        printf 'kubernetes-smoke: required command is unavailable: %s\n' "$command" >&2
        exit 1
    }
done

docker image inspect "$image" >/dev/null
[[ $(docker image inspect --format '{{.Architecture}}' "$image") == amd64 ]]
[[ $(docker image inspect --format '{{.Config.User}}' "$image") == 1000:1000 ]]
entrypoint=$(docker image inspect --format '{{json .Config.Entrypoint}}' "$image")
[[ "$entrypoint" == '["/usr/local/bin/install-jellyfin-npgsql","/usr/local/share/jellyfin-npgsql","/config/data/plugins"]' ]]

new_volume() {
    created_volume="$prefix-$1"
    docker volume create "$created_volume" >/dev/null
    volumes+=("$created_volume")
}

prepare_volume() {
    local volume=$1 uid=${2:-1000} gid=${3:-1000}
    docker run --rm --user 0:0 --entrypoint /bin/bash -v "$volume:/config" "$image" \
        -euo pipefail -c 'chown "$1:$2" /config; chmod 0750 /config' -- "$uid" "$gid"
}

run_seed() {
    local volume=$1 uid=${2:-1000} gid=${3:-1000}
    docker run --rm --read-only --user "$uid:$gid" \
        --cap-drop ALL --security-opt no-new-privileges:true \
        --tmpfs /tmp:rw,noexec,nosuid,nodev,size=32m \
        -v "$volume:/config" "$image"
}

root_in_volume() {
    local volume=$1 script=$2
    shift 2
    docker run --rm --user 0:0 --entrypoint /bin/bash -v "$volume:/config" "$image" \
        -euo pipefail -c "$script" -- "$@"
}

snapshot_volume() {
    local volume=$1
    root_in_volume "$volume" '
        find /config -xdev -printf "%P|%y|%m|%U:%G|%l\n" | sort
        find /config -xdev -type f -exec sha256sum -- {} + | sort
    '
}

assert_conflict_unchanged() {
    local volume=$1 label=$2 before after
    before=$(snapshot_volume "$volume")
    if run_seed "$volume" >/dev/null 2>"$workspace/$label.err"; then
        printf 'kubernetes-smoke: conflict unexpectedly succeeded: %s\n' "$label" >&2
        exit 1
    fi
    after=$(snapshot_volume "$volume")
    [[ "$before" == "$after" ]] || {
        printf 'kubernetes-smoke: failed conflict mutated volume: %s\n' "$label" >&2
        exit 1
    }
}

assert_exact_plugin() {
    local volume=$1 uid=${2:-1000} gid=${3:-1000}
    root_in_volume "$volume" '
        destination="/config/data/plugins/Npgsql Database_0.1.3.0"
        test "$(stat -c "%a %u:%g" "$destination")" = "750 $1:$2"
        find "$destination" -mindepth 1 -maxdepth 1 -type f -printf "%f\n" | sort >/tmp/actual
        sort /usr/local/share/jellyfin-npgsql/package-files.txt >/tmp/expected
        cmp -s /tmp/expected /tmp/actual
        test -z "$(find "$destination" -mindepth 1 -maxdepth 1 ! -type f -print -quit)"
        while IFS= read -r name; do
            cmp -s -- "/usr/local/share/jellyfin-npgsql/package/$name" "$destination/$name"
            test "$(stat -c "%a %u:%g" "$destination/$name")" = "640 $1:$2"
        done </tmp/expected
    ' "$uid" "$gid"
}

# Empty and idempotent/reused volume, with only /tmp and the PVC writable.
new_volume base
base_volume=$created_volume
prepare_volume "$base_volume"
run_seed "$base_volume"
assert_exact_plugin "$base_volume"
root_in_volume "$base_volume" 'printf reused >/config/reused.marker; chown 1000:1000 /config/reused.marker'
run_seed "$base_volume"
assert_exact_plugin "$base_volume"
root_in_volume "$base_volume" 'test "$(cat /config/reused.marker)" = reused'

# Ownerless operation works for a numeric UID that has no passwd entry.
new_volume numeric
numeric_volume=$created_volume
prepare_volume "$numeric_volume" 12345 12345
docker run --rm --read-only --user 12345:12345 \
    --cap-drop ALL --security-opt no-new-privileges:true \
    --tmpfs /tmp:rw,noexec,nosuid,nodev,size=32m -v "$numeric_volume:/config" \
    --entrypoint /bin/bash "$image" -euo pipefail -c '
        ! grep -Eq "^[^:]*:[^:]*:12345:" /etc/passwd
        exec /usr/local/bin/install-jellyfin-npgsql /usr/local/share/jellyfin-npgsql /config/data/plugins
    '
assert_exact_plugin "$numeric_volume" 12345 12345

# The optional owner path supports numeric identities without passwd/group
# entries and sets final metadata before publication.
new_volume owner
owner_volume=$created_volume
prepare_volume "$owner_volume" 0 0
docker run --rm --read-only --user 0:0 \
    --tmpfs /tmp:rw,noexec,nosuid,nodev,size=32m -v "$owner_volume:/config" \
    --entrypoint /usr/local/bin/install-jellyfin-npgsql "$image" \
    /usr/local/share/jellyfin-npgsql /config/data/plugins 12346:12346
assert_exact_plugin "$owner_volume" 12346 12346

make_conflict_volume() {
    local label=$1
    new_volume "conflict-$label"
    conflict_volume=$created_volume
    prepare_volume "$conflict_volume"
    run_seed "$conflict_volume" >/dev/null
}

make_conflict_volume content
conflict=$conflict_volume
root_in_volume "$conflict" 'printf corrupt >>"/config/data/plugins/Npgsql Database_0.1.3.0/Npgsql.dll"'
assert_conflict_unchanged "$conflict" content

make_conflict_volume missing
conflict=$conflict_volume
root_in_volume "$conflict" 'rm -- "/config/data/plugins/Npgsql Database_0.1.3.0/Npgsql.dll"'
assert_conflict_unchanged "$conflict" missing

make_conflict_volume extra
conflict=$conflict_volume
root_in_volume "$conflict" 'touch "/config/data/plugins/Npgsql Database_0.1.3.0/extra.dll"; chown 1000:1000 "/config/data/plugins/Npgsql Database_0.1.3.0/extra.dll"'
assert_conflict_unchanged "$conflict" extra

make_conflict_volume nonregular
conflict=$conflict_volume
root_in_volume "$conflict" 'mkdir "/config/data/plugins/Npgsql Database_0.1.3.0/untrusted-directory"; chown 1000:1000 "/config/data/plugins/Npgsql Database_0.1.3.0/untrusted-directory"'
assert_conflict_unchanged "$conflict" nonregular

make_conflict_volume symlink
conflict=$conflict_volume
root_in_volume "$conflict" 'ln -s Npgsql.dll "/config/data/plugins/Npgsql Database_0.1.3.0/linked.dll"'
assert_conflict_unchanged "$conflict" symlink

make_conflict_volume mode
conflict=$conflict_volume
root_in_volume "$conflict" 'chmod 0660 "/config/data/plugins/Npgsql Database_0.1.3.0/Npgsql.dll"'
assert_conflict_unchanged "$conflict" mode

make_conflict_volume owner
conflict=$conflict_volume
root_in_volume "$conflict" 'chown 0:0 "/config/data/plugins/Npgsql Database_0.1.3.0/Npgsql.dll"'
assert_conflict_unchanged "$conflict" owner

new_volume destination-symlink
conflict=$created_volume
prepare_volume "$conflict"
root_in_volume "$conflict" 'mkdir -p /config/data/plugins; chown -R 1000:1000 /config/data; ln -s /tmp "/config/data/plugins/Npgsql Database_0.1.3.0"'
assert_conflict_unchanged "$conflict" destination-symlink

# Corrupt seed names and symlink components fail before touching the PVC.
new_volume unsafe-seed
unsafe_seed=$created_volume
root_in_volume "$unsafe_seed" 'cp -a /usr/local/share/jellyfin-npgsql/. /config/; printf "../escape\n" >/config/package-files.txt'
new_volume unsafe-config
unsafe_config=$created_volume
prepare_volume "$unsafe_config"
before=$(snapshot_volume "$unsafe_config")
if docker run --rm --read-only --user 1000:1000 --tmpfs /tmp:rw,noexec,nosuid,nodev,size=32m \
    --cap-drop ALL --security-opt no-new-privileges:true \
    -v "$unsafe_seed:/seed:ro" -v "$unsafe_config:/config" \
    --entrypoint /usr/local/bin/install-jellyfin-npgsql "$image" \
    /seed /config/data/plugins >/dev/null 2>"$workspace/unsafe-seed.err"; then
    printf 'kubernetes-smoke: unsafe seed name unexpectedly succeeded\n' >&2
    exit 1
fi
[[ "$before" == "$(snapshot_volume "$unsafe_config")" ]]

new_volume component-symlink
symlink_config=$created_volume
prepare_volume "$symlink_config"
root_in_volume "$symlink_config" 'ln -s /tmp /config/data'
assert_conflict_unchanged "$symlink_config" component-symlink

# Full stock LinuxServer + PostgreSQL 16.6 qualification and restart.
new_volume system
system_volume=$created_volume
prepare_volume "$system_volume"
docker network create "$network" >/dev/null
mkdir -p "$workspace/media"
chmod 0755 "$workspace" "$workspace/media"

docker run -d --name "$postgres" --network "$network" --network-alias db \
    -e POSTGRES_DB=jellyfin -e POSTGRES_USER=jellyfin -e "POSTGRES_PASSWORD=$password" \
    "$postgres_image" >/dev/null
for _ in $(seq 1 60); do
    docker exec "$postgres" pg_isready -U jellyfin -d jellyfin >/dev/null 2>&1 && break
    sleep 1
done
docker exec "$postgres" pg_isready -U jellyfin -d jellyfin >/dev/null

root_in_volume "$system_volume" '
    cat > /config/database.xml <<EOF
<?xml version="1.0" encoding="utf-8"?>
<DatabaseConfigurationOptions>
  <DatabaseType>PLUGIN_PROVIDER</DatabaseType>
  <CustomProviderOptions>
    <PluginName>Npgsql Database_0.1.3.0</PluginName>
    <PluginAssembly>Jellyfin.Plugin.Database.Npgsql.dll</PluginAssembly>
    <ConnectionString>Host=db;Port=5432;Database=jellyfin;Username=jellyfin;Password=jellyfin-seed-smoke-password</ConnectionString>
    <Options />
  </CustomProviderOptions>
  <LockingBehavior>NoLock</LockingBehavior>
</DatabaseConfigurationOptions>
EOF
    chown 1000:1000 /config/database.xml
    chmod 0644 /config/database.xml
'
run_seed "$system_volume" >/dev/null

start_app() {
    docker run -d --name "$app" --network "$network" \
        --security-opt no-new-privileges:true \
        -e PUID=1000 -e PGID=1000 -e TZ=Etc/UTC \
        -v "$system_volume:/config" -v "$workspace/media:/media:ro" \
        -p 127.0.0.1::8096 "$linuxserver_image" >/dev/null
}

wait_for_app() {
    local port response
    port=$(docker port "$app" 8096/tcp | sed -n 's/^.*://p')
    [[ -n "$port" ]]
    for _ in $(seq 1 240); do
        if [[ $(docker inspect --format '{{.State.Running}}' "$app") != true ]]; then
            docker logs "$app" >&2 || true
            return 1
        fi
        response=$(curl --silent --show-error --fail --max-time 2 "http://127.0.0.1:$port/health" 2>/dev/null || true)
        [[ "$response" == Healthy ]] && return 0
        sleep 1
    done
    docker logs "$app" >&2 || true
    return 1
}

verify_system() {
    assert_exact_plugin "$system_volume"
    docker exec "$app" /bin/bash -euo pipefail -c '
        grep -Fq "<DatabaseType>PLUGIN_PROVIDER</DatabaseType>" /config/database.xml
        test -z "$(find /config -type f \( -name "jellyfin.db" -o -name "jellyfin.db-*" -o -name "*.sqlite" -o -name "*.sqlite3" \) -print -quit)"
    '
    docker logs "$app" 2>&1 | grep -F 'Npgsql database provider' >/dev/null
    [[ $(docker exec "$postgres" psql -U jellyfin -d jellyfin -Atqc \
        "SELECT CASE WHEN to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL AND EXISTS (SELECT 1 FROM pg_proc WHERE proname = '__jellyfin_sqlite_like') AND EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory') THEN 'ok' ELSE 'missing' END") == ok ]]
}

start_app
wait_for_app
verify_system
docker rm -f "$app" >/dev/null
run_seed "$system_volume" >/dev/null
start_app
wait_for_app
verify_system

printf 'kubernetes-smoke: installer conflict matrix and stock Jellyfin PostgreSQL restart passed\n'
