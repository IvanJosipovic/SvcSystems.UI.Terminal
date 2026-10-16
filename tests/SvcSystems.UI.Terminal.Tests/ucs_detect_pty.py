#!/usr/bin/env python3
"""Run ucs-detect in a PTY and exchange framed bytes with the xUnit host."""

from __future__ import annotations

import fcntl
import json
import os
import pathlib
import pty
import site
import select
import signal
import struct
import subprocess
import sys
import termios
import time
import traceback


MAX_CONTROL_FRAME = 1024 * 1024


def read_pinned_requirements(requirements_path):
    requirements = []
    for raw_line in pathlib.Path(requirements_path).read_text(encoding="utf-8").splitlines():
        line = raw_line.partition("#")[0].strip()
        if not line:
            continue
        if "==" not in line:
            raise ValueError(f"Expected an exact package pin in {requirements_path}: {line}")
        name, expected = line.split("==", 1)
        requirements.append((name.strip(), expected.strip()))
    return requirements


def dependency_mismatches(requirements):
    from importlib.metadata import PackageNotFoundError, version

    result = []
    for name, expected in requirements:
        try:
            actual = version(name)
        except PackageNotFoundError:
            actual = None
        if actual != expected:
            result.append(f"{name}: expected {expected}, found {actual or 'missing'}")
    return result


def ensure_dependencies(requirements_path):
    requirements = read_pinned_requirements(requirements_path)
    missing = dependency_mismatches(requirements)
    if not missing:
        return

    try:
        install = subprocess.run(
            [sys.executable, "-m", "pip", "install", "--disable-pip-version-check", "-r", requirements_path],
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
        )
    except OSError as error:
        raise RuntimeError(
            "Could not run pip for the selected Python environment. Ensure it is a virtual "
            "environment with pip available."
        ) from error

    if install.returncode != 0:
        raise RuntimeError(
            "Failed to install the pinned ucs-detect requirements. "
            "Check Python/pip availability and network access.\n" + install.stdout
        )

    missing = dependency_mismatches(requirements)
    if missing:
        raise RuntimeError("The Python environment still does not match the pinned requirements: " + "; ".join(missing))


def check_dependencies(requirements_path):
    requirements = read_pinned_requirements(requirements_path)
    missing = dependency_mismatches(requirements)
    if missing:
        print("Pinned ucs-detect requirements need installation: " + "; ".join(missing), file=sys.stderr)
        return 2
    return 0


def read_exact(stream, count):
    parts = bytearray()
    while len(parts) < count:
        chunk = stream.read(count - len(parts))
        if not chunk:
            if not parts:
                return None
            raise EOFError("incomplete control frame")
        parts.extend(chunk)
    return bytes(parts)


def read_frame(stream):
    header = read_exact(stream, 5)
    if header is None:
        return None, None
    kind, size = struct.unpack(">BI", header)
    if size > MAX_CONTROL_FRAME:
        raise ValueError(f"control frame is too large: {size} bytes")
    payload = read_exact(stream, size)
    if payload is None:
        raise EOFError("missing control frame payload")
    return kind, payload


def write_frame(kind, payload):
    output = getattr(sys.stdout, "buffer", sys.stdout)
    output.write(struct.pack(">BI", kind, len(payload)))
    output.write(payload)
    output.flush()


def write_json_frame(kind, value):
    write_frame(kind, json.dumps(value, ensure_ascii=False).encode("utf-8"))


def write_transcript_record(transcript, direction, payload):
    transcript.write(direction + struct.pack(">I", len(payload)) + payload)


def dependency_versions():
    from importlib.metadata import version

    try:
        import blessed
        import ucs_detect
        import wcwidth
    except ImportError as error:
        raise RuntimeError(
            "Missing ucs-detect test dependencies. Create a Python virtual environment and "
            "install tests/requirements-ucs-detect.txt; see the Testing section in README.md."
        ) from error

    return {
        "ucs_detect": version("ucs-detect"),
        "blessed": version("blessed"),
        "wcwidth": version("wcwidth"),
        "prettytable": version("prettytable"),
        "PyYAML": version("PyYAML"),
        "requests": version("requests"),
        "jinxed": version("jinxed"),
        "certifi": version("certifi"),
        "charset-normalizer": version("charset-normalizer"),
        "idna": version("idna"),
        "urllib3": version("urllib3"),
        "colorama": version("colorama"),
        "ucs_detect_module": str(pathlib.Path(ucs_detect.__file__).resolve()),
        "blessed_module": str(pathlib.Path(blessed.__file__).resolve()),
    }


def run(request):
    versions = dependency_versions()
    expected = request["versions"]
    actual = {name: versions[name] for name in expected}
    if actual != expected:
        raise RuntimeError(f"Python test dependency versions differ: expected {expected}, found {actual}")

    report_path = pathlib.Path(request["report_path"])
    transcript_path = pathlib.Path(request["transcript_path"])
    report_path.parent.mkdir(parents=True, exist_ok=True)
    transcript_path.parent.mkdir(parents=True, exist_ok=True)

    env = os.environ.copy()
    terminal_identity_variables = (
        "TERM_PROGRAM", "TERM_PROGRAM_VERSION", "COLORTERM", "NO_COLOR", "CLICOLOR",
        "CLICOLOR_FORCE", "COLUMNS", "LINES", "KITTY_WINDOW_ID", "KITTY_LISTEN_ON",
        "WEZTERM_PANE", "WEZTERM_UNIX_SOCKET", "VTE_VERSION", "KONSOLE_VERSION",
        "KONSOLE_DBUS_SESSION", "WT_SESSION", "WT_PROFILE_ID", "TMUX", "TMUX_PANE",
        "TERM_SESSION_ID", "LC_TERMINAL", "LC_TERMINAL_VERSION", "SSH_TTY",
    )
    for key in terminal_identity_variables:
        env.pop(key, None)
    env.update({"TERM": "xterm-256color", "COLORTERM": "truecolor"})
    # Keep the venv's bin directory: resolving its Python symlink points back to the base
    # interpreter and hides console scripts installed into the venv (including ucs-detect).
    python_bin_directory = str(pathlib.Path(sys.executable).parent)
    user_script_directory = str(pathlib.Path(site.getuserbase()) / "bin")
    env["PATH"] = os.pathsep.join(
        part for part in (python_bin_directory, user_script_directory, env.get("PATH", "")) if part
    )

    rows = int(request.get("rows", 28))
    cols = int(request.get("cols", 131))
    child, master = pty.fork()
    if child == 0:
        try:
            fcntl.ioctl(0, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, cols * 21, rows * 52))
            terminal_settings = termios.tcgetattr(0)
            terminal_settings[3] &= ~termios.ECHO
            termios.tcsetattr(0, termios.TCSANOW, terminal_settings)
            argv = [
                "ucs-detect", "--test-only", "all", "--all", "--terminal-full-probe",
                "--detect-all-dec-modes",
                "--probe-silently", "--limit-category-time", "0", "--limit-errors", "0",
                "--timeout-cps", "3.0", "--timeout-query", "auto",
                "--save-json", str(report_path), "--no-final-summary",
            ]
            os.execvpe(argv[0], argv, env)
        except BaseException:
            traceback.print_exc()
            os._exit(127)

    os.set_blocking(master, False)
    input_fd = sys.stdin.fileno()
    os.set_blocking(input_fd, False)
    input_buffer = bytearray()
    input_open = True
    child_status = None
    transcript_started = time.monotonic()

    def terminate_child(signum, frame):
        del signum, frame
        try:
            os.kill(child, signal.SIGKILL)
        except ProcessLookupError:
            pass
        raise InterruptedError("xUnit host stopped the ucs-detect PTY runner")

    old_term_handler = signal.signal(signal.SIGTERM, terminate_child)
    with transcript_path.open("wb") as transcript:
        try:
            while child_status is None:
                readers = [master] if master >= 0 else []
                if input_open:
                    readers.append(input_fd)
                ready, _, _ = select.select(readers, [], [], 0.2)
                if master in ready:
                    try:
                        output = os.read(master, 65536)
                    except OSError as error:
                        if error.errno == 5:  # EIO: the PTY child closed its slave.
                            output = b""
                        else:
                            raise
                    if output:
                        write_transcript_record(transcript, b"O", output)
                        write_frame(1, output)
                    else:
                        os.close(master)
                        master = -1

                if input_open and input_fd in ready:
                    try:
                        data = os.read(input_fd, 65536)
                    except BlockingIOError:
                        data = None
                    if data == b"":
                        input_open = False
                    elif data:
                        input_buffer.extend(data)
                        while len(input_buffer) >= 5:
                            kind, size = struct.unpack(">BI", input_buffer[:5])
                            if size > MAX_CONTROL_FRAME:
                                raise ValueError(f"control frame is too large: {size} bytes")
                            if len(input_buffer) < 5 + size:
                                break
                            payload = bytes(input_buffer[5:5 + size])
                            del input_buffer[:5 + size]
                            if kind == 2:
                                if master < 0:
                                    raise BrokenPipeError("ucs-detect closed its PTY before receiving input")
                                write_transcript_record(transcript, b"I", payload)
                                view = memoryview(payload)
                                while view:
                                    count = os.write(master, view)
                                    view = view[count:]
                            elif kind == 3:
                                input_open = False
                                if master >= 0:
                                    os.close(master)
                                    master = -1
                            else:
                                raise ValueError(f"unknown host control frame: {kind}")
                        transcript.flush()

                waited_pid, status = os.waitpid(child, os.WNOHANG)
                if waited_pid == child:
                    child_status = os.waitstatus_to_exitcode(status)

                if time.monotonic() - transcript_started > 1800:
                    raise TimeoutError("ucs-detect exceeded the 30-minute test timeout")

            if master >= 0:
                try:
                    while True:
                        output = os.read(master, 65536)
                        if not output:
                            break
                        write_transcript_record(transcript, b"O", output)
                        write_frame(1, output)
                except OSError:
                    pass
            transcript.flush()

        except BaseException:
            try:
                os.kill(child, signal.SIGKILL)
            except ProcessLookupError:
                pass
            try:
                os.waitpid(child, 0)
            except ChildProcessError:
                pass
            raise
        finally:
            signal.signal(signal.SIGTERM, old_term_handler)
            if master >= 0:
                os.close(master)

    write_json_frame(2, {
        "exit_code": child_status,
        "report_path": str(report_path),
        "transcript_path": str(transcript_path),
        "dependencies": versions,
    })


def main():
    kind, payload = read_frame(getattr(sys.stdin, "buffer", sys.stdin))
    if kind != 1:
        raise ValueError("the xUnit host must send a start frame first")
    run(json.loads(payload))


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] in ("--check-dependencies", "--ensure-dependencies"):
        try:
            result = check_dependencies(sys.argv[2]) if sys.argv[1] == "--check-dependencies" else 0
            if sys.argv[1] == "--ensure-dependencies":
                ensure_dependencies(sys.argv[2])
        except BaseException:
            traceback.print_exc(file=sys.stderr)
            sys.exit(1)
        sys.exit(result)

    try:
        main()
    except BaseException as error:
        write_json_frame(3, {"error": str(error), "traceback": traceback.format_exc()})
        sys.exit(1)
