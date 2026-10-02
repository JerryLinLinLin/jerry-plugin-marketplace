"""Select expensive Windows validation from the PR's changed paths."""
import argparse
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
WINDOWS_PREFIXES = ("src/HyperVControl/", "tests/HyperVControl.Tests/", "plugins/hyper-v-control/", ".github/workflows/")
WINDOWS_FILES = {
    ".gitattributes", "HyperVControl.slnx", "scripts/ci-plan.py",
    "scripts/build-hyperv-control.ps1", "scripts/test-installer.ps1", "scripts/test-release-isolation.ps1",
    "scripts/test-hyperv-launcher.ps1", "scripts/test-runtime-packaging.py",
    "plugins/rizin-re-toolkit/skills/rizin-re-toolkit/scripts/install.ps1",
}


def windows_required(paths):
    return any(not path.lower().endswith(".md") and
               (path in WINDOWS_FILES or path.startswith(WINDOWS_PREFIXES)) for path in paths)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", help="Base Git revision for a local check")
    parser.add_argument("--head", default="HEAD", help="Head Git revision for a local check")
    args = parser.parse_args()
    base, head = args.base, args.head
    if not base:
        if os.environ.get("GITHUB_EVENT_NAME") == "workflow_dispatch":
            required = True
        else:
            event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
            base, head = event["pull_request"]["base"]["sha"], event["pull_request"]["head"]["sha"]
    if base:
        changed = subprocess.check_output(["git", "diff", "--name-only", "-z", base, head, "--"], cwd=ROOT)
        paths = changed.decode("utf-8").rstrip("\0").split("\0") if changed else []
        required = windows_required(paths)
    result = {"windows": str(required).lower()}
    print(json.dumps(result))
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write(f'windows={result["windows"]}\n')


if __name__ == "__main__":
    main()
