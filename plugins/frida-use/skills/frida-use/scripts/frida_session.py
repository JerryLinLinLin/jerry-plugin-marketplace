"""Bounded local Frida capture with explicit agent readiness and owned-spawn cleanup."""
from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import math
from pathlib import Path
import platform
import queue
import sys
import threading
import time
from datetime import datetime, timezone


class WindowsExitProbe:
    """Hold a read-only process handle so exit status survives PID reuse."""

    def __init__(self, pid):
        import ctypes
        from ctypes import wintypes
        self.ctypes = ctypes
        self.api = ctypes.WinDLL("kernel32", use_last_error=True)
        self.api.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.api.OpenProcess.restype = wintypes.HANDLE
        self.api.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self.api.WaitForSingleObject.restype = wintypes.DWORD
        self.api.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        self.api.GetExitCodeProcess.restype = wintypes.BOOL
        self.api.CloseHandle.argtypes = [wintypes.HANDLE]
        self.api.CloseHandle.restype = wintypes.BOOL
        self.handle = self.api.OpenProcess(0x100000 | 0x1000, False, pid)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())

    def exit_code(self, wait_ms=0):
        status = self.api.WaitForSingleObject(self.handle, wait_ms)
        if status == 0x102:
            return None
        if status != 0:
            raise self.ctypes.WinError(self.ctypes.get_last_error())
        value = self.ctypes.c_ulong()
        if not self.api.GetExitCodeProcess(self.handle, self.ctypes.byref(value)):
            raise self.ctypes.WinError(self.ctypes.get_last_error())
        return value.value

    def close(self):
        if self.handle:
            self.api.CloseHandle(self.handle)
            self.handle = None


class Recorder:
    """Callbacks enqueue; the controlling thread owns all filesystem writes."""

    def __init__(self, directory: Path, max_blob=1048576, max_total=67108864):
        directory.mkdir(parents=True, exist_ok=False)
        self.directory = directory
        self.stream = (directory / "events.jsonl").open("x", encoding="utf-8", newline="\n")
        self.pending = queue.Queue(maxsize=128)
        self.max_blob = max_blob
        self.max_total = max_total
        self.binary_bytes = 0
        self.sequence = 0
        self.dropped = 0
        self.truncated = 0
        self.lock = threading.Lock()
        self.accepting = True

    def enqueue(self, kind, fields=None, data=None):
        record = {"kind": kind, "utc": datetime.now(timezone.utc).isoformat(),
                  "monotonic_ns": time.monotonic_ns(), **(fields or {})}
        blob = None
        if data is not None:
            record["original_binary_size"] = len(data)
            blob = bytes(data[:self.max_blob])
        with self.lock:
            if not self.accepting:
                return
            try:
                self.pending.put_nowait((record, blob))
            except queue.Full:
                self.dropped += 1

    def write(self, record, blob=None):
        self.sequence += 1
        record = {"sequence": self.sequence, **record}
        if blob is not None:
            blob = blob[:max(0, self.max_total - self.binary_bytes)]
            truncated = len(blob) != record["original_binary_size"]
            self.truncated += int(truncated)
            name = f"blob-{self.sequence:06d}.bin"
            (self.directory / name).write_bytes(blob)
            self.binary_bytes += len(blob)
            record["binary"] = {"file": name, "size": len(blob), "truncated": truncated,
                                "sha256": hashlib.sha256(blob).hexdigest()}
        self.stream.write(json.dumps(record, ensure_ascii=True) + "\n")
        self.stream.flush()

    def drain(self):
        # Bounded batch: a busy producer must not starve the capture deadline.
        for _ in range(128):
            try:
                record, blob = self.pending.get_nowait()
            except queue.Empty:
                break
            self.write(record, blob)

    def close(self, code):
        with self.lock:
            self.accepting = False
        while not self.pending.empty():
            self.drain()
        self.write({"kind": "summary", "exit_code": code, "dropped_events": self.dropped,
                    "truncated_blobs": self.truncated, "binary_bytes": self.binary_bytes})
        self.stream.close()


def finite_positive(value):
    result = float(value)
    if not math.isfinite(result) or result <= 0:
        raise argparse.ArgumentTypeError("must be a finite positive number")
    return result


def positive_int(value):
    result = int(value)
    if result <= 0:
        raise argparse.ArgumentTypeError("must be positive")
    return result


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    target = result.add_mutually_exclusive_group(required=True)
    target.add_argument("--pid", type=positive_int, help="attach to an existing local PID")
    target.add_argument("--spawn", type=Path, help="spawn an executable, with target arguments after --")
    result.add_argument("--script", type=Path, default=Path(__file__).resolve().parents[1] / "assets/windows-observe.js")
    result.add_argument("--out", type=Path, required=True, help="new capture directory; existing directories are refused")
    result.add_argument("--duration", type=finite_positive, default=30.0)
    result.add_argument("--ready-timeout", type=finite_positive, default=10.0)
    result.add_argument("--call-timeout", type=finite_positive, default=10.0)
    result.add_argument("--runtime", choices=("qjs", "v8"), default="qjs")
    result.add_argument("--debug-port", type=positive_int, help="V8 Inspector port; use --runtime v8")
    result.add_argument("--kill-on-exit", action="store_true", help="kill only the target spawned by this run")
    result.add_argument("args", nargs=argparse.REMAINDER)
    return result


def run(options, frida):
    source_bytes = options.script.resolve().read_bytes()
    source = source_bytes.decode("utf-8-sig")
    recorder = Recorder(options.out.resolve())
    ready, ended, failed = threading.Event(), threading.Event(), threading.Event()
    session = script = device = None
    spawned_pid = None
    resumed = False
    target_gone = False
    exit_probe = None
    code = 0

    def call(function, *args, **kwargs):
        # Frida's cancellation is cooperative; the target is never force-killed
        # just to interrupt an attach-mode operation.
        with frida.Cancellable() as cancellable:
            timer = threading.Timer(options.call_timeout, cancellable.cancel)
            timer.daemon = True
            timer.start()
            try:
                return function(*args, **kwargs)
            finally:
                timer.cancel()

    def on_message(message, data):
        recorder.enqueue("message", {"message": message}, data)
        if message.get("type") == "error":
            failed.set()
        payload = message.get("payload")
        if message.get("type") == "send" and isinstance(payload, dict) and payload.get("type") == "ready":
            ready.set()

    def on_detached(reason, crash=None):
        nonlocal target_gone
        details = None if crash is None else {
            key: getattr(crash, key, None) for key in ("pid", "process_name", "summary", "report")
        }
        recorder.enqueue("detached", {"reason": reason, "crash": details})
        if crash is not None or reason not in ("application-requested", "process-terminated"):
            failed.set()
        if reason == "process-terminated":
            target_gone = True
        ended.set()

    try:
        try:
            tools_version = importlib.metadata.version("frida-tools")
        except importlib.metadata.PackageNotFoundError:
            tools_version = None
        recorder.enqueue("host", {"python": sys.executable, "python_version": platform.python_version(),
            "os": platform.platform(), "frida": frida.__version__, "frida_tools": tools_version,
            "script": str(options.script.resolve()), "script_sha256": hashlib.sha256(source_bytes).hexdigest(),
            "runtime": options.runtime, "duration_seconds": options.duration})
        device = call(frida.get_local_device)
        if options.spawn:
            target_args = options.args[1:] if options.args[:1] == ["--"] else options.args
            argv = [str(options.spawn.resolve()), *target_args]
            spawned_pid = call(device.spawn, argv, stdio="pipe")
            pid = spawned_pid
            recorder.enqueue("spawned", {"pid": pid, "argv": argv})
        else:
            pid = options.pid
        if sys.platform == "win32":
            try:
                exit_probe = WindowsExitProbe(pid)
            except OSError as exc:
                recorder.enqueue("exit_status_unavailable", {"description": str(exc)})
        session = call(device.attach, pid)
        session.on("detached", on_detached)
        recorder.enqueue("attached", {"pid": pid, "owned_spawn": spawned_pid is not None})
        script = call(session.create_script, source, name=options.script.stem, runtime=options.runtime)
        script.on("message", on_message)
        script.set_log_handler(lambda level, text: recorder.enqueue("console", {"level": level, "text": text}))
        if options.debug_port:
            call(script.enable_debugger, options.debug_port)
            recorder.enqueue("inspector", {"port": options.debug_port})
        call(script.load)
        deadline = time.monotonic() + options.ready_timeout
        while not ready.is_set():
            recorder.drain()
            if failed.is_set() or ended.is_set():
                raise RuntimeError("agent failed or target detached before readiness")
            if time.monotonic() >= deadline:
                raise TimeoutError("agent did not send {type: 'ready'} before the deadline")
            ready.wait(0.05)
        if failed.is_set() or ended.is_set():
            raise RuntimeError("agent failed or target detached during startup")
        if spawned_pid is not None:
            call(device.resume, spawned_pid)
            resumed = True
            recorder.enqueue("resumed", {"pid": spawned_pid})
        deadline = time.monotonic() + options.duration
        while not ended.is_set() and not failed.is_set() and time.monotonic() < deadline:
            recorder.drain()
            ended.wait(min(0.05, max(0, deadline - time.monotonic())))
        if failed.is_set():
            code = 1
    except KeyboardInterrupt:
        code = 130
        recorder.enqueue("interrupted")
    except Exception as exc:
        code = 1
        recorder.enqueue("controller_error", {"error": type(exc).__name__, "description": str(exc)})
        print(f"{type(exc).__name__}: {exc}", file=sys.stderr)
    finally:
        if exit_probe is not None:
            try:
                exit_status = exit_probe.exit_code(1000 if target_gone else 0)
                if exit_status is not None:
                    target_gone = True
                    recorder.enqueue("target_exit", {"windows_exit_code": exit_status, "hex": f"0x{exit_status:08x}"})
                    if exit_status != 0 and code == 0:
                        code = 1
            except OSError as exc:
                recorder.enqueue("exit_status_unavailable", {"description": str(exc)})
        # Kill a failed/suspended owned spawn before detaching, since detach can
        # release its suspension. Never kill an externally supplied attach PID.
        if spawned_pid is not None and not target_gone and (options.kill_on_exit or not resumed):
            try:
                call(device.kill, spawned_pid)
                target_gone = True
                recorder.enqueue("killed_owned_spawn", {"pid": spawned_pid})
            except frida.ProcessNotFoundError:
                target_gone = True
            except Exception as exc:
                code = 1
                recorder.enqueue("cleanup_error", {"operation": "kill", "pid": spawned_pid, "description": str(exc)})
        if script is not None and not target_gone and not ended.is_set():
            try:
                if "dispose" in call(script.list_exports_sync):
                    call(script.exports_sync.dispose)
            except frida.InvalidOperationError:
                pass  # The target can exit between the liveness check and RPC.
            except Exception as exc:
                code = 1
                recorder.enqueue("cleanup_error", {"operation": "dispose", "description": str(exc)})
            try:
                call(script.unload)
            except frida.InvalidOperationError:
                pass  # A natural target exit may race cleanup.
            except Exception as exc:
                code = 1
                recorder.enqueue("cleanup_error", {"operation": "unload", "description": str(exc)})
        if session is not None:
            try:
                call(session.detach)
            except frida.InvalidOperationError:
                pass
            except Exception as exc:
                code = 1
                recorder.enqueue("cleanup_error", {"operation": "detach", "description": str(exc)})
        if recorder.dropped and code == 0:
            code = 2
        if exit_probe is not None:
            exit_probe.close()
        recorder.close(code)
    print(f"Capture: {recorder.directory} (exit {code})")
    return code


def main(argv=None):
    cli = parser()
    options = cli.parse_args(argv)
    if options.pid and (options.kill_on_exit or options.args):
        cli.error("--kill-on-exit and target arguments require --spawn")
    if options.debug_port and (options.runtime != "v8" or options.debug_port > 65535):
        cli.error("--debug-port requires --runtime v8 and a port from 1 to 65535")
    if options.spawn and not options.spawn.is_file():
        cli.error("--spawn must name an existing executable")
    if not options.script.is_file():
        cli.error("--script must name an existing JavaScript file or compiled Frida bundle")
    if options.out.exists():
        cli.error("--out must be a new directory")
    try:
        import frida
        return run(options, frida)
    except (ImportError, OSError, UnicodeError) as exc:
        print(f"{type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
