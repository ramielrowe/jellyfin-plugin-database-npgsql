#!/usr/bin/env python3
"""Linux-only pidfd/subreaper containment for black-box test processes."""

from __future__ import annotations

import argparse
import ctypes
import dataclasses
import os
import pathlib
import select
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


def pidfd_exited(descriptor: int) -> bool:
    poller = select.poll()
    poller.register(descriptor, select.POLLIN)
    return bool(poller.poll(0))


@dataclasses.dataclass
class ProcessHandle:
    pid: int
    parent: int
    group: int
    start: int
    descriptor: int

    def alive(self) -> bool:
        return self.descriptor >= 0 and not pidfd_exited(self.descriptor)

    def signal(self, signum: int) -> None:
        if not self.alive():
            return
        try:
            signal.pidfd_send_signal(self.descriptor, signum)
        except ProcessLookupError:
            pass

    def close(self) -> None:
        descriptor = self.descriptor
        self.descriptor = -1
        if descriptor < 0:
            return
        try:
            os.close(descriptor)
        except OSError:
            pass


def open_process(pid: int) -> ProcessHandle | None:
    """Open first, then identify while the pidfd makes numeric PID reuse impossible."""
    try:
        descriptor = os.pidfd_open(pid, 0)
    except (ProcessLookupError, PermissionError, OSError):
        return None
    keep = False
    try:
        if pidfd_exited(descriptor):
            return None
        identity = process_identity(pid)
        if identity is None or pidfd_exited(descriptor):
            return None
        parent, group, start = identity
        keep = True
        return ProcessHandle(pid, parent, group, start, descriptor)
    finally:
        if not keep:
            os.close(descriptor)


def refresh_descendants(root_pid: int, known: dict[int, ProcessHandle]) -> None:
    for pid, handle in tuple(known.items()):
        if not handle.alive():
            handle.close()
            known.pop(pid, None)
    candidates: dict[int, ProcessHandle] = {}
    try:
        process_ids = [int(path.name) for path in pathlib.Path("/proc").iterdir() if path.name.isdecimal()]
    except OSError:
        process_ids = []
    for pid in process_ids:
        if pid == root_pid or pid in known:
            continue
        handle = open_process(pid)
        if handle is not None:
            candidates[pid] = handle

    selected = {root_pid, *known.keys()}
    changed = True
    while changed:
        changed = False
        for pid, handle in candidates.items():
            if pid not in selected and handle.parent in selected:
                selected.add(pid)
                changed = True
    for pid, handle in candidates.items():
        if pid in selected and handle.alive():
            known[pid] = handle
        else:
            handle.close()


def signal_all(handles: dict[int, ProcessHandle], signum: int) -> None:
    for handle in tuple(handles.values()):
        handle.signal(signum)


def close_all(handles: dict[int, ProcessHandle]) -> None:
    for handle in handles.values():
        handle.close()
    handles.clear()


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


def write_identity(path: str, watchdog: ProcessHandle, supervisor: ProcessHandle, target: ProcessHandle, fail: bool) -> None:
    if fail:
        raise RuntimeError("injected publication failure")
    destination = pathlib.Path(path)
    temporary = destination.with_name(destination.name + f".{os.getpid()}.tmp")
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        payload = (
            f"v2 {watchdog.pid} {watchdog.start} {supervisor.pid} {supervisor.start} {supervisor.group} "
            f"{target.pid} {target.start} {target.group}\n"
        ).encode()
        os.write(descriptor, payload)
        os.fsync(descriptor)
    finally:
        os.close(descriptor)
    os.replace(temporary, destination)


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
            prctl(PR_SET_PDEATHSIG, signal.SIGKILL)
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
        except BaseException:
            try:
                os.write(2, b"process-supervisor: target launch failed closed\n")
            finally:
                os._exit(127)

    os.close(stdout_write)
    os.close(stderr_write)
    os.close(ready_read)
    os.close(setup_write)
    target = open_process(pid)
    if target is None:
        for descriptor in (setup_read, ready_write, stdout_read, stderr_read):
            os.close(descriptor)
        raise RuntimeError("could not open target pidfd")
    try:
        setup_poller = select.poll()
        setup_poller.register(setup_read, select.POLLIN)
        if not setup_poller.poll(5000) or os.read(setup_read, 1) != b"1":
            raise RuntimeError("target setup timed out")
        refreshed = process_identity(target.pid)
        if refreshed is None or refreshed[1] != target.pid or not target.alive():
            raise RuntimeError("target group setup failed")
        target.parent, target.group, target.start = refreshed
        return target, ready_write, stdout_read, stderr_read
    except BaseException:
        target.signal(signal.SIGKILL)
        target.close()
        for descriptor in (ready_write, stdout_read, stderr_read):
            os.close(descriptor)
        raise
    finally:
        os.close(setup_read)


def enable_subreaper() -> None:
    prctl(PR_SET_CHILD_SUBREAPER, 1)
    enabled = ctypes.c_int()
    prctl(PR_GET_CHILD_SUBREAPER, ctypes.byref(enabled))
    if enabled.value != 1:
        raise RuntimeError("subreaper unavailable")


def wait_status_code(status: int | None) -> int:
    if status is None:
        return 125
    if os.WIFEXITED(status):
        return os.WEXITSTATUS(status)
    if os.WIFSIGNALED(status):
        return 128 + os.WTERMSIG(status)
    return 125


def supervise_target(options, command: list[str], watchdog: ProcessHandle) -> int:
    enable_subreaper()
    original_parent = os.getppid()
    prctl(PR_SET_PDEATHSIG, signal.SIGTERM)
    if os.getppid() != original_parent:
        raise RuntimeError("watchdog exited during initialization")
    shutdown_signal = 0

    def request_shutdown(signum, _frame):
        nonlocal shutdown_signal
        shutdown_signal = shutdown_signal or signum

    for signum in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
        signal.signal(signum, request_shutdown)
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
            raise RuntimeError("malformed private environment")
        environment[name.decode()] = value.decode()
    secrets = read_nul_file(options.secrets_file, delete=True)
    log_descriptor = os.open(options.log_file, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    supervisor = open_process(os.getpid())
    if supervisor is None:
        os.close(log_descriptor)
        raise RuntimeError("supervisor pidfd unavailable")
    selector = selectors.DefaultSelector()
    known: dict[int, ProcessHandle] = {}
    target: ProcessHandle | None = None
    release_descriptor: int | None = None
    root_status: int | None = None
    terminating = False
    kill_phase = False
    deadline = 0.0
    remaining_output = options.maximum_output_bytes
    try:
        target, release_descriptor, stdout_pipe, stderr_pipe = fork_target(command, environment, options.working_directory)
        known[target.pid] = target
        write_identity(options.identity_file, watchdog, supervisor, target, options.test_fail_publication)
        os.write(release_descriptor, b"1")
        os.close(release_descriptor)
        release_descriptor = None
        redactors: dict[int, StreamingRedactor] = {}
        for descriptor in (stdout_pipe, stderr_pipe):
            os.set_blocking(descriptor, False)
            selector.register(descriptor, selectors.EVENT_READ)
            redactors[descriptor] = StreamingRedactor(secrets)
        while True:
            refresh_descendants(os.getpid(), known)
            while True:
                try:
                    reaped_pid, status = os.waitpid(-1, os.WNOHANG)
                except ChildProcessError:
                    break
                if reaped_pid == 0:
                    break
                if reaped_pid == target.pid:
                    root_status = status
            if (shutdown_signal or root_status is not None) and not terminating:
                terminating = True
                deadline = time.monotonic() + options.grace_seconds
                signal_all(known, signal.SIGTERM)
            if terminating:
                refresh_descendants(os.getpid(), known)
                signal_all(known, signal.SIGKILL if kill_phase else signal.SIGTERM)
                if not kill_phase and time.monotonic() >= deadline:
                    kill_phase = True
                    deadline = time.monotonic() + options.grace_seconds
                    signal_all(known, signal.SIGKILL)
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
            if terminating and not known and not selector.get_map():
                break
            if terminating and kill_phase and time.monotonic() >= deadline and known:
                raise RuntimeError("supervised descendants survived bounded shutdown")
    finally:
        if release_descriptor is not None:
            os.close(release_descriptor)
        if known:
            signal_all(known, signal.SIGKILL)
            final_deadline = time.monotonic() + options.grace_seconds
            while known and time.monotonic() < final_deadline:
                refresh_descendants(os.getpid(), known)
                time.sleep(0.01)
        for key in list(selector.get_map().values()):
            try:
                selector.unregister(key.fd)
                os.close(key.fd)
            except OSError:
                pass
        selector.close()
        close_all(known)
        supervisor.close()
        os.fsync(log_descriptor)
        os.close(log_descriptor)
    if shutdown_signal:
        return 128 + shutdown_signal
    return wait_status_code(root_status)


def containment_watchdog(options, command: list[str]) -> int:
    if not hasattr(os, "pidfd_open") or not hasattr(signal, "pidfd_send_signal"):
        raise RuntimeError("pidfds unavailable")
    enable_subreaper()
    original_parent = os.getppid()
    prctl(PR_SET_PDEATHSIG, signal.SIGTERM)
    if os.getppid() != original_parent:
        raise RuntimeError("caller exited during initialization")
    shutdown_signal = 0

    def request_shutdown(signum, _frame):
        nonlocal shutdown_signal
        shutdown_signal = shutdown_signal or signum

    for signum in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
        signal.signal(signum, request_shutdown)
    watchdog = open_process(os.getpid())
    if watchdog is None:
        raise RuntimeError("watchdog pidfd unavailable")
    worker_pid = os.fork()
    if worker_pid == 0:
        try:
            watchdog_copy = open_process(os.getppid())
            if watchdog_copy is None:
                os._exit(125)
            result = supervise_target(options, command, watchdog_copy)
            watchdog_copy.close()
            os._exit(result)
        except BaseException:
            os.write(2, b"process-supervisor: launch supervisor failed closed\n")
            os._exit(125)
    worker = open_process(worker_pid)
    if worker is None:
        watchdog.close()
        raise RuntimeError("launch supervisor pidfd unavailable")
    known: dict[int, ProcessHandle] = {worker.pid: worker}
    worker_status: int | None = None
    worker_reaped = False
    terminating = False
    kill_phase = False
    deadline = 0.0
    try:
        while True:
            refresh_descendants(os.getpid(), known)
            if not worker_reaped:
                try:
                    waited, status = os.waitpid(worker_pid, os.WNOHANG)
                except ChildProcessError:
                    waited, status = worker_pid, None
            else:
                waited, status = 0, None
            if not worker_reaped and waited == worker_pid:
                worker_reaped = True
                worker_status = status
                handle = known.pop(worker_pid, None)
                if handle is not None:
                    handle.close()
                terminating = True
                deadline = time.monotonic() + options.grace_seconds
                signal_all(known, signal.SIGTERM)
            if shutdown_signal and not terminating:
                terminating = True
                deadline = time.monotonic() + options.grace_seconds
                worker.signal(shutdown_signal)
            if terminating:
                refresh_descendants(os.getpid(), known)
                if worker_reaped:
                    signal_all(known, signal.SIGKILL if kill_phase else signal.SIGTERM)
                if not kill_phase and time.monotonic() >= deadline:
                    kill_phase = True
                    deadline = time.monotonic() + options.grace_seconds
                    signal_all(known, signal.SIGKILL)
                if worker_reaped and not known:
                    break
                if kill_phase and time.monotonic() >= deadline:
                    raise RuntimeError("containment watchdog could not reclaim descendants")
            time.sleep(0.01)
    finally:
        signal_all(known, signal.SIGKILL)
        final_deadline = time.monotonic() + options.grace_seconds
        while known and time.monotonic() < final_deadline:
            refresh_descendants(os.getpid(), known)
            signal_all(known, signal.SIGKILL)
            time.sleep(0.01)
        close_all(known)
        watchdog.close()
    if shutdown_signal:
        return 128 + shutdown_signal
    return wait_status_code(worker_status)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--identity-file", required=True)
    parser.add_argument("--log-file", required=True)
    parser.add_argument("--environment-file")
    parser.add_argument("--secrets-file")
    parser.add_argument("--working-directory")
    parser.add_argument("--maximum-output-bytes", type=int, default=16 * 1024 * 1024)
    parser.add_argument("--grace-seconds", type=float, default=5.0)
    parser.add_argument("--test-fail-publication", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    options = parser.parse_args()
    command = options.command[1:] if options.command[:1] == ["--"] else options.command
    if sys.platform != "linux" or not command:
        parser.error("Linux and a command after -- are required")
    if options.maximum_output_bytes < 1 or options.maximum_output_bytes > 16 * 1024 * 1024:
        parser.error("maximum output must be between 1 byte and 16 MiB")
    if options.grace_seconds <= 0 or options.grace_seconds > 30:
        parser.error("grace period must be between zero and thirty seconds")
    return containment_watchdog(options, command)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception:
        print("process-supervisor: containment watchdog failed closed", file=sys.stderr)
        raise SystemExit(125)
