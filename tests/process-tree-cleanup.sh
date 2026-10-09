#!/usr/bin/env bash

snapshot_process_tree() {
    local root_pid=$1
    python3 - "$root_pid" <<'PY'
import pathlib, sys
root = int(sys.argv[1])
items = {}
for path in pathlib.Path('/proc').glob('[0-9]*/stat'):
    try:
        data = path.read_text()
        end = data.rfind(')')
        pid = int(data[:data.find(' ')])
        fields = data[end + 2:].split()
        items[pid] = (int(fields[1]), int(fields[2]), int(fields[19]))
    except (OSError, ValueError, IndexError):
        pass
selected = {root}
changed = True
while changed:
    changed = False
    for pid, (ppid, _pgid, _start) in items.items():
        if ppid in selected and pid not in selected:
            selected.add(pid)
            changed = True
for pid in sorted(selected):
    if pid in items:
        _ppid, pgid, start = items[pid]
        print(pid, pgid, start)
PY
}

process_identity_matches() {
    local pid=$1
    local expected_pgid=$2
    local expected_start=$3
    local current_pgid current_start
    [ -r "/proc/$pid/stat" ] || return 1
    read -r current_pgid current_start < <(python3 - "$pid" <<'PY'
import pathlib, sys
data = pathlib.Path('/proc', sys.argv[1], 'stat').read_text()
fields = data[data.rfind(')') + 2:].split()
print(fields[2], fields[19])
PY
) || return 1
    [ "$current_pgid" = "$expected_pgid" ] && [ "$current_start" = "$expected_start" ]
}

terminate_recorded_process_tree() {
    local root_pid=$1
    local root_pgid=$2
    local root_start=$3
    process_identity_matches "$root_pid" "$root_pgid" "$root_start" \
        || { echo "FAIL: refusing to terminate a process tree whose root PID identity changed" >&2; return 1; }

    local -a pids=() pgids=() starts=()
    local pid pgid start index
    while read -r pid pgid start; do
        pids+=("$pid")
        pgids+=("$pgid")
        starts+=("$start")
    done < <(snapshot_process_tree "$root_pid")
    [ "${#pids[@]}" -gt 0 ] \
        || { echo "FAIL: could not snapshot the process tree before signaling" >&2; return 1; }

    kill -TERM -- "-$root_pgid" || return 1
    for index in "${!pids[@]}"; do
        [ "${pgids[$index]}" = "$root_pgid" ] && continue
        if process_identity_matches "${pids[$index]}" "${pgids[$index]}" "${starts[$index]}"; then
            kill -TERM "${pids[$index]}" || return 1
        fi
    done

    for _ in $(seq 1 100); do
        local survivor=0
        for index in "${!pids[@]}"; do
            process_identity_matches "${pids[$index]}" "${pgids[$index]}" "${starts[$index]}" && survivor=1
        done
        [ "$survivor" -eq 0 ] && break
        sleep 0.05
    done

    for index in "${!pids[@]}"; do
        if process_identity_matches "${pids[$index]}" "${pgids[$index]}" "${starts[$index]}"; then
            kill -KILL "${pids[$index]}" || return 1
        fi
    done

    for _ in $(seq 1 100); do
        local survivor=0
        for index in "${!pids[@]}"; do
            process_identity_matches "${pids[$index]}" "${pgids[$index]}" "${starts[$index]}" && survivor=1
        done
        [ "$survivor" -eq 0 ] && return 0
        sleep 0.05
    done

    for index in "${!pids[@]}"; do
        if process_identity_matches "${pids[$index]}" "${pgids[$index]}" "${starts[$index]}"; then
            echo "FAIL: descendant PID ${pids[$index]} start ${starts[$index]} survived bounded TERM/KILL" >&2
        fi
    done
    return 1
}
