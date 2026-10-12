"""Read-only Frida environment report. Uses the interpreter running this file."""
import importlib.metadata
import json
import platform
import shutil
import struct
import sys


def report():
    result = {
        "python": sys.executable,
        "python_version": platform.python_version(),
        "python_bits": struct.calcsize("P") * 8,
        "os": platform.platform(),
        "machine": platform.machine(),
        "commands": {name: shutil.which(name) for name in ("frida", "frida-ps", "frida-trace")},
        "packages": {},
    }
    for name in ("frida", "frida-tools"):
        try:
            result["packages"][name] = importlib.metadata.version(name)
        except importlib.metadata.PackageNotFoundError:
            result["packages"][name] = None
    try:
        import frida
        result["frida_import"] = {"path": frida.__file__, "version": frida.__version__}
    except (ImportError, OSError) as exc:
        result["import_error"] = str(exc)
    return result


if __name__ == "__main__":
    data = report()
    print(json.dumps(data, ensure_ascii=True, indent=2))
    raise SystemExit(1 if "import_error" in data else 0)
