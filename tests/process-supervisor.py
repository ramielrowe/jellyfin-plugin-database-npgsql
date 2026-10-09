#!/usr/bin/env python3
"""Linux-only, identity-safe subreaper for black-box test processes."""

from __future__ import annotations

import argparse
import ctypes
import os
import pathlib
import selectors
import signal
import sys
import time


PR_SET_PDEATHSIG = 1
PR_SET_CHILD_SUBREAPER = 36
PR_GET_CHILD_SUBREAPER = 37
LIBC = ctypes.CDLL("libc.so.6", use_errno=True)


def prctl(option: int, argument) -> None:
    if LIBC.prctl(option, argument, 0, 0, 0) != 0:
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error))


def process_identity(pid: int) -> tuple[int, int, int] | None:
    try:
        data = pathlib.Path("/proc", str(pid), "stat").read_text()
        fields = data[data.rfind(")") + 2 :].split()
        return int(fields[1]), int(fields[2]), int(fields[19])
    except (FileNotFoundError, ProcessLookupError, PermissionError, OSError, ValueError, IndexError):
        return None


def current(identity: tuple[int, int]) -> bool:
    pid, expected_start = identity
    observed = process_identity(pid)
    return observed is not None and observed[2] == expected_start


def snapshot_descendants(supervisor_pid: int) -> dict[int, tuple[int, int]]:
    processes: dict[int, tuple[int, int, int]] = {}
    try:
        paths = pathlib.Path("/proc").glob("[0-9]*/stat")
        for path in paths:
            try:
                pid = int(path.parent.name)
                identity = process_identity(pid)
                if identity is not None:
                    processes[pid] = identity
            except (FileNotFoundError, PermissionError, OSError, ValueError):
                continue
    except (FileNotFoundError, PermissionError, OSError):
        return {}

    selected = {supervisor_pid}
    changed = True
    while changed:
        changed = False
        for pid, (parent, _group, _start) in processes.items():
            if parent in selected and pid not in selected:
                selected.add(pid)
                changed = True
    return {pid: (group, start) for pid, (_parent, group, start) in processes.items() if pid in selected and pid != supervisor_pid}


class StreamingRedactor:
    def __init__(self, secrets: list[bytes]):
        self.secrets = sorted({item for item in secrets if item}, key=len, reverse=True)
        self.pending = b""
        self.overlap = max((len(item) for item in self.secrets), default=1) - 1

    def redact(self, chunk: bytes, final: bool = False) -> bytes:
        combined = self.pending + chunk
        limit = len(combined) if final else max(0, len(combined) - self.overlap)
        output = bytearray()
        position = 0
        while position < limit:
            secret = next((item for item in self.secrets if combined.startswith(item, position)), None)
            if secret is None:
                output.append(combined[position])
                position += 1
            else:
                output.extend(b"<redacted>")
                position += len(secret)
        self.pending = combined[position:]
        if final:
            output.extend(self.pending)
            self.pending = b""
        return bytes(output)


def read_nul_file(path: str | None, delete: bool) -> list[bytes]:
    if path is None:
        return []
    file_path = pathlib.Path(path)
    try:
        data = file_path.read_bytes()
    finally:
        if delete:
            try:
                file_path.unlink()
            except FileNotFoundError:
                pass
    return [item for item in data.split(b"\0") if item]


def write_identity(path: str, supervisor_start: int, host_pid: int, host_start: int, host_group: int) -> None:
    destination = pathlib.Path(path)
    temporary = destination.with_name(destination.name + f".{os.getpid()}.tmp")
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        payload = f"v1 {os.getpid()} {supervisor_start} {host_pid} {host_start} {host_group}\n".encode()
        os.write(descriptor, payload)
        os.fsync(descriptor)
    finally:
        os.close(descriptor)
    os.replace(temporary, destination)


def signal_process(pid: int, expected_start: int, signum: int) -> None:
    if not current((pid, expected_start)):
        return
    try:
        os.kill(pid, signum)
    except ProcessLookupError:
        return


def signal_verified_group(host_pid: int, host_start: int, host_group: int, signum: int) -> bool:
    identity = process_identity(host_pid)
    if identity is None or identity[1] != host_group or identity[2] != host_start or host_pid != host_group:
        return False
    try:
        os.killpg(host_group, signum)
    except ProcessLookupError:
        pass
    return True


def fork_target(command: list[str], environment: dict[str, str], working_directory: str | None):
    stdout_read, stdout_write = os.pipe2(os.O_CLOEXEC)
    stderr_read, stderr_write = os.pipe2(os.O_CLOEXEC)
    ready_read, ready_write = os.pipe2(os.O_CLOEXEC)
    setup_read, setup_write = os.pipe2(os.O_CLOEXEC)
    pid = os.fork()
    if pid == 0:
        try:
            os.close(stdout_read)
            os.close(stderr_read)
            os.close(ready_write)
            os.close(setup_read)
            os.setsid()
            os.write(setup_write, b"1")
            os.close(setup_write)
            os.dup2(stdout_write, 1)
            os.dup2(stderr_write, 2)
            os.close(stdout_write)
            os.close(stderr_write)
            if os.read(ready_read, 1) != b"1":
                os._exit(126)
            os.close(ready_read)
            if working_directory is not None:
                os.chdir(working_directory)
            os.execvpe(command[0], command, environment)
        except BaseException as exception:  # child must report exec/setup errors through the drained pipe
            try:
                os.write(2, f"process-supervisor: target launch failed: {exception}\n".encode(errors="replace"))
            finally:
                os._exit(127)

    os.close(stdout_write)
    os.close(stderr_write)
    os.close(ready_read)
    os.close(setup_write)
    if os.read(setup_read, 1) != b"1":
        os.close(setup_read)
        os.close(ready_write)
        os.close(stdout_read)
        os.close(stderr_read)
        raise RuntimeError("the target failed before establishing its dedicated process group")
    os.close(setup_read)
    identity = process_identity(pid)
    if identity is None or identity[1] != pid:
        os.close(ready_write)
        os.close(stdout_read)
        os.close(stderr_read)
        try:
            os.kill(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        raise RuntimeError("could not establish the target's dedicated process identity")
    os.write(ready_write, b"1")
    os.close(ready_write)
    return pid, identity[2], identity[1], stdout_read, stderr_read


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--identity-file", required=True)
    parser.add_argument("--log-file", required=True)
    parser.add_argument("--environment-file")
    parser.add_argument("--secrets-file")
    parser.add_argument("--working-directory")
    parser.add_argument("--maximum-output-bytes", type=int, default=16 * 1024 * 1024)
    parser.add_argument("--grace-seconds", type=float, default=5.0)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    options = parser.parse_args()
    command = options.command[1:] if options.command[:1] == ["--"] else options.command
    if sys.platform != "linux" or not command:
        parser.error("Linux and a command after -- are required")
    if options.maximum_output_bytes < 1 or options.maximum_output_bytes > 16 * 1024 * 1024:
        parser.error("maximum output must be between 1 byte and 16 MiB")
    if options.grace_seconds <= 0 or options.grace_seconds > 30:
        parser.error("grace period must be between zero and thirty seconds")

    shutdown_signal = 0

    def request_shutdown(signum, _frame):
        nonlocal shutdown_signal
        shutdown_signal = shutdown_signal or signum

    signal.signal(signal.SIGTERM, request_shutdown)
    signal.signal(signal.SIGINT, request_shutdown)
    signal.signal(signal.SIGHUP, request_shutdown)
    original_parent = os.getppid()
    prctl(PR_SET_CHILD_SUBREAPER, 1)
    enabled = ctypes.c_int()
    prctl(PR_GET_CHILD_SUBREAPER, ctypes.byref(enabled))
    if enabled.value != 1:
        raise RuntimeError("the kernel did not enable child-subreaper supervision")
    prctl(PR_SET_PDEATHSIG, signal.SIGTERM)
    if os.getppid() != original_parent:
        raise RuntimeError("the supervisor parent exited during initialization")

    environment = os.environ.copy()
    for name in (
        "PGUSER", "PGPASSWORD", "PGPASSFILE", "PGSSLCERT", "PGSSLKEY", "PGSSLROOTCERT",
        "PGCLIENTENCODING", "PGTZ", "PGOPTIONS", "PGTARGETSESSIONATTRS", "PGSSLNEGOTIATION",
        "PGGSSENCMODE", "PGREQUIREAUTH", "PGAPPNAME",
    ):
        environment.pop(name, None)
    for item in read_nul_file(options.environment_file, delete=True):
        name, separator, value = item.partition(b"=")
        if not separator or not name:
            raise RuntimeError("the private environment file contains a malformed entry")
        environment[name.decode()] = value.decode()
    secrets = read_nul_file(options.secrets_file, delete=True)
    if shutdown_signal:
        raise RuntimeError("the supervisor parent exited during initialization")

    log_descriptor = os.open(options.log_file, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    supervisor_identity = process_identity(os.getpid())
    if supervisor_identity is None:
        os.close(log_descriptor)
        raise RuntimeError("could not capture the supervisor process identity")

    host_pid, host_start, host_group, stdout_pipe, stderr_pipe = fork_target(command, environment, options.working_directory)
    write_identity(options.identity_file, supervisor_identity[2], host_pid, host_start, host_group)

    selector = selectors.DefaultSelector()
    redactors: dict[int, StreamingRedactor] = {}
    for descriptor in (stdout_pipe, stderr_pipe):
        os.set_blocking(descriptor, False)
        selector.register(descriptor, selectors.EVENT_READ)
        redactors[descriptor] = StreamingRedactor(secrets)

    known: dict[int, tuple[int, int]] = {host_pid: (host_group, host_start)}
    root_status: int | None = None
    terminating = False
    kill_phase = False
    deadline = 0.0
    remaining_output = options.maximum_output_bytes

    try:
        while True:
            observed = snapshot_descendants(os.getpid())
            known.update(observed)

            while True:
                try:
                    reaped_pid, status = os.waitpid(-1, os.WNOHANG)
                except ChildProcessError:
                    break
                if reaped_pid == 0:
                    break
                if reaped_pid == host_pid:
                    root_status = status

            if (shutdown_signal or root_status is not None) and not terminating:
                terminating = True
                deadline = time.monotonic() + options.grace_seconds
                grouped = signal_verified_group(host_pid, host_start, host_group, signal.SIGTERM)
                for pid, (_group, start) in tuple(known.items()):
                    if grouped and _group == host_group:
                        continue
                    signal_process(pid, start, signal.SIGTERM)

            if terminating:
                for pid, (_group, start) in observed.items():
                    if pid not in known:
                        known[pid] = (_group, start)
                    signal_process(pid, start, signal.SIGKILL if kill_phase else signal.SIGTERM)
                if not kill_phase and time.monotonic() >= deadline:
                    kill_phase = True
                    deadline = time.monotonic() + options.grace_seconds
                    grouped = signal_verified_group(host_pid, host_start, host_group, signal.SIGKILL)
                    for pid, (group, start) in tuple(known.items()):
                        if grouped and group == host_group:
                            continue
                        signal_process(pid, start, signal.SIGKILL)

            for key, _mask in selector.select(0.02):
                descriptor = key.fd
                try:
                    chunk = os.read(descriptor, 65536)
                except BlockingIOError:
                    continue
                if not chunk:
                    sanitized = redactors[descriptor].redact(b"", final=True)
                    selector.unregister(descriptor)
                    os.close(descriptor)
                else:
                    sanitized = redactors[descriptor].redact(chunk)
                if sanitized and remaining_output:
                    kept = sanitized[:remaining_output]
                    os.write(log_descriptor, kept)
                    remaining_output -= len(kept)

            alive = {pid: identity for pid, identity in known.items() if current((pid, identity[1]))}
            known.update(alive)
            if terminating and not alive and not selector.get_map():
                break
            if terminating and kill_phase and time.monotonic() >= deadline and alive:
                identities = ", ".join(f"{pid}/{start}" for pid, (_group, start) in alive.items())
                raise RuntimeError(f"could not terminate verified supervised descendants: {identities}")
    finally:
        for key in list(selector.get_map().values()):
            try:
                selector.unregister(key.fd)
                os.close(key.fd)
            except OSError:
                pass
        selector.close()
        os.fsync(log_descriptor)
        os.close(log_descriptor)

    if shutdown_signal:
        return 128 + shutdown_signal
    if root_status is None:
        return 1
    if os.WIFEXITED(root_status):
        return os.WEXITSTATUS(root_status)
    if os.WIFSIGNALED(root_status):
        return 128 + os.WTERMSIG(root_status)
    return 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exception:
        print(f"process-supervisor: {exception}", file=sys.stderr)
        raise SystemExit(125)
