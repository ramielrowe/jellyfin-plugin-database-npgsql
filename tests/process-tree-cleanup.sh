#!/usr/bin/env bash

process_identity_matches() {
    local pid=$1
    local expected_pgid=$2
    local expected_start=$3
    python3 - "$pid" "$expected_pgid" "$expected_start" <<'PY'
import os, pathlib, select, sys
pid, expected_group, expected_start = map(int, sys.argv[1:])
try:
    descriptor = os.pidfd_open(pid)
    if select.select([descriptor], [], [], 0)[0]:
        raise SystemExit(1)
    data = pathlib.Path('/proc', str(pid), 'stat').read_text()
    fields = data[data.rfind(')') + 2:].split()
    matches = int(fields[2]) == expected_group and int(fields[19]) == expected_start
    if select.select([descriptor], [], [], 0)[0]:
        matches = False
    os.close(descriptor)
    raise SystemExit(0 if matches else 1)
except (FileNotFoundError, ProcessLookupError, PermissionError, OSError, ValueError, IndexError):
    raise SystemExit(1)
PY
}

process_start_matches() {
    local pid=$1
    local expected_start=$2
    python3 - "$pid" "$expected_start" <<'PY'
import os, pathlib, select, sys
pid, expected_start = map(int, sys.argv[1:])
try:
    descriptor = os.pidfd_open(pid)
    if select.select([descriptor], [], [], 0)[0]:
        raise SystemExit(1)
    data = pathlib.Path('/proc', str(pid), 'stat').read_text()
    fields = data[data.rfind(')') + 2:].split()
    matches = int(fields[19]) == expected_start
    if select.select([descriptor], [], [], 0)[0]:
        matches = False
    os.close(descriptor)
    raise SystemExit(0 if matches else 1)
except (FileNotFoundError, ProcessLookupError, PermissionError, OSError, ValueError, IndexError):
    raise SystemExit(1)
PY
}

pidfd_signal_verified() {
    local pid=$1
    local expected_pgid=$2
    local expected_start=$3
    local signal_number=$4
    python3 - "$pid" "$expected_pgid" "$expected_start" "$signal_number" <<'PY'
import os, pathlib, select, signal, sys
pid, expected_group, expected_start, signal_number = map(int, sys.argv[1:])
try:
    descriptor = os.pidfd_open(pid)
    if select.select([descriptor], [], [], 0)[0]:
        raise SystemExit(0)
    data = pathlib.Path('/proc', str(pid), 'stat').read_text()
    fields = data[data.rfind(')') + 2:].split()
    if int(fields[2]) != expected_group or int(fields[19]) != expected_start:
        raise SystemExit(1)
    if select.select([descriptor], [], [], 0)[0]:
        raise SystemExit(0)
    signal.pidfd_send_signal(descriptor, signal_number)
    os.close(descriptor)
except ProcessLookupError:
    pass
except (FileNotFoundError, PermissionError, OSError, ValueError, IndexError):
    raise SystemExit(1)
PY
}

pidfd_wait_verified() {
    local pid=$1
    local expected_pgid=$2
    local expected_start=$3
    local timeout_milliseconds=$4
    python3 - "$pid" "$expected_pgid" "$expected_start" "$timeout_milliseconds" <<'PY'
import os, pathlib, select, sys
pid, expected_group, expected_start, timeout = map(int, sys.argv[1:])
try:
    descriptor = os.pidfd_open(pid)
    data = pathlib.Path('/proc', str(pid), 'stat').read_text()
    fields = data[data.rfind(')') + 2:].split()
    if int(fields[2]) != expected_group or int(fields[19]) != expected_start:
        raise SystemExit(2)
    poller = select.poll()
    poller.register(descriptor, select.POLLIN)
    exited = bool(poller.poll(timeout))
    os.close(descriptor)
    raise SystemExit(0 if exited else 1)
except ProcessLookupError:
    raise SystemExit(0)
except (FileNotFoundError, PermissionError, OSError, ValueError, IndexError):
    raise SystemExit(2)
PY
}

terminate_recorded_process_tree() {
    local root_pid=$1
    local root_pgid=$2
    local root_start=$3
    python3 - "$root_pid" "$root_pgid" "$root_start" <<'PY'
import os, pathlib, select, signal, sys, time
root, expected_group, expected_start = map(int, sys.argv[1:])

def opened(pid):
    try:
        fd = os.pidfd_open(pid)
        if select.select([fd], [], [], 0)[0]:
            os.close(fd); return None
        data = pathlib.Path('/proc', str(pid), 'stat').read_text()
        fields = data[data.rfind(')') + 2:].split()
        item = [pid, int(fields[1]), int(fields[2]), int(fields[19]), fd]
        if select.select([fd], [], [], 0)[0]:
            os.close(fd); return None
        return item
    except (FileNotFoundError, ProcessLookupError, PermissionError, OSError, ValueError, IndexError):
        return None

root_item = opened(root)
if root_item is None or root_item[2] != expected_group or root_item[3] != expected_start:
    raise SystemExit(1)
items = {root: root_item}
for path in pathlib.Path('/proc').iterdir():
    if path.name.isdecimal() and int(path.name) != root:
        item = opened(int(path.name))
        if item is not None:
            items[item[0]] = item
selected = {root}
changed = True
while changed:
    changed = False
    for pid, item in items.items():
        if pid not in selected and item[1] in selected:
            selected.add(pid); changed = True
for pid, item in list(items.items()):
    if pid not in selected:
        os.close(item[4]); del items[pid]
for signum in (signal.SIGTERM, signal.SIGKILL):
    for item in items.values():
        if not select.select([item[4]], [], [], 0)[0]:
            try: signal.pidfd_send_signal(item[4], signum)
            except ProcessLookupError: pass
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline and any(not select.select([item[4]], [], [], 0)[0] for item in items.values()):
        time.sleep(.025)
if any(not select.select([item[4]], [], [], 0)[0] for item in items.values()):
    raise SystemExit(1)
for item in items.values(): os.close(item[4])
PY
}
