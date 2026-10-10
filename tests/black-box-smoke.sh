#!/usr/bin/env bash
set -euo pipefail
umask 077

PLUGIN_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
# shellcheck source=process-tree-cleanup.sh
source "$PLUGIN_ROOT/tests/process-tree-cleanup.sh"

docker() {
    command timeout --foreground 65s docker "$@"
}

curl() {
    command timeout --foreground 15s curl --connect-timeout 2 --max-time 10 --proto '=http' --max-redirs 0 "$@"
}

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

validate_port() {
    local name=$1
    local value=$2
    if [[ ! "$value" =~ ^[0-9]{1,5}$ ]] || (( 10#$value < 1 || 10#$value > 65535 )); then
        echo "FAIL: $name must be a decimal TCP port from 1 through 65535" >&2
        return 1
    fi
}

reap_stale_smoke_containers() {
    local listing listing_status
    set +e
    listing=$(docker ps --all --no-trunc \
        --filter 'label=org.jellyfin.database-npgsql.smoke-managed=true' \
        --filter "label=org.jellyfin.database-npgsql.smoke-scope=$SMOKE_SCOPE" \
        --format '{{.ID}}|{{.Names}}|{{.Label "org.jellyfin.database-npgsql.smoke-managed"}}|{{.Label "org.jellyfin.database-npgsql.smoke-scope"}}|{{.Label "org.jellyfin.database-npgsql.owner-lease"}}|{{.Label "org.jellyfin.database-npgsql.owner-pid"}}|{{.Label "org.jellyfin.database-npgsql.owner-start"}}' 2>&1)
    listing_status=$?
    set -e
    [ "$listing_status" -eq 0 ] \
        || fail "could not enumerate scoped test containers before reclamation: $listing"

    local -a stale_ids=() stale_names=() stale_leases=() stale_pids=() stale_starts=()
    local -A seen_ids=() seen_names=() seen_leases=()
    local line stale_id stale_name managed scope stale_lease stale_pid stale_start extra
    while IFS= read -r line; do
        [ -n "$line" ] || continue
        IFS='|' read -r stale_id stale_name managed scope stale_lease stale_pid stale_start extra <<<"$line"
        if [ -n "${extra:-}" ] \
            || [[ ! "$stale_id" =~ ^[0-9a-f]{64}$ ]] \
            || [[ ! "$stale_name" =~ ^jellyfin-npgsql-smoke-[0-9]{14}-[0-9]+-[0-9a-f]{32}$ ]] \
            || [ "$managed" != true ] \
            || [ "$scope" != "$SMOKE_SCOPE" ] \
            || [[ ! "$stale_lease" =~ ^[0-9]{14}-[0-9]+-[0-9a-f]{32}$ ]] \
            || [ "$stale_name" != "jellyfin-npgsql-smoke-$stale_lease" ] \
            || [[ ! "$stale_pid" =~ ^[1-9][0-9]*$ ]] \
            || [[ ! "$stale_start" =~ ^[1-9][0-9]*$ ]]; then
            fail "managed container in scope '$SMOKE_SCOPE' has malformed ID, name, or ownership labels ('$line'); no scoped container was removed"
        fi
        if [ -n "${seen_ids[$stale_id]:-}" ] || [ -n "${seen_names[$stale_name]:-}" ] || [ -n "${seen_leases[$stale_lease]:-}" ]; then
            fail "scoped container discovery returned a duplicate immutable ID, name, or lease ('$line'); no scoped container was removed"
        fi
        seen_ids[$stale_id]=1
        seen_names[$stale_name]=1
        seen_leases[$stale_lease]=1
        stale_ids+=("$stale_id")
        stale_names+=("$stale_name")
        stale_leases+=("$stale_lease")
        stale_pids+=("$stale_pid")
        stale_starts+=("$stale_start")
    done <<<"$listing"

    local index stale_lease_fd inspected inspect_status removal removal_status expected
    for index in "${!stale_ids[@]}"; do
        stale_id=${stale_ids[$index]}
        stale_name=${stale_names[$index]}
        stale_lease=${stale_leases[$index]}
        stale_pid=${stale_pids[$index]}
        stale_start=${stale_starts[$index]}
        exec {stale_lease_fd}>"$CONTAINER_LEASE_ROOT/$stale_lease.lock"
        if ! flock -n "$stale_lease_fd"; then
            eval "exec ${stale_lease_fd}>&-"
            continue
        fi

        set +e
        inspected=$(docker inspect --format '{{.Id}}|{{.Name}}|{{index .Config.Labels "org.jellyfin.database-npgsql.smoke-managed"}}|{{index .Config.Labels "org.jellyfin.database-npgsql.smoke-scope"}}|{{index .Config.Labels "org.jellyfin.database-npgsql.owner-lease"}}|{{index .Config.Labels "org.jellyfin.database-npgsql.owner-pid"}}|{{index .Config.Labels "org.jellyfin.database-npgsql.owner-start"}}' "$stale_id" 2>&1)
        inspect_status=$?
        set -e
        if [ "$inspect_status" -ne 0 ]; then
            eval "exec ${stale_lease_fd}>&-"
            if [[ "$inspected" == *"No such object:"* ]] || [[ "$inspected" == *"No such container:"* ]]; then
                continue
            fi
            fail "could not revalidate exact stale container ID '$stale_id': $inspected"
        fi
        expected="$stale_id|/$stale_name|true|$SMOKE_SCOPE|$stale_lease|$stale_pid|$stale_start"
        if [ "$inspected" != "$expected" ]; then
            eval "exec ${stale_lease_fd}>&-"
            fail "exact stale container ID '$stale_id' changed name or ownership labels; no removal was attempted for it"
        fi

        set +e
        removal=$(docker rm --force --volumes "$stale_id" 2>&1)
        removal_status=$?
        set -e
        eval "exec ${stale_lease_fd}>&-"
        if [ "$removal_status" -ne 0 ] \
            && [[ "$removal" != *"No such container:"* ]] \
            && [[ "$removal" != *"No such object:"* ]]; then
            fail "could not remove exact stale test container ID '$stale_id': $removal"
        fi
    done
}

if [ "${1:-}" = "--test-container-reaper-worker" ]; then
    [ "$#" -eq 2 ] || { echo "usage: $0 --test-container-reaper-worker SCENARIO" >&2; exit 2; }
    SMOKE_SCOPE=${FAKE_DOCKER_SCOPE:?}
    CONTAINER_LEASE_ROOT=${FAKE_DOCKER_LEASE_ROOT:?}
    mkdir -p -m 700 "$CONTAINER_LEASE_ROOT"
    exec {worker_scope_fd}>"${FAKE_DOCKER_LOCK:?}"
    flock "$worker_scope_fd"
    reap_stale_smoke_containers
    exit 0
fi

if [ "${1:-}" = "--test-container-reaper" ]; then
    test_root=$(mktemp -d)
    trap 'rm -rf "$test_root"' EXIT
    export PATH="$PLUGIN_ROOT/tests/fixtures:$PATH"
    export FAKE_DOCKER_SCOPE
    FAKE_DOCKER_SCOPE=$(printf 'e%.0s' $(seq 1 64))
    export FAKE_DOCKER_LEASE_ROOT="$test_root/leases"
    export FAKE_DOCKER_LOCK="$test_root/scope.lock"
    export FAKE_DOCKER_STATE="$test_root/state"
    export FAKE_DOCKER_LOG="$test_root/removals"
    for rejected in malformed duplicate decoy; do
        : >"$FAKE_DOCKER_STATE"
        : >"$FAKE_DOCKER_LOG"
        export FAKE_DOCKER_SCENARIO=$rejected
        if "$0" --test-container-reaper-worker "$rejected" >/dev/null 2>&1; then
            echo "FAIL: $rejected scoped metadata was accepted" >&2
            exit 1
        fi
        [ ! -s "$FAKE_DOCKER_LOG" ] || { echo "FAIL: $rejected discovery mutated a container" >&2; exit 1; }
    done
    for accepted in disappeared; do
        : >"$FAKE_DOCKER_STATE"
        : >"$FAKE_DOCKER_LOG"
        export FAKE_DOCKER_SCENARIO=$accepted
        "$0" --test-container-reaper-worker "$accepted"
        [ ! -s "$FAKE_DOCKER_LOG" ] || { echo "FAIL: an already-disappeared ID was removed" >&2; exit 1; }
    done
    : >"$FAKE_DOCKER_STATE"
    : >"$FAKE_DOCKER_LOG"
    export FAKE_DOCKER_SCENARIO=two-process
    "$0" --test-container-reaper-worker two-process & first_worker=$!
    "$0" --test-container-reaper-worker two-process & second_worker=$!
    wait "$first_worker"
    wait "$second_worker"
    [ "$(wc -l <"$FAKE_DOCKER_LOG")" -eq 1 ] \
        || { echo "FAIL: two-process reclamation did not serialize to one exact removal" >&2; exit 1; }
    [ "$(cat "$FAKE_DOCKER_LOG")" = "rm|--force|--volumes|$(printf 'a%.0s' $(seq 1 64))" ] \
        || { echo "FAIL: scoped reclamation did not request exact container-and-volume removal" >&2; exit 1; }
    exit 0
fi

if [ "${1:-}" = "--validate-port" ]; then
    [ "$#" -eq 3 ] || { echo "usage: $0 --validate-port NAME VALUE" >&2; exit 2; }
    validate_port "$2" "$3"
    exit
fi

if [ "${1:-}" = "--test-detached-cleanup" ]; then
    test_root=$(mktemp -d)
    pid_file="$test_root/detached.pid"
    identity_file="$test_root/identity"
    log_file="$test_root/output"
    secrets_file="$test_root/secrets"
    printf 'detached-secret\0' >"$secrets_file"
    chmod 600 "$secrets_file"
    python3 "$PLUGIN_ROOT/tests/process-supervisor.py" \
        --identity-file "$identity_file" \
        --log-file "$log_file" \
        --secrets-file "$secrets_file" \
        --grace-seconds 0.25 \
        -- sh -c "(sh -c \"setsid sh -c 'trap \\\"\\\" TERM; echo \\\$\\\$ > \\\"$pid_file\\\"; printf detached-; sleep 0.05; printf secret-inherited-output; sleep 30' & exit 0\" &) ; exit 0" &
    supervisor_pid=$!
    for _ in $(seq 1 100); do
        [ -s "$pid_file" ] && [ -s "$identity_file" ] && break
        sleep 0.01
    done
    [ -s "$pid_file" ] && [ -s "$identity_file" ] || { echo "FAIL: subreaper regression did not initialize" >&2; exit 1; }
    read -r _version recorded_watchdog recorded_start recorded_supervisor recorded_supervisor_start recorded_supervisor_group _host_pid _host_start _host_group <"$identity_file"
    [ "$recorded_watchdog" = "$supervisor_pid" ] || { echo "FAIL: watchdog PID handoff mismatch" >&2; exit 1; }
    actual_start=$(awk '{print $22}' "/proc/$supervisor_pid/stat")
    [ "$actual_start" = "$recorded_start" ] || { echo "FAIL: supervisor start identity mismatch" >&2; exit 1; }
    if process_start_matches "$supervisor_pid" "$((recorded_start + 1))"; then
        echo "FAIL: PID-reuse guard accepted the wrong start identity" >&2
        exit 1
    fi
    supervisor_group=$(awk '{print $5}' "/proc/$supervisor_pid/stat")
    if pidfd_signal_verified "$supervisor_pid" "$supervisor_group" "$((recorded_start + 1))" 15; then
        echo "FAIL: pidfd signal guard accepted a wrong/reused process identity" >&2
        exit 1
    fi
    process_identity_matches "$supervisor_pid" "$supervisor_group" "$recorded_start" \
        || { echo "FAIL: wrong-identity probe disturbed the live watchdog" >&2; exit 1; }
    pidfd_signal_verified "$supervisor_pid" "$supervisor_group" "$recorded_start" 15 \
        || { echo "FAIL: could not terminate the containment watchdog through its pidfd" >&2; exit 1; }
    pidfd_wait_verified "$supervisor_pid" "$supervisor_group" "$recorded_start" 10000 \
        || { echo "FAIL: containment watchdog did not stop within ten seconds" >&2; exit 1; }
    wait "$supervisor_pid" 2>/dev/null || true
    detached_pid=$(cat "$pid_file")
    if [ -r "/proc/$detached_pid/stat" ]; then
        echo "FAIL: detached-child cleanup regression left PID $detached_pid alive" >&2
        exit 1
    fi
    grep -q '<redacted>-inherited-output' "$log_file" \
        && ! grep -q 'detached-secret' "$log_file" \
        || { echo "FAIL: inherited output was not continuously drained and redacted" >&2; exit 1; }

    crash_pid_file="$test_root/crash-detached.pid"
    crash_identity_file="$test_root/crash-identity"
    crash_log_file="$test_root/crash-output"
    python3 "$PLUGIN_ROOT/tests/process-supervisor.py" \
        --identity-file "$crash_identity_file" \
        --log-file "$crash_log_file" \
        --grace-seconds 0.25 \
        -- sh -c "setsid sh -c 'trap \"\" TERM; echo \$\$ > \"$crash_pid_file\"; sleep 30' & sleep 30" &
    crash_watchdog=$!
    for _ in $(seq 1 100); do
        [ -s "$crash_pid_file" ] && [ -s "$crash_identity_file" ] && break
        sleep 0.01
    done
    [ -s "$crash_pid_file" ] && [ -s "$crash_identity_file" ] \
        || { echo "FAIL: supervisor-crash regression did not initialize" >&2; exit 1; }
    crash_detached_pid=$(cat "$crash_pid_file")
    crash_detached_start=$(awk '{print $22}' "/proc/$crash_detached_pid/stat") \
        || { echo "FAIL: could not capture detached descendant identity" >&2; exit 1; }
    read -r _version _watchdog _watchdog_start crash_supervisor crash_supervisor_start crash_supervisor_group _target _target_start _target_group <"$crash_identity_file"
    crash_watchdog_group=$(awk '{print $5}' "/proc/$crash_watchdog/stat") \
        || { echo "FAIL: could not capture containment-watchdog group identity" >&2; exit 1; }
    pidfd_signal_verified "$crash_supervisor" "$crash_supervisor_group" "$crash_supervisor_start" 9 \
        || { echo "FAIL: could not inject launch-supervisor SIGKILL through its pidfd" >&2; exit 1; }
    pidfd_wait_verified "$crash_watchdog" "$crash_watchdog_group" "$_watchdog_start" 10000 \
        || { echo "FAIL: containment watchdog did not recover from supervisor SIGKILL within ten seconds" >&2; exit 1; }
    wait "$crash_watchdog" 2>/dev/null || true
    if process_start_matches "$crash_detached_pid" "$crash_detached_start"; then
        echo "FAIL: containment watchdog left a detached descendant after supervisor SIGKILL" >&2
        exit 1
    fi
    rm -rf "$test_root"
    exit 0
fi

if [ "${1:-}" = "--test-supervisor-publication-failure" ]; then
    test_root=$(mktemp -d)
    identity_file="$test_root/identity"
    log_file="$test_root/output"
    error_file="$test_root/errors"
    target_file="$test_root/target-ran"
    secrets_file="$test_root/secrets"
    printf 'publication-secret\0%s\0' "$test_root" >"$secrets_file"
    chmod 600 "$secrets_file"
    set +e
    timeout --foreground 10s python3 "$PLUGIN_ROOT/tests/process-supervisor.py" \
        --identity-file "$identity_file" \
        --log-file "$log_file" \
        --secrets-file "$secrets_file" \
        --grace-seconds 0.25 \
        --test-fail-publication \
        -- sh -c "echo \$\$ > \"$target_file\"; sleep 30" 2>"$error_file"
    publication_status=$?
    set -e
    [ "$publication_status" -eq 125 ] \
        || { echo "FAIL: identity-publication failure did not fail closed with status 125" >&2; exit 1; }
    [ ! -e "$identity_file" ] && [ ! -e "$target_file" ] \
        || { echo "FAIL: publication failure released or published the target" >&2; exit 1; }
    ! grep -F 'publication-secret' "$error_file" >/dev/null \
        && ! grep -F "$test_root" "$error_file" >/dev/null \
        || { echo "FAIL: supervisor failure diagnostics were not redacted" >&2; exit 1; }
    exit 0
fi

HOST_INPUT_MODE=""
HOST_REPOSITORY=""
HOST_PUBLISH=""
UNSUPPORTED_HOST_PUBLISH=""
if [ -n "${JELLYFIN_HOST_PUBLISH:-}" ]; then
    [ -d "$JELLYFIN_HOST_PUBLISH" ] \
        || { echo "FAIL: JELLYFIN_HOST_PUBLISH is not a directory: $JELLYFIN_HOST_PUBLISH" >&2; exit 1; }
    HOST_PUBLISH=$(cd "$JELLYFIN_HOST_PUBLISH" && pwd)
    HOST_INPUT_MODE="publish"
elif [ -n "${JELLYFIN_HOST_REPOSITORY:-}" ]; then
    [ -d "$JELLYFIN_HOST_REPOSITORY" ] \
        || { echo "FAIL: JELLYFIN_HOST_REPOSITORY is not a directory: $JELLYFIN_HOST_REPOSITORY" >&2; exit 1; }
    HOST_REPOSITORY=$(cd "$JELLYFIN_HOST_REPOSITORY" && pwd)
    HOST_INPUT_MODE="legacy-repository"
else
    echo "FAIL: set JELLYFIN_HOST_PUBLISH to an exact vanilla Jellyfin publish; a parent source checkout is not required or inferred" >&2
    exit 1
fi
SMOKE_ID="$(date -u +%Y%m%d%H%M%S)-$$-$(tr -d '-' </proc/sys/kernel/random/uuid)"
SMOKE_SCOPE=$(printf '%s' "$PLUGIN_ROOT" | sha256sum | cut -d' ' -f1)
RUN_ROOT="$PLUGIN_ROOT/.artifacts/smoke-$SMOKE_ID"
POSTGRES_CONTAINER="jellyfin-npgsql-smoke-$SMOKE_ID"
CONTAINER_LEASE_ROOT="$PLUGIN_ROOT/.artifacts/container-leases"
mkdir -p -m 700 "$CONTAINER_LEASE_ROOT"
chmod 700 "$CONTAINER_LEASE_ROOT"
exec {CONTAINER_LEASE_FD}>"$CONTAINER_LEASE_ROOT/$SMOKE_ID.lock"
flock -n "$CONTAINER_LEASE_FD" || { echo "FAIL: could not acquire this smoke run's container lease" >&2; exit 1; }
CONTAINER_SCOPE_LOCK_ROOT="$PLUGIN_ROOT/.artifacts/container-reclaim"
mkdir -p -m 700 "$CONTAINER_SCOPE_LOCK_ROOT"
chmod 700 "$CONTAINER_SCOPE_LOCK_ROOT"
exec {CONTAINER_SCOPE_LOCK_FD}>"$CONTAINER_SCOPE_LOCK_ROOT/$SMOKE_SCOPE.lock"
chmod 600 "$CONTAINER_SCOPE_LOCK_ROOT/$SMOKE_SCOPE.lock"
flock "$CONTAINER_SCOPE_LOCK_FD" || { echo "FAIL: could not acquire the stable scope-wide container lock" >&2; exit 1; }
PORT_LEASE_ROOT="$PLUGIN_ROOT/.artifacts/port-leases"
mkdir -p -m 700 "$PORT_LEASE_ROOT"
chmod 700 "$PORT_LEASE_ROOT"

reserve_port() {
    local output_name=$1
    local output_fd_name=$2
    local requested=${3:-}
    local candidate candidate_fd
    for _ in $(seq 1 100); do
        candidate=${requested:-$(python3 -c 'import socket
with socket.socket() as item:
    item.bind(("127.0.0.1", 0))
    print(item.getsockname()[1])')}
        validate_port "$output_name" "$candidate" || return 1
        exec {candidate_fd}>"$PORT_LEASE_ROOT/$candidate.lock"
        if flock -n "$candidate_fd" \
            && python3 -c 'import socket, sys
with socket.socket() as item:
    item.bind(("127.0.0.1", int(sys.argv[1])))' "$candidate"; then
            printf -v "$output_name" '%s' "$candidate"
            printf -v "$output_fd_name" '%s' "$candidate_fd"
            return 0
        fi

        eval "exec ${candidate_fd}>&-"
        [ -z "$requested" ] || return 1
    done

    echo "FAIL: could not reserve a coordinated loopback port for $output_name" >&2
    return 1
}

reserve_port POSTGRES_PORT POSTGRES_PORT_LEASE_FD "${POSTGRES_PORT:-}"
reserve_port HTTP_PORT HTTP_PORT_LEASE_FD "${HTTP_PORT:-}"
reserve_port HTTPS_PORT HTTPS_PORT_LEASE_FD "${HTTPS_PORT:-}"
[ "$HTTP_PORT" != "$HTTPS_PORT" ] || { echo "FAIL: HTTP_PORT and HTTPS_PORT must be distinct" >&2; exit 1; }
JELLYFIN_HOST_COMMIT="${JELLYFIN_HOST_COMMIT:-ca0f16eb7c195f720a1493ed469a27c4657db0c6}"
UNSUPPORTED_HOST_COMMIT="${UNSUPPORTED_HOST_COMMIT:-284997ebe28678ebca971f9f64fcfe02976ed937}"
POSTGRES_IMAGE="${POSTGRES_IMAGE:-postgres:16.6@sha256:557fea37a744d5f4c8faab304b0a90858b53ab119735a88c131fd19dab802f36}"
if [[ ! "$POSTGRES_IMAGE" =~ ^[^[:space:]]+@sha256:[0-9a-fA-F]{64}$ ]]; then
    echo "FAIL: POSTGRES_IMAGE must be pinned by a complete SHA-256 digest" >&2
    exit 1
fi
NUGET_PACKAGES="$PLUGIN_ROOT/.packages"
NUGET_HTTP_CACHE_PATH="$PLUGIN_ROOT/.nuget-http"
PLUGIN_PROJECT="$PLUGIN_ROOT/src/Jellyfin.Plugin.Database.Npgsql/Jellyfin.Plugin.Database.Npgsql.csproj"
HASH_HELPER_PROJECT="$PLUGIN_ROOT/tests/Jellyfin.Plugin.Database.Npgsql.TestHarness/Jellyfin.Plugin.Database.Npgsql.TestHarness.csproj"
HASH_HELPER="$PLUGIN_ROOT/tests/Jellyfin.Plugin.Database.Npgsql.TestHarness/bin/Release/net10.0/Jellyfin.Plugin.Database.Npgsql.TestHarness.dll"
PACKAGE_ZIP="$PLUGIN_ROOT/artifacts/jellyfin-plugin-database-npgsql_0.1.0.0.zip"
PLUGIN_FOLDER_NAME="Npgsql Database_0.1.0.0"
PLUGIN_FOLDER="$RUN_ROOT/runtime/data/plugins/$PLUGIN_FOLDER_NAME"
AMBIGUOUS_PLUGIN_FOLDER="$RUN_ROOT/runtime/data/plugins/$PLUGIN_FOLDER_NAME-ambiguous"
OLD_PLUGIN_FOLDER="$RUN_ROOT/runtime/data/plugins/Npgsql Database_0.0.9.0"
BOOTSTRAP_PASSWORD=$(tr -d '-' </proc/sys/kernel/random/uuid)
APPLICATION_PASSWORD=$(tr -d '-' </proc/sys/kernel/random/uuid)
ADMIN_PASSWORD=$(tr -d '-' </proc/sys/kernel/random/uuid)
HOST_PID=""
HOST_START=""
HOST_PGID=""
HOST_SUPERVISOR_PID=""
HOST_SUPERVISOR_START=""
HOST_SUPERVISOR_PGID=""
HOST_SUPERVISOR_ERRORS=""
POSTGRES_CONTAINER_ID=""
POSTGRES_VOLUME_IDS=""
OWNER_START=$(awk '{print $22}' "/proc/$$/stat")
export NUGET_PACKAGES NUGET_HTTP_CACHE_PATH
mkdir -p -m 700 "$RUN_ROOT"
chmod 700 "$RUN_ROOT"

cleanup() {
    local original_status=$?
    trap - EXIT
    set +e
    if declare -F terminate_host_group >/dev/null; then
        terminate_host_group || original_status=1
    fi

    if [[ "$POSTGRES_CONTAINER_ID" =~ ^[0-9a-f]{64}$ ]]; then
        local inspected inspect_status
        inspected=$(docker inspect --format '{{.Id}}|{{.Name}}|{{index .Config.Labels "org.jellyfin.database-npgsql.smoke-managed"}}|{{index .Config.Labels "org.jellyfin.database-npgsql.smoke-scope"}}|{{index .Config.Labels "org.jellyfin.database-npgsql.owner-lease"}}|{{index .Config.Labels "org.jellyfin.database-npgsql.owner-pid"}}|{{index .Config.Labels "org.jellyfin.database-npgsql.owner-start"}}' "$POSTGRES_CONTAINER_ID" 2>&1)
        inspect_status=$?
        if [ "$inspect_status" -eq 0 ]; then
            if [ "$inspected" != "$POSTGRES_CONTAINER_ID|/$POSTGRES_CONTAINER|true|$SMOKE_SCOPE|$SMOKE_ID|$$|$OWNER_START" ]; then
                echo "FAIL: container cleanup identity mismatch for exact ID '$POSTGRES_CONTAINER_ID' and expected name '$POSTGRES_CONTAINER'. No removal attempted. Inspect with: docker inspect $POSTGRES_CONTAINER_ID" >&2
                original_status=1
            elif ! docker rm --force --volumes "$POSTGRES_CONTAINER_ID" >/dev/null; then
                echo "FAIL: could not remove exact test container ID '$POSTGRES_CONTAINER_ID' (name '$POSTGRES_CONTAINER') with its anonymous volumes; retry: docker rm --force --volumes $POSTGRES_CONTAINER_ID" >&2
                original_status=1
            else
                local volume_id volume_inspection volume_status
                while IFS= read -r volume_id; do
                    [ -n "$volume_id" ] || continue
                    volume_inspection=$(docker volume inspect "$volume_id" 2>&1)
                    volume_status=$?
                    if [ "$volume_status" -eq 0 ] || [[ "${volume_inspection,,}" != *"no such volume"* ]]; then
                        echo "FAIL: anonymous volume '$volume_id' from exact test container '$POSTGRES_CONTAINER_ID' leaked or could not be verified absent: $volume_inspection" >&2
                        original_status=1
                    fi
                done <<<"$POSTGRES_VOLUME_IDS"
            fi
        elif [[ "$inspected" != *"No such"* ]]; then
            echo "FAIL: could not inspect exact test container ID '$POSTGRES_CONTAINER_ID' (name '$POSTGRES_CONTAINER'): $inspected" >&2
            original_status=1
        fi
    fi
    eval "exec ${CONTAINER_SCOPE_LOCK_FD}>&-"
    eval "exec ${CONTAINER_LEASE_FD}>&-"
    eval "exec ${POSTGRES_PORT_LEASE_FD}>&-"
    eval "exec ${HTTP_PORT_LEASE_FD}>&-"
    eval "exec ${HTTPS_PORT_LEASE_FD}>&-"

    if [ -d "$RUN_ROOT" ]; then
        chmod -R u+rwX "$RUN_ROOT" 2>/dev/null || true
        local retained="$RUN_ROOT.retained"
        rm -rf "$retained"
        mkdir -p -m 700 "$retained"
        local redaction_secrets="$RUN_ROOT/.redaction-secrets"
        printf '%s\n' \
            "$BOOTSTRAP_PASSWORD" "$APPLICATION_PASSWORD" "$ADMIN_PASSWORD" \
            "${SECRET_FILE_MARKER:-}" "${PASSFILE_UNRELATED_SECRET:-}" \
            "${PASSFILE_FIRST_HOST_SECRET:-}" "${PASSFILE_LOWER_PRECEDENCE_SECRET:-}" \
            "${RUNTIME_PASSFILE:-}" "${FIRST_TOKEN:-}" "${RESTART_TOKEN:-}" \
            "${MALFORMED_TOKEN:-}" "${RECOVERY_TOKEN:-}" "$RUN_ROOT" >"$redaction_secrets"
        chmod 600 "$redaction_secrets"
        python3 - "$RUN_ROOT" "$retained" "$redaction_secrets" <<'PY'
import pathlib, sys
source, retained, secret_file = sys.argv[1:]
source, retained = pathlib.Path(source), pathlib.Path(retained)
secrets = sorted((item.encode() for item in pathlib.Path(secret_file).read_text().splitlines() if item), key=len, reverse=True)
private_key = b"PRIVATE KEY-----"
maximum_pattern = max([len(private_key), *(len(item) for item in secrets)])
files = list(source.glob("*.log"))
evidence = source / "evidence.txt"
if evidence.exists():
    files.append(evidence)
unsafe = False
for path in files:
    destination = retained / path.name
    pending = b""
    private_tail = b""
    written = 0
    with path.open("rb") as input_file, destination.open("xb") as output_file:
        while written < 4 * 1024 * 1024:
            chunk = input_file.read(65536)
            final = not chunk
            private_window = private_tail + chunk
            if private_key in private_window:
                unsafe = True
                break
            private_tail = b"" if final else private_window[-(len(private_key) - 1):]
            combined = pending + chunk
            process_limit = len(combined) if final else max(0, len(combined) - maximum_pattern + 1)
            output = bytearray()
            position = 0
            while position < process_limit:
                match = next((secret for secret in secrets if combined.startswith(secret, position)), None)
                if match is None:
                    output.append(combined[position])
                    position += 1
                else:
                    output.extend(b"<redacted>")
                    position += len(match)
            pending = combined[position:]
            if final:
                output.extend(pending)
                pending = b""
            kept = output[:4 * 1024 * 1024 - written]
            output_file.write(kept)
            written += len(kept)
            if final:
                break
    destination.chmod(0o600)
for path in retained.iterdir():
    tails = {pattern: b"" for pattern in [private_key, *secrets]}
    with path.open("rb") as retained_file:
        while chunk := retained_file.read(65536):
            for pattern in tails:
                window = tails[pattern] + chunk
                if pattern in window:
                    unsafe = True
                tails[pattern] = window[-(len(pattern) - 1):] if len(pattern) > 1 else b""
if unsafe:
    for path in retained.iterdir():
        path.unlink()
    raise SystemExit("secret material detected; retained no failure artifacts")
PY
        local scan_status=$?
        rm -rf "$RUN_ROOT"
        mv "$retained" "$RUN_ROOT"
        chmod 700 "$RUN_ROOT"
        find "$RUN_ROOT" -type f -exec chmod 600 '{}' +
        if [ "$scan_status" -ne 0 ]; then
            original_status=1
        fi
    fi

    exit "$original_status"
}
trap cleanup EXIT

dotnet build "$HASH_HELPER_PROJECT" --configuration Release --no-restore --nologo >/dev/null
[ -f "$HASH_HELPER" ] || fail "the prebuilt machine-readable hash helper is missing"

hash_host_publish() {
    local digest diagnostics
    diagnostics=$(mktemp "$RUN_ROOT/hash-errors.XXXXXX")
    digest=$(dotnet "$HASH_HELPER" hash-publish "$1" 2>"$diagnostics") \
        || { sed -n '1,20p' "$diagnostics" >&2; rm -f "$diagnostics"; return 1; }
    rm -f "$diagnostics"
    [[ "$digest" =~ ^[0-9a-f]{64}$ ]] \
        || { echo "FAIL: hash helper produced non-machine-readable output" >&2; return 1; }
    printf '%s\n' "$digest"
}

assert_pristine_host_unchanged() {
    local current_hash
    current_hash=$(hash_host_publish "$RUN_ROOT/host-publish")
    [ "$current_hash" = "$COPIED_HOST_SHA256" ] \
        || fail "the isolated pristine Jellyfin publish changed during the smoke test"
}

if [ "$HOST_INPUT_MODE" = "legacy-repository" ]; then
    if [ ! -f "$HOST_REPOSITORY/Jellyfin.Server/Jellyfin.Server.csproj" ] \
        || ! git -C "$HOST_REPOSITORY" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
        fail "Jellyfin host checkout not found at $HOST_REPOSITORY"
    fi

    JELLYFIN_HOST_COMMIT=$(
        git -C "$HOST_REPOSITORY" rev-parse --verify --end-of-options "$JELLYFIN_HOST_COMMIT^{commit}"
    ) || fail "could not resolve Jellyfin host revision to a commit"
    readonly JELLYFIN_HOST_COMMIT
    UNSUPPORTED_HOST_COMMIT=$(
        git -C "$HOST_REPOSITORY" rev-parse --verify --end-of-options "$UNSUPPORTED_HOST_COMMIT^{commit}"
    ) || fail "could not resolve the unsupported-host Jellyfin revision to a commit"
    readonly UNSUPPORTED_HOST_COMMIT
else
    [ -f "$HOST_PUBLISH/jellyfin.dll" ] \
        || fail "JELLYFIN_HOST_PUBLISH does not contain jellyfin.dll"
    [ -n "${JELLYFIN_HOST_SHA256:-}" ] \
        || fail "JELLYFIN_HOST_SHA256 is required with JELLYFIN_HOST_PUBLISH; calculate it with the documented test-harness command"
    ACTUAL_HOST_SHA256=$(hash_host_publish "$HOST_PUBLISH")
    [ "$ACTUAL_HOST_SHA256" = "$JELLYFIN_HOST_SHA256" ] \
        || fail "JELLYFIN_HOST_PUBLISH did not match JELLYFIN_HOST_SHA256"
    JELLYFIN_HOST_COMMIT="publish-sha256:$ACTUAL_HOST_SHA256"

    if [ -n "${UNSUPPORTED_JELLYFIN_HOST_PUBLISH:-}" ]; then
        [ -d "$UNSUPPORTED_JELLYFIN_HOST_PUBLISH" ] \
            || fail "UNSUPPORTED_JELLYFIN_HOST_PUBLISH is not a directory"
        UNSUPPORTED_HOST_PUBLISH=$(cd "$UNSUPPORTED_JELLYFIN_HOST_PUBLISH" && pwd)
        [ -f "$UNSUPPORTED_HOST_PUBLISH/jellyfin.dll" ] \
            || fail "UNSUPPORTED_JELLYFIN_HOST_PUBLISH does not contain jellyfin.dll"
        [ -n "${UNSUPPORTED_JELLYFIN_HOST_SHA256:-}" ] \
            || fail "UNSUPPORTED_JELLYFIN_HOST_SHA256 is required with UNSUPPORTED_JELLYFIN_HOST_PUBLISH"
        ACTUAL_UNSUPPORTED_HOST_SHA256=$(hash_host_publish "$UNSUPPORTED_HOST_PUBLISH")
        [ "$ACTUAL_UNSUPPORTED_HOST_SHA256" = "$UNSUPPORTED_JELLYFIN_HOST_SHA256" ] \
            || fail "UNSUPPORTED_JELLYFIN_HOST_PUBLISH did not match UNSUPPORTED_JELLYFIN_HOST_SHA256"
        UNSUPPORTED_HOST_COMMIT="publish-sha256:$ACTUAL_UNSUPPORTED_HOST_SHA256"
    else
        UNSUPPORTED_HOST_COMMIT="not-supplied"
    fi
fi

wait_for_health() {
    local port=$1
    for _ in $(seq 1 120); do
        if ss -H -ltnp "sport = :$port" 2>/dev/null | grep -q .; then
            assert_host_owns_listener "$port"
            if curl --silent --fail "http://127.0.0.1:$port/health" 2>/dev/null | grep -qx 'Healthy'; then
                assert_host_owns_listener "$port"
                return 0
            fi
        fi

        if ! kill -0 "$HOST_PID" 2>/dev/null; then
            return 1
        fi

        sleep 1
    done

    return 1
}

assert_host_owns_listener() {
    local port=$1
    assert_host_identity
    local listener_pid listener_pgid
    listener_pid=$(ss -H -ltnp "sport = :$port" 2>/dev/null \
        | sed -n 's/.*127\.0\.0\.1:'"$port"'.*pid=\([0-9][0-9]*\),.*/\1/p' | head -n 1)
    [ -n "$listener_pid" ] || fail "the expected loopback listener is absent"
    listener_pgid=$(awk '{print $5}' "/proc/$listener_pid/stat" 2>/dev/null || true)
    [ "$listener_pgid" = "$HOST_PGID" ] \
        || fail "refusing to control a listener outside the launched Jellyfin process group"
}

assert_host_identity() {
    [ -n "$HOST_PID" ] && kill -0 "$HOST_PID" 2>/dev/null \
        || fail "the launched Jellyfin process is not running"
    local current_start current_pgid
    read -r current_pgid current_start < <(awk '{print $5, $22}' "/proc/$HOST_PID/stat" 2>/dev/null) \
        || fail "could not read the launched Jellyfin process identity"
    [ "$current_start" = "$HOST_START" ] && [ "$current_pgid" = "$HOST_PGID" ] && [ "$HOST_PGID" = "$HOST_PID" ] \
        || fail "the launched Jellyfin PID start time or dedicated process group changed"
}

launch_host_process() {
    local environment_file=$1
    local log_file=$2
    shift 2
    local identity_file="$log_file.identity"
    local secrets_file="$log_file.secrets"
    printf '%s\0' \
        "$BOOTSTRAP_PASSWORD" "$APPLICATION_PASSWORD" "$ADMIN_PASSWORD" \
        "${SECRET_FILE_MARKER:-}" "${PASSFILE_UNRELATED_SECRET:-}" \
        "${PASSFILE_FIRST_HOST_SECRET:-}" "${PASSFILE_LOWER_PRECEDENCE_SECRET:-}" \
        "${RUNTIME_PASSFILE:-}" "${FIRST_TOKEN:-}" "${RESTART_TOKEN:-}" \
        "${MALFORMED_TOKEN:-}" "${RECOVERY_TOKEN:-}" "$RUN_ROOT" >"$secrets_file"
    chmod 600 "$secrets_file"
    python3 "$PLUGIN_ROOT/tests/process-supervisor.py" \
        --identity-file "$identity_file" \
        --log-file "$log_file" \
        --environment-file "$environment_file" \
        --secrets-file "$secrets_file" \
        --working-directory "$RUN_ROOT/host-publish" \
        -- "$@" >/dev/null 2>"$log_file.supervisor-errors" &
    HOST_SUPERVISOR_ERRORS="$log_file.supervisor-errors"
    HOST_SUPERVISOR_PID=$!
    read -r HOST_SUPERVISOR_PGID HOST_SUPERVISOR_START < <(awk '{print $5, $22}' "/proc/$HOST_SUPERVISOR_PID/stat") \
        || fail "could not capture the process-supervisor identity"
    for _ in $(seq 1 500); do
        [ -s "$identity_file" ] && break
        kill -0 "$HOST_SUPERVISOR_PID" 2>/dev/null || break
        sleep 0.01
    done
    [ -s "$identity_file" ] || { sed -n '1,20p' "$log_file.supervisor-errors" >&2; fail "the process supervisor failed closed during initialization"; }
    local version reported_watchdog reported_watchdog_start launch_supervisor launch_supervisor_start launch_supervisor_group
    read -r version reported_watchdog reported_watchdog_start launch_supervisor launch_supervisor_start launch_supervisor_group HOST_PID HOST_START HOST_PGID <"$identity_file" \
        || fail "could not parse the process-supervisor identity handoff"
    [ "$version" = v2 ] \
        && [ "$reported_watchdog" = "$HOST_SUPERVISOR_PID" ] \
        && [ "$reported_watchdog_start" = "$HOST_SUPERVISOR_START" ] \
        && process_identity_matches "$launch_supervisor" "$launch_supervisor_group" "$launch_supervisor_start" \
        && [ "$HOST_PGID" = "$HOST_PID" ] \
        && process_start_matches "$HOST_PID" "$HOST_START" \
        || fail "the process-supervisor identity handoff was malformed or stale"
}

terminate_host_group() {
    [ -n "$HOST_SUPERVISOR_PID" ] || return 0
    local supervisor_status
    if process_identity_matches "$HOST_SUPERVISOR_PID" "$HOST_SUPERVISOR_PGID" "$HOST_SUPERVISOR_START"; then
        pidfd_signal_verified "$HOST_SUPERVISOR_PID" "$HOST_SUPERVISOR_PGID" "$HOST_SUPERVISOR_START" 15 || return 1
    elif kill -0 "$HOST_SUPERVISOR_PID" 2>/dev/null; then
        echo "FAIL: refusing to signal a process supervisor whose PID identity changed" >&2
        return 1
    fi
    if ! pidfd_wait_verified "$HOST_SUPERVISOR_PID" "$HOST_SUPERVISOR_PGID" "$HOST_SUPERVISOR_START" 20000; then
        pidfd_signal_verified "$HOST_SUPERVISOR_PID" "$HOST_SUPERVISOR_PGID" "$HOST_SUPERVISOR_START" 9 || return 1
        pidfd_wait_verified "$HOST_SUPERVISOR_PID" "$HOST_SUPERVISOR_PGID" "$HOST_SUPERVISOR_START" 10000 \
            || { echo "FAIL: the containment watchdog survived bounded TERM/KILL" >&2; return 1; }
    fi
    set +e
    wait "$HOST_SUPERVISOR_PID" 2>/dev/null
    supervisor_status=$?
    set -e
    if [ "$supervisor_status" -eq 125 ]; then
        sed -n '1,20p' "$HOST_SUPERVISOR_ERRORS" >&2
        echo "FAIL: the Linux process supervisor reported an internal failure" >&2
        return 1
    fi
    if process_start_matches "$HOST_PID" "$HOST_START"; then
        echo "FAIL: the verified host process survived supervisor shutdown" >&2
        return 1
    fi
    HOST_PID="" HOST_START="" HOST_PGID=""
    HOST_SUPERVISOR_PID="" HOST_SUPERVISOR_START="" HOST_SUPERVISOR_PGID=""
    HOST_SUPERVISOR_ERRORS=""
}

start_host() {
    local log_file=$1
    shift
    local environment_file="$RUN_ROOT/host-environment-$(basename "$log_file").bin"
    printf '%s\0' "$@" >"$environment_file"
    chmod 600 "$environment_file"
    launch_host_process "$environment_file" "$log_file" dotnet "$RUN_ROOT/host-publish/jellyfin.dll" \
        --datadir "$RUN_ROOT/runtime/data" \
        --configdir "$RUN_ROOT/runtime/config" \
        --cachedir "$RUN_ROOT/runtime/cache" \
        --logdir "$RUN_ROOT/runtime/log" \
        --nowebclient \
        --nonetchange \
        --ffmpeg "$PLUGIN_ROOT/tests/fixtures/fake-ffmpeg.sh"
}

restore_database_configuration() {
    cp "$RUN_ROOT/database.xml.baseline" "$RUN_ROOT/runtime/config/database.xml"
}

set_database_connection_string() {
    local connection_string=$1
    printf '%s' "$connection_string" | python3 -c 'import sys, xml.etree.ElementTree as ET
path = sys.argv[1]
connection_string = sys.stdin.read()
tree = ET.parse(path)
node = tree.find("./CustomProviderOptions/ConnectionString")
if node is None:
    raise SystemExit("database.xml has no ConnectionString element")
node.text = connection_string
tree.write(path, encoding="utf-8", xml_declaration=True)' \
        "$RUN_ROOT/runtime/config/database.xml"
}

assert_log_redacted() {
    local log_file=$1
    shift
    local sensitive
    for sensitive in "$@"; do
        [ -n "$sensitive" ] || continue
        if printf '%s' "$sensitive" | python3 -c 'import pathlib, sys
secret = sys.stdin.buffer.read()
tail = b""
found = False
with pathlib.Path(sys.argv[1]).open("rb") as stream:
    while chunk := stream.read(65536):
        window = tail + chunk
        if secret and secret in window:
            found = True
            break
        tail = window[-(len(secret) - 1):] if len(secret) > 1 else b""
raise SystemExit(0 if found else 1)' "$log_file"; then
            fail "startup log exposed sensitive configuration from $log_file"
        fi
    done
}

assert_environment_rejected() {
    local variable=$1
    local value=$2
    local test_name=$3
    local log_file="$RUN_ROOT/$test_name.log"

    restore_database_configuration
    start_host "$log_file" "$variable=$value"
    wait_for_host_exit || fail "host did not stop after $variable supplied ambient PostgreSQL configuration"
    grep -F "environment variable '$variable' must be unset because database.xml is the only supported configuration source" "$log_file" >/dev/null \
        || fail "$variable was not rejected by the untouched host before it changed database behavior"
    grep -F "No database connection was attempted" "$log_file" >/dev/null \
        || fail "$variable rejection did not state its pre-connection boundary"
    assert_log_redacted "$log_file" "$value" "$APPLICATION_PASSWORD" "$BOOTSTRAP_PASSWORD" "$ADMIN_PASSWORD"
}

assert_secret_file_rejected() {
    local test_name=$1
    local expected_message=$2
    local sensitive_path=$3
    local sensitive_content=$4
    local log_file="$RUN_ROOT/$test_name.log"

    start_host "$log_file"
    wait_for_host_exit || fail "host did not stop after $test_name secret-file validation failed"
    grep -F "$expected_message" "$log_file" >/dev/null \
        || fail "$test_name did not produce the sanitized secret-file diagnostic"
    grep -F "No database connection was attempted" "$log_file" >/dev/null \
        || fail "$test_name did not state its pre-connection boundary"
    assert_log_redacted \
        "$log_file" \
        "$sensitive_path" \
        "$sensitive_content" \
        "$APPLICATION_PASSWORD" \
        "$BOOTSTRAP_PASSWORD" \
        "$ADMIN_PASSWORD"
}

terminate_validation_host() {
    terminate_host_group || fail "validation host process group could not be terminated"
}

assert_secret_configuration_accepted() {
    local test_name=$1
    shift
    local log_file="$RUN_ROOT/$test_name.log"

    start_host "$log_file"
    for _ in $(seq 1 100); do
        if grep -F "PostgreSQL configured at" "$log_file" >/dev/null 2>&1; then
            break
        fi

        if ! kill -0 "$HOST_PID" 2>/dev/null; then
            break
        fi

        sleep 0.1
    done
    grep -F "PostgreSQL configured at" "$log_file" >/dev/null \
        || fail "$test_name did not pass the provider's file and configuration validation"
    if grep -F "provider configuration in database.xml is invalid" "$log_file" >/dev/null; then
        fail "$test_name unexpectedly failed provider configuration validation"
    fi
    assert_log_redacted "$log_file" "$@" "$APPLICATION_PASSWORD" "$BOOTSTRAP_PASSWORD" "$ADMIN_PASSWORD"
    terminate_validation_host
}

start_unsupported_host() {
    local log_file=$1
    local environment_file="$RUN_ROOT/host-environment-unsupported.bin"
    : >"$environment_file"
    chmod 600 "$environment_file"
    launch_host_process "$environment_file" "$log_file" dotnet "$RUN_ROOT/unsupported-host-publish/jellyfin.dll" \
        --datadir "$RUN_ROOT/runtime/data" \
        --configdir "$RUN_ROOT/runtime/config" \
        --cachedir "$RUN_ROOT/runtime/cache" \
        --logdir "$RUN_ROOT/runtime/log" \
        --nowebclient \
        --nonetchange \
        --ffmpeg "$PLUGIN_ROOT/tests/fixtures/fake-ffmpeg.sh"
}

assert_loopback_listener() {
    local port=$1
    local listener
    listener=$(ss -H -ltn "sport = :$port" | awk '{print $4}')
    [ "$listener" = "127.0.0.1:$port" ] \
        || fail "Jellyfin listener was not restricted to IPv4 loopback: ${listener:-absent}"
    assert_host_owns_listener "$port"
}

assert_log_origin() {
    local log_file=$1
    local role=$2
    local expected_path=$3
    [[ "$expected_path" == "$RUN_ROOT/"* ]] \
        || fail "$role expected assembly path is outside the owned run root"
    local redacted_path="<redacted>/${expected_path#"$RUN_ROOT/"}"
    grep -F "Npgsql provider $role:" "$log_file" \
        | grep -F " from $redacted_path in load context Default" >/dev/null \
        || fail "$role assembly did not load from the expected redacted run-root-relative path in the default load context"
}

assert_host_remains_healthy() {
    local port=$1
    local log_file=$2
    for _ in $(seq 1 15); do
        sleep 1
        assert_host_owns_listener "$port"
        curl --silent --show-error --fail "http://127.0.0.1:$port/health" >/dev/null \
            || fail "vanilla Jellyfin stopped reporting healthy after startup"
    done
    if grep -F 'The configured execution strategy' "$log_file" \
        | grep -F 'user-initiated transactions' >/dev/null; then
        fail "vanilla Jellyfin hit the delayed retry-strategy/user-transaction incompatibility"
    fi
}

wait_for_host_exit() {
    for _ in $(seq 1 60); do
        if ! kill -0 "$HOST_PID" 2>/dev/null; then
            terminate_host_group
            return $?
        fi

        sleep 1
    done

    return 1
}

assert_missing_dependency() {
    local dependency=$1
    local test_name=$2
    local held_dependency="$RUN_ROOT/$dependency.$test_name.missing-test"
    local log_file="$RUN_ROOT/$test_name.log"

    mv "$PLUGIN_FOLDER/$dependency" "$held_dependency"
    start_host "$log_file"
    wait_for_host_exit || fail "host did not stop after $dependency was removed"
    grep -F "Required Npgsql dependency '$dependency' is missing or incompatible" "$log_file" >/dev/null \
        || fail "$dependency failure did not provide the package-reinstall diagnostic"
    grep -F "Restore the complete Jellyfin.Plugin.Database.Npgsql" "$log_file" >/dev/null \
        || fail "$dependency failure did not explain how to reinstall the package"
    grep -F "or reinstall the previous compatible package before restarting" "$log_file" >/dev/null \
        || fail "$dependency failure did not explain how to recover the prior package"
    grep -F "No database connection was attempted" "$log_file" >/dev/null \
        || fail "$dependency failure did not state its pre-connection boundary"
    mv "$held_dependency" "$PLUGIN_FOLDER/$dependency"
}

authenticate() {
    local port=$1
    local device_id=$2
    local request_file="$RUN_ROOT/auth-$device_id.json"
    printf '{"Username":"feasibility-admin","Pw":"%s"}' "$ADMIN_PASSWORD" >"$request_file"
    chmod 600 "$request_file"
    assert_host_owns_listener "$port"
    curl --silent --show-error --fail \
        --request POST \
        --header 'Content-Type: application/json' \
        --header "Authorization: MediaBrowser Client=\"npgsql-feasibility\", Device=\"smoke\", DeviceId=\"$device_id\", Version=\"0.0.1\"" \
        --data-binary "@$request_file" \
        "http://127.0.0.1:$port/Users/AuthenticateByName" \
        | sed -n 's/.*"AccessToken":"\([^"]*\)".*/\1/p'
    local curl_status=${PIPESTATUS[0]}
    rm -f "$request_file"
    [ "$curl_status" -eq 0 ] || return "$curl_status"
    assert_host_owns_listener "$port"
}

stop_host() {
    local port=$1
    local token=$2
    local config_file="$RUN_ROOT/shutdown-curl-$SMOKE_ID.conf"
    printf 'request = "POST"\nheader = "Authorization: MediaBrowser Token=\\"%s\\""\nurl = "http://127.0.0.1:%s/System/Shutdown"\n' "$token" "$port" >"$config_file"
    chmod 600 "$config_file"
    assert_host_owns_listener "$port"
    curl --silent --show-error --fail --config "$config_file" >/dev/null
    rm -f "$config_file"

    for _ in $(seq 1 30); do
        if ! kill -0 "$HOST_PID" 2>/dev/null; then
            terminate_host_group
            return $?
        fi

        sleep 1
    done

    return 1
}

psql_value() {
    docker exec \
        --env PGPASSFILE=/run/jellyfin-test-secrets/application.pgpass \
        "$POSTGRES_CONTAINER_ID" \
        psql --username jellyfin --dbname jellyfin --tuples-only --no-align --command "$1"
}

psql_exec() {
    docker exec \
        --env PGPASSFILE=/run/jellyfin-test-secrets/application.pgpass \
        "$POSTGRES_CONTAINER_ID" \
        psql --username jellyfin --dbname jellyfin --set ON_ERROR_STOP=1 --command "$1" >/dev/null
}

identity_sequence_state() {
    local sequence_name=$1
    psql_value "SELECT last_value::text || '|' || is_called::text FROM $sequence_name"
}

mkdir -p -m 700 \
    "$RUN_ROOT/host-publish" \
    "$RUN_ROOT/unsupported-host-publish" \
    "$RUN_ROOT/failure-plugin" \
    "$PLUGIN_FOLDER" \
    "$RUN_ROOT/runtime/config" \
    "$RUN_ROOT/runtime/cache" \
    "$RUN_ROOT/runtime/log" \
    "$RUN_ROOT/runtime/media"
find "$RUN_ROOT" -type d -exec chmod 700 '{}' +

if [ "$HOST_INPUT_MODE" = "legacy-repository" ]; then
    mkdir -p "$RUN_ROOT/host-source" "$RUN_ROOT/unsupported-host-source"
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
else
    cp -a "$HOST_PUBLISH/." "$RUN_ROOT/host-publish/"
    if [ -n "$UNSUPPORTED_HOST_PUBLISH" ]; then
        cp -a "$UNSUPPORTED_HOST_PUBLISH/." "$RUN_ROOT/unsupported-host-publish/"
    fi
fi
COPIED_HOST_SHA256=$(hash_host_publish "$RUN_ROOT/host-publish")
readonly COPIED_HOST_SHA256
if [ "$HOST_INPUT_MODE" = "publish" ]; then
    [ "$COPIED_HOST_SHA256" = "$JELLYFIN_HOST_SHA256" ] \
        || fail "the copied Jellyfin publish changed after validation; refusing a TOCTOU-mutated host"
fi

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
sed 's/"status": "Active"/"status": "Disabled"/' "$PLUGIN_ROOT/meta.json" >"$OLD_PLUGIN_FOLDER/meta.json"
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

CONTAINER_SECRET_ROOT="$RUN_ROOT/container-secrets"
mkdir -p -m 700 "$CONTAINER_SECRET_ROOT"
BOOTSTRAP_PASSWORD_FILE="$CONTAINER_SECRET_ROOT/bootstrap-password"
APPLICATION_PASSWORD_FILE="$CONTAINER_SECRET_ROOT/application-password"
ADMIN_PASSFILE="$CONTAINER_SECRET_ROOT/admin.pgpass"
APPLICATION_PASSFILE="$CONTAINER_SECRET_ROOT/application.pgpass"
printf '%s\n' "$BOOTSTRAP_PASSWORD" >"$BOOTSTRAP_PASSWORD_FILE"
printf '%s' "$APPLICATION_PASSWORD" >"$APPLICATION_PASSWORD_FILE"
printf '*:*:*:postgres:%s\n' "$BOOTSTRAP_PASSWORD" >"$ADMIN_PASSFILE"
printf '*:*:*:jellyfin:%s\n' "$APPLICATION_PASSWORD" >"$APPLICATION_PASSFILE"
chmod 600 "$BOOTSTRAP_PASSWORD_FILE" "$APPLICATION_PASSWORD_FILE" "$ADMIN_PASSFILE" "$APPLICATION_PASSFILE"
python3 - "$RUN_ROOT/runtime/config/database.xml" "$POSTGRES_PORT" "$APPLICATION_PASSWORD_FILE" <<'PY'
import sys, xml.etree.ElementTree as ET
path, port, password_file = sys.argv[1:]
password = open(password_file, encoding="utf-8").read()
root = ET.Element("DatabaseConfigurationOptions")
ET.SubElement(root, "DatabaseType").text = "PLUGIN_PROVIDER"
custom = ET.SubElement(root, "CustomProviderOptions")
ET.SubElement(custom, "PluginName").text = "Npgsql Database_0.1.0.0"
ET.SubElement(custom, "PluginAssembly").text = "Jellyfin.Plugin.Database.Npgsql.dll"
ET.SubElement(custom, "ConnectionString").text = f"Host=127.0.0.1;Port={port};Database=jellyfin;Username=jellyfin;Password={password};Maximum Pool Size=4"
ET.SubElement(custom, "Options")
ET.SubElement(root, "LockingBehavior").text = "NoLock"
ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
PY
cp "$RUN_ROOT/runtime/config/database.xml" "$RUN_ROOT/database.xml.baseline"
python3 - "$RUN_ROOT/runtime/config/network.xml" "$HTTP_PORT" "$HTTPS_PORT" <<'PY'
import sys, xml.etree.ElementTree as ET
path, http, https = sys.argv[1:]
root = ET.Element("NetworkConfiguration")
for name, value in (("InternalHttpPort", http), ("InternalHttpsPort", https), ("PublicHttpPort", http), ("PublicHttpsPort", https)):
    ET.SubElement(root, name).text = value
ET.SubElement(root, "AutoDiscovery").text = "false"
ET.SubElement(root, "EnableIPv4").text = "true"
ET.SubElement(root, "EnableIPv6").text = "false"
ET.SubElement(root, "EnableRemoteAccess").text = "false"
addresses = ET.SubElement(root, "LocalNetworkAddresses")
ET.SubElement(addresses, "string").text = "127.0.0.1"
ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
PY

for private_directory in \
    "$RUN_ROOT" \
    "$RUN_ROOT/runtime" \
    "$RUN_ROOT/runtime/config" \
    "$CONTAINER_SECRET_ROOT"; do
    [ "$(stat -c '%a' "$private_directory")" = "700" ] \
        || fail "secret-bearing directory was not mode 0700 before host launch: $private_directory"
done
for private_file in \
    "$RUN_ROOT/runtime/config/database.xml" \
    "$RUN_ROOT/runtime/config/network.xml" \
    "$RUN_ROOT/database.xml.baseline" \
    "$BOOTSTRAP_PASSWORD_FILE" \
    "$APPLICATION_PASSWORD_FILE" \
    "$ADMIN_PASSFILE" \
    "$APPLICATION_PASSFILE"; do
    [ "$(stat -c '%a' "$private_file")" = "600" ] \
        || fail "secret-bearing file was not mode 0600 before host launch: $private_file"
done

# These failures occur before PostgreSQL is started. They prove that either
# independently missing ZIP dependency and Jellyfin's prefix-selected ambiguous
# directory are rejected before database access, and that restoring the
# canonical package is sufficient recovery.
assert_missing_dependency "Npgsql.dll" "missing-npgsql-driver"
assert_missing_dependency "Npgsql.EntityFrameworkCore.PostgreSQL.dll" "missing-npgsql-ef-provider"

mv "$PLUGIN_FOLDER" "$RUN_ROOT/canonical-package-hold"
start_host "$RUN_ROOT/ambiguous-folder.log"
wait_for_host_exit || fail "host did not stop after the ambiguous folder was selected"
grep -F "requires the exact folder and PluginName '$PLUGIN_FOLDER_NAME'" "$RUN_ROOT/ambiguous-folder.log" >/dev/null \
    || fail "ambiguous prefix selection did not provide the canonical-folder recovery diagnostic"
grep -F "No database connection was attempted" "$RUN_ROOT/ambiguous-folder.log" >/dev/null \
    || fail "ambiguous prefix selection did not state its pre-connection boundary"
mv "$RUN_ROOT/canonical-package-hold" "$PLUGIN_FOLDER"
sed 's/"status": "Active"/"status": "Disabled"/' "$PLUGIN_ROOT/meta.json" >"$AMBIGUOUS_PLUGIN_FOLDER/meta.json"

if [ "$HOST_INPUT_MODE" = "legacy-repository" ] || [ -n "$UNSUPPORTED_HOST_PUBLISH" ]; then
    start_unsupported_host "$RUN_ROOT/unsupported-abi.log"
    wait_for_host_exit || fail "unsupported vanilla Jellyfin host did not stop after the ABI guard"
    grep -F "supports Jellyfin database ABI 12.2.0.0; the host loaded 13.0.0.0" "$RUN_ROOT/unsupported-abi.log" >/dev/null \
        || fail "unsupported host ABI failure did not identify the supported and loaded versions"
    grep -F "No database connection was attempted" "$RUN_ROOT/unsupported-abi.log" >/dev/null \
        || fail "unsupported host ABI failure did not state its pre-connection boundary"
fi

# Npgsql 10.0.3 reads these ambient variables after parsing the connection
# string. Each untouched-host control must fail before PostgreSQL is started,
# without logging the variable value. The two PGOPTIONS cases cover both schema
# selection and read-only transaction state rather than treating PGOPTIONS as a
# single syntactic edge case.
assert_environment_rejected "PGOPTIONS" "-c search_path=private" "ambient-pgoptions-search-path"
assert_environment_rejected "PGOPTIONS" "-c default_transaction_read_only=on" "ambient-pgoptions-read-only"
assert_environment_rejected "PGPASSWORD" "ambient-password-secret" "ambient-password"
assert_environment_rejected "PGTARGETSESSIONATTRS" "read-only" "ambient-target-session"
assert_environment_rejected "PGSSLCERT" "/sensitive/ambient-client-certificate.pem" "ambient-client-certificate"
assert_environment_rejected "PGAPPNAME" "" "ambient-empty-application-name"

SECRET_FILE_MARKER="secret-file-content-$SMOKE_ID"
MISSING_PASSFILE="$RUN_ROOT/sensitive-missing-passfile-$SMOKE_ID.pgpass"
restore_database_configuration
set_database_connection_string "Host=127.0.0.1;Port=$POSTGRES_PORT;Database=jellyfin;Username=jellyfin;Passfile=$MISSING_PASSFILE"
assert_secret_file_rejected \
    "missing-passfile" \
    "Passfile must name a readable, well-formed PostgreSQL password file" \
    "$MISSING_PASSFILE" \
    "$SECRET_FILE_MARKER"

UNREADABLE_PASSFILE="$RUN_ROOT/sensitive-unreadable-passfile-$SMOKE_ID.pgpass"
printf '127.0.0.1:%s:jellyfin:jellyfin:%s\n' "$POSTGRES_PORT" "$SECRET_FILE_MARKER" >"$UNREADABLE_PASSFILE"
chmod 000 "$UNREADABLE_PASSFILE"
restore_database_configuration
set_database_connection_string "Host=127.0.0.1;Port=$POSTGRES_PORT;Database=jellyfin;Username=jellyfin;Passfile=$UNREADABLE_PASSFILE"
assert_secret_file_rejected \
    "unreadable-passfile" \
    "Passfile must name a readable, well-formed PostgreSQL password file" \
    "$UNREADABLE_PASSFILE" \
    "$SECRET_FILE_MARKER"
chmod 600 "$UNREADABLE_PASSFILE"

# These controls run against a pristine published Jellyfin host before
# PostgreSQL starts. Each common over-broad Unix mode must be rejected based on
# the opened file, without leaking its path or contents into the host log.
for insecure_mode in 0640 0644 0666; do
    INSECURE_PASSFILE="$RUN_ROOT/sensitive-insecure-$insecure_mode-$SMOKE_ID.pgpass"
    printf '127.0.0.1:%s:jellyfin:jellyfin:%s\n' "$POSTGRES_PORT" "$SECRET_FILE_MARKER" >"$INSECURE_PASSFILE"
    chmod "$insecure_mode" "$INSECURE_PASSFILE"
    restore_database_configuration
    set_database_connection_string "Host=127.0.0.1;Port=$POSTGRES_PORT;Database=jellyfin;Username=jellyfin;Passfile=$INSECURE_PASSFILE"
    assert_secret_file_rejected \
        "insecure-passfile-$insecure_mode" \
        "Passfile must name a readable, well-formed PostgreSQL password file and, on Unix, use owner-only permissions" \
        "$INSECURE_PASSFILE" \
        "$SECRET_FILE_MARKER"
    chmod 600 "$INSECURE_PASSFILE"
done

MISSING_ROOT_CERTIFICATE="$RUN_ROOT/sensitive-missing-root-$SMOKE_ID.pem"
restore_database_configuration
set_database_connection_string "Host=127.0.0.1;Port=$POSTGRES_PORT;Database=jellyfin;Username=jellyfin;Password=$APPLICATION_PASSWORD;SSL Mode=VerifyFull;Root Certificate=$MISSING_ROOT_CERTIFICATE"
assert_secret_file_rejected \
    "missing-root-certificate" \
    "Root Certificate must name a readable, valid PEM or unencrypted PFX certificate file" \
    "$MISSING_ROOT_CERTIFICATE" \
    "$SECRET_FILE_MARKER"

UNREADABLE_CLIENT_CERTIFICATE="$RUN_ROOT/sensitive-unreadable-client-$SMOKE_ID.pem"
UNREADABLE_CLIENT_KEY="$RUN_ROOT/sensitive-unreadable-client-key-$SMOKE_ID.pem"
printf '%s\n' "$SECRET_FILE_MARKER" >"$UNREADABLE_CLIENT_CERTIFICATE"
printf '%s\n' "$SECRET_FILE_MARKER" >"$UNREADABLE_CLIENT_KEY"
chmod 000 "$UNREADABLE_CLIENT_CERTIFICATE" "$UNREADABLE_CLIENT_KEY"
restore_database_configuration
set_database_connection_string "Host=127.0.0.1;Port=$POSTGRES_PORT;Database=jellyfin;Username=jellyfin;Password=$APPLICATION_PASSWORD;SSL Mode=Require;SSL Certificate=$UNREADABLE_CLIENT_CERTIFICATE;SSL Key=$UNREADABLE_CLIENT_KEY"
assert_secret_file_rejected \
    "unreadable-client-certificate" \
    "SSL Certificate and SSL Key must name a readable, valid PEM pair" \
    "$UNREADABLE_CLIENT_CERTIFICATE" \
    "$SECRET_FILE_MARKER"
assert_log_redacted "$RUN_ROOT/unreadable-client-certificate.log" "$UNREADABLE_CLIENT_KEY"
chmod 600 "$UNREADABLE_CLIENT_CERTIFICATE" "$UNREADABLE_CLIENT_KEY"
restore_database_configuration

# Generate real client credential material for untouched-host permission
# controls. The 0600 checks below prove only that the vanilla host accepts and
# preloads the configuration before attempting a database connection; they do
# not claim that a TLS handshake has completed.
CLIENT_CERTIFICATE="$RUN_ROOT/sensitive-client-$SMOKE_ID.pem"
CLIENT_KEY="$RUN_ROOT/sensitive-client-key-$SMOKE_ID.pem"
CLIENT_PKCS12="$RUN_ROOT/sensitive-client-$SMOKE_ID.p12"
openssl req \
    -x509 \
    -newkey rsa:2048 \
    -sha256 \
    -days 1 \
    -nodes \
    -subj "/CN=jellyfin-smoke-${SMOKE_ID##*-}" \
    -keyout "$CLIENT_KEY" \
    -out "$CLIENT_CERTIFICATE" \
    >/dev/null 2>&1
openssl pkcs12 \
    -export \
    -passout pass: \
    -inkey "$CLIENT_KEY" \
    -in "$CLIENT_CERTIFICATE" \
    -out "$CLIENT_PKCS12" \
    >/dev/null 2>&1
chmod 600 "$CLIENT_KEY" "$CLIENT_PKCS12"

restore_database_configuration
set_database_connection_string "Host=127.0.0.1;Port=$POSTGRES_PORT;Database=jellyfin;Username=jellyfin;Password=$APPLICATION_PASSWORD;SSL Mode=Require;SSL Certificate=$CLIENT_CERTIFICATE;SSL Key=$CLIENT_KEY"
assert_secret_configuration_accepted \
    "secure-pem-client-configuration" \
    "$CLIENT_CERTIFICATE" \
    "$CLIENT_KEY"

restore_database_configuration
set_database_connection_string "Host=127.0.0.1;Port=$POSTGRES_PORT;Database=jellyfin;Username=jellyfin;Password=$APPLICATION_PASSWORD;SSL Mode=Require;SSL Certificate=$CLIENT_PKCS12"
assert_secret_configuration_accepted \
    "secure-pkcs12-client-configuration" \
    "$CLIENT_PKCS12"

for insecure_mode in 0640 0644 0666; do
    chmod "$insecure_mode" "$CLIENT_KEY"
    restore_database_configuration
    set_database_connection_string "Host=127.0.0.1;Port=$POSTGRES_PORT;Database=jellyfin;Username=jellyfin;Password=$APPLICATION_PASSWORD;SSL Mode=Require;SSL Certificate=$CLIENT_CERTIFICATE;SSL Key=$CLIENT_KEY"
    assert_secret_file_rejected \
        "insecure-pem-client-key-$insecure_mode" \
        "SSL Certificate and SSL Key must name a readable, valid PEM pair" \
        "$CLIENT_KEY" \
        "$SECRET_FILE_MARKER"
    assert_log_redacted "$RUN_ROOT/insecure-pem-client-key-$insecure_mode.log" "$CLIENT_CERTIFICATE"
    chmod 600 "$CLIENT_KEY"

    chmod "$insecure_mode" "$CLIENT_PKCS12"
    restore_database_configuration
    set_database_connection_string "Host=127.0.0.1;Port=$POSTGRES_PORT;Database=jellyfin;Username=jellyfin;Password=$APPLICATION_PASSWORD;SSL Mode=Require;SSL Certificate=$CLIENT_PKCS12"
    assert_secret_file_rejected \
        "insecure-pkcs12-client-$insecure_mode" \
        "SSL Certificate and SSL Key must name a readable, valid PEM pair" \
        "$CLIENT_PKCS12" \
        "$SECRET_FILE_MARKER"
    chmod 600 "$CLIENT_PKCS12"
done

reap_stale_smoke_containers

POSTGRES_CONTAINER_ID=$(docker run \
    --detach \
    --name "$POSTGRES_CONTAINER" \
    --label org.jellyfin.database-npgsql.smoke-managed=true \
    --label "org.jellyfin.database-npgsql.smoke-scope=$SMOKE_SCOPE" \
    --label "org.jellyfin.database-npgsql.owner-lease=$SMOKE_ID" \
    --label "org.jellyfin.database-npgsql.owner-pid=$$" \
    --label "org.jellyfin.database-npgsql.owner-start=$OWNER_START" \
    --env POSTGRES_USER=postgres \
    --mount "type=bind,source=$CONTAINER_SECRET_ROOT,target=/run/jellyfin-test-secrets,readonly" \
    --env POSTGRES_PASSWORD_FILE=/run/jellyfin-test-secrets/bootstrap-password \
    --env POSTGRES_DB=postgres \
    --publish "127.0.0.1:$POSTGRES_PORT:5432" \
    "$POSTGRES_IMAGE" \
    postgres -c max_connections=20)
[[ "$POSTGRES_CONTAINER_ID" =~ ^[0-9a-f]{64}$ ]] || fail "Docker did not return one immutable lowercase container ID"
POSTGRES_VOLUME_IDS=$(docker inspect --format '{{range .Mounts}}{{if eq .Type "volume"}}{{.Name}}{{"\n"}}{{end}}{{end}}' "$POSTGRES_CONTAINER_ID")
while IFS= read -r volume_id; do
    [ -z "$volume_id" ] || [[ "$volume_id" =~ ^[0-9a-f]{64}$ ]] \
        || fail "test container attached unexpected non-anonymous volume '$volume_id'"
done <<<"$POSTGRES_VOLUME_IDS"
[ -n "$POSTGRES_VOLUME_IDS" ] || fail "test PostgreSQL container did not expose its expected anonymous data volume"

for _ in $(seq 1 60); do
    if docker exec "$POSTGRES_CONTAINER_ID" pg_isready --username postgres --dbname postgres >/dev/null 2>&1; then
        break
    fi

    sleep 1
done
docker exec "$POSTGRES_CONTAINER_ID" pg_isready --username postgres --dbname postgres >/dev/null
POSTGRES_SERVER_VERSION=$(docker exec --env PGPASSFILE=/run/jellyfin-test-secrets/admin.pgpass "$POSTGRES_CONTAINER_ID" psql --username postgres --dbname postgres --tuples-only --no-align --command 'SHOW server_version')
POSTGRES_SERVER_VERSION_NUM=$(docker exec --env PGPASSFILE=/run/jellyfin-test-secrets/admin.pgpass "$POSTGRES_CONTAINER_ID" psql --username postgres --dbname postgres --tuples-only --no-align --command "SELECT current_setting('server_version_num')::integer")
[ "$POSTGRES_SERVER_VERSION_NUM" = "160006" ] \
    || fail "the pinned PostgreSQL image did not report exact server_version_num 160006"
printf "CREATE ROLE jellyfin LOGIN PASSWORD '%s' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 6;\nCREATE DATABASE jellyfin OWNER jellyfin CONNECTION LIMIT 8;\n" "$APPLICATION_PASSWORD" \
    | docker exec --interactive --env PGPASSFILE=/run/jellyfin-test-secrets/admin.pgpass "$POSTGRES_CONTAINER_ID" \
        psql --username postgres --dbname postgres --set ON_ERROR_STOP=1 >/dev/null

[ "$(psql_value "SELECT rolcanlogin::text || '|' || rolsuper::text || '|' || rolcreatedb::text || '|' || rolcreaterole::text || '|' || rolreplication::text || '|' || rolbypassrls::text || '|' || rolconnlimit::text FROM pg_catalog.pg_roles WHERE rolname = current_user")" = "true|false|false|false|false|false|6" ] \
    || fail "Jellyfin runtime role has unexpected cluster privileges"
[ "$(psql_value "SELECT pg_catalog.pg_get_userbyid(datdba) FROM pg_catalog.pg_database WHERE datname = current_database()")" = "jellyfin" ] \
    || fail "Jellyfin runtime role does not own its database"
[ "$(psql_value "SELECT datconnlimit FROM pg_catalog.pg_database WHERE datname = current_database()")" = "8" ] \
    || fail "Jellyfin database does not have the required connection limit"
if docker exec --env PGPASSFILE=/run/jellyfin-test-secrets/application.pgpass "$POSTGRES_CONTAINER_ID" \
    psql --username jellyfin --dbname jellyfin --command 'CREATE DATABASE jellyfin_privilege_probe' >/dev/null 2>&1; then
    fail "Jellyfin runtime role unexpectedly created a database"
fi
if docker exec --env PGPASSFILE=/run/jellyfin-test-secrets/application.pgpass "$POSTGRES_CONTAINER_ID" \
    psql --username jellyfin --dbname jellyfin --command 'CREATE ROLE jellyfin_privilege_probe' >/dev/null 2>&1; then
    fail "Jellyfin runtime role unexpectedly created a role"
fi

# Use a real passfile for every successful/guarded host start. The provider
# must remove its path before Npgsql owns the data source, then reopen it through
# a sanitized per-pool callback whenever a physical connection needs a password.
RUNTIME_PASSFILE="$RUN_ROOT/runtime/sensitive-runtime-$SMOKE_ID.pgpass"
PASSFILE_UNRELATED_SECRET="unrelated-$SECRET_FILE_MARKER"
PASSFILE_FIRST_HOST_SECRET="first-host-$SECRET_FILE_MARKER"
PASSFILE_LOWER_PRECEDENCE_SECRET="lower-precedence-$SECRET_FILE_MARKER"
{
    printf 'unrelated.invalid:%s:jellyfin:jellyfin:%s\n' "$POSTGRES_PORT" "$PASSFILE_UNRELATED_SECRET"
    printf '127.0.0.2:%s:jellyfin:jellyfin:%s\n' "$POSTGRES_PORT" "$PASSFILE_FIRST_HOST_SECRET"
    printf '127.0.0.1:%s:jellyfin:jellyfin:%s\n' "$POSTGRES_PORT" "$APPLICATION_PASSWORD"
    printf '127.0.0.1:%s:jellyfin:jellyfin:%s\n' "$POSTGRES_PORT" "$PASSFILE_LOWER_PRECEDENCE_SECRET"
} >"$RUNTIME_PASSFILE"
chmod 600 "$RUNTIME_PASSFILE"
restore_database_configuration
# The first child pool is deliberately unreachable and has a different
# credential. Reaching PostgreSQL through the second child proves that Npgsql's
# per-pool callback settings, rather than the comma-separated parent Host or a
# wildcard credential reused across pools, select each password.
set_database_connection_string "Host=127.0.0.2,127.0.0.1;Port=$POSTGRES_PORT;Database=jellyfin;Username=jellyfin;Passfile=$RUNTIME_PASSFILE;Maximum Pool Size=4"

# These variables are standard libpq settings but are deliberately not consumed
# by Npgsql 10.0.3. Supplying hostile values on the real host proves that the
# explicit database.xml endpoint and SSL mode remain authoritative.
start_host \
    "$RUN_ROOT/first-start.log" \
    "PGHOST=attacker.invalid" \
    "PGPORT=1" \
    "PGDATABASE=attacker_database" \
    "PGSSLMODE=disable"
# PROVIDER_CORE_ONLY remains accepted for existing CI callers. The provider now
# implements the complete backup contract, so it deliberately runs the same
# successful migration, restore, restart, and public-API proof as the full path.

wait_for_health "$HTTP_PORT" || fail "vanilla Jellyfin 12.2 did not become healthy"
assert_loopback_listener "$HTTP_PORT"
assert_host_remains_healthy "$HTTP_PORT" "$RUN_ROOT/first-start.log"
grep -q 'Npgsql database provider 0.1.0.0 loaded.*in Default' "$RUN_ROOT/first-start.log" \
    || fail "provider was not loaded in the default load context"
assert_log_origin "$RUN_ROOT/first-start.log" "provider" "$PLUGIN_FOLDER/Jellyfin.Plugin.Database.Npgsql.dll"
assert_log_origin "$RUN_ROOT/first-start.log" "Npgsql EF" "$PLUGIN_FOLDER/Npgsql.EntityFrameworkCore.PostgreSQL.dll"
assert_log_origin "$RUN_ROOT/first-start.log" "Npgsql" "$PLUGIN_FOLDER/Npgsql.dll"
assert_log_origin "$RUN_ROOT/first-start.log" "host database" "$RUN_ROOT/host-publish/Jellyfin.Database.Implementations.dll"
assert_log_origin "$RUN_ROOT/first-start.log" "EF Core" "$RUN_ROOT/host-publish/Microsoft.EntityFrameworkCore.dll"
grep -F 'Registering whitelisted assemblies for plugin "Npgsql Database"' "$RUN_ROOT/first-start.log" >/dev/null \
    || fail "normal plugin loading did not register the required restore guard assembly"
grep -F "Loaded assembly Jellyfin.Plugin.Database.Npgsql" "$RUN_ROOT/first-start.log" \
    | grep -F " from <redacted>/runtime/data/plugins/$PLUGIN_FOLDER_NAME/Jellyfin.Plugin.Database.Npgsql.dll" >/dev/null \
    || fail "normal PluginLoadContext scan did not report the restore guard assembly origin"
grep -q 'Deleted PostgreSQL migration snapshot' "$RUN_ROOT/first-start.log" \
    || fail "successful migration did not exercise DeleteBackup"

assert_host_owns_listener "$HTTP_PORT"
curl --silent --show-error --fail "http://127.0.0.1:$HTTP_PORT/Startup/User" >/dev/null
assert_host_owns_listener "$HTTP_PORT"
SETUP_USER_FILE="$RUN_ROOT/setup-user.json"
printf '{"Name":"feasibility-admin","Password":"%s"}' "$ADMIN_PASSWORD" >"$SETUP_USER_FILE"
chmod 600 "$SETUP_USER_FILE"
curl --silent --show-error --fail \
    --request POST \
    --header 'Content-Type: application/json' \
    --data-binary "@$SETUP_USER_FILE" \
    "http://127.0.0.1:$HTTP_PORT/Startup/User" >/dev/null
rm -f "$SETUP_USER_FILE"
assert_host_owns_listener "$HTTP_PORT"
curl --silent --show-error --fail --request POST "http://127.0.0.1:$HTTP_PORT/Startup/Complete" >/dev/null
assert_host_owns_listener "$HTTP_PORT"

FIRST_TOKEN=$(authenticate "$HTTP_PORT" "npgsql-first")
[ -n "$FIRST_TOKEN" ] || fail "could not authenticate the user created through Jellyfin"
stop_host "$HTTP_PORT" "$FIRST_TOKEN" || fail "first host did not stop cleanly"

[ "$(psql_value 'SELECT "Username" FROM "Users"')" = "feasibility-admin" ] \
    || fail "Jellyfin CRUD did not persist the administrator"
[ "$(psql_value "SELECT character_maximum_length FROM information_schema.columns WHERE table_schema='public' AND table_name='BaseItems' AND column_name='Type'")" = "511" ] \
    || fail "late model override was not present in PostgreSQL"
FOREIGN_KEY_COUNT=$(psql_value "SELECT COUNT(*) FROM pg_catalog.pg_constraint c JOIN pg_catalog.pg_class r ON r.oid=c.conrelid JOIN pg_catalog.pg_namespace n ON n.oid=r.relnamespace WHERE n.nspname='public' AND c.contype='f'")
[ "$FOREIGN_KEY_COUNT" -gt 0 ] || fail "baseline contains no foreign keys"

start_host "$RUN_ROOT/active-scan.log"
wait_for_health "$HTTP_PORT" || fail "vanilla Jellyfin did not survive the later active plugin scan"
grep -F 'Registering whitelisted assemblies for plugin "Npgsql Database"' "$RUN_ROOT/active-scan.log" >/dev/null \
    || fail "normal plugin loading did not exercise the provider assembly allowlist"
grep -F "Loaded assembly Jellyfin.Plugin.Database.Npgsql" "$RUN_ROOT/active-scan.log" \
    | grep -F " from <redacted>/runtime/data/plugins/$PLUGIN_FOLDER_NAME/Jellyfin.Plugin.Database.Npgsql.dll" >/dev/null \
    || fail "normal PluginLoadContext scan did not report the provider's canonical assembly origin"
if grep -E 'Failed to load assembly .*Jellyfin.Plugin.Database.Npgsql|Error creating .*Npgsql' "$RUN_ROOT/active-scan.log" >/dev/null; then
    fail "normal PluginLoadContext scanning reported a type or context failure"
fi
RESTART_TOKEN=$(authenticate "$HTTP_PORT" "npgsql-active-scan")
[ -n "$RESTART_TOKEN" ] || fail "persisted user could not authenticate after restart"
stop_host "$HTTP_PORT" "$RESTART_TOKEN" || fail "active-scan host did not stop cleanly"

printf '{ malformed plugin metadata\n' >"$PLUGIN_FOLDER/meta.json"
start_host "$RUN_ROOT/malformed-metadata.log"
wait_for_host_exit || fail "host did not stop after malformed ordinary-plugin metadata removed the restore guard"
grep -F 'ordinary plugin metadata is missing, disabled, malformed' "$RUN_ROOT/malformed-metadata.log" >/dev/null \
    || fail "malformed metadata did not produce the pre-restore fail-closed diagnostic"
grep -F 'No database connection or restore was attempted' "$RUN_ROOT/malformed-metadata.log" >/dev/null \
    || fail "malformed metadata did not state its pre-connection and pre-restore boundary"
cp "$PLUGIN_ROOT/meta.json" "$PLUGIN_FOLDER/meta.json"

mapfile -t SEQUENCE_PROBES < <(psql_value "SELECT format('%I.%I', schemaname, sequencename) FROM pg_catalog.pg_sequences WHERE schemaname = 'public' ORDER BY sequencename LIMIT 4")
[ "${#SEQUENCE_PROBES[@]}" -eq 4 ] || fail "supported schema did not expose four identity sequences for rollback state probes"
psql_exec "SELECT setval('${SEQUENCE_PROBES[0]}'::regclass, 900, true)"
psql_exec "SELECT setval('${SEQUENCE_PROBES[1]}'::regclass, 1, true)"
psql_exec "SELECT setval('${SEQUENCE_PROBES[2]}'::regclass, 1, false)"
psql_exec "SELECT setval('${SEQUENCE_PROBES[3]}'::regclass, 37, false)"
ADVANCED_SEQUENCE_STATE=$(identity_sequence_state "${SEQUENCE_PROBES[0]}")
DELETED_SEQUENCE_STATE=$(identity_sequence_state "${SEQUENCE_PROBES[1]}")
EMPTY_SEQUENCE_STATE=$(identity_sequence_state "${SEQUENCE_PROBES[2]}")
FALSE_SEQUENCE_STATE=$(identity_sequence_state "${SEQUENCE_PROBES[3]}")
[ "$ADVANCED_SEQUENCE_STATE" = "900|true" ] || fail "advanced sequence fixture was not established"
[ "$DELETED_SEQUENCE_STATE" = "1|true" ] || fail "called sequence fixture was not established"
[ "$EMPTY_SEQUENCE_STATE" = "1|false" ] || fail "never-called sequence fixture was not established"
[ "$FALSE_SEQUENCE_STATE" = "37|false" ] || fail "explicit false-state sequence fixture was not established"

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
terminate_host_group || fail "forced-failure host process group could not be terminated"

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
[ "$(identity_sequence_state "${SEQUENCE_PROBES[0]}")" = "$ADVANCED_SEQUENCE_STATE" ] \
    || fail "restore changed an identity sequence advanced beyond its table maximum"
[ "$(identity_sequence_state "${SEQUENCE_PROBES[1]}")" = "$DELETED_SEQUENCE_STATE" ] \
    || fail "restore changed a called identity sequence"
[ "$(identity_sequence_state "${SEQUENCE_PROBES[2]}")" = "$EMPTY_SEQUENCE_STATE" ] \
    || fail "restore changed a never-called identity sequence"
[ "$(identity_sequence_state "${SEQUENCE_PROBES[3]}")" = "$FALSE_SEQUENCE_STATE" ] \
    || fail "restore changed an explicitly advanced identity sequence with is_called=false"
cp "$PLUGIN_ROOT/artifacts/package/Jellyfin.Plugin.Database.Npgsql.dll" "$PLUGIN_FOLDER/Jellyfin.Plugin.Database.Npgsql.dll"
start_host "$RUN_ROOT/recovery.log"
wait_for_health "$HTTP_PORT" || fail "normal package did not recover after the forced failure"
RECOVERY_TOKEN=$(authenticate "$HTTP_PORT" "npgsql-recovery")
[ -n "$RECOVERY_TOKEN" ] || fail "restored user could not authenticate during recovery"
stop_host "$HTTP_PORT" "$RECOVERY_TOKEN" || fail "recovery host did not stop cleanly"
assert_pristine_host_unchanged

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
    echo "postgres=$POSTGRES_SERVER_VERSION"
    echo "postgres_major=16"
    echo "runtime_role_connection_limit=6"
    echo "database_connection_limit=8"
    echo "npgsql_maximum_pool_size=4"
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
    echo "early_failure_guards=missing-npgsql-driver,missing-npgsql-ef-provider,ambiguous-prefix,unsupported-abi"
    echo "normal_plugin_scans=active-guard,active-restart,malformed-metadata-fail-closed"
    echo "passfile_multi_host_failover=true"
    echo "credential_file_modes=0600-accepted,0640-0644-0666-rejected"
} | tee "$RUN_ROOT/evidence.txt"

echo "Black-box smoke test passed. Sanitized evidence will be retained under $RUN_ROOT"
